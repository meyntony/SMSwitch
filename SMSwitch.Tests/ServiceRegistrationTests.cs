using Microsoft.Extensions.DependencyInjection;
using SMSwitch.Common;

namespace SMSwitch.Tests
{
	public sealed class ServiceRegistrationTests
	{
		/// <summary>
		/// SMSwitchService resolves providers by SmsProvider key instead of switching on them, so
		/// the compiler no longer catches a provider that has been added to the enum but not wired
		/// up. Previously that showed as an unreachable NotImplementedException arm; now it would
		/// only surface as a resolution failure at send time. This test is that safety net.
		/// </summary>
		[Fact]
		public void Every_SmsProvider_has_a_keyed_registration()
		{
			var services = new ServiceCollection();
			services.AddSMSwitchServices();

			var keysRegistered = services
				.Where(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(IServiceMobileNumbers))
				.Select(descriptor => descriptor.ServiceKey)
				.ToHashSet();

			foreach (var smsProvider in Enum.GetValues<SmsProvider>())
			{
				Assert.Contains(smsProvider, keysRegistered);
			}
		}

		[Fact]
		public void Keyed_providers_are_scoped_like_the_services_they_resolve()
		{
			var services = new ServiceCollection();
			services.AddSMSwitchServices();

			var keyedProviders = services
				.Where(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(IServiceMobileNumbers))
				.ToList();

			Assert.NotEmpty(keyedProviders);
			Assert.All(keyedProviders, descriptor => Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime));
		}

		/// <summary>
		/// CountryDbService and SMSwitchDbService are registered as singletons and separately as
		/// hosted services. AddHostedService&lt;T&gt;() would build a second instance, so the one
		/// running StartAsync would not be the one handling requests.
		/// </summary>
		[Fact]
		public void Hosted_services_reuse_the_singleton_instance()
		{
			var services = new ServiceCollection();
			services.AddSMSwitchServices();

			var hostedDescriptors = services
				.Where(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
				.ToList();

			Assert.NotEmpty(hostedDescriptors);
			// A factory registration resolves the existing singleton; an implementation-type
			// registration would construct a second instance.
			Assert.All(hostedDescriptors, descriptor =>
			{
				Assert.Null(descriptor.ImplementationType);
				Assert.NotNull(descriptor.ImplementationFactory);
			});
		}
	}
}
