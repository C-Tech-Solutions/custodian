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
}
