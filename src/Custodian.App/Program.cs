using Velopack;

namespace Custodian.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetArgs(args)
            .SetAutoApplyOnStartup(false)
            .Run(null);

        var app = new App();
        app.InitializeComponent();
        app.Run(new MainWindow());
    }
}
