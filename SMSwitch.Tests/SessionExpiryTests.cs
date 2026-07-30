using SMSwitch.Common;
using SMSwitch.Database.DTOs;

namespace SMSwitch.Tests
{
	/// <summary>
	/// HasNotExpired is what stands between a caller and unlimited OTP guesses, so each of its
	/// four conditions is pinned down separately.
	/// </summary>
	public sealed class SessionExpiryTests
	{
		private const byte MaximumFailedAttemptsToVerify = 3;

		private static SMSwitchSession CreateSession(
			Queue<SmsProvider>? smsProvidersQueue = null,
			int failedVerificationAttempts = 0,
			DateTimeOffset? successfullyVerifiedTimestampUTC = null,
			DateTimeOffset? expiryTimeUTC = null) =>
			new()
			{
				SessionId = Guid.NewGuid().ToString(),
				CountryPhoneCodeAndPhoneNumber = "4512345678",
				StartTimeUTC = DateTimeOffset.UtcNow,
				ExpiryTimeUTC = expiryTimeUTC ?? DateTimeOffset.UtcNow.AddMinutes(4),
				SmsProvidersQueue = smsProvidersQueue,
				SuccessfullyVerifiedTimestampUTC = successfullyVerifiedTimestampUTC,
				FailedVerificationAttemptsDateTimeOffset = Enumerable
					.Range(0, failedVerificationAttempts)
					.Select(_ => DateTimeOffset.UtcNow)
					.ToList()
			};

		[Fact]
		public void A_fresh_session_with_no_queue_yet_has_not_expired()
		{
			Assert.True(CreateSession().HasNotExpired(MaximumFailedAttemptsToVerify));
		}

		[Fact]
		public void A_session_with_providers_left_has_not_expired()
		{
			var session = CreateSession(new Queue<SmsProvider>([SmsProvider.Twilio, SmsProvider.Plivo]));
			Assert.True(session.HasNotExpired(MaximumFailedAttemptsToVerify));
		}

		/// <summary>
		/// An empty queue means every provider has been tried and failed.
		/// </summary>
		[Fact]
		public void A_session_whose_queue_is_exhausted_has_expired()
		{
			var session = CreateSession(new Queue<SmsProvider>());
			Assert.False(session.HasNotExpired(MaximumFailedAttemptsToVerify));
		}

		[Theory]
		[InlineData(0, true)]
		[InlineData(2, true)]
		[InlineData(3, false)]
		[InlineData(4, false)]
		public void The_failed_attempt_limit_is_enforced(int failedVerificationAttempts, bool expectedHasNotExpired)
		{
			var session = CreateSession(
				new Queue<SmsProvider>([SmsProvider.Twilio]),
				failedVerificationAttempts: failedVerificationAttempts);

			Assert.Equal(expectedHasNotExpired, session.HasNotExpired(MaximumFailedAttemptsToVerify));
		}

		[Fact]
		public void An_already_verified_session_has_expired()
		{
			var session = CreateSession(
				new Queue<SmsProvider>([SmsProvider.Twilio]),
				successfullyVerifiedTimestampUTC: DateTimeOffset.UtcNow);

			Assert.False(session.HasNotExpired(MaximumFailedAttemptsToVerify));
		}

		[Fact]
		public void A_session_past_its_expiry_time_has_expired()
		{
			var session = CreateSession(
				new Queue<SmsProvider>([SmsProvider.Twilio]),
				expiryTimeUTC: DateTimeOffset.UtcNow.AddSeconds(-1));

			Assert.False(session.HasNotExpired(MaximumFailedAttemptsToVerify));
		}

		[Fact]
		public void SendSMS_sessions_expire_on_the_same_four_conditions()
		{
			var live = new SMSwitchSendSMSSession
			{
				SessionId = Guid.NewGuid().ToString(),
				DedupeKey = "dedupe",
				CountryPhoneCodeAndPhoneNumber = "4512345678",
				ShortMessageServiceMessage = "Hello",
				StartTimeUTC = DateTimeOffset.UtcNow,
				ExpiryTimeUTC = DateTimeOffset.UtcNow.AddMinutes(4)
			};
			Assert.True(live.HasNotExpired(MaximumFailedAttemptsToVerify));

			live.SuccessfullySentTimestampUTC = DateTimeOffset.UtcNow;
			Assert.False(live.HasNotExpired(MaximumFailedAttemptsToVerify));
		}

		/// <summary>
		/// SmsProvider values are persisted by number inside SmsProvidersQueue, so renumbering them
		/// would silently reinterpret every session already in the database.
		/// </summary>
		[Fact]
		public void SmsProvider_values_are_pinned()
		{
			Assert.Equal(0, (int)SmsProvider.Twilio);
			Assert.Equal(1, (int)SmsProvider.Plivo);
			Assert.Equal(2, (int)SmsProvider.DevConsole);
		}
	}
}
