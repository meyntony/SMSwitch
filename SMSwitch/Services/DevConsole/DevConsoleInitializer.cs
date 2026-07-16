using Microsoft.Extensions.Configuration;
using SMSwitch.Common;

namespace SMSwitch.Services.DevConsole
{
	public sealed class DevConsoleInitializer : SMSwitchGeneralInitializer
	{
		public DevConsoleInitializer(IConfiguration configuration) : base(configuration)
		{
		}
	}
}
