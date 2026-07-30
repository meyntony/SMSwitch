using HumanLanguages;
using Microsoft.Extensions.Logging;
using SMSwitch.Common;
using SMSwitch.Common.DTOs;
using SMSwitch.Services.Plivo.Database;

namespace SMSwitch.Services.Plivo
{
	public sealed class PlivoService : IServiceMobileNumbers
	{
		private readonly PlivoInitializer _plivoInitializer;
		private readonly ILogger<PlivoService> _logger;
		private readonly PlivoDbService _plivoDbService;

		public PlivoService(PlivoInitializer plivoInitializer, ILogger<PlivoService> logger, PlivoDbService plivoDbService)
		{
			_plivoInitializer = plivoInitializer;
			_logger = logger;
			_plivoDbService = plivoDbService;
		}

		/// <summary>
		/// Plivo support said we need to contact them to add more translations of their SMS template in different languages
		/// I contacted them and added da for Danish
		/// </summary>
		private static readonly SupportedLocales _supportedLocalesForVerifyDefaultTemplate = new(
			"en",
			"da");


		public async Task<SMSwitchResponseSendOTP> SendOTP(MobileNumber mobileWithCountryCode, HashSet<LanguageIsoCode> preferredLanguageIsoCodeList, UserAgent userAgent, byte resendCooldownPeriodInSeconds = 60, CancellationToken cancellationToken = default)
		{
			if (_plivoInitializer.PlivoApi is null || _plivoInitializer.PlivoSettings is null)
				return new SMSwitchResponseSendOTP() { IsSent = false };

			string preferredLocale = "en";
			try
			{
				preferredLocale = _supportedLocalesForVerifyDefaultTemplate.Resolve(preferredLanguageIsoCodeList);

				var verifySessionResponse = _plivoInitializer.PlivoApi.VerifySession.Create(
					recipient: mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber,
					app_uuid: _plivoInitializer.PlivoSettings.PlivoPrivateSettings.AppUuid,
					url: _plivoInitializer.NotificationUrl,
					method: "GET",
					channel: "sms",
					locale: preferredLocale);

				await _plivoDbService.SetLatestSessionUUID(mobileWithCountryCode, verifySessionResponse.SessionUUID, cancellationToken);

				bool isSent = false;
				if (IsSuccessStatusCode(verifySessionResponse.StatusCode))
				{
					isSent = await _plivoDbService.KeepCheckingTheDatabaseIfSentEvery2seconds(verifySessionResponse.SessionUUID, expiry: DateTimeOffset.UtcNow.AddSeconds(resendCooldownPeriodInSeconds), cancellationToken);
				}
				return new SMSwitchResponseSendOTP()
				{
					IsSent = isSent,
					OtpLength = _plivoInitializer.PlivoSettings.OtpLength
				};
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Could not send OTP to +{MobileNumber} in {preferredLocale}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber, preferredLocale);
				return new SMSwitchResponseSendOTP()
				{
					IsSent = false
				};
			}
		}

		public async Task<bool> SendSMS(MobileNumber mobileWithCountryCode, string shortMessageServiceMessage, byte resendCooldownPeriodInSeconds = 60, CancellationToken cancellationToken = default)
		{
			if (_plivoInitializer.PlivoApi is null || _plivoInitializer.PlivoSettings is null)
				return false;

			if (string.IsNullOrWhiteSpace(_plivoInitializer.PlivoSettings.PlivoPrivateSettings.SourceNumber))
			{
				_logger.LogCritical("SourceNumber missing!!");
				return false;
			}
			try
			{
				// Send SMS using Plivo API
				var response = await _plivoInitializer.PlivoApi.Message.CreateAsync(
					src: _plivoInitializer.PlivoSettings.PlivoPrivateSettings.SourceNumber, // Replace with your Plivo source number
					dst: mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber,
					text: shortMessageServiceMessage
				);

				// Check if the message was accepted by Plivo
				if (response != null && response.MessageUuid.Any())
				{
					var messageUuid = response.MessageUuid.First();
					return await KeepCheckingIfSentEvery2seconds(messageUuid, expiry: DateTimeOffset.UtcNow.AddSeconds(resendCooldownPeriodInSeconds), cancellationToken);
				}

				_logger.LogWarning("Failed to send SMS to {ToNumber}. Response: {Response}",
					mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber, response);
				return false;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error occurred while sending SMS to {PhoneNumber}",
					mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
				return false;
			}
		}

		/// <summary>
		/// Plivo reports the HTTP status on the response object. Testing it with
		/// StatusCode.ToString().StartsWith("2") also matched 422 Unprocessable Entity, so a
		/// rejected request was read as a success.
		/// </summary>
		private static bool IsSuccessStatusCode(uint statusCode) => statusCode is >= 200 and < 300;

		private async Task<bool> KeepCheckingIfSentEvery2seconds(string messageUuid, DateTimeOffset expiry, CancellationToken cancellationToken)
		{
			// Fetch the message status
			var messageDetails = await _plivoInitializer.PlivoApi!.Message.GetAsync(messageUuid);

			// Check if the message was delivered
			if (messageDetails.MessageState == "delivered")
			{
				return true;
			}
			else if (DateTimeOffset.UtcNow > expiry || cancellationToken.IsCancellationRequested)
			{
				return false;
			}
			else
			{
				// The Plivo SDK takes no CancellationToken, so the token cannot reach the fetch
				// itself. Honouring it around the wait still stops this loop from running on for
				// the rest of the window after the caller has gone away.
				await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
				return await KeepCheckingIfSentEvery2seconds(messageUuid, expiry, cancellationToken);
			}
		}

		public async Task<SMSwitchResponseVerifyOTP> VerifyOTP(MobileNumber mobileWithCountryCode, string OTP, CancellationToken cancellationToken = default)
		{
			if (_plivoInitializer.PlivoApi is null || _plivoInitializer.PlivoSettings is null)
				return new SMSwitchResponseVerifyOTP() { Verified = false };

			try
			{
				var sessionUuid = await _plivoDbService.GetLatestSessionUUID(mobileWithCountryCode, cancellationToken);
				if (string.IsNullOrWhiteSpace(sessionUuid))
				{
					_logger.LogInformation("No Plivo session found for +{MobileNumber}, unable to verify OTP", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
					return new SMSwitchResponseVerifyOTP()
					{
						Verified = false
					};
				}
				// Deciding from the Validate response itself keeps this to one round trip.
				// Validating and then separately Get-ing the session status meant two concurrent
				// verifies of the same OTP could both observe "verified" and both succeed — the
				// same race that was fixed for the DevConsole provider.
				var response = _plivoInitializer.PlivoApi.VerifySession.Validate(session_uuid: sessionUuid, otp: OTP);
				if (response is not null && IsSuccessStatusCode(response.StatusCode))
				{
					await _plivoDbService.ClearSessionUUID(mobileWithCountryCode, cancellationToken);
					return new SMSwitchResponseVerifyOTP()
					{
						Verified = true
					};
				}
				_logger.LogInformation("Plivo rejected the OTP for +{MobileNumber}: status {StatusCode} {Message}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber, response?.StatusCode, response?.Message);
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Could not verify OTP for +{MobileNumber}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
			}
			return new SMSwitchResponseVerifyOTP()
			{
				Verified = false
			};
		}
	}
}
