using Custodian.App.Logging;
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
            VelopackApp.Build()
                .SetArgs(args)
                .SetAutoApplyOnStartup(false)
                .Run(null);

            var app = new App();
            app.InitializeComponent();
            app.Run(new MainWindow());
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
