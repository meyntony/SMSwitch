using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SMSwitch.Common;

namespace SMSwitch.Tests
{
	public sealed class SMSwitchInitializerTests
	{
		private static SMSwitchInitializer Create(Dictionary<string, string?> settings) =>
			new(
				new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
				NullLogger<SMSwitchInitializer>.Instance);

		private static Dictionary<string, string?> MinimumSettings() => new()
		{
			["SMSwitchSettings:Controls:FallBackPriority:0"] = "Twilio",
			["SMSwitchSettings:Controls:PriorityBasedOnCountryPhoneCode:45:0"] = "Plivo",
			["SMSwitchSettings:Controls:PriorityBasedOnCountryPhoneCode:45:1"] = "Twilio"
		};

		[Fact]
		public void Controls_fall_back_to_documented_defaults()
		{
			var controls = Create(MinimumSettings()).SmsControls;

			// These defaults are what the README's settings table promises.
			Assert.Equal(3, controls.MaximumFailedAttemptsToVerify);
			Assert.Equal(240, controls.SessionTimeoutInSeconds);
			Assert.Equal(1, controls.MaxRoundRobinAttempts);
		}

		[Fact]
		public void Controls_are_read_from_configuration()
		{
			var settings = MinimumSettings();
			settings["SMSwitchSettings:Controls:MaximumFailedAttemptsToVerify"] = "5";
			settings["SMSwitchSettings:Controls:SessionTimeoutInSeconds"] = "600";
			settings["SMSwitchSettings:Controls:MaxRoundRobinAttempts"] = "2";

			var controls = Create(settings).SmsControls;

			Assert.Equal(5, controls.MaximumFailedAttemptsToVerify);
			Assert.Equal(600, controls.SessionTimeoutInSeconds);
			Assert.Equal(2, controls.MaxRoundRobinAttempts);
		}

		[Fact]
		public void Per_country_priorities_are_bound()
		{
			var controls = Create(MinimumSettings()).SmsControls;

			Assert.True(controls.PriorityBasedOnCountryPhoneCode.TryGetValue("45", out var providers));
			Assert.Equal([SmsProvider.Plivo, SmsProvider.Twilio], providers!);
		}

		/// <summary>
		/// These are ordered priorities. They were HashSets, which has no ordering contract and
		/// silently collapsed a deliberate repeat, so a round-robin like Twilio, Plivo, Twilio
		/// could not be expressed at all.
		/// </summary>
		[Fact]
		public void Priority_order_and_repeats_are_preserved()
		{
			var settings = MinimumSettings();
			settings["SMSwitchSettings:Controls:PriorityBasedOnCountryPhoneCode:91:0"] = "Twilio";
			settings["SMSwitchSettings:Controls:PriorityBasedOnCountryPhoneCode:91:1"] = "Plivo";
			settings["SMSwitchSettings:Controls:PriorityBasedOnCountryPhoneCode:91:2"] = "Twilio";

			var providers = Create(settings).SmsControls.PriorityBasedOnCountryPhoneCode["91"];

			Assert.Equal([SmsProvider.Twilio, SmsProvider.Plivo, SmsProvider.Twilio], providers);
		}

		/// <summary>
		/// A country entry containing an unknown provider is dropped so the country falls back to
		/// the global priority. It must not take the known providers down with it, and it must not
		/// throw.
		/// </summary>
		[Fact]
		public void A_country_entry_with_an_unknown_provider_is_dropped_not_fatal()
		{
			var settings = MinimumSettings();
			settings["SMSwitchSettings:Controls:PriorityBasedOnCountryPhoneCode:91:0"] = "Twilio";
			settings["SMSwitchSettings:Controls:PriorityBasedOnCountryPhoneCode:91:1"] = "NotAProvider";

			var controls = Create(settings).SmsControls;

			Assert.False(controls.PriorityBasedOnCountryPhoneCode.ContainsKey("91"));
			Assert.True(controls.PriorityBasedOnCountryPhoneCode.ContainsKey("45"));
		}

		[Fact]
		public void Unknown_entries_in_the_fallback_are_ignored_when_a_known_one_remains()
		{
			var settings = MinimumSettings();
			settings["SMSwitchSettings:Controls:FallBackPriority:1"] = "NotAProvider";

			var controls = Create(settings).SmsControls;

			Assert.Equal([SmsProvider.Twilio], controls.FallBackPriority);
		}

		[Fact]
		public void A_fallback_priority_with_no_known_provider_is_rejected()
		{
			var settings = MinimumSettings();
			settings["SMSwitchSettings:Controls:FallBackPriority:0"] = "NotAProvider";

			var exception = Assert.Throws<InvalidOperationException>(() => Create(settings));
			Assert.Contains("FallBackPriority", exception.Message);
		}

		[Fact]
		public void A_missing_fallback_priority_section_is_rejected()
		{
			var settings = new Dictionary<string, string?>
			{
				["SMSwitchSettings:Controls:PriorityBasedOnCountryPhoneCode:45:0"] = "Twilio"
			};

			Assert.ThrowsAny<InvalidOperationException>(() => Create(settings));
		}
	}
}
