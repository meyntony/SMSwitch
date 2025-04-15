using HumanLanguages;
using MongoDbService;
using SMSwitch;
using SMSwitch.Common.DTOs;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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




app.MapPost("/sendotp", async (SMSwitchService smsSwitchService,
	string countryIsoCode = "IN",
	string countryPhoneCode = "91",
	string phoneNumber = "",
	string preferredLanguageIsoCode = "en") =>
{
	var mobileNumber = new MobileNumber() {
		CountryIsoCodeString = countryIsoCode,
		CountryPhoneCode = countryPhoneCode,
		PhoneNumber = phoneNumber
	};
	var languageCodes = new HashSet<LanguageIsoCode> { HumanHelper.CreateLanguageIsoCode(preferredLanguageIsoCode) };
	

	var response = await smsSwitchService.SendOTP(mobileNumber, languageCodes, UserAgent.WebBrowser );
	return response.IsSent
		? Results.Ok($"OTP sent to {mobileNumber.CountryPhoneCodeAndPhoneNumber}")
		: Results.BadRequest("Failed to send OTP");
})
.WithName("SendOTP")
.WithOpenApi();

app.MapPost("/verifyotp", async (SMSwitchService smsSwitchService,
	string countryIsoCode = "IN",
	string countryPhoneCode = "91",
	string phoneNumber = "",
	string oneTimePassword = "") =>
{
	var mobileNumber = new MobileNumber()
	{
		CountryIsoCodeString = countryIsoCode,
		CountryPhoneCode = countryPhoneCode,
		PhoneNumber = phoneNumber
	};

	var response = await smsSwitchService.VerifyOTP(mobileNumber, oneTimePassword);
	return response.Verified
		? Results.Ok($"OTP verified for {mobileNumber.CountryPhoneCodeAndPhoneNumber}")
		: Results.BadRequest("Failed to verify OTP");
})
.WithName("VerifyOTP")
.WithOpenApi();

app.MapPost("/sendsms", async (SMSwitchService smsSwitchService,
	string countryIsoCode = "IN",
	string countryPhoneCode = "91",
	string phoneNumber = "",
	string message="") =>
{
	var mobileNumber = new MobileNumber()
	{
		CountryIsoCodeString = countryIsoCode,
		CountryPhoneCode = countryPhoneCode,
		PhoneNumber = phoneNumber
	};

	var isSent = await smsSwitchService.SendSMS(mobileNumber, message);
	return isSent
		? Results.Ok($"SMS sent to {mobileNumber.CountryPhoneCodeAndPhoneNumber}")
		: Results.BadRequest("Failed to send SMS");
})
.WithName("SendSMS")
.WithOpenApi();

app.Run();


