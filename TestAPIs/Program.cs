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
	string countryIsoCode = "DK",
	string countryPhoneCode = "45",
	string phoneNumber = "",
	string preferredLanguageIsoCode = "en",
	byte resendCooldownPeriodInSeconds = 30) =>
{
	var mobileNumber = new MobileNumber() {
		CountryIsoCodeString = countryIsoCode,
		CountryPhoneCode = countryPhoneCode,
		PhoneNumber = phoneNumber
	};
	var languageCodes = new HashSet<LanguageIsoCode> { HumanHelper.CreateLanguageIsoCode(preferredLanguageIsoCode) };
	

	return await smsSwitchService.SendOTP(mobileNumber, languageCodes, UserAgent.WebBrowser, resendCooldownPeriodInSeconds);
})
.WithName("SendOTP");

app.MapPost("/verifyotp", async (SMSwitchService smsSwitchService,
	string countryIsoCode = "DK",
	string countryPhoneCode = "45",
	string phoneNumber = "",
	string oneTimePassword = "") =>
{
	var mobileNumber = new MobileNumber()
	{
		CountryIsoCodeString = countryIsoCode,
		CountryPhoneCode = countryPhoneCode,
		PhoneNumber = phoneNumber
	};

	return await smsSwitchService.VerifyOTP(mobileNumber, oneTimePassword);
})
.WithName("VerifyOTP");

app.MapPost("/sendsms", async (SMSwitchService smsSwitchService,
	string countryIsoCode = "DK",
	string countryPhoneCode = "45",
	string phoneNumber = "",
	string message="",
	byte resendCooldownPeriodInSeconds = 30) =>
{
	var mobileNumber = new MobileNumber()
	{
		CountryIsoCodeString = countryIsoCode,
		CountryPhoneCode = countryPhoneCode,
		PhoneNumber = phoneNumber
	};

	return await smsSwitchService.SendSMS(mobileNumber, message, resendCooldownPeriodInSeconds);
})
.WithName("SendSMS");

app.Run();


