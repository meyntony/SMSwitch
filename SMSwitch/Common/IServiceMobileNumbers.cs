using HumanLanguages;
using SMSwitch.Common.DTOs;

namespace SMSwitch.Common
{
	/// <summary>
	/// What a single SMS provider does. There is no resend cooldown here: deduplicating repeated
	/// sends is the switchboard's job, and a provider only needs to know how long to wait for
	/// delivery confirmation before giving up.
	/// </summary>
	public interface IServiceMobileNumbers
	{
		Task<SMSwitchResponseSendOTP> SendOTP(MobileNumber mobileWithCountryCode, HashSet<LanguageIsoCode> preferredLanguageIsoCodeList, UserAgent userAgent, byte deliveryConfirmationTimeoutInSeconds = 60, CancellationToken cancellationToken = default);
		Task<SMSwitchResponseVerifyOTP> VerifyOTP(MobileNumber mobileWithCountryCode, string OTP, CancellationToken cancellationToken = default);
		Task<bool> SendSMS(MobileNumber mobileWithCountryCode, string shortMessageServiceMessage, byte deliveryConfirmationTimeoutInSeconds = 60, CancellationToken cancellationToken = default);
	}
}
