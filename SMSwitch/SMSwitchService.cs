using HumanLanguages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMSwitch.Common;
using SMSwitch.Common.DTOs;
using SMSwitch.Countries.Database;
using SMSwitch.Database;
using SMSwitch.Database.DTOs;

namespace SMSwitch
{
	public sealed class SMSwitchService : IServiceMobileNumbers
	{

		private readonly SMSwitchInitializer _smSwitchInitializer;

		private readonly IServiceProvider _serviceProvider;

		private readonly SMSwitchDbService _smSwitchDbService;
		private readonly CountryDbService _countryDbService;
		private readonly ILogger<SMSwitchService> _logger;


		public SMSwitchService(
			SMSwitchInitializer smSwitchInitializer,
			IServiceProvider serviceProvider,
			SMSwitchDbService smSwitchDbService,
			CountryDbService countryDbService,
			ILogger<SMSwitchService> logger
			)
		{
			_smSwitchInitializer = smSwitchInitializer;
			_serviceProvider = serviceProvider;
			_smSwitchDbService = smSwitchDbService;
			_countryDbService = countryDbService;
			_logger = logger;
		}

		/// <summary>
		/// Replaces the switch that used to be repeated in SendOTP, SendSMS and VerifyOTP, each
		/// with its own unreachable <see cref="NotImplementedException"/>. Adding a provider is now
		/// a registration in <see cref="ServiceCollectionExtensions.AddSMSwitchServices"/>.
		/// </summary>
		private IServiceMobileNumbers ProviderFor(SmsProvider smsProvider) =>
			_serviceProvider.GetRequiredKeyedService<IServiceMobileNumbers>(smsProvider);

		// IServiceMobileNumbers is the single-provider contract and carries only the delivery
		// confirmation timeout, because deduplicating repeated sends is this class's job rather than
		// a provider's. Implemented explicitly so that the richer overloads above are what ordinary
		// callers see, with no overload ambiguity. Reached through the interface, the one timeout
		// serves as both, which is exactly how the single parameter used to behave.
		Task<SMSwitchResponseSendOTP> IServiceMobileNumbers.SendOTP(MobileNumber mobileWithCountryCode, HashSet<LanguageIsoCode> preferredLanguageIsoCodeList, UserAgent userAgent, byte deliveryConfirmationTimeoutInSeconds, CancellationToken cancellationToken) =>
			SendOTP(mobileWithCountryCode, preferredLanguageIsoCodeList, userAgent, deliveryConfirmationTimeoutInSeconds, deliveryConfirmationTimeoutInSeconds, cancellationToken);

		Task<bool> IServiceMobileNumbers.SendSMS(MobileNumber mobileWithCountryCode, string shortMessageServiceMessage, byte deliveryConfirmationTimeoutInSeconds, CancellationToken cancellationToken) =>
			SendSMS(mobileWithCountryCode, shortMessageServiceMessage, deliveryConfirmationTimeoutInSeconds, deliveryConfirmationTimeoutInSeconds, cancellationToken);

		/// <param name="resendCooldownPeriodInSeconds">
		/// How long a repeated send returns the previous result instead of sending again.
		/// </param>
		/// <param name="deliveryConfirmationTimeoutInSeconds">
		/// How long a provider waits for delivery confirmation before giving up. This is what the
		/// call can block for, so keep it short on an interactive request path.
		/// </param>
		public async Task<SMSwitchResponseSendOTP> SendOTP(MobileNumber mobileWithCountryCode, HashSet<LanguageIsoCode> preferredLanguageIsoCodeList, UserAgent userAgent, byte resendCooldownPeriodInSeconds = 60, byte deliveryConfirmationTimeoutInSeconds = 60, CancellationToken cancellationToken = default)
		{
			if (!(mobileWithCountryCode?.IsValid() ?? false))
			{
				_logger.LogWarning("Refusing to send OTP: the mobile number is missing or contains no digits");
				return new SMSwitchResponseSendOTP()
				{
					IsSent = false
				};
			}

			SMSwitchResponseSendOTP? responseSendOTP = null;
			SMSwitchSession? session = null;
			try
			{
				session = await _smSwitchDbService.GetOrCreateAndGetLatestSession(mobileWithCountryCode, cancellationToken);

				if (session is null)
				{
					_logger.LogError("Unable to send OTP to {PhoneNumber} because no session was created!!", mobileWithCountryCode?.CountryPhoneCodeAndPhoneNumber);
					return new SMSwitchResponseSendOTP()
					{
						IsSent = false
					};
				}

				if (session.SentAttempts.Any())
				{
					var latestSentAttempt = session.SentAttempts.Last();
					if ((latestSentAttempt?.Response?.IsSent ?? false) && latestSentAttempt.AttemptTimeInUTC.AddSeconds(resendCooldownPeriodInSeconds) > DateTimeOffset.UtcNow)
					{
						_logger.LogInformation("OTP already sent to {PhoneNumber} with SessionId: {SessionId}", mobileWithCountryCode?.CountryPhoneCodeAndPhoneNumber, session?.SessionId);
						return latestSentAttempt.Response;
					}
				}

				var smsProvidersQueue = ProviderFailover.BuildQueue(
					_smSwitchInitializer.SmsControls,
					mobileWithCountryCode.CountryPhoneCodeAsNumericString,
					session.SmsProvidersQueue);

				if (smsProvidersQueue.Count == 0)
				{
					return new SMSwitchResponseSendOTP()
					{
						IsSent = false
					};
				}

				responseSendOTP = await ProviderFailover.TrySendThroughQueue(
					smsProvidersQueue,
					hasEarlierAttempts: session.SentAttempts.Any(),
					send: smsProvider => ProviderFor(smsProvider).SendOTP(mobileWithCountryCode, preferredLanguageIsoCodeList, userAgent, deliveryConfirmationTimeoutInSeconds, cancellationToken),
					succeeded: response => response.IsSent,
					recordAttempt: (smsProvider, response) => session.SentAttempts.Add(new AttemptDetailsSendOTP(DateTimeOffset.UtcNow, smsProvider, response)));

				session.SmsProvidersQueue = smsProvidersQueue;
				await _smSwitchDbService.UpdateSession(session, cancellationToken);

				if (responseSendOTP == null || !responseSendOTP.IsSent)
				{
					_logger.LogError("Unable to send OTP to {PhoneNumber} with SessionId: {SessionId}", mobileWithCountryCode?.CountryPhoneCodeAndPhoneNumber, session?.SessionId);
				}
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Unable to send OTP to {PhoneNumber} with SessionId: {SessionId}", mobileWithCountryCode?.CountryPhoneCodeAndPhoneNumber, session?.SessionId);
			}

			return responseSendOTP ?? new SMSwitchResponseSendOTP() { IsSent = false };
		}

		/// <param name="resendCooldownPeriodInSeconds">
		/// How long a repeated send of the same message to the same number returns the previous
		/// result instead of sending again.
		/// </param>
		/// <param name="deliveryConfirmationTimeoutInSeconds">
		/// How long a provider waits for delivery confirmation before giving up. This is what the
		/// call can block for, so keep it short on an interactive request path.
		/// </param>
		public async Task<bool> SendSMS(MobileNumber mobileWithCountryCode, string shortMessageServiceMessage, byte resendCooldownPeriodInSeconds = 60, byte deliveryConfirmationTimeoutInSeconds = 60, CancellationToken cancellationToken = default)
		{
			if (!(mobileWithCountryCode?.IsValid() ?? false))
			{
				_logger.LogWarning("Refusing to send SMS: the mobile number is missing or contains no digits");
				return false;
			}

			try
			{
				var session = await _smSwitchDbService.GetOrCreateAndGetLatestSendSMSSession(mobileWithCountryCode, shortMessageServiceMessage, cancellationToken);

				if (session.SentAttempts.Any())
				{
					var latestSentAttempt = session.SentAttempts.Last();
					if ((latestSentAttempt?.IsSent ?? false) && latestSentAttempt.AttemptTimeInUTC.AddSeconds(resendCooldownPeriodInSeconds) > DateTimeOffset.UtcNow)
					{
						_logger.LogInformation("Message already sent to {PhoneNumber} with SessionId: {SessionId}", mobileWithCountryCode?.CountryPhoneCodeAndPhoneNumber, session?.SessionId);
						return latestSentAttempt.IsSent;
					}
				}

				var smsProvidersQueue = ProviderFailover.BuildQueue(
					_smSwitchInitializer.SmsControls,
					mobileWithCountryCode.CountryPhoneCodeAsNumericString,
					session.SmsProvidersQueue);

				if (smsProvidersQueue.Count == 0)
				{
					return false;
				}

				var isSent = await ProviderFailover.TrySendThroughQueue(
					smsProvidersQueue,
					hasEarlierAttempts: session.SentAttempts.Any(),
					send: smsProvider => ProviderFor(smsProvider).SendSMS(mobileWithCountryCode, shortMessageServiceMessage, deliveryConfirmationTimeoutInSeconds, cancellationToken),
					succeeded: sent => sent,
					recordAttempt: (smsProvider, sent) =>
					{
						session.SentAttempts.Add(new AttemptDetailsSendSMS(DateTimeOffset.UtcNow, smsProvider, sent));
						if (sent)
						{
							session.SuccessfullySentTimestampUTC = DateTimeOffset.UtcNow;
						}
						else
						{
							session.FailedAttemptsDateTimeOffset.Add(DateTimeOffset.UtcNow);
						}
					});

				session.SmsProvidersQueue = smsProvidersQueue;
				await _smSwitchDbService.UpdateSendSMSSession(session, cancellationToken);

				if (!isSent)
				{
					_logger.LogError("Unable to send SMS to {PhoneNumber} with SessionId: {SessionId}", mobileWithCountryCode?.CountryPhoneCodeAndPhoneNumber, session?.SessionId);
				}

				return isSent;
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Unable to send SMS to {PhoneNumber} with message: {shortMessageServiceMessage}", mobileWithCountryCode?.CountryPhoneCodeAndPhoneNumber, shortMessageServiceMessage);
				return false;
			}
		}

		public async Task<SMSwitchResponseVerifyOTP> VerifyOTP(MobileNumber mobileWithCountryCode, string OTP, CancellationToken cancellationToken = default)
		{
			if (!(mobileWithCountryCode?.IsValid() ?? false))
			{
				_logger.LogWarning("Refusing to verify OTP: the mobile number is missing or contains no digits");
				return new SMSwitchResponseVerifyOTP()
				{
					Verified = false,
					Expired = true
				};
			}

			try
			{
				var session = await _smSwitchDbService.GetLatestSession(mobileWithCountryCode, cancellationToken);

				if (session?.SmsProvidersQueue?.Any() ?? false)
				{
					var mobileNumberVerified = await ProviderFor(session.SmsProvidersQueue.Peek())
						.VerifyOTP(mobileWithCountryCode, OTP, cancellationToken);

					SMSwitchSession? updatedSession;
					if (mobileNumberVerified.Verified)
					{
						updatedSession = await _smSwitchDbService.RecordSuccessfulVerification(session.SessionId, cancellationToken);
						if (updatedSession is null)
						{
							// The session was exhausted, expired or already verified by a concurrent
							// request while this provider call was in flight, so this success cannot
							// be honoured.
							_logger.LogInformation("Verification succeeded too late to be accepted for {PhoneNumber} with SessionId: {SessionId}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber, session.SessionId);
							return new SMSwitchResponseVerifyOTP()
							{
								Verified = false,
								Expired = true
							};
						}

						// Deliberately not awaited: recording the observed number length must not
						// slow down or fail a verification. The continuation keeps a failure from
						// becoming an unobserved task exception.
						_ = _countryDbService.FeedbackAsync(
							countryPhoneCode: mobileWithCountryCode.CountryPhoneCodeAsNumericString,
							phoneNumberLength: (byte)mobileWithCountryCode.PhoneNumberAsNumericString.Length,
							countryIsoCode: mobileWithCountryCode.CountryIsoCode)
							.ContinueWith(
								task => _logger.LogError(task.Exception, "Unable to record country feedback for {CountryIsoCode}", mobileWithCountryCode.CountryIsoCode),
								TaskContinuationOptions.OnlyOnFaulted);
					}
					else
					{
						updatedSession = await _smSwitchDbService.RecordFailedVerificationAttempt(session.SessionId, cancellationToken);
					}

					mobileNumberVerified.Expired = !(updatedSession?.HasNotExpired(_smSwitchInitializer.SmsControls.MaximumFailedAttemptsToVerify) ?? false);
					return mobileNumberVerified;
				}

				if (session is not null)
				{
					await _smSwitchDbService.RecordFailedVerificationAttempt(session.SessionId, cancellationToken);
				}
				else
				{
					_logger.LogInformation("Session not found: Unable to verify OTP for {PhoneNumber}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
				}
			}
			catch (Exception exception)
			{
				// SendOTP and SendSMS both contain their failures; this method used to let a
				// database error escape to the caller instead.
				_logger.LogError(exception, "Unable to verify OTP for {PhoneNumber}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
			}

			return new SMSwitchResponseVerifyOTP()
			{
				Verified = false,
				Expired = true
			};
		}
	}
}
