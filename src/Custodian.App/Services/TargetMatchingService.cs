using Custodian.Core.Model;
using Custodian.Platform.Windows.Services;

namespace Custodian.App.Services;

internal static class TargetMatchingService
{
    internal static TargetRow? FindEquivalentTargetRow(IEnumerable<TargetRow> targetRows, TargetRow previousTarget)
    {
        var targets = targetRows as IReadOnlyList<TargetRow> ?? targetRows.ToList();
        return previousTarget.Kind switch
        {
            TargetKind.RecycleBin => targets.FirstOrDefault(row => row.Kind == TargetKind.RecycleBin),
            TargetKind.Drive => targets.FirstOrDefault(row =>
                row.Kind == TargetKind.Drive &&
                string.Equals(row.RootPath, previousTarget.RootPath, StringComparison.OrdinalIgnoreCase)),
            TargetKind.CloudProvider => targets.FirstOrDefault(row =>
                row.Kind == TargetKind.CloudProvider &&
                string.Equals(row.RootPath, previousTarget.RootPath, StringComparison.OrdinalIgnoreCase) &&
                CloudProvidersMatch(row.CloudProvider, previousTarget.CloudProvider)),
            TargetKind.PortableDevice when previousTarget.PortableTarget is { } portableTarget =>
                FindPortableTargetRowForTarget(targets, portableTarget),
            _ => null
        };
    }

    internal static TargetRow? FindPortableTargetRowForTarget(IEnumerable<TargetRow> targetRows, PortableDeviceTarget previousTarget)
    {
        var candidates = targetRows
            .Where(row => row.Kind == TargetKind.PortableDevice && row.PortableTarget is not null)
            .ToList();

        return candidates.FirstOrDefault(row => PortableTargetMatchesTargetExactly(row.PortableTarget!, previousTarget)) ??
            candidates.FirstOrDefault(row => PortableTargetMatchesTargetByName(row.PortableTarget!, previousTarget)) ??
            candidates.FirstOrDefault(row => PortableTargetMatchesDeviceAvailabilityTransition(row.PortableTarget!, previousTarget));
    }

    private static bool CloudProvidersMatch(CloudProviderMetadata? current, CloudProviderMetadata? previous)
    {
        if (current is null || previous is null)
        {
            return current is null && previous is null;
        }

        return string.Equals(current.ProviderId, previous.ProviderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(current.RootPath, previous.RootPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PortableTargetMatchesTargetExactly(PortableDeviceTarget current, PortableDeviceTarget previous)
    {
        if (!string.Equals(current.DeviceId, previous.DeviceId, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(current.TargetId, previous.TargetId, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(previous.StorageObjectId) &&
             string.Equals(current.StorageObjectId, previous.StorageObjectId, StringComparison.Ordinal));
    }

    private static bool PortableTargetMatchesTargetByName(PortableDeviceTarget current, PortableDeviceTarget previous)
    {
        if (!string.Equals(current.DeviceId, previous.DeviceId, StringComparison.Ordinal))
        {
            return false;
        }

        return (!string.IsNullOrWhiteSpace(previous.StorageName) &&
                string.Equals(current.StorageName, previous.StorageName, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(current.DisplayPath, previous.DisplayPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PortableTargetMatchesDeviceAvailabilityTransition(PortableDeviceTarget current, PortableDeviceTarget previous)
        => string.Equals(current.DeviceId, previous.DeviceId, StringComparison.Ordinal) &&
            current.IsAvailable != previous.IsAvailable;
}
