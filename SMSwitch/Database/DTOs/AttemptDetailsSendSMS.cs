using SMSwitch.Common;

namespace SMSwitch.Database.DTOs
{
	public record AttemptDetailsSendSMS(DateTimeOffset AttemptTimeInUTC, SmsProvider SmsProvider, bool IsSent);
}
