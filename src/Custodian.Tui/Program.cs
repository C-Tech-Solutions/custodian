using Custodian.Platform.Windows.Logging;
using Custodian.Platform.Windows.Services;
using Microsoft.Extensions.Logging;
using Velopack;

namespace Custodian.Tui;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLogging.Initialize();
        var logger = AppLogging.CreateLogger("Custodian.Tui.Program");

        try
        {
            var velopackArgs = ElevationService.RemoveCustodianArguments(args);
            var launchPath = ElevationService.GetLaunchPath(args);

            VelopackApp.Build()
                .SetArgs(velopackArgs.ToArray())
                .SetAutoApplyOnStartup(false)
                .Run(null);

            var options = TuiLaunchOptions.Parse(args, launchPath);
            TuiApplication.Run(options);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Unhandled exception terminated the TUI.");
            throw;
        }
        finally
        {
            AppLogging.Shutdown();
        }
    }
}
