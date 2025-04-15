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
		private static HashSet<string> _supportedLanguageIsoCodeStringsForVerifyDefaultTemplate => 
            ["af",
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
			"zh-HK"];

        private static HashSet<LanguageIsoCode> _supportedLanguageIsoCodesForVerifyDefaultTemplate => _supportedLanguageIsoCodeStringsForVerifyDefaultTemplate.Select(isoCodeString => HumanHelper.CreateLanguageIsoCode(isoCodeString)).ToHashSet();

		public async Task<SMSwitchResponseSendOTP> SendOTP(MobileNumber mobileWithCountryCode, HashSet<LanguageIsoCode> preferredLanguageIsoCodeList, UserAgent userAgent, byte resendCooldownPeriodInSeconds = 60)
        {
            var locale = preferredLanguageIsoCodeList.FirstOrDefault(l => _supportedLanguageIsoCodesForVerifyDefaultTemplate.Contains(l))?.ToIsoCodeString()
				??
				preferredLanguageIsoCodeList.FirstOrDefault(l => _supportedLanguageIsoCodesForVerifyDefaultTemplate.Select(isoCode => isoCode.LanguageId).Contains(l.LanguageId))?.ToIsoCodeString()
				??
				"en";

			if (!_supportedLanguageIsoCodeStringsForVerifyDefaultTemplate.Contains(locale))
			{
				var localeAsLanguageIsoCode = HumanHelper.CreateLanguageIsoCode(locale);
				locale = _supportedLanguageIsoCodeStringsForVerifyDefaultTemplate.FirstOrDefault(isoCode => isoCode == localeAsLanguageIsoCode.ToIsoCodeString('-') 
				|| isoCode == localeAsLanguageIsoCode.LanguageId.ToString()) ?? "en";
			}

            try
            {
                var verification = await VerificationResource.CreateAsync(
                    to: $"+{mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber}",
                    channel: "sms",
                    locale: locale,
                    pathServiceSid: _twilioInitializer.TwilioSettings.TwilioPrivateSettings.ServiceSid,
                    appHash: userAgent == UserAgent.Android ? _twilioInitializer.TwilioSettings.AndroidAppHash : null
				);

                return new SMSwitchResponseSendOTP() { 
                    IsSent = !string.IsNullOrEmpty(verification?.Sid),
                    OtpLength = _twilioInitializer.TwilioSettings.OtpLength
				};
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not send OTP to +{MobileNumber} in {locale}", mobileWithCountryCode?.CountryPhoneCodeAndPhoneNumber, locale);
                return new SMSwitchResponseSendOTP() {
                    IsSent = false
                };
            }
        }

        public async Task<bool> SendSMS(MobileNumber mobileWithCountryCode, string shortMessageServiceMessage)
        {
			try
			{
				var message = await MessageResource.CreateAsync(
					to: new PhoneNumber($"+{mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber}"),
					from: _twilioInitializer.TwilioSettings.TwilioPrivateSettings.RegisteredSenderPhoneNumber,
					body: shortMessageServiceMessage
				);

				if (!string.IsNullOrEmpty(message?.Sid))
				{
					_logger.LogInformation("SMS sent successfully to +{MobileNumber}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
					return true;
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

        public async Task<SMSwitchResponseVerifyOTP> VerifyOTP(MobileNumber mobileWithCountryCode, string OTP)
        {
            bool verified = false;
            try
            {
                var verification = await VerificationCheckResource.CreateAsync(
                    to: $"+{mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber}",
                    code: OTP,
                    pathServiceSid: _twilioInitializer.TwilioSettings.TwilioPrivateSettings.ServiceSid
                );
                verified = verification?.Status?.ToLower()?.Equals("approved") ?? false;

                if (!verified)
                {
					_logger.LogInformation("Verification Status: {Status} for +{MobileNumber}", verification?.Status, mobileWithCountryCode?.CountryPhoneCodeAndPhoneNumber);
				}
            }
            catch (Exception exception)
            {
				_logger.LogCritical(exception, "Could not verify OTP for +{MobileNumber}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
				return new SMSwitchResponseVerifyOTP()
				{
					Verified = verified,
                    Expired = true
				};
			}
            return new SMSwitchResponseVerifyOTP() {
                Verified = verified
            };
        }
    }
}
