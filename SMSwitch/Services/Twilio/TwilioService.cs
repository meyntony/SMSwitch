using HumanLanguages;
using Microsoft.Extensions.Logging;
using SMSwitch.Common;
using SMSwitch.Common.DTOs;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Rest.Verify.V2.Service;
using Twilio.Types;

namespace SMSwitch.Services.Twilio
{
	public sealed class TwilioService : IServiceMobileNumbers
	{
		private readonly TwilioInitializer _twilioInitializer;
		private readonly ILogger<TwilioService> _logger;



		public TwilioService(TwilioInitializer twilioInitializer, ILogger<TwilioService> logger)
		{
			_logger = logger;
			_twilioInitializer = twilioInitializer;

		}
		/// <summary>
		/// //https://www.twilio.com/docs/verify/supported-languages#verify-default-template
		/// These are the supported language ISO codes as of 13-July-2024
		/// </summary>
		private static readonly SupportedLocales _supportedLocalesForVerifyDefaultTemplate = new(
			"af",
			"ar",
			"ca",
			"zh",
			"hr",
			"cs",
			"da",
			"nl",
			"en",
			"et",
			"fi",
			"fr",
			"de",
			"el",
			"he",
			"hi",
			"hu",
			"id",
			"it",
			"ja",
			"kn",
			"ko",
			"lt",
			"ms",
			"mr",
			"nb",
			"pl",
			"pt",
			"ro",
			"ru",
			"sk",
			"es",
			"sv",
			"tl",
			"te",
			"th",
			"tr",
			"uk",
			"vi",
			"pt-BR",
			"zh-CN",
			"zh-HK");

		public async Task<SMSwitchResponseSendOTP> SendOTP(MobileNumber mobileWithCountryCode, HashSet<LanguageIsoCode> preferredLanguageIsoCodeList, UserAgent userAgent, byte deliveryConfirmationTimeoutInSeconds = 60, CancellationToken cancellationToken = default)
		{
			if (_twilioInitializer.TwilioSettings is null)
				return new SMSwitchResponseSendOTP() { IsSent = false };

			var locale = _supportedLocalesForVerifyDefaultTemplate.Resolve(preferredLanguageIsoCodeList);

			try
			{
				var verificationMessage = await VerificationResource.CreateAsync(
					to: $"+{mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber}",
					channel: "sms",
					locale: locale,
					pathServiceSid: _twilioInitializer.TwilioSettings.TwilioPrivateSettings.ServiceSid,
					appHash: userAgent == UserAgent.Android ? _twilioInitializer.TwilioSettings.AndroidAppHash : null
				);


				var isSent = !string.IsNullOrWhiteSpace(verificationMessage?.Sid);

				return new SMSwitchResponseSendOTP()
				{
					IsSent = isSent,
					OtpLength = isSent ? _twilioInitializer.TwilioSettings.OtpLength : (byte)0
				};
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Could not send OTP to +{MobileNumber} in {locale}", mobileWithCountryCode?.CountryPhoneCodeAndPhoneNumber, locale);
				return new SMSwitchResponseSendOTP()
				{
					IsSent = false
				};
			}
		}



		public async Task<bool> SendSMS(MobileNumber mobileWithCountryCode, string shortMessageServiceMessage, byte deliveryConfirmationTimeoutInSeconds = 60, CancellationToken cancellationToken = default)
		{
			if (_twilioInitializer.TwilioSettings is null)
				return false;

			if (string.IsNullOrWhiteSpace(_twilioInitializer.TwilioSettings.TwilioPrivateSettings.RegisteredSenderPhoneNumber))
			{
				_logger.LogCritical("RegisteredSenderPhoneNumber missing!!");
				return false;
			}

			try
			{
				// Send the SMS
				var message = await MessageResource.CreateAsync(
					to: new PhoneNumber($"+{mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber}"),
					from: _twilioInitializer.TwilioSettings.TwilioPrivateSettings.RegisteredSenderPhoneNumber,
					body: shortMessageServiceMessage
				);

				if (!string.IsNullOrEmpty(message?.Sid))
				{
					return await KeepCheckingIfSentEvery2seconds(message.Sid, expiry: DateTimeOffset.UtcNow.AddSeconds(deliveryConfirmationTimeoutInSeconds), cancellationToken);
				}
				else
				{
					_logger.LogWarning("Failed to send SMS to +{MobileNumber}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
					return false;
				}
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Error occurred while sending SMS to +{MobileNumber}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
				return false;
			}
		}

		private async Task<bool> KeepCheckingIfSentEvery2seconds(string messageSid, DateTimeOffset expiry, CancellationToken cancellationToken)
		{
			// Fetch the message status
			var fetchedMessage = await MessageResource.FetchAsync(messageSid);

			// Check if the message was delivered
			if (fetchedMessage.Status == MessageResource.StatusEnum.Delivered)
			{
				return true;
			}
			else if (DateTimeOffset.UtcNow > expiry || cancellationToken.IsCancellationRequested)
			{
				return false;
			}
			else
			{
				// The Twilio SDK takes no CancellationToken, so the token cannot reach the fetch
				// itself. Honouring it around the wait still stops this loop from running on for
				// the rest of the window after the caller has gone away.
				await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
				return await KeepCheckingIfSentEvery2seconds(messageSid, expiry, cancellationToken);
			}
		}

		public async Task<SMSwitchResponseVerifyOTP> VerifyOTP(MobileNumber mobileWithCountryCode, string OTP, CancellationToken cancellationToken = default)
		{
			if (_twilioInitializer.TwilioSettings is null)
				return new SMSwitchResponseVerifyOTP() { Verified = false };

			bool verified = false;
			try
			{
				var verification = await VerificationCheckResource.CreateAsync(
					to: $"+{mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber}",
					code: OTP,
					pathServiceSid: _twilioInitializer.TwilioSettings.TwilioPrivateSettings.ServiceSid
				);
				verified = string.Equals(verification?.Status, "approved", StringComparison.OrdinalIgnoreCase);

				if (!verified)
				{
					_logger.LogInformation("Verification Status: {Status} for +{MobileNumber}", verification?.Status, mobileWithCountryCode?.CountryPhoneCodeAndPhoneNumber);
				}
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Could not verify OTP for +{MobileNumber}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
				return new SMSwitchResponseVerifyOTP()
				{
					Verified = verified,
					Expired = true
				};
			}
			return new SMSwitchResponseVerifyOTP()
			{
				Verified = verified
			};
		}
	}
}
