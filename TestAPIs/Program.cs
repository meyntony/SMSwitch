using HumanLanguages;
using MongoDbService;
using SMSwitch;
using SMSwitch.Common.DTOs;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// These endpoints spend real money per request, so cap how fast a single caller can drive them.
builder.Services.AddRateLimiter(options =>
{
	options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
	options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
		RateLimitPartition.GetFixedWindowLimiter(
			partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
			factory: _ => new FixedWindowRateLimiterOptions
			{
				PermitLimit = 5,
				Window = TimeSpan.FromMinutes(1)
			}));
});

builder.Services.AddMongoDbServices();

builder.Services.AddSMSwitchServices();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
	app.UseSwagger();
	app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseRateLimiter();

// This project exists only to exercise the library locally. The endpoints below have no
// authentication and will send real SMS at the account owner's expense, so they are mapped in
// Development only — otherwise anyone able to reach a deployed instance could drive the Twilio or
// Plivo balance to zero against arbitrary numbers.
if (app.Environment.IsDevelopment())
{
	// Parameters are taken from the request body rather than the query string so that phone
	// numbers and one-time passwords do not end up in server and proxy access logs.
	app.MapPost("/sendotp", async (SMSwitchService smsSwitchService, SendOtpRequest request, CancellationToken cancellationToken) =>
	{
		var mobileNumber = new MobileNumber()
		{
			CountryIsoCodeString = request.CountryIsoCode,
			CountryPhoneCode = request.CountryPhoneCode,
			PhoneNumber = request.PhoneNumber
		};

		if (!mobileNumber.IsValid())
		{
			return Results.BadRequest("countryPhoneCode and phoneNumber must both contain digits.");
		}

		var languageCodes = new HashSet<LanguageIsoCode> { HumanHelper.CreateLanguageIsoCode(request.PreferredLanguageIsoCode) };

		return Results.Ok(await smsSwitchService.SendOTP(mobileNumber, languageCodes, UserAgent.WebBrowser, request.ResendCooldownPeriodInSeconds, cancellationToken));
	})
	.WithName("SendOTP");

	app.MapPost("/verifyotp", async (SMSwitchService smsSwitchService, VerifyOtpRequest request, CancellationToken cancellationToken) =>
	{
		var mobileNumber = new MobileNumber()
		{
			CountryIsoCodeString = request.CountryIsoCode,
			CountryPhoneCode = request.CountryPhoneCode,
			PhoneNumber = request.PhoneNumber
		};

		if (!mobileNumber.IsValid())
		{
			return Results.BadRequest("countryPhoneCode and phoneNumber must both contain digits.");
		}

		return Results.Ok(await smsSwitchService.VerifyOTP(mobileNumber, request.OneTimePassword, cancellationToken));
	})
	.WithName("VerifyOTP");

	app.MapPost("/sendsms", async (SMSwitchService smsSwitchService, SendSmsRequest request, CancellationToken cancellationToken) =>
	{
		var mobileNumber = new MobileNumber()
		{
			CountryIsoCodeString = request.CountryIsoCode,
			CountryPhoneCode = request.CountryPhoneCode,
			PhoneNumber = request.PhoneNumber
		};

		if (!mobileNumber.IsValid())
		{
			return Results.BadRequest("countryPhoneCode and phoneNumber must both contain digits.");
		}

		return Results.Ok(await smsSwitchService.SendSMS(mobileNumber, request.Message, request.ResendCooldownPeriodInSeconds, cancellationToken));
	})
	.WithName("SendSMS");
}

app.Run();


public sealed record SendOtpRequest
{
	public string CountryIsoCode { get; init; } = "DK";
	public string CountryPhoneCode { get; init; } = "45";
	public string PhoneNumber { get; init; } = "";
	public string PreferredLanguageIsoCode { get; init; } = "en";
	public byte ResendCooldownPeriodInSeconds { get; init; } = 30;
}

public sealed record VerifyOtpRequest
{
	public string CountryIsoCode { get; init; } = "DK";
	public string CountryPhoneCode { get; init; } = "45";
	public string PhoneNumber { get; init; } = "";
	public string OneTimePassword { get; init; } = "";
}

public sealed record SendSmsRequest
{
	public string CountryIsoCode { get; init; } = "DK";
	public string CountryPhoneCode { get; init; } = "45";
	public string PhoneNumber { get; init; } = "";
	public string Message { get; init; } = "";
	public byte ResendCooldownPeriodInSeconds { get; init; } = 30;
}
