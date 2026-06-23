using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Custodian.App.Logging;
using Custodian.Core.Formatting;
using Custodian.Core.Model;
using Custodian.Core.Portable;
using Custodian.Core.Scanning;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using PROPERTYKEY = Vanara.PInvoke.Ole32.PROPERTYKEY;
using PROPVARIANT = Vanara.PInvoke.Ole32.PROPVARIANT;

namespace Custodian.App.Services;

internal sealed class PortableDeviceService
{
    private const string PathSeparator = "/";
    private const string LockedDeviceDetail = "Unlock the phone and choose USB File Transfer mode.";
    private const int MaxStorageDiscoveryDepth = 4;
    private const int MaxStorageDiscoveryObjects = 512;
    private static readonly ILogger Logger = AppLogging.CreateLogger(typeof(PortableDeviceService).FullName!);

    public Task<IReadOnlyList<PortableDeviceTarget>> GetTargetsAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => GetTargets(cancellationToken), cancellationToken);

    public Task<ScanResult> ScanAsync(
        PortableDeviceTarget target,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!target.IsAvailable || string.IsNullOrWhiteSpace(target.StorageObjectId))
        {
            throw new InvalidOperationException(target.DetailText);
        }

        return Task.Run(() => Scan(target, progress, cancellationToken), cancellationToken);
    }

    public Task<PortableCopyResult> CopyToPcAsync(
        ScanResult result,
        IReadOnlyList<FileSystemEntry> selectedEntries,
        string destinationRoot,
        IProgress<PortableCopyProgress>? progress,
        CancellationToken cancellationToken)
        => Task.Run(
            () => CopyToPcCoreAsync(result, selectedEntries, destinationRoot, progress, cancellationToken),
            cancellationToken);

    public static string BuildUnavailableTargetId(string deviceId)
        => $"wpd:{StableToken(deviceId)}:unavailable";

    private static IReadOnlyList<PortableDeviceTarget> GetTargets(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        PortableDeviceApi.IPortableDeviceManager? manager = null;
        try
        {
            manager = (PortableDeviceApi.IPortableDeviceManager)new PortableDeviceApi.PortableDeviceManager();
            manager.RefreshDeviceList();

            var targets = new List<PortableDeviceTarget>();
            var localVolumeLabels = GetLocalVolumeLabels();
            foreach (var deviceId in GetDeviceIds(manager))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var deviceName = GetDeviceName(manager, deviceId);
                try
                {
                    var storageRoots = GetDeviceStorageRoots(deviceId, cancellationToken);
                    targets.AddRange(BuildTargetsForDevice(deviceId, deviceName, storageRoots, localVolumeLabels));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.LogInformation(ex, "Portable device {DeviceName} is not exposing readable storage.", deviceName);
                    targets.AddRange(BuildTargetsForDevice(deviceId, deviceName, [], localVolumeLabels));
                }
            }

            return targets;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Failed to enumerate portable devices.");
            return [];
        }
        finally
        {
            ReleaseComObject(manager);
        }
    }

    private static IReadOnlyList<PortableStorageObject> GetDeviceStorageRoots(
        string deviceId,
        CancellationToken cancellationToken)
    {
        PortableDeviceApi.IPortableDevice? device = null;
        PortableDeviceApi.IPortableDeviceContent? content = null;
        PortableDeviceApi.IPortableDeviceProperties? properties = null;
        PortableDeviceApi.IPortableDeviceKeyCollection? keys = null;
        try
        {
            device = OpenDevice(deviceId);
            content = device.Content();
            properties = content.Properties();
            keys = CreatePropertyKeys(includeStorage: true);

            return DiscoverStorageRoots(
                PortableDeviceApi.WPD_DEVICE_OBJECT_ID,
                parentObjectId => EnumerateChildIds(content, parentObjectId, cancellationToken),
                objectId => ReadObjectProperties(properties, keys, objectId),
                cancellationToken);
        }
        finally
        {
            ReleaseComObject(keys);
            ReleaseComObject(properties);
            ReleaseComObject(content);
            CloseAndRelease(device);
        }
    }

    private static ScanResult Scan(
        PortableDeviceTarget target,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        PortableDeviceApi.IPortableDevice? device = null;
        PortableDeviceApi.IPortableDeviceContent? content = null;
        PortableDeviceApi.IPortableDeviceProperties? properties = null;
        PortableDeviceApi.IPortableDeviceKeyCollection? keys = null;
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var skipped = new List<SkippedEntry>();
        var counters = new PortableScanCounters();
        var throttle = new PortableProgressThrottle(progress);

        try
        {
            device = OpenDevice(target.DeviceId);
            content = device.Content();
            properties = content.Properties();
            keys = CreatePropertyKeys(includeStorage: false);

            var root = new FileSystemEntry
            {
                Name = target.StorageName,
                FullPath = target.DisplayPath,
                IsDirectory = true,
                Attributes = "PortableDevice",
                PortableObjectId = target.StorageObjectId
            };

            ScanChildren(content, properties, keys, target.StorageObjectId, root, target.DisplayPath, skipped, counters, throttle, cancellationToken);
            watch.Stop();

            var descriptor = new PortableDeviceScanDescriptor(
                target.TargetId,
                target.TargetId,
                target.DisplayPath,
                target.DeviceId,
                target.StorageObjectId,
                target.DeviceName,
                target.StorageName);
            return PortableDeviceScanTreeBuilder.Build(
                descriptor,
                root,
                started,
                DateTimeOffset.UtcNow,
                skipped,
                [new ScanPhaseTiming("MTP metadata enumeration", watch.Elapsed)],
                ["Allocated size is not available over MTP; logical size is used for allocated size."]);
        }
        finally
        {
            ReleaseComObject(keys);
            ReleaseComObject(properties);
            ReleaseComObject(content);
            CloseAndRelease(device);
        }
    }

    private static void ScanChildren(
        PortableDeviceApi.IPortableDeviceContent content,
        PortableDeviceApi.IPortableDeviceProperties properties,
        PortableDeviceApi.IPortableDeviceKeyCollection keys,
        string parentObjectId,
        FileSystemEntry parent,
        string parentDisplayPath,
        List<SkippedEntry> skipped,
        PortableScanCounters counters,
        PortableProgressThrottle progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> childIds;
        try
        {
            childIds = EnumerateChildIds(content, parentObjectId, cancellationToken);
        }
        catch (Exception ex) when (IsPortableDeviceException(ex))
        {
            skipped.Add(new SkippedEntry(parentDisplayPath, ex.Message));
            return;
        }

        foreach (var childId in childIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var item = ReadObjectProperties(properties, keys, childId);
                var name = FirstNonEmpty(item.OriginalFileName, item.Name, childId);
                var fullPath = CombinePortablePath(parentDisplayPath, name);
                var entry = new FileSystemEntry
                {
                    Name = name,
                    FullPath = fullPath,
                    IsDirectory = item.IsFolder,
                    LogicalSizeBytes = item.IsFolder ? 0 : Math.Max(0, item.SizeBytes ?? 0),
                    AllocatedSizeBytes = item.IsFolder ? 0 : Math.Max(0, item.SizeBytes ?? 0),
                    Extension = item.IsFolder ? string.Empty : Path.GetExtension(name).ToLowerInvariant(),
                    Attributes = item.Attributes,
                    LastWriteTime = item.LastWriteTime,
                    PortableObjectId = childId,
                    PortablePersistentId = item.PersistentUniqueId ?? string.Empty
                };

                parent.Children.Add(entry);
                if (entry.IsDirectory)
                {
                    counters.Directories++;
                    progress.Report(fullPath, counters.Files, counters.Directories, counters.Bytes, "Scanning phone folders");
                    ScanChildren(content, properties, keys, childId, entry, fullPath, skipped, counters, progress, cancellationToken);
                }
                else
                {
                    counters.Files++;
                    counters.Bytes += entry.LogicalSizeBytes;
                    progress.Report(fullPath, counters.Files, counters.Directories, counters.Bytes, "Scanning phone files");
                }
            }
            catch (Exception ex) when (IsPortableDeviceException(ex))
            {
                skipped.Add(new SkippedEntry(CombinePortablePath(parentDisplayPath, childId), ex.Message));
            }
        }
    }

    internal static IReadOnlyList<PortableDeviceTarget> BuildTargetsForDevice(
        string deviceId,
        string deviceName,
        IReadOnlyList<PortableStorageObject> storageRoots,
        IReadOnlySet<string> localVolumeLabels)
    {
        if (storageRoots.Count == 0)
        {
            return ShouldCreateUnavailableTarget(deviceId, deviceName, localVolumeLabels)
                ? [PortableDeviceTarget.Unavailable(deviceId, deviceName, LockedDeviceDetail)]
                : [];
        }

        var targets = new List<PortableDeviceTarget>(storageRoots.Count);
        foreach (var storageRoot in storageRoots)
        {
            var item = storageRoot.Properties;
            var storageName = FirstNonEmpty(item.Name, item.OriginalFileName, item.StorageDescription, "Portable storage");
            var displayPath = CombinePortablePath(deviceName, storageName);
            var storageTargetKey = FirstNonEmpty(item.PersistentUniqueId, storageName, storageRoot.ObjectId);
            var targetId = BuildTargetId(deviceId, storageTargetKey);
            var capacity = item.CapacityBytes;
            var free = item.FreeBytes;
            var detail = capacity > 0
                ? $"{SizeFormatter.Format(Math.Max(0, capacity.Value - (free ?? 0)))} used"
                : "Portable device storage";

            targets.Add(new PortableDeviceTarget(
                targetId,
                deviceId,
                deviceName,
                storageRoot.ObjectId,
                storageName,
                displayPath,
                capacity,
                free,
                IsAvailable: true,
                detail));
        }

        return targets;
    }

    private static async Task<PortableCopyResult> CopyToPcCoreAsync(
        ScanResult result,
        IReadOnlyList<FileSystemEntry> selectedEntries,
        string destinationRoot,
        IProgress<PortableCopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (result.SourceKind != ScanSourceKind.PortableDevice)
        {
            throw new InvalidOperationException("Copy to PC is only available for portable-device scans.");
        }

        if (string.IsNullOrWhiteSpace(result.PortableDeviceId) ||
            string.IsNullOrWhiteSpace(result.PortableStorageObjectId))
        {
            throw new InvalidOperationException("This scan does not contain enough phone identity metadata. Rescan the phone and try again.");
        }

        var plan = PortableCopyPlanner.BuildPlan(selectedEntries, destinationRoot);
        PortableDeviceApi.IPortableDevice? device = null;
        PortableDeviceApi.IPortableDeviceContent? content = null;
        PortableDeviceApi.IPortableDeviceProperties? properties = null;
        PortableDeviceApi.IPortableDeviceKeyCollection? keys = null;
        PortableDeviceApi.IPortableDeviceResources? resources = null;
        try
        {
            device = OpenDevice(result.PortableDeviceId);
            content = device.Content();
            properties = content.Properties();
            keys = CreatePropertyKeys(includeStorage: false);
            resources = content.Transfer();

            var provider = new PortableObjectStreamProvider(
                content,
                properties,
                keys,
                resources);
            return await new PortableCopyExecutor(provider)
                .CopyAsync(plan, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ReleaseComObject(resources);
            ReleaseComObject(keys);
            ReleaseComObject(properties);
            ReleaseComObject(content);
            CloseAndRelease(device);
        }
    }

    private static PortableDeviceApi.IPortableDevice OpenDevice(string deviceId)
    {
        PortableDeviceApi.IPortableDeviceValues? clientInfo = null;
        PortableDeviceApi.IPortableDevice? device = null;
        try
        {
            clientInfo = CreateClientInfo();
            device = (PortableDeviceApi.IPortableDevice)new PortableDeviceApi.PortableDeviceFTM();
            device.Open(deviceId, clientInfo);
            var result = device;
            device = null;
            return result;
        }
        catch
        {
            ReleaseComObject(device);
            throw;
        }
        finally
        {
            ReleaseComObject(clientInfo);
        }
    }

    private static PortableDeviceApi.IPortableDeviceValues CreateClientInfo()
    {
        var values = (PortableDeviceApi.IPortableDeviceValues)new PortableDeviceApi.PortableDeviceValues();
        SetString(values, PortableDeviceApi.WPD_CLIENT_NAME, "Custodian");
        SetUnsignedInteger(values, PortableDeviceApi.WPD_CLIENT_MAJOR_VERSION, 1);
        SetUnsignedInteger(values, PortableDeviceApi.WPD_CLIENT_MINOR_VERSION, 0);
        SetUnsignedInteger(values, PortableDeviceApi.WPD_CLIENT_REVISION, 0);
        return values;
    }

    private static PortableDeviceApi.IPortableDeviceKeyCollection CreatePropertyKeys(bool includeStorage)
    {
        var keys = (PortableDeviceApi.IPortableDeviceKeyCollection)new PortableDeviceApi.PortableDeviceKeyCollection();
        AddKey(keys, PortableDeviceApi.WPD_OBJECT_NAME);
        AddKey(keys, PortableDeviceApi.WPD_OBJECT_ORIGINAL_FILE_NAME);
        AddKey(keys, PortableDeviceApi.WPD_OBJECT_PERSISTENT_UNIQUE_ID);
        AddKey(keys, PortableDeviceApi.WPD_OBJECT_CONTENT_TYPE);
        AddKey(keys, PortableDeviceApi.WPD_OBJECT_SIZE);
        AddKey(keys, PortableDeviceApi.WPD_OBJECT_ISHIDDEN);
        AddKey(keys, PortableDeviceApi.WPD_OBJECT_ISSYSTEM);
        AddKey(keys, PortableDeviceApi.WPD_OBJECT_DATE_MODIFIED);
        AddKey(keys, PortableDeviceApi.WPD_FUNCTIONAL_OBJECT_CATEGORY);

        if (includeStorage)
        {
            AddKey(keys, PortableDeviceApi.WPD_STORAGE_DESCRIPTION);
            AddKey(keys, PortableDeviceApi.WPD_STORAGE_CAPACITY);
            AddKey(keys, PortableDeviceApi.WPD_STORAGE_FREE_SPACE_IN_BYTES);
        }

        return keys;
    }

    internal static IReadOnlyList<PortableStorageObject> DiscoverStorageRoots(
        string rootObjectId,
        Func<string, IReadOnlyList<string>> enumerateChildren,
        Func<string, PortableObjectProperties> readProperties,
        CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<PortableStorageObject>();
        var queue = new Queue<(string ObjectId, int Depth)>();

        foreach (var objectId in enumerateChildren(rootObjectId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(objectId))
            {
                continue;
            }

            PortableObjectProperties item;
            try
            {
                item = readProperties(objectId);
            }
            catch (Exception ex) when (IsPortableDeviceException(ex))
            {
                Logger.LogDebug(ex, "Failed to read properties for portable object {ObjectId}.", objectId);
                continue;
            }

            if (item.IsStorage)
            {
                roots.Add(new PortableStorageObject(objectId, item));
            }
            else if (item.CanContainStorage)
            {
                queue.Enqueue((objectId, 1));
            }
        }

        if (roots.Count > 0)
        {
            return roots;
        }

        var inspected = visited.Count;
        while (queue.Count > 0 && inspected < MaxStorageDiscoveryObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (parentObjectId, depth) = queue.Dequeue();
            if (depth >= MaxStorageDiscoveryDepth)
            {
                continue;
            }

            IReadOnlyList<string> childIds;
            try
            {
                childIds = enumerateChildren(parentObjectId);
            }
            catch (Exception ex) when (IsPortableDeviceException(ex))
            {
                Logger.LogDebug(ex, "Failed to enumerate child portable objects under {ObjectId}.", parentObjectId);
                continue;
            }

            foreach (var childId in childIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visited.Add(childId))
                {
                    continue;
                }

                inspected++;
                PortableObjectProperties item;
                try
                {
                    item = readProperties(childId);
                }
                catch (Exception ex) when (IsPortableDeviceException(ex))
                {
                    Logger.LogDebug(ex, "Failed to read properties for portable object {ObjectId}.", childId);
                    continue;
                }

                if (item.IsStorage)
                {
                    roots.Add(new PortableStorageObject(childId, item));
                }
                else if (item.CanContainStorage && inspected < MaxStorageDiscoveryObjects)
                {
                    queue.Enqueue((childId, depth + 1));
                }
            }
        }

        return roots;
    }

    internal static IReadOnlySet<string> CreateLocalVolumeLabelSet(IEnumerable<string?> labels)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in labels)
        {
            var normalized = NormalizeVolumeLabel(label);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                set.Add(normalized);
            }
        }

        return set;
    }

    internal static bool IsLocalVolumeProjection(
        string deviceId,
        string deviceName,
        IReadOnlySet<string> localVolumeLabels,
        bool hasReadableStorage)
    {
        if (hasReadableStorage ||
            !deviceId.StartsWith(@"SWD\WPDBUSENUM\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedName = NormalizeVolumeLabel(deviceName);
        return !string.IsNullOrWhiteSpace(normalizedName) &&
            localVolumeLabels.Any(label =>
                string.Equals(normalizedName, label, StringComparison.OrdinalIgnoreCase) ||
                normalizedName.EndsWith(" " + label, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool ShouldCreateUnavailableTarget(
        string deviceId,
        string deviceName,
        IReadOnlySet<string> localVolumeLabels)
        => !IsLocalVolumeProjection(deviceId, deviceName, localVolumeLabels, hasReadableStorage: false);

    private static IReadOnlySet<string> GetLocalVolumeLabels()
    {
        if (!OperatingSystem.IsWindows())
        {
            return CreateLocalVolumeLabelSet([]);
        }

        return CreateLocalVolumeLabelSet(DriveInfo
            .GetDrives()
            .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(TryGetVolumeLabel));
    }

    private static string? TryGetVolumeLabel(DriveInfo drive)
    {
        try
        {
            return drive.IsReady ? drive.VolumeLabel : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string NormalizeVolumeLabel(string? label)
        => string.IsNullOrWhiteSpace(label) ? string.Empty : label.Trim();

    private static IReadOnlyList<string> GetDeviceIds(PortableDeviceApi.IPortableDeviceManager manager)
    {
        uint count = 0;
        var hr = manager.GetDevices(null!, ref count);
        if (hr.Failed)
        {
            hr.ThrowIfFailed("Failed to count portable devices.");
        }

        if (count == 0)
        {
            return [];
        }

        var ids = new string[count];
        hr = manager.GetDevices(ids, ref count);
        if (hr.Failed)
        {
            hr.ThrowIfFailed("Failed to enumerate portable devices.");
        }

        return ids.Where(id => !string.IsNullOrWhiteSpace(id)).Take((int)count).ToList();
    }

    private static IReadOnlyList<string> EnumerateChildIds(
        PortableDeviceApi.IPortableDeviceContent content,
        string parentObjectId,
        CancellationToken cancellationToken)
    {
        PortableDeviceApi.IEnumPortableDeviceObjectIDs? enumerator = null;
        try
        {
            enumerator = content.EnumObjects(0, parentObjectId, null!);
            var results = new List<string>();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = new string[32];
                uint fetched = 0;
                var hr = enumerator.Next((uint)batch.Length, batch, out fetched);
                if (hr.Failed)
                {
                    hr.ThrowIfFailed("Failed to enumerate portable device objects.");
                }

                if (fetched == 0)
                {
                    break;
                }

                for (var i = 0; i < fetched; i++)
                {
                    if (!string.IsNullOrWhiteSpace(batch[i]))
                    {
                        results.Add(batch[i]);
                    }
                }
            }

            return results;
        }
        finally
        {
            ReleaseComObject(enumerator);
        }
    }

    private static PortableObjectProperties ReadObjectProperties(
        PortableDeviceApi.IPortableDeviceProperties properties,
        PortableDeviceApi.IPortableDeviceKeyCollection keys,
        string objectId)
    {
        PortableDeviceApi.IPortableDeviceValues? values = null;
        try
        {
            values = properties.GetValues(objectId, keys);
            var contentType = TryGetGuid(values, PortableDeviceApi.WPD_OBJECT_CONTENT_TYPE);
            var functionalCategory = TryGetGuid(values, PortableDeviceApi.WPD_FUNCTIONAL_OBJECT_CATEGORY);
            var hidden = TryGetBool(values, PortableDeviceApi.WPD_OBJECT_ISHIDDEN);
            var system = TryGetBool(values, PortableDeviceApi.WPD_OBJECT_ISSYSTEM);
            var attributes = BuildAttributes(hidden, system);

            return new PortableObjectProperties(
                TryGetString(values, PortableDeviceApi.WPD_OBJECT_NAME),
                TryGetString(values, PortableDeviceApi.WPD_OBJECT_ORIGINAL_FILE_NAME),
                contentType,
                functionalCategory,
                TryGetString(values, PortableDeviceApi.WPD_OBJECT_PERSISTENT_UNIQUE_ID),
                TryGetUnsignedLong(values, PortableDeviceApi.WPD_OBJECT_SIZE),
                TryGetDate(values, PortableDeviceApi.WPD_OBJECT_DATE_MODIFIED),
                attributes,
                TryGetString(values, PortableDeviceApi.WPD_STORAGE_DESCRIPTION),
                TryGetUnsignedLong(values, PortableDeviceApi.WPD_STORAGE_CAPACITY),
                TryGetUnsignedLong(values, PortableDeviceApi.WPD_STORAGE_FREE_SPACE_IN_BYTES));
        }
        finally
        {
            ReleaseComObject(values);
        }
    }

    private static string GetDeviceName(PortableDeviceApi.IPortableDeviceManager manager, string deviceId)
    {
        return FirstNonEmpty(
            TryGetManagerString(manager.GetDeviceFriendlyName, deviceId),
            TryGetManagerString(manager.GetDeviceDescription, deviceId),
            TryGetManagerString(manager.GetDeviceManufacturer, deviceId),
            "Portable device");
    }

    private static string? TryGetManagerString(
        PortableDeviceManagerStringGetter getter,
        string deviceId)
    {
        try
        {
            uint length = 0;
            var hr = getter(deviceId, null!, ref length);
            if (hr.Failed || length == 0)
            {
                return null;
            }

            var builder = new StringBuilder((int)length);
            hr = getter(deviceId, builder, ref length);
            return hr.Succeeded ? builder.ToString().TrimEnd('\0') : null;
        }
        catch (Exception ex) when (IsPortableDeviceException(ex))
        {
            return null;
        }
    }

    private static void AddKey(PortableDeviceApi.IPortableDeviceKeyCollection keys, PROPERTYKEY key)
    {
        keys.Add(in key);
    }

    private static void SetString(PortableDeviceApi.IPortableDeviceValues values, PROPERTYKEY key, string value)
    {
        values.SetStringValue(in key, value);
    }

    private static void SetUnsignedInteger(PortableDeviceApi.IPortableDeviceValues values, PROPERTYKEY key, uint value)
    {
        values.SetUnsignedIntegerValue(in key, value);
    }

    private static string? TryGetString(PortableDeviceApi.IPortableDeviceValues? values, PROPERTYKEY key)
    {
        if (values is null)
        {
            return null;
        }

        try
        {
            return values.GetStringValue(in key);
        }
        catch (Exception ex) when (IsPortableDeviceException(ex))
        {
            return null;
        }
    }

    private static Guid? TryGetGuid(PortableDeviceApi.IPortableDeviceValues? values, PROPERTYKEY key)
    {
        if (values is null)
        {
            return null;
        }

        try
        {
            return values.GetGuidValue(in key);
        }
        catch (Exception ex) when (IsPortableDeviceException(ex))
        {
            return null;
        }
    }

    private static ulong? TryGetUnsignedLong(PortableDeviceApi.IPortableDeviceValues? values, PROPERTYKEY key)
    {
        if (values is null)
        {
            return null;
        }

        try
        {
            return values.GetUnsignedLargeIntegerValue(in key);
        }
        catch (Exception ex) when (IsPortableDeviceException(ex))
        {
            return null;
        }
    }

    private static bool? TryGetBool(PortableDeviceApi.IPortableDeviceValues? values, PROPERTYKEY key)
    {
        if (values is null)
        {
            return null;
        }

        try
        {
            return values.GetBoolValue(in key);
        }
        catch (Exception ex) when (IsPortableDeviceException(ex))
        {
            return null;
        }
    }

    private static DateTimeOffset? TryGetDate(PortableDeviceApi.IPortableDeviceValues? values, PROPERTYKEY key)
    {
        if (values is null)
        {
            return null;
        }

        try
        {
            using var variant = values.GetValue(in key);
            if (variant.Value is DateTime date)
            {
                return TryCreateDateTimeOffset(date);
            }

            if (variant.Value is DateTimeOffset dateTimeOffset)
            {
                return dateTimeOffset;
            }
        }
        catch (Exception ex) when (IsPortableDeviceException(ex))
        {
        }

        var value = TryGetString(values, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? TryCreateDateTimeOffset(DateTime date)
    {
        try
        {
            var normalized = date.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(date, DateTimeKind.Local)
                : date;
            return new DateTimeOffset(normalized);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static long? ToLong(ulong? value)
    {
        if (value is null)
        {
            return null;
        }

        return value > long.MaxValue ? long.MaxValue : (long)value.Value;
    }

    private static string BuildAttributes(bool? hidden, bool? system)
    {
        var attributes = new List<string> { "PortableDevice" };
        if (hidden == true)
        {
            attributes.Add("Hidden");
        }

        if (system == true)
        {
            attributes.Add("System");
        }

        return string.Join(", ", attributes);
    }

    private static string BuildTargetId(string deviceId, string storageObjectId)
        => $"wpd:{StableToken(deviceId)}:{StableToken(storageObjectId)}";

    private static string StableToken(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string CombinePortablePath(string parent, string child)
    {
        var safeChild = string.IsNullOrWhiteSpace(child)
            ? "Unnamed"
            : child.Replace('\\', '_').Replace('/', '_').Trim();
        return string.IsNullOrWhiteSpace(parent)
            ? safeChild
            : parent.TrimEnd('/', '\\') + PathSeparator + safeChild;
    }

    private static string FirstNonEmpty(string? first, string? second, string? third)
    {
        if (!string.IsNullOrWhiteSpace(first)) return first;
        if (!string.IsNullOrWhiteSpace(second)) return second;
        if (!string.IsNullOrWhiteSpace(third)) return third;
        return string.Empty;
    }

    private static string FirstNonEmpty(string? first, string? second, string? third, string? fourth)
    {
        if (!string.IsNullOrWhiteSpace(first)) return first;
        if (!string.IsNullOrWhiteSpace(second)) return second;
        if (!string.IsNullOrWhiteSpace(third)) return third;
        if (!string.IsNullOrWhiteSpace(fourth)) return fourth;
        return string.Empty;
    }

    private static bool IsPortableDeviceException(Exception ex)
        => ex is COMException or InvalidOperationException or ArgumentException or ExternalException or UnauthorizedAccessException;

    private static void CloseAndRelease(PortableDeviceApi.IPortableDevice? device)
    {
        if (device is null)
        {
            return;
        }

        try
        {
            device.Close();
        }
        catch (Exception ex) when (IsPortableDeviceException(ex))
        {
            Logger.LogDebug(ex, "Failed to close portable device cleanly.");
        }
        finally
        {
            ReleaseComObject(device);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    internal sealed record PortableStorageObject(string ObjectId, PortableObjectProperties Properties);

    internal sealed record PortableObjectProperties(
        string? Name,
        string? OriginalFileName,
        Guid? ContentType,
        Guid? FunctionalCategory,
        string? PersistentUniqueId,
        ulong? SizeBytesValue,
        DateTimeOffset? LastWriteTime,
        string Attributes,
        string? StorageDescription,
        ulong? CapacityBytesValue,
        ulong? FreeBytesValue)
    {
        public bool IsFolder =>
            ContentType == PortableDeviceApi.WPD_CONTENT_TYPE_FOLDER ||
            ContentType == PortableDeviceApi.WPD_CONTENT_TYPE_FUNCTIONAL_OBJECT ||
            IsStorage;

        public bool IsStorage =>
            FunctionalCategory == PortableDeviceApi.WPD_FUNCTIONAL_CATEGORY_STORAGE ||
            HasStorageMetadata;

        public bool CanContainStorage =>
            IsFolder ||
            ContentType is null;

        private bool HasStorageMetadata =>
            !string.IsNullOrWhiteSpace(StorageDescription) ||
            CapacityBytesValue is not null ||
            FreeBytesValue is not null;

        public long? SizeBytes => ToLong(SizeBytesValue);
        public long? CapacityBytes => ToLong(CapacityBytesValue);
        public long? FreeBytes => ToLong(FreeBytesValue);
    }

    private sealed class PortableScanCounters
    {
        public long Files { get; set; }
        public long Directories { get; set; }
        public long Bytes { get; set; }
    }

    private sealed class PortableObjectStreamProvider(
        PortableDeviceApi.IPortableDeviceContent content,
        PortableDeviceApi.IPortableDeviceProperties properties,
        PortableDeviceApi.IPortableDeviceKeyCollection keys,
        PortableDeviceApi.IPortableDeviceResources resources) : IPortableObjectStreamProvider
    {
        public Task<Stream?> OpenReadAsync(FileSystemEntry entry, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(entry.PortablePersistentId))
            {
                if (!string.IsNullOrWhiteSpace(entry.PortableObjectId) &&
                    TryPersistentIdMatches(entry.PortableObjectId, entry.PortablePersistentId) &&
                    TryOpenDefaultResource(entry.PortableObjectId, out var stream))
                {
                    return Task.FromResult<Stream?>(stream);
                }

                var refreshedObjectId = TryGetObjectIdFromPersistentId(entry.PortablePersistentId);
                if (!string.IsNullOrWhiteSpace(refreshedObjectId) &&
                    TryOpenDefaultResource(refreshedObjectId, out stream))
                {
                    return Task.FromResult<Stream?>(stream);
                }

                return Task.FromResult<Stream?>(null);
            }

            if (!string.IsNullOrWhiteSpace(entry.PortableObjectId) &&
                TryOpenDefaultResource(entry.PortableObjectId, out var objectStream))
            {
                return Task.FromResult<Stream?>(objectStream);
            }

            return Task.FromResult<Stream?>(null);
        }

        private bool TryPersistentIdMatches(string objectId, string expectedPersistentId)
        {
            try
            {
                var item = ReadObjectProperties(properties, keys, objectId);
                return string.Equals(item.PersistentUniqueId, expectedPersistentId, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (IsPortableDeviceException(ex))
            {
                return false;
            }
        }

        private string? TryGetObjectIdFromPersistentId(string persistentId)
        {
            PortableDeviceApi.IPortableDevicePropVariantCollection? persistentIds = null;
            PortableDeviceApi.IPortableDevicePropVariantCollection? objectIds = null;
            try
            {
                persistentIds = (PortableDeviceApi.IPortableDevicePropVariantCollection)new PortableDeviceApi.PortableDevicePropVariantCollection();
                using var persistentVariant = new PROPVARIANT(persistentId);
                persistentIds.Add(persistentVariant);

                objectIds = content.GetObjectIDsFromPersistentUniqueIDs(persistentIds);
                foreach (var variant in PortableDeviceApi.Enumerate(objectIds))
                {
                    using (variant)
                    {
                        if (variant.Value is string objectId &&
                            !string.IsNullOrWhiteSpace(objectId) &&
                            !string.Equals(objectId, PortableDeviceApi.WPD_DEVICE_OBJECT_ID, StringComparison.OrdinalIgnoreCase) &&
                            TryPersistentIdMatches(objectId, persistentId))
                        {
                            return objectId;
                        }
                    }
                }
            }
            catch (Exception ex) when (IsPortableDeviceException(ex))
            {
                return null;
            }
            finally
            {
                ReleaseComObject(objectIds);
                ReleaseComObject(persistentIds);
            }

            return null;
        }

        private bool TryOpenDefaultResource(string objectId, out Stream stream)
        {
            stream = Stream.Null;
            try
            {
                var key = PortableDeviceApi.WPD_RESOURCE_DEFAULT;
                var portableStream = resources.GetStream(objectId, in key, STGM.STGM_READ, out _);
                stream = new PortableDeviceReadStream(portableStream);
                return true;
            }
            catch (Exception ex) when (IsPortableDeviceException(ex))
            {
                return false;
            }
        }
    }

    private sealed class PortableDeviceReadStream(System.Runtime.InteropServices.ComTypes.IStream stream) : Stream
    {
        private System.Runtime.InteropServices.ComTypes.IStream? _stream = stream;
        private IntPtr _bytesReadPointer = Marshal.AllocCoTaskMem(sizeof(int));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if ((uint)offset > (uint)buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if ((uint)count > (uint)(buffer.Length - offset))
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            var currentStream = _stream ?? throw new ObjectDisposedException(nameof(PortableDeviceReadStream));
            if (count == 0)
            {
                return 0;
            }

            var bytesReadPointer = _bytesReadPointer;
            if (bytesReadPointer == IntPtr.Zero)
            {
                throw new ObjectDisposedException(nameof(PortableDeviceReadStream));
            }

            byte[]? rentedBuffer = null;
            var readBuffer = buffer;
            if (offset != 0)
            {
                rentedBuffer = ArrayPool<byte>.Shared.Rent(count);
                readBuffer = rentedBuffer;
            }

            try
            {
                Marshal.WriteInt32(bytesReadPointer, 0);
                currentStream.Read(readBuffer, count, bytesReadPointer);
                var bytesRead = Marshal.ReadInt32(bytesReadPointer);
                if (offset != 0 && bytesRead > 0)
                {
                    Buffer.BlockCopy(readBuffer, 0, buffer, offset, bytesRead);
                }

                return bytesRead;
            }
            finally
            {
                if (rentedBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                }
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<int>(cancellationToken);
            }

            try
            {
                return Task.FromResult(Read(buffer, offset, count));
            }
            catch (Exception ex)
            {
                return Task.FromException<int>(ex);
            }
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MemoryMarshal.TryGetArray<byte>(buffer, out var segment))
            {
                return ValueTask.FromResult(Read(segment.Array!, segment.Offset, segment.Count));
            }

            byte[]? rentedBuffer = null;
            try
            {
                rentedBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length);
                var bytesRead = Read(rentedBuffer, 0, buffer.Length);
                rentedBuffer.AsSpan(0, bytesRead).CopyTo(buffer.Span);
                return ValueTask.FromResult(bytesRead);
            }
            finally
            {
                if (rentedBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var streamToRelease = Interlocked.Exchange(ref _stream, null);
                ReleaseComObject(streamToRelease);
            }

            var bytesReadPointer = Interlocked.Exchange(ref _bytesReadPointer, IntPtr.Zero);
            if (bytesReadPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(bytesReadPointer);
            }

            base.Dispose(disposing);
            if (disposing)
            {
                GC.SuppressFinalize(this);
            }
        }

        ~PortableDeviceReadStream()
        {
            Dispose(disposing: false);
        }
    }

    private delegate HRESULT PortableDeviceManagerStringGetter(string deviceId, StringBuilder builder, ref uint length);

    private sealed class PortableProgressThrottle(IProgress<ScanProgress>? progress, TimeSpan? interval = null)
    {
        private readonly TimeSpan _interval = interval ?? TimeSpan.FromMilliseconds(250);
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private TimeSpan _lastReport = TimeSpan.MinValue;

        public void Report(string currentPath, long filesSeen, long directoriesSeen, long bytesSeen, string message)
        {
            if (progress is null)
            {
                return;
            }

            var elapsed = _watch.Elapsed;
            if (_lastReport != TimeSpan.MinValue && elapsed - _lastReport < _interval)
            {
                return;
            }

            _lastReport = elapsed;
            progress.Report(new ScanProgress(currentPath, filesSeen, directoriesSeen, bytesSeen, message));
        }
    }
}
