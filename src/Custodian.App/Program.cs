using Custodian.App.Logging;
using Custodian.App.Services;
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
