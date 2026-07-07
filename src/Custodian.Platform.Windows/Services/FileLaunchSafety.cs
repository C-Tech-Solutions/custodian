using System.IO;
using Custodian.Platform.Windows.Logging;
using Microsoft.Extensions.Logging;

namespace Custodian.Platform.Windows.Services;

internal enum FileLaunchConfirmationReason
{
    None,
    RemotePath,
    LoadedScanExecutableOrScript
}

internal static class FileLaunchSafety
{
    private static readonly ILogger Logger = AppLogging.CreateLogger(typeof(FileLaunchSafety).FullName!);

    private static readonly HashSet<string> ExecutableOrScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".appref-ms",
        ".bat",
        ".cmd",
        ".com",
        ".cpl",
        ".exe",
        ".hta",
        ".inf",
        ".ins",
        ".isp",
        ".jar",
        ".js",
        ".jse",
        ".lnk",
        ".msc",
        ".msi",
        ".msp",
        ".mst",
        ".ps1",
        ".ps1xml",
        ".ps2",
        ".ps2xml",
        ".psc1",
        ".psc2",
        ".reg",
        ".scr",
        ".sct",
        ".shb",
        ".url",
        ".vb",
        ".vbe",
        ".vbs",
        ".ws",
        ".wsc",
        ".wsf",
        ".wsh"
    };

    public static bool IsRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsUnc))
        {
            return true;
        }

        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Logger.LogWarning(ex, "Unable to classify drive type for a selected item; treating it as remote.");
            return true;
        }
    }

    public static bool HasExecutableOrScriptExtension(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(extension) && ExecutableOrScriptExtensions.Contains(extension);
    }

    public static FileLaunchConfirmationReason OpenConfirmationReason(string path, bool loadedFromScanFile)
    {
        if (IsRemotePath(path))
        {
            return FileLaunchConfirmationReason.RemotePath;
        }

        return loadedFromScanFile && HasExecutableOrScriptExtension(path)
            ? FileLaunchConfirmationReason.LoadedScanExecutableOrScript
            : FileLaunchConfirmationReason.None;
    }

    public static FileLaunchConfirmationReason RevealConfirmationReason(string path, bool loadedFromScanFile)
        => IsRemotePath(path)
            ? FileLaunchConfirmationReason.RemotePath
            : loadedFromScanFile && HasExecutableOrScriptExtension(path)
            ? FileLaunchConfirmationReason.LoadedScanExecutableOrScript
            : FileLaunchConfirmationReason.None;
}
