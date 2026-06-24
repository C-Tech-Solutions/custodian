using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace Custodian.App.Services;

internal sealed class WhatsNewMenuService(
    WpfMenuItem checkUpdatesMenuItem,
    Dispatcher dispatcher,
    Func<string?> getLastSeenVersion,
    Action<string?> setLastSeenVersion,
    Func<Task> persistSettingsAsync,
    Func<bool> isClosing,
    Action<string, string> updateFooterStatus,
    Action<string, Exception> showOperationError,
    Action<Exception> logPromptFailure)
{
    private static readonly TimeSpan InitialPromptDelay = TimeSpan.FromMilliseconds(750);

    private bool _menuInstalled;
    private bool _promptQueued;
    private WpfMenuItem? _helpMenuItem;
    private WpfMenuItem? _whatsNewMenuItem;

    public void InstallMenuItem()
    {
        if (_menuInstalled ||
            ItemsControl.ItemsControlFromItemContainer(checkUpdatesMenuItem) is not WpfMenuItem helpMenu)
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

        var checkUpdatesIndex = helpMenu.Items.IndexOf(checkUpdatesMenuItem);
        helpMenu.Items.Insert(checkUpdatesIndex >= 0 ? checkUpdatesIndex : helpMenu.Items.Count, whatsNewMenuItem);
        _menuInstalled = true;
    }

    public void QueueInitialPrompt()
    {
        if (_promptQueued)
        {
            return;
        }

        _promptQueued = true;
        dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(async () =>
            {
                try
                {
                    await ShowInitialPromptAsync();
                }
                catch (Exception ex)
                {
                    logPromptFailure(ex);
                }
            }));
    }

    private async Task ShowInitialPromptAsync()
    {
        await Task.Delay(InitialPromptDelay);
        if (isClosing())
        {
            return;
        }

        var currentVersionTag = WhatsNewLinkBuilder.BuildCurrentVersionTag();
        if (!WhatsNewPromptPolicy.ShouldShowForVersion(currentVersionTag, getLastSeenVersion()))
        {
            return;
        }

        if (!OpenPrompt())
        {
            return;
        }

        // Mark after successfully opening the nudge so dismissing it does not nag again.
        setLastSeenVersion(currentVersionTag);
        await persistSettingsAsync();
    }

    private bool OpenPrompt()
    {
        if (_helpMenuItem is null || _whatsNewMenuItem is null)
        {
            return false;
        }

        _helpMenuItem.Focus();
        Keyboard.Focus(_helpMenuItem);
        _helpMenuItem.IsSubmenuOpen = true;
        dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusWhatsNewMenuItem));
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
            updateFooterStatus("What's New", "Opened release notes in your browser.");
        }
        catch (Exception ex)
        {
            showOperationError("What's New failed", ex);
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
