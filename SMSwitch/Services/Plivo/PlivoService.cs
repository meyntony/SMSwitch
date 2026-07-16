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
		private static HashSet<string> _supportedLanguageIsoCodeStringsForVerifyDefaultTemplate =>
			["en",
			"da"];
		private static HashSet<LanguageIsoCode> _supportedLanguageIsoCodesForVerifyDefaultTemplate => _supportedLanguageIsoCodeStringsForVerifyDefaultTemplate.Select(isoCodeString => HumanHelper.CreateLanguageIsoCode(isoCodeString)).ToHashSet();


		public async Task<SMSwitchResponseSendOTP> SendOTP(MobileNumber mobileWithCountryCode, HashSet<LanguageIsoCode> preferredLanguageIsoCodeList, UserAgent userAgent, byte resendCooldownPeriodInSeconds = 60)
		{
			if (_plivoInitializer.PlivoApi is null || _plivoInitializer.PlivoSettings is null)
				return new SMSwitchResponseSendOTP() { IsSent = false };

			string preferredLocale = "en";
			try
			{
				preferredLocale = preferredLanguageIsoCodeList.FirstOrDefault(l => _supportedLanguageIsoCodesForVerifyDefaultTemplate.Contains(l))?.ToIsoCodeString()
				??
				preferredLanguageIsoCodeList.FirstOrDefault(l => _supportedLanguageIsoCodesForVerifyDefaultTemplate.Select(isoCode => isoCode.LanguageId).Contains(l.LanguageId))?.ToIsoCodeString()
				??
				"en";

				if (!_supportedLanguageIsoCodeStringsForVerifyDefaultTemplate.Contains(preferredLocale))
				{
					var localeAsLanguageIsoCode = HumanHelper.CreateLanguageIsoCode(preferredLocale);
					preferredLocale = _supportedLanguageIsoCodeStringsForVerifyDefaultTemplate.FirstOrDefault(isoCode => isoCode == localeAsLanguageIsoCode.ToIsoCodeString('-')
					|| isoCode == localeAsLanguageIsoCode.LanguageId.ToString()) ?? "en";
				}

				var verifySessionResponse = _plivoInitializer.PlivoApi.VerifySession.Create(
					recipient: mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber,
					app_uuid: _plivoInitializer.PlivoSettings.PlivoPrivateSettings.AppUuid,
					url: _plivoInitializer.NotificationUrl ,
					method: "GET",
					channel: "sms",
					locale: preferredLocale);

				await _plivoDbService.SetLatestSessionUUID(mobileWithCountryCode, verifySessionResponse.SessionUUID);

				bool isSent = false;
				if (verifySessionResponse.StatusCode.ToString().StartsWith("2"))
				{
					isSent = await _plivoDbService.KeepCheckingTheDatabaseIfSentEvery2seconds(verifySessionResponse.SessionUUID, expiry: DateTimeOffset.UtcNow.AddSeconds(resendCooldownPeriodInSeconds));
				}
				return new SMSwitchResponseSendOTP()
				{
					IsSent = isSent,
					OtpLength = _plivoInitializer.PlivoSettings.OtpLength
				};
			}
			catch(Exception exception)
			{
				_logger.LogError(exception, "Could not send OTP to +{MobileNumber} in {preferredLocale}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber, preferredLocale);
				return new SMSwitchResponseSendOTP()
				{
					IsSent = false
				};
			}
		}

		public async Task<bool> SendSMS(MobileNumber mobileWithCountryCode, string shortMessageServiceMessage, byte resendCooldownPeriodInSeconds = 60)
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
					return await KeepCheckingIfSentEvery2seconds(messageUuid, expiry: DateTimeOffset.UtcNow.AddSeconds(resendCooldownPeriodInSeconds));
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

		private async Task<bool> KeepCheckingIfSentEvery2seconds(string messageUuid, DateTimeOffset expiry)
		{
			// Fetch the message status
			var messageDetails = await _plivoInitializer.PlivoApi!.Message.GetAsync(messageUuid);

			// Check if the message was delivered
			if (messageDetails.MessageState == "delivered")
			{
				return true;
			}
			else if (DateTimeOffset.UtcNow > expiry)
			{
				return false;
			}
			else 
			{
				await Task.Delay(TimeSpan.FromSeconds(2));
				return await KeepCheckingIfSentEvery2seconds(messageUuid, expiry);
			}
		}

		public async Task<SMSwitchResponseVerifyOTP> VerifyOTP(MobileNumber mobileWithCountryCode, string OTP)
		{
			if (_plivoInitializer.PlivoApi is null || _plivoInitializer.PlivoSettings is null)
				return new SMSwitchResponseVerifyOTP() { Verified = false };

			try
			{
				var sessionUuid = await _plivoDbService.GetLatestSessionUUID(mobileWithCountryCode);
				if (string.IsNullOrWhiteSpace(sessionUuid))
				{
					_logger.LogInformation("No Plivo session found for +{MobileNumber}, unable to verify OTP", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
					return new SMSwitchResponseVerifyOTP()
					{
						Verified = false
					};
				}
				var response = _plivoInitializer.PlivoApi.VerifySession.Validate(session_uuid: sessionUuid, otp: OTP);
				if (_plivoInitializer.PlivoApi.VerifySession.Get(sessionUuid).Status.ToLower() == "verified")
				{
					await _plivoDbService.ClearSessionUUID(mobileWithCountryCode);
					return new SMSwitchResponseVerifyOTP()
					{
						Verified = true
					};
				}
			}
			catch(Exception exception)
			{
				_logger.LogError(exception, "Could not verify OTP for +{MobileNumber}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
			}
			return new SMSwitchResponseVerifyOTP(){
				Verified = false
			};
		}
	}
}
