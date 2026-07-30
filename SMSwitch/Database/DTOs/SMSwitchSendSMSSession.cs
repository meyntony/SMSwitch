using MongoDB.Bson.Serialization.Attributes;
using SMSwitch.Common;

namespace SMSwitch.Database.DTOs
{
	public sealed class SMSwitchSendSMSSession
	{
		[BsonId]
		public required string SessionId { get; init; }
		/// <summary>
		/// Hash of the recipient and the message text, used to find an in-flight session for the
		/// same message. It deliberately is not the _id: Mongo refuses to modify _id, so a session
		/// keyed by this hash could never be superseded once the first one completed.
		/// </summary>
		public required string DedupeKey { get; init; }
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
