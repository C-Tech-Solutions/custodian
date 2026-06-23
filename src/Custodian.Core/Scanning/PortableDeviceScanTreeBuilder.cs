using Custodian.Core.Analysis;
using Custodian.Core.Model;

namespace Custodian.Core.Scanning;

public static class PortableDeviceScanTreeBuilder
{
    public static ScanResult Build(
        PortableDeviceScanDescriptor descriptor,
        FileSystemEntry root,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        IEnumerable<SkippedEntry>? skippedEntries = null,
        IEnumerable<ScanPhaseTiming>? phaseTimings = null,
        IEnumerable<string>? diagnostics = null)
    {
        var indexBuilder = new ScanGlobalIndexBuilder();
        Aggregate(root, indexBuilder);

        var result = new ScanResult
        {
            RootPath = descriptor.RootPath,
            SourceKind = descriptor.SourceKind,
            SourceId = descriptor.SourceId,
            DisplayRootPath = descriptor.DisplayRootPath,
            PortableDeviceId = descriptor.DeviceId,
            PortableStorageObjectId = descriptor.StorageObjectId,
            PortableDeviceName = descriptor.DeviceName,
            PortableStorageName = descriptor.StorageName,
            Engine = descriptor.Engine,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Root = root,
            GlobalIndex = indexBuilder.Build(root)
        };

        if (skippedEntries is not null)
        {
            result.SkippedEntries.AddRange(skippedEntries);
        }

        if (phaseTimings is not null)
        {
            result.PhaseTimings.AddRange(phaseTimings);
        }

        if (diagnostics is not null)
        {
            result.Diagnostics.AddRange(diagnostics);
        }

        return result;
    }

    private static void Aggregate(FileSystemEntry entry, ScanGlobalIndexBuilder indexBuilder)
    {
        if (!entry.IsDirectory)
        {
            entry.FileCount = 1;
            if (entry.AllocatedSizeBytes <= 0)
            {
                entry.AllocatedSizeBytes = entry.LogicalSizeBytes;
            }

            indexBuilder.Observe(entry);
            return;
        }

        entry.LogicalSizeBytes = 0;
        entry.AllocatedSizeBytes = 0;
        entry.FileCount = 0;
        entry.DirectoryCount = 0;

        foreach (var child in entry.Children)
        {
            Aggregate(child, indexBuilder);
            entry.LogicalSizeBytes += child.LogicalSizeBytes;
            entry.AllocatedSizeBytes += child.AllocatedSizeBytes;
            entry.FileCount += child.FileCount;
            entry.DirectoryCount += child.IsDirectory ? child.DirectoryCount + 1 : 0;
        }

        indexBuilder.Observe(entry);
    }
}
