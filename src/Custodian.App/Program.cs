using Custodian.App.Services;
using Custodian.Platform.Windows.Logging;
using Custodian.Platform.Windows.Services;
using Microsoft.Extensions.Logging;
using Velopack;

namespace Custodian.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLogging.Initialize();
        var logger = AppLogging.CreateLogger("Custodian.App.Program");

        try
        {
            var velopackArgs = ElevationService.RemoveCustodianArguments(args);
            var launchPath = ElevationService.GetLaunchPath(args);

            VelopackApp.Build()
                .SetArgs(velopackArgs.ToArray())
                .SetAutoApplyOnStartup(false)
                .Run(null);

            // Velopack has already exited for its hook arguments, so this only runs for
            // real launches. Elevating here, rather than with a Windows compatibility
            // layer, keeps Velopack's non-elevated installer and updater able to start us.
            if (ElevationService.TryRelaunchElevatedAtStartup(args, launchPath, logger))
            {
                return;
            }

            var app = new App();
            app.InitializeComponent();
            app.Run(new MainWindow(launchPath));
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Unhandled exception terminated the application.");
            throw;
        }
        finally
        {
            AppLogging.Shutdown();
        }
    }
}
