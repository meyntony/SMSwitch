using EarthCountriesInfo;
using HumanLanguages;
using SMSwitch.Common.DTOs;

namespace SMSwitch.Tests
{
	/// <summary>
	/// SMSwitch leans on two behaviours of its dependencies that neither the compiler nor a
	/// reviewer can confirm from this repository alone. Both hold today. Both would fail silently
	/// rather than loudly if a package upgrade changed them, so they are pinned here.
	/// </summary>
	public sealed class ExternalPackageAssumptionsTests
	{
		/// <summary>
		/// TwilioService and PlivoService pick a locale with
		/// `_supportedLanguageIsoCodes.Contains(l)`. LanguageIsoCode is a reference type, so if it
		/// ever stopped overriding Equals and GetHashCode that branch would never match and locale
		/// selection would quietly fall through to the LanguageId comparison, and then to "en" —
		/// every OTP in English with nothing logged.
		/// </summary>
		[Fact]
		public void LanguageIsoCode_has_value_equality()
		{
			var danish = HumanHelper.CreateLanguageIsoCode("da");
			var alsoDanish = HumanHelper.CreateLanguageIsoCode("da");

			Assert.False(ReferenceEquals(danish, alsoDanish));
			Assert.Equal(danish, alsoDanish);
			Assert.Equal(danish.GetHashCode(), alsoDanish.GetHashCode());
			Assert.Contains(alsoDanish, new HashSet<LanguageIsoCode> { danish });
		}

		/// <summary>
		/// CountryDbService.FeedbackAsync only records a length when the stored CountryPhoneCode
		/// equals MobileNumber.CountryPhoneCodeAsNumericString, which is digits only. If
		/// EarthCountriesInfo ever formatted these as "+45" the comparison would never match and
		/// feedback would silently stop recording anything.
		/// </summary>
		[Theory]
		[InlineData(CountryIsoCode.DK, "45")]
		[InlineData(CountryIsoCode.IN, "91")]
		[InlineData(CountryIsoCode.US, "1")]
		public void CountryPhoneCode_is_digits_only(CountryIsoCode countryIsoCode, string expected)
		{
			var countryPhoneCode = EarthCountriesInfo.Countries.CountryPropertiesDictionary[countryIsoCode].CountryPhoneCode;

			Assert.Equal(expected, countryPhoneCode);
			Assert.All(countryPhoneCode, character => Assert.True(char.IsAsciiDigit(character)));
		}

		/// <summary>
		/// EarthCountriesInfo documents ValidLengthsAndFormat as null, not empty, when a country's
		/// lengths are unknown. That null is what FeedbackAsync has to write into, and a plain $set
		/// on a dotted path fails against BSON null, so this asserts the case is real rather than
		/// theoretical. If it ever stops being real the pipeline update in FeedbackAsync could be
		/// simplified back to a $set.
		/// </summary>
		[Fact]
		public void Some_countries_have_no_known_lengths_at_all()
		{
			var countriesWithoutLengths = EarthCountriesInfo.Countries.CountryPropertiesDictionary
				.Where(country => country.Value.ValidLengthsAndFormat is null)
				.ToList();

			Assert.NotEmpty(countriesWithoutLengths);
		}

		/// <summary>
		/// The value is a display mask whose '#' count equals its length key. FeedbackAsync writes
		/// `new string('#', length)`, so it has to satisfy the same invariant as the curated data.
		/// </summary>
		[Fact]
		public void Every_curated_length_mask_has_one_hash_per_digit()
		{
			var masks = EarthCountriesInfo.Countries.CountryPropertiesDictionary
				.Where(country => country.Value.ValidLengthsAndFormat is not null)
				.SelectMany(country => country.Value.ValidLengthsAndFormat!)
				.ToList();

			Assert.NotEmpty(masks);
			Assert.All(masks, entry => Assert.Equal(entry.Key, entry.Value.Count(character => character == '#')));
		}

		/// <summary>
		/// The same equality, end to end: what MobileNumber produces has to match what the country
		/// database stores, or the feedback filter silently matches nothing.
		/// </summary>
		[Fact]
		public void MobileNumber_country_phone_code_matches_the_country_database()
		{
			var mobileNumber = new MobileNumber
			{
				CountryIsoCodeString = "DK",
				CountryPhoneCode = "+45",
				PhoneNumber = "12 34 56 78"
			};

			Assert.Equal(
				EarthCountriesInfo.Countries.CountryPropertiesDictionary[CountryIsoCode.DK].CountryPhoneCode,
				mobileNumber.CountryPhoneCodeAsNumericString);
		}
	}
}
