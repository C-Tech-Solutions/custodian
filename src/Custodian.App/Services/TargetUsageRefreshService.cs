using System.IO;
using Custodian.Core.Model;
using Custodian.Core.Scanning;

namespace Custodian.App.Services;

internal static class TargetUsageRefreshService
{
    internal static IReadOnlyList<string> RefreshDriveTargetsForPath(
        IList<TargetRow> targetRows,
        IList<DriveRow> driveRows,
        IReadOnlyList<DriveRow> freshDriveRows,
        string scanPath,
        Func<string, bool> isScanCached,
        Func<string, bool> isScanActive)
    {
        ArgumentNullException.ThrowIfNull(targetRows);
        ArgumentNullException.ThrowIfNull(driveRows);
        ArgumentNullException.ThrowIfNull(freshDriveRows);
        ArgumentNullException.ThrowIfNull(isScanCached);
        ArgumentNullException.ThrowIfNull(isScanActive);

        var affectedDrives = freshDriveRows
            .Where(row => IsPathWithinRoot(scanPath, row.RootPath))
            .ToArray();
        if (affectedDrives.Length == 0)
        {
            return [];
        }

        var refreshedRoots = new List<string>(affectedDrives.Length);
        foreach (var freshDrive in affectedDrives)
        {
            ReplaceDriveRow(driveRows, freshDrive);
            ReplaceTargetRow(targetRows, freshDrive, isScanCached, isScanActive);
            refreshedRoots.Add(freshDrive.RootPath);
        }

        return refreshedRoots;
    }

    internal static bool IsPathWithinRoot(string path, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        try
        {
            var normalizedPath = ScanPathUtility.NormalizeRoot(path);
            var normalizedRoot = ScanPathUtility.NormalizeRoot(rootPath);
            if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return normalizedPath.Length > normalizedRoot.Length &&
                normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                (normalizedRoot.EndsWith(Path.DirectorySeparatorChar) ||
                 normalizedPath[normalizedRoot.Length] == Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ReplaceDriveRow(IList<DriveRow> driveRows, DriveRow freshDrive)
    {
        for (var i = 0; i < driveRows.Count; i++)
        {
            if (string.Equals(driveRows[i].RootPath, freshDrive.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!EqualityComparer<DriveRow>.Default.Equals(driveRows[i], freshDrive))
                {
                    driveRows[i] = freshDrive;
                }

                return;
            }
        }

        driveRows.Add(freshDrive);
    }

    private static void ReplaceTargetRow(
        IList<TargetRow> targetRows,
        DriveRow freshDrive,
        Func<string, bool> isScanCached,
        Func<string, bool> isScanActive)
    {
        for (var i = 0; i < targetRows.Count; i++)
        {
            var target = targetRows[i];
            if (target.Kind != TargetKind.Drive ||
                !string.Equals(target.RootPath, freshDrive.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var replacement = TargetRow.FromDrive(
                freshDrive,
                scanCached: isScanCached(freshDrive.RootPath),
                scanActive: isScanActive(freshDrive.RootPath));
            if (!EqualityComparer<TargetRow>.Default.Equals(target, replacement) ||
                target.IsCloudDrive != replacement.IsCloudDrive ||
                !EqualityComparer<CloudProviderMetadata?>.Default.Equals(target.CloudProvider, replacement.CloudProvider))
            {
                targetRows[i] = replacement;
            }

            return;
        }
    }
}
