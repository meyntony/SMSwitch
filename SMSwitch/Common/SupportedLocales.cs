using HumanLanguages;

namespace SMSwitch.Common
{
	/// <summary>
	/// Picks the locale for a provider's hosted OTP template from the caller's preferred languages.
	/// Twilio and Plivo support very different sets of locales, but they choose from them
	/// identically, and both previously carried their own copy of this logic.
	/// </summary>
	internal sealed class SupportedLocales
	{
		private const string DefaultLocale = "en";

		private readonly HashSet<string> _localeStrings;
		private readonly HashSet<LanguageIsoCode> _isoCodes;
		private readonly HashSet<LanguageId> _languageIds;

		/// <remarks>
		/// Everything is built once, up front. These were expression-bodied properties, so the sets
		/// were reallocated and every ISO code re-parsed on each access, several times per send.
		/// </remarks>
		internal SupportedLocales(params string[] localeStrings)
		{
			_localeStrings = [.. localeStrings];
			_isoCodes = _localeStrings.Select(localeString => HumanHelper.CreateLanguageIsoCode(localeString)).ToHashSet();
			_languageIds = _isoCodes.Select(isoCode => isoCode.LanguageId).ToHashSet();
		}

		/// <summary>
		/// Prefers an exact match, then any preferred language whose base language is supported
		/// (so "de-AT" can still reach "de"), then English.
		/// </summary>
		internal string Resolve(HashSet<LanguageIsoCode> preferredLanguageIsoCodeList)
		{
			var locale = preferredLanguageIsoCodeList.FirstOrDefault(preferred => _isoCodes.Contains(preferred))?.ToIsoCodeString()
				?? preferredLanguageIsoCodeList.FirstOrDefault(preferred => _languageIds.Contains(preferred.LanguageId))?.ToIsoCodeString()
				?? DefaultLocale;

			if (_localeStrings.Contains(locale))
			{
				return locale;
			}

			// The ISO code the language library produces is not always spelled the way the provider
			// spells it, so try the hyphenated form and the bare language before giving up.
			var localeAsLanguageIsoCode = HumanHelper.CreateLanguageIsoCode(locale);
			return _localeStrings.FirstOrDefault(supported =>
				supported == localeAsLanguageIsoCode.ToIsoCodeString('-')
				|| supported == localeAsLanguageIsoCode.LanguageId.ToString()) ?? DefaultLocale;
		}
	}
}
