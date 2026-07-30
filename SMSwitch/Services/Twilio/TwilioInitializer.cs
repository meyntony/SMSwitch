using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SMSwitch.Common;
using Twilio;
using Twilio.Rest.Verify.V2;
using Twilio.Types;

namespace SMSwitch.Services.Twilio
{
	public sealed class TwilioInitializer : SMSwitchGeneralInitializer
	{
		internal readonly TwilioSettings? TwilioSettings;
		public TwilioInitializer(
			IConfiguration configuration,
			ILogger<TwilioInitializer> logger) : base(configuration)
		{
			try
			{

				var twilioConfig = SMSwitchSettings.GetSection(SmsProvider.Twilio.ToString());

				var accountSid = twilioConfig["AccountSid"];
				var authToken = twilioConfig["AuthToken"];
				var serviceSid = twilioConfig["ServiceSid"];

				// TwilioSettings used to be assigned before TwilioClient.Init could throw, so a
				// missing configuration left it non-null holding nulls. That defeated every
				// "TwilioSettings is null" guard in TwilioService and turned a startup
				// misconfiguration into a per-request failure against the Twilio API.
				if (string.IsNullOrWhiteSpace(accountSid)
					|| string.IsNullOrWhiteSpace(authToken)
					|| string.IsNullOrWhiteSpace(serviceSid))
				{
					logger.LogWarning("Twilio is not configured (AccountSid, AuthToken and ServiceSid are all required). The Twilio provider is disabled.");
					return;
				}

				var twilioSettings = new TwilioSettings()
				{
					AndroidAppHash = SMSwitchGeneralSettings.AndroidAppHash,
					OtpLength = SMSwitchGeneralSettings.OtpLength,
					TwilioPrivateSettings = new TwilioPrivateSettings()
					{
						AccountSid = accountSid,
						AuthToken = authToken,
						ServiceSid = serviceSid,
						// Only needed for plain SMS, so it is not required to enable OTPs.
						RegisteredSenderPhoneNumber = twilioConfig["RegisteredSenderPhoneNumber"] ?? string.Empty,
					}
				};

				TwilioClient.Init(twilioSettings.TwilioPrivateSettings.AccountSid, twilioSettings.TwilioPrivateSettings.AuthToken);

				// Started without awaiting because this is a constructor. The continuation is what
				// keeps a failure from being discarded as an unobserved task exception. Note this
				// changes the code length on the Twilio Verify service itself, which is
				// account-wide, and it races the first SendOTP.
				_ = ServiceResource.UpdateAsync(
					codeLength: twilioSettings.OtpLength,
					pathSid: twilioSettings.TwilioPrivateSettings.ServiceSid
				).ContinueWith(
					task => logger.LogError(task.Exception, "Unable to set the Twilio Verify code length to {OtpLength}", twilioSettings.OtpLength),
					TaskContinuationOptions.OnlyOnFaulted);

				TwilioSettings = twilioSettings;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Unable to initialize Twilio");
			}
		}

	}
}
