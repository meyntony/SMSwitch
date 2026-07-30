namespace SMSwitch.Common
{
	public sealed class SmsControls
	{
		public byte MaximumFailedAttemptsToVerify { get; init; }
		public int SessionTimeoutInSeconds { get; init; }
		public byte MaxRoundRobinAttempts { get; set; }
		// Lists, not sets: these are ordered priorities. HashSet has no ordering contract, and it
		// silently collapsed a deliberate repeat such as [ "Twilio", "Plivo", "Twilio" ].
		public required Dictionary<string, List<SmsProvider>> PriorityBasedOnCountryPhoneCode { get; set; }
		public required List<SmsProvider> FallBackPriority { get; set; }
	}
}
