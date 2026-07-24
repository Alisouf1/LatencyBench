using System.Security.Principal;

namespace LatencyBench.Core.Elevation;

public static class ElevationHelper
{
	public static bool IsRunningAsAdministrator()
	{
		using WindowsIdentity ntIdentity = WindowsIdentity.GetCurrent();
		WindowsPrincipal windowsPrincipal = new WindowsPrincipal(ntIdentity);
		return windowsPrincipal.IsInRole(WindowsBuiltInRole.Administrator);
	}
}
