using Microsoft.Extensions.DependencyInjection;
using MongoDbTokenManager;
using SMSwitch.Common;
using SMSwitch.Countries;
using SMSwitch.Countries.Database;
using SMSwitch.Database;
using SMSwitch.Services.DevConsole;
using SMSwitch.Services.Plivo;
using SMSwitch.Services.Plivo.Database;
using SMSwitch.Services.Twilio;
using uSignIn.CommonSettings.Settings;

namespace SMSwitch
{
	public static class ServiceCollectionExtensions
	{
		public static void AddSMSwitchServices(this IServiceCollection services)
		{
			services.AddSingleton<SettingsService>();
			services.AddSingleton<CountryInitializer>();
			services.AddSingleton<CountryDbService>();
			// Resolve the singleton rather than registering the type again: AddHostedService<T>()
			// is a separate descriptor, so it built a second CountryDbService and the instance that
			// ran StartAsync was not the one injected into SMSwitchService.
			services.AddHostedService(sp => sp.GetRequiredService<CountryDbService>());

			services.AddSingleton<SMSwitchInitializer>();
			services.AddSingleton<SMSwitchDbService>();
			services.AddHostedService(sp => sp.GetRequiredService<SMSwitchDbService>());

			services.AddSingleton<TwilioInitializer>();
			services.AddScoped<TwilioService>();

			services.AddSingleton<PlivoInitializer>();
			services.AddSingleton<PlivoDbService>();
			services.AddHostedService(sp => sp.GetRequiredService<PlivoDbService>());
			services.AddScoped<PlivoService>();

			services.AddMongoDbTokenServices();
			services.AddSingleton<DevConsoleInitializer>();
			services.AddScoped<DevConsoleService>();

			// Keyed by provider so SMSwitchService can resolve one by SmsProvider instead of
			// switching on it in three places. The factories resolve the concrete registrations
			// above, so there is still one instance of each per scope and anything already
			// injecting TwilioService or PlivoService directly keeps working.
			services.AddKeyedScoped<IServiceMobileNumbers>(SmsProvider.Twilio, (sp, _) => sp.GetRequiredService<TwilioService>());
			services.AddKeyedScoped<IServiceMobileNumbers>(SmsProvider.Plivo, (sp, _) => sp.GetRequiredService<PlivoService>());
			services.AddKeyedScoped<IServiceMobileNumbers>(SmsProvider.DevConsole, (sp, _) => sp.GetRequiredService<DevConsoleService>());

			services.AddScoped<SMSwitchService>();
		}
	}
}
