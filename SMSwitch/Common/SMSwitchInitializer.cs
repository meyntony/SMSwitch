using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SMSwitch.Common
{
	public sealed class SMSwitchInitializer
	{
		/// <summary>
		/// Days a session is kept after expiry when nothing is configured. This was the hard-coded
		/// value before the setting existed, so leaving it unset keeps the previous behaviour rather
		/// than silently lengthening retention on an existing deployment.
		/// </summary>
		internal const int DefaultSessionRetentionDays = 30;

		public readonly SmsControls SmsControls;
		public SMSwitchInitializer(IConfiguration configuration, ILogger<SMSwitchInitializer> logger)
		{
			var smsControlsConfig = configuration.GetSection("SMSwitchSettings:Controls");
			SmsControls = new SmsControls()
			{
				MaximumFailedAttemptsToVerify = byte.TryParse(smsControlsConfig["MaximumFailedAttemptsToVerify"], out byte maximumFailedAttemptsToVerify) ? maximumFailedAttemptsToVerify : (byte)3,
				SessionTimeoutInSeconds = int.TryParse(smsControlsConfig["SessionTimeoutInSeconds"], out int sessionTimeoutInSeconds) ? sessionTimeoutInSeconds : 240,
				MaxRoundRobinAttempts = byte.TryParse(smsControlsConfig["MaxRoundRobinAttempts"], out byte maxRoundRobinAttempts) ? maxRoundRobinAttempts : (byte)1,
				SessionRetentionDays = getSessionRetentionDays(smsControlsConfig, logger),
				PriorityBasedOnCountryPhoneCode = getPriorityBasedOnCountryPhoneCode(smsControlsConfig, logger),
				FallBackPriority = getFallBackPriority(smsControlsConfig.GetRequiredSection("FallBackPriority").Get<string[]>() ?? [], logger)
			};
		}

		/// <summary>
		/// Never fails startup. Zero or less is a legitimate operator choice - keep the audit trail
		/// and prune it some other way - rather than a misconfiguration, so it is logged and honoured.
		/// </summary>
		private static int getSessionRetentionDays(IConfigurationSection smsControlsConfig, ILogger logger)
		{
			var configured = smsControlsConfig["SessionRetentionDays"];

			if (string.IsNullOrWhiteSpace(configured))
			{
				return DefaultSessionRetentionDays;
			}

			if (!int.TryParse(configured, out var sessionRetentionDays))
			{
				logger.LogWarning("SMSwitchSettings:Controls:SessionRetentionDays is not a number, falling back to {DefaultSessionRetentionDays} days.", DefaultSessionRetentionDays);
				return DefaultSessionRetentionDays;
			}

			if (sessionRetentionDays <= 0)
			{
				logger.LogWarning("SMSwitchSettings:Controls:SessionRetentionDays is {SessionRetentionDays}, so sessions are kept indefinitely and the collections will grow without bound.", sessionRetentionDays);
			}

			return sessionRetentionDays;
		}

		private static Dictionary<string, List<SmsProvider>> getPriorityBasedOnCountryPhoneCode(IConfigurationSection smsControlsConfig, ILogger logger)
		{
			var priorityBasedOnCountryPhoneCode = new Dictionary<string, List<SmsProvider>>();

			foreach (var countryCodeSection in smsControlsConfig.GetRequiredSection("PriorityBasedOnCountryPhoneCode").GetChildren())
			{
				if (string.IsNullOrEmpty(countryCodeSection.Key))
				{
					continue;
				}

				var configuredProviders = countryCodeSection.Get<string[]>() ?? [];
				var unknownProviders = configuredProviders.Where(p => !Enum.TryParse(p, out SmsProvider _)).ToArray();

				if (unknownProviders.Length > 0)
				{
					// Dropping the entry silently meant one typo made a country quietly fall back to
					// the global priority with no diagnostic anywhere.
					logger.LogWarning("Ignoring the provider priority configured for country phone code {CountryPhoneCode}: {UnknownProviders} not recognised. Known providers are {KnownProviders}. This country will use FallBackPriority instead.",
						countryCodeSection.Key,
						string.Join(", ", unknownProviders),
						string.Join(", ", Enum.GetNames<SmsProvider>()));
					continue;
				}

				priorityBasedOnCountryPhoneCode[countryCodeSection.Key] = configuredProviders.Select(p => Enum.Parse<SmsProvider>(p)).ToList();
			}

			return priorityBasedOnCountryPhoneCode;
		}

		private static List<SmsProvider> getFallBackPriority(string[] value, ILogger logger)
		{
			var unknownProviders = value.Where(p => !Enum.TryParse(p, out SmsProvider _)).ToArray();
			if (unknownProviders.Length > 0)
			{
				logger.LogWarning("Ignoring unrecognised entries in FallBackPriority: {UnknownProviders}. Known providers are {KnownProviders}.",
					string.Join(", ", unknownProviders),
					string.Join(", ", Enum.GetNames<SmsProvider>()));
			}

			var valuesFromConfig = value.Where(p => Enum.TryParse(p, out SmsProvider _)).Select(p => Enum.Parse<SmsProvider>(p)).ToList();
			if (valuesFromConfig.Count < 1)
			{
				throw new InvalidOperationException($"{ConstantStrings.SMSwitchSettingsName}:Controls:FallBackPriority must list at least one known provider ({string.Join(", ", Enum.GetNames<SmsProvider>())}).");
			}
			return valuesFromConfig;
		}
	}
}
