using MongoDB.Bson.Serialization.Attributes;
using SMSwitch.Common;

namespace SMSwitch.Database.DTOs
{
	public sealed class SMSwitchSendSMSSession
	{
		[BsonId]
		public required string SessionId { get; init; }
		public required string CountryPhoneCodeAndPhoneNumber { get; init; }
		public required string ShortMessageServiceMessage { get; init; }
		public required DateTimeOffset StartTimeUTC { get; init; }
		public required DateTimeOffset ExpiryTimeUTC { get; init; }
		public Queue<SmsProvider>? SmsProvidersQueue { get; set; }
		public List<AttemptDetailsSendSMS> SentAttempts { get; init; } = [];
		public List<DateTimeOffset> FailedAttemptsDateTimeOffset { get; set; } = [];
		public DateTimeOffset? SuccessfullySentTimestampUTC { get; set; }
		internal bool HasNotExpired(byte maximumFailedAttemptsToVerify) =>
			(SmsProvidersQueue?.Any() ?? true) && // if it has become empty from failed attempts then it has expired
			FailedAttemptsDateTimeOffset.Count() < maximumFailedAttemptsToVerify &&
			SuccessfullySentTimestampUTC == null &&
			DateTimeOffset.UtcNow < ExpiryTimeUTC;
	}
}