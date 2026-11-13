using Microsoft.Win32;

namespace Custodian.Platform.Windows.Services;

/// <summary>
/// Stores the "always open as administrator" preference for one Custodian executable.
/// </summary>
/// <remarks>
/// The preference lives under Custodian's own HKCU key. It used to be a Windows
/// <c>RUNASADMIN</c> compatibility layer on the executable, but that layer makes every
/// non-elevated <c>CreateProcess</c> of the exe fail with error 740, which breaks
/// Velopack's install/update hooks and its post-install launch. The app now elevates
/// itself at startup instead, and <see cref="MigrateLegacyCompatibilityLayer"/> converts
/// the old layer into this setting.
/// </remarks>
internal sealed class RunAsAdministratorLaunchSetting
{
    internal const string SettingSubKey = @"Software\Custodian\RunAsAdministratorOnLaunch";
    internal const string CompatibilityLayersSubKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string RunAsAdministratorLayer = "RUNASADMIN";
    private const string CompatibilityLayerPrefix = "~";

    private readonly RegistryKey _userRoot;
    private readonly string _executablePath;

    public RunAsAdministratorLaunchSetting(RegistryKey userRoot, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(userRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _userRoot = userRoot;
        _executablePath = executablePath;
    }

    public bool IsEnabled()
    {
        using var key = _userRoot.OpenSubKey(SettingSubKey, writable: false);
        return key?.GetValue(_executablePath) is int value && value != 0;
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = _userRoot.CreateSubKey(SettingSubKey, writable: true)
                ?? throw new InvalidOperationException("Custodian could not open its administrator launch setting.");
            key.SetValue(_executablePath, 1, RegistryValueKind.DWord);
        }
        else
        {
            using var key = _userRoot.OpenSubKey(SettingSubKey, writable: true);
            key?.DeleteValue(_executablePath, throwOnMissingValue: false);
        }

        // Whichever way the user chose, the compatibility layer must not stay on the exe.
        RemoveLegacyCompatibilityLayer();
    }

    /// <summary>
    /// Converts a <c>RUNASADMIN</c> compatibility layer on this executable into the
    /// Custodian-owned setting and removes the layer.
    /// </summary>
    /// <returns><see langword="true"/> when a legacy layer was found and migrated.</returns>
    public bool MigrateLegacyCompatibilityLayer()
    {
        string? layers;
        using (var key = _userRoot.OpenSubKey(CompatibilityLayersSubKey, writable: false))
        {
            layers = key?.GetValue(_executablePath) as string;
        }

        if (!HasRunAsAdministratorLayer(layers))
        {
            return false;
        }

        // Records the preference before removing the layer, so a failure part-way
        // through never loses the user's choice.
        SetEnabled(true);
        return true;
    }

    private void RemoveLegacyCompatibilityLayer()
    {
        using var key = _userRoot.OpenSubKey(CompatibilityLayersSubKey, writable: true);
        if (key?.GetValue(_executablePath) is not string existing || !HasRunAsAdministratorLayer(existing))
        {
            return;
        }

        var updated = RemoveRunAsAdministratorLayer(existing);
        if (updated is null)
        {
            key.DeleteValue(_executablePath, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(_executablePath, updated, RegistryValueKind.String);
        }
    }

    internal static bool HasRunAsAdministratorLayer(string? layers)
        => LayerTokens(layers).Any(token => string.Equals(token, RunAsAdministratorLayer, StringComparison.OrdinalIgnoreCase));

    internal static string? RemoveRunAsAdministratorLayer(string? layers)
    {
        var tokens = LayerTokens(layers)
            .Where(token => !string.Equals(token, RunAsAdministratorLayer, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (tokens.Count == 0 ||
            tokens.All(token => string.Equals(token, CompatibilityLayerPrefix, StringComparison.Ordinal)))
        {
            return null;
        }

        return string.Join(' ', tokens);
    }

    private static IEnumerable<string> LayerTokens(string? layers)
        => (layers ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
