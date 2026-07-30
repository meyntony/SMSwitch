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

		public async Task<SMSwitchResponseSendOTP> SendOTP(MobileNumber mobileWithCountryCode, HashSet<LanguageIsoCode> preferredLanguageIsoCodeList, UserAgent userAgent, byte resendCooldownPeriodInSeconds = 60, CancellationToken cancellationToken = default)
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

				Queue<SmsProvider>? smsProvidersQueue = null;
				if (session.SmsProvidersQueue?.Any() ?? false)
				{
					smsProvidersQueue = session.SmsProvidersQueue;
				}
				else
				{
					smsProvidersQueue = new();
					var smsProviders = _smSwitchInitializer.SmsControls.PriorityBasedOnCountryPhoneCode.TryGetValue(mobileWithCountryCode.CountryPhoneCodeAsNumericString, out var configuredProviders)
							? configuredProviders
							: _smSwitchInitializer.SmsControls.FallBackPriority;
					for (int i = 0; i < _smSwitchInitializer.SmsControls.MaxRoundRobinAttempts; i++)
					{
						foreach (SmsProvider smsProvider in smsProviders)
						{
							smsProvidersQueue.Enqueue(smsProvider);
						}
					}
				}

				if (smsProvidersQueue.Count == 0)
				{
					return new SMSwitchResponseSendOTP()
					{
						IsSent = false
					};
				}

				while (smsProvidersQueue.Any())
				{
					if (session.SentAttempts.Any())
					{
						smsProvidersQueue.Dequeue();
						if (!smsProvidersQueue.Any())
						{
							break;
						}
					}
					responseSendOTP = await ProviderFor(smsProvidersQueue.Peek())
						.SendOTP(mobileWithCountryCode, preferredLanguageIsoCodeList, userAgent, resendCooldownPeriodInSeconds, cancellationToken);

					session.SentAttempts.Add(new AttemptDetailsSendOTP(DateTimeOffset.UtcNow, smsProvidersQueue.Peek(), responseSendOTP));
					if (responseSendOTP.IsSent)
					{
						break;
					}
				}

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

		public async Task<bool> SendSMS(MobileNumber mobileWithCountryCode, string shortMessageServiceMessage, byte resendCooldownPeriodInSeconds = 60, CancellationToken cancellationToken = default)
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

				Queue<SmsProvider>? smsProvidersQueue = null;
				if (session.SmsProvidersQueue?.Any() ?? false)
				{
					smsProvidersQueue = session.SmsProvidersQueue;
				}
				else
				{
					smsProvidersQueue = new();
					var smsProviders = _smSwitchInitializer.SmsControls.PriorityBasedOnCountryPhoneCode.TryGetValue(mobileWithCountryCode.CountryPhoneCodeAsNumericString, out var configuredProviders)
							? configuredProviders
							: _smSwitchInitializer.SmsControls.FallBackPriority;
					for (int i = 0; i < _smSwitchInitializer.SmsControls.MaxRoundRobinAttempts; i++)
					{
						foreach (SmsProvider smsProvider in smsProviders)
						{
							smsProvidersQueue.Enqueue(smsProvider);
						}
					}
				}

				if (smsProvidersQueue.Count == 0)
				{
					return false;
				}

				bool isSent = false;
				while (smsProvidersQueue.Any())
				{
					if (session.SentAttempts.Any())
					{
						smsProvidersQueue.Dequeue();
						if (!smsProvidersQueue.Any())
						{
							break;
						}
					}
					isSent = await ProviderFor(smsProvidersQueue.Peek())
						.SendSMS(mobileWithCountryCode, shortMessageServiceMessage, resendCooldownPeriodInSeconds, cancellationToken);

					session.SentAttempts.Add(new AttemptDetailsSendSMS(DateTimeOffset.UtcNow, smsProvidersQueue.Peek(), isSent));
					if (isSent)
					{
						session.SuccessfullySentTimestampUTC = DateTimeOffset.UtcNow;
						break;
					}
					else
					{
						session.FailedAttemptsDateTimeOffset.Add(DateTimeOffset.UtcNow);
					}
				}

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

			return new SMSwitchResponseVerifyOTP() {
				Verified = false,
				Expired = true
			};
		}
	}
}
