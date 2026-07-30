using SMSwitch.Common.DTOs;

namespace SMSwitch.Tests
{
	public sealed class MobileNumberTests
	{
		private static MobileNumber Create(string countryPhoneCode, string phoneNumber, string countryIsoCode = "DK") =>
			new()
			{
				CountryIsoCodeString = countryIsoCode,
				CountryPhoneCode = countryPhoneCode,
				PhoneNumber = phoneNumber
			};

		[Theory]
		[InlineData("45", "12345678", "4512345678")]
		[InlineData("+45", "12 34 56 78", "4512345678")]
		[InlineData("+45", "(12) 34-56-78", "4512345678")]
		[InlineData("91", "9876543210", "919876543210")]
		public void CountryPhoneCodeAndPhoneNumber_strips_formatting(string countryPhoneCode, string phoneNumber, string expected)
		{
			Assert.Equal(expected, Create(countryPhoneCode, phoneNumber).CountryPhoneCodeAndPhoneNumber);
		}

		/// <summary>
		/// The number used to be round-tripped through long.Parse, which dropped the leading trunk
		/// zero that many countries write national mobile numbers with, so the SMS went to a
		/// different number than the caller asked for.
		/// </summary>
		[Theory]
		[InlineData("31", "0612345678", "310612345678")]
		[InlineData("45", "00012345678", "4500012345678")]
		public void CountryPhoneCodeAndPhoneNumber_keeps_leading_zeros(string countryPhoneCode, string phoneNumber, string expected)
		{
			Assert.Equal(expected, Create(countryPhoneCode, phoneNumber).CountryPhoneCodeAndPhoneNumber);
		}

		/// <summary>
		/// long.Parse("") threw FormatException. Because SMSwitchService evaluates this property
		/// inside its catch blocks, that exception escaped the very handler meant to contain it.
		/// </summary>
		[Theory]
		[InlineData("", "")]
		[InlineData("45", "")]
		[InlineData("", "12345678")]
		[InlineData("abc", "not-a-number")]
		public void CountryPhoneCodeAndPhoneNumber_does_not_throw_on_junk(string countryPhoneCode, string phoneNumber)
		{
			var exception = Record.Exception(() => Create(countryPhoneCode, phoneNumber).CountryPhoneCodeAndPhoneNumber);
			Assert.Null(exception);
		}

		/// <summary>
		/// A number longer than long.MaxValue used to throw OverflowException.
		/// </summary>
		[Fact]
		public void CountryPhoneCodeAndPhoneNumber_does_not_throw_on_an_over_long_number()
		{
			var thirtyDigits = new string('9', 30);
			var exception = Record.Exception(() => Create("45", thirtyDigits).CountryPhoneCodeAndPhoneNumber);
			Assert.Null(exception);
		}

		[Theory]
		[InlineData("45", "12345678", true)]
		[InlineData("+45", "12 34 56 78", true)]
		[InlineData("45", "", false)]
		[InlineData("", "12345678", false)]
		[InlineData("", "", false)]
		[InlineData("abc", "def", false)]
		public void IsValid_requires_digits_in_both_parts(string countryPhoneCode, string phoneNumber, bool expected)
		{
			Assert.Equal(expected, Create(countryPhoneCode, phoneNumber).IsValid());
		}

		/// <summary>
		/// The recorded length feeds CountryDbService.FeedbackAsync, so it has to agree with the
		/// digits actually sent rather than with a zero-stripped parse of them.
		/// </summary>
		[Theory]
		[InlineData("12345678", 8)]
		[InlineData("0612345678", 10)]
		[InlineData("12 34 56 78", 8)]
		public void PhoneNumberNumericLength_counts_every_digit(string phoneNumber, byte expected)
		{
			var mobileNumber = Create("45", phoneNumber);
			Assert.Equal(expected, mobileNumber.PhoneNumberNumericLength());
			Assert.Equal(expected, mobileNumber.PhoneNumberAsNumericString.Length);
		}

		[Fact]
		public void PhoneNumberNumericLength_saturates_rather_than_overflowing()
		{
			Assert.Equal(byte.MaxValue, Create("45", new string('9', 300)).PhoneNumberNumericLength());
		}

		[Theory]
		[InlineData("DK")]
		[InlineData("dk")]
		public void CountryIsoCode_parses_case_insensitively(string countryIsoCode)
		{
			Assert.NotNull(Create("45", "12345678", countryIsoCode).CountryIsoCode);
		}

		[Fact]
		public void CountryIsoCode_is_null_when_unparseable()
		{
			Assert.Null(Create("45", "12345678", "not-a-country").CountryIsoCode);
		}
	}
}
