using HumanLanguages;
using SMSwitch.Common.DTOs;

namespace SMSwitch.Common
{
	public interface IServiceMobileNumbers
	{
		Task<SMSwitchResponseSendOTP> SendOTP(MobileNumber mobileWithCountryCode, HashSet<LanguageIsoCode> preferredLanguageIsoCodeList, UserAgent userAgent, byte resendCooldownPeriodInSeconds = 60, CancellationToken cancellationToken = default);
		Task<SMSwitchResponseVerifyOTP> VerifyOTP(MobileNumber mobileWithCountryCode, string OTP, CancellationToken cancellationToken = default);
		Task<bool> SendSMS(MobileNumber mobileWithCountryCode, string shortMessageServiceMessage, byte resendCooldownPeriodInSeconds = 60, CancellationToken cancellationToken = default);
	}
}
