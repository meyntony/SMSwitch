# SMSwitch

[![NuGet](https://img.shields.io/nuget/v/SMSwitch.svg)](https://www.nuget.org/packages/SMSwitch)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SMSwitch.svg)](https://www.nuget.org/packages/SMSwitch)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)

**SMSwitch** is an open-source C# class library that acts as a switchboard in front of multiple SMS providers. It sends one-time passwords (OTPs) and plain SMS messages through **Twilio** or **Plivo**, choosing the provider per destination country and automatically failing over to the next provider when one fails. All sessions and attempts are stored in your own MongoDB instance for auditing.

## Features

- **Multi-provider with failover** — configure a provider priority per country phone code plus a global fallback; failed sends automatically retry with the next provider in the queue.
- **OTP send & verify** — delegates to the providers' hosted verification products (Twilio Verify, Plivo Verify), including localized OTP message templates.
- **Plain SMS** — with delivery-status confirmation and resend-cooldown deduplication.
- **`DevConsole` provider for local testing** — prints the OTP to the console instead of sending a real SMS ([see below](#local-testing-without-real-sms)).
- **Audit trail in your own MongoDB** — every session, attempt, and delivery notification is stored in your database.
- **Android SMS Retriever support** — pass your app hash so OTP messages can be auto-read on Android.

## How it works

For each phone number, SMSwitch builds a queue of providers from your configured priorities (`PriorityBasedOnCountryPhoneCode`, falling back to `FallBackPriority`), repeated `MaxRoundRobinAttempts` times. Each send works through the queue until a provider succeeds; verification is routed to the provider that sent the OTP. A verification session expires after `SessionTimeoutInSeconds` or `MaximumFailedAttemptsToVerify` failed attempts. Repeated sends inside the resend cooldown return the previous result instead of sending again.

Provider priority is an ordered list, and repeats are meaningful: `[ "Twilio", "Plivo", "Twilio" ]` is a valid priority that tries Twilio, then Plivo, then Twilio again.

Sessions are kept for **30 days after they expire** and are then removed automatically by a MongoDB TTL index, so the audit trail stays available for recent activity without the collections growing without bound. SMSwitch creates the indexes it needs at startup.

## Getting started

### 1. Install

```bash
dotnet add package SMSwitch
```

### 2. Prerequisites

SMSwitch builds on two companion packages that are installed automatically but need configuration:

- [MongoDbService](https://www.nuget.org/packages/MongoDbService) — provides the MongoDB connection. Requires a `MongoDbSettings` section (connection string + database name).
- [uSignIn.CommonSettings](https://www.nuget.org/packages/uSignIn.CommonSettings) — provides your application's public base URL, which SMSwitch uses to build the Plivo delivery-notification callback URL. Requires a `Settings` section with a `BaseUrl`.

### 3. Configure

Add the following to your `appsettings.json` and adjust the values (keep real credentials in user secrets or environment variables):

```json
{
  "Settings": {
    "BaseUrl": "https://your-public-hostname/"
  },
  "MongoDbSettings": {
    "ConnectionString": "MovedToSecret",
    "DatabaseName": "MyDatabase"
  },
  "SMSwitchSettings": {
    "SupportedCountriesIsoCodes": [ "IN", "FI", "DK" ],
    "Controls": {
      "MaximumFailedAttemptsToVerify": 4,
      "SessionTimeoutInSeconds": 240,
      "MaxRoundRobinAttempts": 2,
      "PriorityBasedOnCountryPhoneCode": {
        "44": [ "Twilio", "Plivo" ],
        "45": [ "Twilio", "Plivo" ],
        "91": [ "Plivo", "Twilio" ]
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
      "SourceNumber": "MovedToSecret",
      "WebhookSecret": "MovedToSecret"
    }
  }
}
```

| Setting | Meaning |
| --- | --- |
| `SupportedCountriesIsoCodes` | Countries marked as supported in the country database. Empty list means all countries are supported. |
| `Controls:MaximumFailedAttemptsToVerify` | Failed verification attempts before a session expires (default 3). |
| `Controls:SessionTimeoutInSeconds` | Lifetime of an OTP session (default 240). |
| `Controls:MaxRoundRobinAttempts` | How many times the provider priority list is repeated in the retry queue (default 1). |
| `Controls:PriorityBasedOnCountryPhoneCode` | Provider order per country phone code. |
| `Controls:FallBackPriority` | Provider order for phone codes not listed above. Required. |
| `AndroidAppHash` | Your Android app hash for SMS Retriever auto-read. |
| `OtpLength` | OTP digit count. Applied to Twilio; Plivo is fixed at 6 (a warning is logged if they differ). |
| `Plivo:SourceNumber` | Sender number for plain SMS via Plivo (not needed for OTPs). |
| `Plivo:WebhookSecret` | **Required if you use Plivo.** Appended to the delivery-notification callback URL registered with Plivo; webhook calls without the matching secret are rejected with `401 Unauthorized`. The webhook fails closed, so if this is not set every delivery notification is rejected and Plivo sends will never be confirmed. |

### 4. Register the services

```csharp
using MongoDbService;
using SMSwitch;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMongoDbServices();
builder.Services.AddSMSwitchServices();

var app = builder.Build();

// Maps the Plivo delivery-notification webhook at /smswitch/plivonotification.
// Required if you use Plivo; harmless otherwise.
app.AddSMSwitchApiEndpoints();

app.Run();
```

### 5. Use it

Dependency-inject `SMSwitchService` wherever you need it:

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
		// sendResponse.IsSent, sendResponse.OtpLength

		// Later, verify the OTP the user typed in
		var verifyResponse = await _smSwitch.VerifyOTP(mobileNumber, "123456");
		// verifyResponse.Verified, verifyResponse.Expired

		// Or send a plain SMS
		var smsSent = await _smSwitch.SendSMS(mobileNumber, "Hello from SMSwitch!");

		return verifyResponse.Verified;
	}
}
```

## Local testing without real SMS

For local development you can route messages to the `DevConsole` provider instead of Twilio or Plivo, so no credits are spent and no credentials are needed. The OTP (or SMS text) is printed to the console via the logger, and OTPs are generated and verified through [MongoDbTokenManager](https://www.nuget.org/packages/MongoDbTokenManager) in your own MongoDB instance — the full `SendOTP` → `VerifyOTP` flow works end to end.

Put this in your `appsettings.Development.json`:

```json
{
  "SMSwitchSettings": {
    "Controls": {
      "PriorityBasedOnCountryPhoneCode": {
        "45": [ "DevConsole" ]
      },
      "FallBackPriority": [ "DevConsole" ]
    }
  }
}
```

As a safety measure the `DevConsole` provider refuses to operate when the app runs in the `Production` environment: it logs a critical error and reports the send as failed, so the provider queue falls through to a real provider if one is configured.

## Contributing

We welcome contributions! If you find a bug or have an idea for an improvement, please submit an issue or a pull request on [GitHub](https://github.com/prmeyn/SMSwitch). The repository includes a `TestAPIs` project — a minimal ASP.NET Core app with Swagger and ready-made `.http` requests for exercising the library locally.

> ⚠️ `TestAPIs` is a local development harness, not a deployable service. Its endpoints have no authentication and send real SMS at your account's expense, so they are only mapped when the app runs in the `Development` environment. Do not remove that guard.

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).

Happy coding! 🚀🌐📚
