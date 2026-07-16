using HumanLanguages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDbTokenManager;
using MongoDbTokenManager.Database;
using SMSwitch.Common;
using SMSwitch.Common.DTOs;

namespace SMSwitch.Services.DevConsole
{
	/// <summary>
	/// A provider for local testing that never sends a real SMS: the OTP (or message text)
	/// is printed to the console via the logger instead. OTPs are generated and verified
	/// through MongoDbTokenManager, so the full SendOTP/VerifyOTP flow can be exercised
	/// without Twilio or Plivo credentials. Refuses to operate in the Production environment.
	/// </summary>
	public sealed class DevConsoleService : IServiceMobileNumbers
	{
		private readonly DevConsoleInitializer _devConsoleInitializer;
		private readonly SMSwitchInitializer _smSwitchInitializer;
		private readonly MongoDbTokenService _mongoDbTokenService;
		private readonly IHostEnvironment _hostEnvironment;
		private readonly ILogger<DevConsoleService> _logger;

		public DevConsoleService(
			DevConsoleInitializer devConsoleInitializer,
			SMSwitchInitializer smSwitchInitializer,
			MongoDbTokenService mongoDbTokenService,
			IHostEnvironment hostEnvironment,
			ILogger<DevConsoleService> logger)
		{
			_devConsoleInitializer = devConsoleInitializer;
			_smSwitchInitializer = smSwitchInitializer;
			_mongoDbTokenService = mongoDbTokenService;
			_hostEnvironment = hostEnvironment;
			_logger = logger;
		}

		public async Task<SMSwitchResponseSendOTP> SendOTP(MobileNumber mobileWithCountryCode, HashSet<LanguageIsoCode> preferredLanguageIsoCodeList, UserAgent userAgent, byte resendCooldownPeriodInSeconds = 60)
		{
			if (RefusedInProduction("send OTP", mobileWithCountryCode))
			{
				return new SMSwitchResponseSendOTP() { IsSent = false };
			}
			try
			{
				TokenIdentifier tokenIdentifier = mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber;
				var otp = await _mongoDbTokenService.Generate(
					logId: Guid.NewGuid().ToString(),
					id: tokenIdentifier,
					validityInSeconds: _smSwitchInitializer.SmsControls.SessionTimeoutInSeconds,
					numberOfDigits: _devConsoleInitializer.SMSwitchGeneralSettings.OtpLength);

				_logger.LogWarning("DevConsole OTP for +{MobileNumber}: {OTP}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber, otp);

				return new SMSwitchResponseSendOTP()
				{
					IsSent = true,
					OtpLength = _devConsoleInitializer.SMSwitchGeneralSettings.OtpLength
				};
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Could not generate DevConsole OTP for +{MobileNumber}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
				return new SMSwitchResponseSendOTP()
				{
					IsSent = false
				};
			}
		}

		public async Task<bool> SendSMS(MobileNumber mobileWithCountryCode, string shortMessageServiceMessage, byte resendCooldownPeriodInSeconds = 60)
		{
			if (RefusedInProduction("send SMS", mobileWithCountryCode))
			{
				return false;
			}
			_logger.LogWarning("DevConsole SMS to +{MobileNumber}: {Message}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber, shortMessageServiceMessage);
			return await Task.FromResult(true);
		}

		public async Task<SMSwitchResponseVerifyOTP> VerifyOTP(MobileNumber mobileWithCountryCode, string OTP)
		{
			if (RefusedInProduction("verify OTP", mobileWithCountryCode))
			{
				return new SMSwitchResponseVerifyOTP() { Verified = false };
			}
			try
			{
				TokenIdentifier tokenIdentifier = mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber;
				var verified = await _mongoDbTokenService.Validate(tokenIdentifier, OTP);
				if (verified)
				{
					await _mongoDbTokenService.Consume(tokenIdentifier);
				}
				return new SMSwitchResponseVerifyOTP()
				{
					Verified = verified
				};
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Could not verify DevConsole OTP for +{MobileNumber}", mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
				return new SMSwitchResponseVerifyOTP()
				{
					Verified = false
				};
			}
		}

		private bool RefusedInProduction(string operation, MobileNumber mobileWithCountryCode)
		{
			if (_hostEnvironment.IsProduction())
			{
				_logger.LogCritical("DevConsole provider must never be used in Production: refusing to {Operation} for +{MobileNumber}. Configure a real SMS provider.", operation, mobileWithCountryCode.CountryPhoneCodeAndPhoneNumber);
				return true;
			}
			return false;
		}
	}
}
