using EarthCountriesInfo;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SMSwitch.Common.DTOs
{
	public sealed class MobileNumber
    {
		[JsonPropertyName("countryIsoCode")] public required string CountryIsoCodeString { get; init; }
		[JsonPropertyName("countryPhoneCode")] public required string CountryPhoneCode { get; set; }
        [JsonPropertyName("phoneNumber")] public required string PhoneNumber { get; set; }

        [JsonIgnore]
        public CountryIsoCode? CountryIsoCode => Enum.TryParse(CountryIsoCodeString, ignoreCase: true, out CountryIsoCode countryIsoCode) ? countryIsoCode : null;
		private string removeNonNumericString(string input) => Regex.Replace(input, "[^0-9]", "");
		[JsonIgnore]
		public string CountryPhoneCodeAsNumericString => removeNonNumericString(CountryPhoneCode);
		[JsonIgnore] 
        public string PhoneNumberAsNumericString => removeNonNumericString(PhoneNumber);
		// Concatenating the numeric strings rather than parsing them as a long keeps this total:
		// long.Parse threw on an empty or over-long number, and it silently dropped the leading
		// trunk zero that many countries write their national mobile numbers with.
		[JsonIgnore]
        public string CountryPhoneCodeAndPhoneNumber => $"{CountryPhoneCodeAsNumericString}{PhoneNumberAsNumericString}";
        public byte PhoneNumberNumericLength() => (byte)Math.Min(PhoneNumberAsNumericString.Length, byte.MaxValue);

        public bool IsValid() => !string.IsNullOrWhiteSpace(CountryPhoneCodeAsNumericString) && !string.IsNullOrWhiteSpace(PhoneNumberAsNumericString);
    }
}
