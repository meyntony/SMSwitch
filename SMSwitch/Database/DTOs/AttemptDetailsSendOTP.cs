using SMSwitch.Common;
using SMSwitch.Common.DTOs;

namespace SMSwitch.Database.DTOs
{
	public record AttemptDetailsSendOTP(DateTimeOffset AttemptTimeInUTC, SmsProvider SmsProvider, SMSwitchResponseSendOTP Response);
}
