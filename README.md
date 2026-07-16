# SMSwitch(https://www.nuget.org/packages/SMSwitch)

**SMSwitch** is an open-source C# class library that provides a wrapper around existing services that are used to verify Mobile numbers and send messages.
The service stores information in a MongoDb database that you configure using the package [MongoDbService](https://www.nuget.org/packages/MongoDbService) 
In order to know the Base Url and other common settings the following package is used [uSignIn.CommonSettings](https://www.nuget.org/packages/uSignIn.CommonSettings) 
## Features

- Covers Twilio, Plivo (possible to cover more if needed)
- Usage information is stored in your own MongoDB instance for audit reasons


## Contributing

We welcome contributions! If you find a bug, have an idea for improvement, please submit an issue or a pull request on GitHub.

## Getting Started

### [NuGet Package](https://www.nuget.org/packages/SMSwitch)

To include **SMSwitch** in your project, [install the NuGet package](https://www.nuget.org/packages/SMSwitch):

```bash
dotnet add package SMSwitch
```
Then in your `appsettings.json` add the following sample configuration and change the values to match the details of your credentials to the various services.
```json
  "SMSwitchSettings": {
    "SupportedCountriesIsoCodes": [ "IN", "FI", "DK" ],
    "Controls": {
      "MaximumFailedAttemptsToVerify": 4,
      "SessionTimeoutInSeconds": 240,
      "MaxRoundRobinAttempts": 2,
      "PriorityBasedOnCountryPhoneCode": {
        "44": [ "Twilio", "Plivo" ],
        "45": [ "Twilio", "Plivo" ],
        "91": [ "Plivo", "Twilio"]
      },
      "FallBackPriority": [ "Twilio", "Plivo" ]
    },
    "AndroidAppHash": "MovedToSecret",
    "OtpLength": 6,
    "Twilio": {
      "AccountSid": "MovedToSecret",
      "AuthToken": "MovedToSecret",
      "ServiceSid": "MovedToSecret",
      "RegisteredSenderPhoneNumber": "MovedToSecret"
    },
    "Plivo": {
      "AuthId": "MovedToSecret",
      "AuthToken": "MovedToSecret",
      "AppUuid": "MovedToSecret",
      "WebhookSecret": "MovedToSecret"
    }
  }
  ```
`Plivo:WebhookSecret` is optional but recommended: when set, it is appended to the delivery-notification callback URL that SMSwitch registers with Plivo, and incoming webhook calls without the matching secret are rejected with `401 Unauthorized`.

After the above is done, register the services and (if you use Plivo) the webhook endpoint in your `Program.cs`:

```csharp
using MongoDbService;
using SMSwitch;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMongoDbServices();
builder.Services.AddSMSwitchServices();

var app = builder.Build();

// Maps the Plivo delivery-notification webhook under /smswitch
app.AddSMSwitchApiEndpoints();

app.Run();
```

Then dependency-inject `SMSwitchService` wherever you need it:

```csharp
using HumanLanguages;
using SMSwitch;
using SMSwitch.Common.DTOs;

public sealed class SignInFlow
{
	private readonly SMSwitchService _smSwitch;

	public SignInFlow(SMSwitchService smSwitch) => _smSwitch = smSwitch;

	public async Task<bool> Demo()
	{
		var mobileNumber = new MobileNumber
		{
			CountryIsoCodeString = "DK",
			CountryPhoneCode = "45",
			PhoneNumber = "12345678"
		};
		var preferredLanguages = new HashSet<LanguageIsoCode> { HumanHelper.CreateLanguageIsoCode("en") };

		// Send a one-time password (provider is picked from your configured priorities)
		var sendResponse = await _smSwitch.SendOTP(mobileNumber, preferredLanguages, UserAgent.WebBrowser);

		// Later, verify the OTP the user typed in
		var verifyResponse = await _smSwitch.VerifyOTP(mobileNumber, "123456");

		// Or send a plain SMS
		var smsSent = await _smSwitch.SendSMS(mobileNumber, "Hello from SMSwitch!");

		return verifyResponse.Verified;
	}
}
```

### GitHub Repository
Visit our GitHub repository for the latest updates, documentation, and community contributions.
https://github.com/prmeyn/SMSwitch


## License

This project is licensed under the GNU GENERAL PUBLIC LICENSE.

Happy coding! 🚀🌐📚



