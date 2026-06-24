using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace Custodian.App;

public partial class MainWindow
{
    private bool _whatsNewMenuInstalled;
    private bool _whatsNewPromptQueued;
    private WpfMenuItem? _helpMenuItem;
    private WpfMenuItem? _whatsNewMenuItem;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        InstallWhatsNewMenuItem();
        QueueInitialWhatsNewPrompt();
    }

    private void InstallWhatsNewMenuItem()
    {
        if (_whatsNewMenuInstalled ||
            ItemsControl.ItemsControlFromItemContainer(CheckUpdatesMenuItem) is not WpfMenuItem helpMenu)
        {
            return;
        }

        _helpMenuItem = helpMenu;
        var whatsNewMenuItem = new WpfMenuItem
        {
            Header = "What's New?"
        };
        whatsNewMenuItem.Click += WhatsNew_Click;
        _whatsNewMenuItem = whatsNewMenuItem;

        var checkUpdatesIndex = helpMenu.Items.IndexOf(CheckUpdatesMenuItem);
        helpMenu.Items.Insert(checkUpdatesIndex >= 0 ? checkUpdatesIndex : helpMenu.Items.Count, whatsNewMenuItem);
        _whatsNewMenuInstalled = true;
    }

    private void QueueInitialWhatsNewPrompt()
    {
        if (_whatsNewPromptQueued)
        {
            return;
        }

        _whatsNewPromptQueued = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(async () =>
            {
                try
                {
                    await ShowInitialWhatsNewPromptAsync();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to show the initial What's New prompt.");
                }
            }));
    }

    private async Task ShowInitialWhatsNewPromptAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(750));
        if (_isClosing)
        {
            return;
        }

        var currentVersionTag = WhatsNewLinkBuilder.BuildCurrentVersionTag();
        if (!WhatsNewPromptPolicy.ShouldShowForVersion(currentVersionTag, _settings.LastSeenWhatsNewVersion))
        {
            return;
        }

        if (!OpenWhatsNewPrompt())
        {
            return;
        }

        _settings.LastSeenWhatsNewVersion = currentVersionTag;
        await PersistSettingsAsync();
    }

    private bool OpenWhatsNewPrompt()
    {
        if (_helpMenuItem is null || _whatsNewMenuItem is null)
        {
            return false;
        }

        _helpMenuItem.Focus();
        Keyboard.Focus(_helpMenuItem);
        _helpMenuItem.IsSubmenuOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusWhatsNewMenuItem));
        return true;
    }

    private void FocusWhatsNewMenuItem()
    {
        if (_helpMenuItem is null || _whatsNewMenuItem is null)
        {
            return;
        }

        _helpMenuItem.IsSubmenuOpen = true;
        _whatsNewMenuItem.BringIntoView();
        _whatsNewMenuItem.Focus();
        Keyboard.Focus(_whatsNewMenuItem);
    }

    private void WhatsNew_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(WhatsNewLinkBuilder.BuildCurrentReleaseNotesUrl())
            {
                UseShellExecute = true
            });
            UpdateFooterStatus("What's New", "Opened release notes in your browser.");
        }
        catch (Exception ex)
        {
            ShowOperationError("What's New failed", ex);
        }
    }
}

internal static class WhatsNewLinkBuilder
{
    internal const string ChangelogUrl = "https://github.com/ctech1313/custodian/blob/master/CHANGELOG.md";
    private const string ReleaseTagUrlPrefix = "https://github.com/ctech1313/custodian/releases/tag/";

    public static string BuildCurrentReleaseNotesUrl()
        => BuildReleaseNotesUrl(BuildCurrentVersionTag());

    internal static string? BuildCurrentVersionTag()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return NormalizeVersionTag(ReadInformationalVersion(assembly));
    }

    internal static string BuildReleaseNotesUrl(string? informationalVersion)
    {
        var tag = NormalizeVersionTag(informationalVersion);
        return tag is null ? ChangelogUrl : ReleaseTagUrlPrefix + Uri.EscapeDataString(tag);
    }

    private static string? ReadInformationalVersion(Assembly assembly)
        => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? assembly.GetName().Version?.ToString();

    internal static string? NormalizeVersionTag(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return null;
        }

        var version = informationalVersion.Trim();
        var metadataStart = version.IndexOf('+', StringComparison.Ordinal);
        if (metadataStart >= 0)
        {
            version = version[..metadataStart];
        }

        return string.IsNullOrWhiteSpace(version) ? null : version;
    }
}

internal static class WhatsNewPromptPolicy
{
    public static bool ShouldShowForVersion(string? currentVersionTag, string? lastSeenVersion)
        => !string.IsNullOrWhiteSpace(currentVersionTag) &&
           !string.Equals(
               currentVersionTag.Trim(),
               lastSeenVersion?.Trim(),
               StringComparison.OrdinalIgnoreCase);
}
