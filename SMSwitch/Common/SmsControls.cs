namespace SMSwitch.Common
{
	public sealed class SmsControls
	{
		public byte MaximumFailedAttemptsToVerify { get; init; }
		public int SessionTimeoutInSeconds { get; init; }
		public byte MaxRoundRobinAttempts { get; set; }

		/// <summary>
		/// Days a session is kept after it expires, before a MongoDB TTL index removes it. Zero or
		/// less keeps sessions indefinitely, and removes the expiry index if one is already present.
		/// </summary>
		public int SessionRetentionDays { get; init; }

		// Lists, not sets: these are ordered priorities. HashSet has no ordering contract, and it
		// silently collapsed a deliberate repeat such as [ "Twilio", "Plivo", "Twilio" ].
		public required Dictionary<string, List<SmsProvider>> PriorityBasedOnCountryPhoneCode { get; set; }
		public required List<SmsProvider> FallBackPriority { get; set; }
	}
}
