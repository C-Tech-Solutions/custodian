using System.Diagnostics;
using System.Windows;
using Custodian.App.Services;
using Microsoft.Extensions.Logging;

namespace Custodian.App;

public partial class MainWindow
{
    private WhatsNewMenuService? _whatsNewMenuService;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        _whatsNewMenuService ??= new WhatsNewMenuService(
            CheckUpdatesMenuItem,
            Dispatcher,
            () => _settings.LastSeenWhatsNewVersion,
            version => _settings.LastSeenWhatsNewVersion = version,
            PersistSettingsAsync,
            () => _isClosing,
            UpdateFooterStatus,
            ShowOperationError,
            ex => Logger.LogWarning(ex, "Failed to show the initial What's New prompt."));

        _whatsNewMenuService.InstallMenuItem();
        _whatsNewMenuService.QueueInitialPrompt();
    }

    private void AboutCustodian_Click(object sender, RoutedEventArgs e)
    {
        var about = AboutInfoProvider.GetCurrent();
        var openRepository = UpdateDialog.ShowConfirmation(
            this,
            "About Custodian",
            $"Version {about.Version}\n\nCustodian is a local-first disk usage analyzer for Windows.\n\n{about.RepositoryUrl}",
            "Open GitHub",
            "Close",
            subtitle: "Custodian Disk Analyzer");
        if (!openRepository)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(about.RepositoryUrl)
            {
                UseShellExecute = true
            });
            UpdateFooterStatus("About", "Opened the Custodian GitHub repository in your browser.");
        }
        catch (Exception ex)
        {
            ShowOperationError("Open GitHub failed", ex);
        }
    }
}
