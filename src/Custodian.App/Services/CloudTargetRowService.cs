using Custodian.Core.Model;
using Custodian.Platform.Windows.Services;

namespace Custodian.App.Services;

internal static class CloudTargetRowService
{
    public static void AddVisibleCloudTargetRows(
        IList<TargetRow> targetRows,
        IEnumerable<DriveRow> driveRows,
        IReadOnlyList<CloudProviderTarget> cloudTargets,
        Func<string, bool> isScanCached,
        Func<string, bool> isScanActive,
        Action<string> addRecentPath)
    {
        var insertIndex = CloudTargetInsertIndex(targetRows);
        foreach (var drive in driveRows.Where(row => row.IsCloudDrive))
        {
            if (targetRows.Any(row =>
                    row.Kind == TargetKind.Drive &&
                    string.Equals(row.RootPath, drive.RootPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            targetRows.Insert(insertIndex, TargetRow.FromDrive(
                drive,
                scanCached: isScanCached(drive.RootPath),
                scanActive: isScanActive(drive.RootPath)));
            insertIndex++;
            addRecentPath(drive.RootPath);
        }

        foreach (var target in cloudTargets)
        {
            if (targetRows.Any(row =>
                    row.Kind == TargetKind.CloudProvider &&
                    string.Equals(row.RootPath, target.RootPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            targetRows.Insert(insertIndex, TargetRow.FromCloudProvider(
                target,
                scanCached: isScanCached(target.RootPath),
                scanActive: isScanActive(target.RootPath)));
            insertIndex++;
            addRecentPath(target.RootPath);
        }
    }

    public static void RemoveCloudTargetRows(IList<TargetRow> targetRows)
    {
        for (var i = targetRows.Count - 1; i >= 0; i--)
        {
            if (IsCloudFilteredTarget(targetRows[i]))
            {
                targetRows.RemoveAt(i);
            }
        }
    }

    internal static int CloudTargetInsertIndex(IEnumerable<TargetRow> targetRows)
    {
        var insertIndex = 0;
        var index = 0;
        foreach (var row in targetRows)
        {
            if (row.Kind is TargetKind.RecycleBin or TargetKind.Drive or TargetKind.CloudProvider)
            {
                insertIndex = index + 1;
            }

            index++;
        }

        return insertIndex;
    }

    internal static bool IsCloudFilteredTarget(TargetRow row)
        => row.Kind == TargetKind.CloudProvider ||
            (row.Kind == TargetKind.Drive && row.IsCloudDrive);

    internal static bool IsCloudDriveVolumeLabel(string? volumeLabel)
        => string.Equals(volumeLabel?.Trim(), "Google Drive", StringComparison.OrdinalIgnoreCase);

    internal static CloudProviderMetadata? CloudProviderMetadataForDrive(DriveRow row)
    {
        if (!row.IsCloudDrive)
        {
            return null;
        }

        return row.CloudProvider ?? new CloudProviderMetadata(
            "google-drive",
            "Google Drive",
            string.Empty,
            row.RootPath);
    }
}
