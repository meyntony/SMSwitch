namespace SMSwitch.Common
{
	/// <summary>
	/// These values are persisted by number inside <c>SMSwitchSession.SmsProvidersQueue</c>, so
	/// they are part of the stored data. Append new providers at the end; renumbering or inserting
	/// a member silently reinterprets every session already in the database.
	/// </summary>
	public enum SmsProvider
	{
		Twilio = 0,
		Plivo = 1,
		DevConsole = 2
	}

}
