using System.IO;
using Custodian.Platform.Windows.Logging;
using Microsoft.Extensions.Logging;

namespace Custodian.App.Services;

internal static class PathClassificationService
{
    private static readonly ILogger Logger = AppLogging.CreateLogger(typeof(PathClassificationService).FullName!);

    public static bool IsRemotePath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
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
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning(ex, "Unable to classify drive type for a selected item; treating it as remote.");
            return true;
        }
    }
}
