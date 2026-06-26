using Custodian.Core.Model;
using Custodian.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Custodian.Tests;

public sealed class ScanStoreTests
{
    [Fact]
    public async Task SaveThenLoadRoundTripsTreeShapeAndMetadata()
    {
        var result = SampleResult();
        using var temp = new TempScanFile();
        var store = new ScanStore();

        await store.SaveAsync(result, temp.Path);
        var loaded = await store.LoadAsync(temp.Path);

        Assert.Equal(result.RootPath, loaded.RootPath);
        Assert.Equal(result.Engine, loaded.Engine);
        Assert.Equal(result.StartedAt, loaded.StartedAt);
        Assert.Equal(result.CompletedAt, loaded.CompletedAt);

        // Tree shape: same set of full paths, same nesting depth.
        var originalPaths = result.Root.Flatten().Select(e => e.FullPath).OrderBy(p => p).ToList();
        var loadedPaths = loaded.Root.Flatten().Select(e => e.FullPath).OrderBy(p => p).ToList();
        Assert.Equal(originalPaths, loadedPaths);

        Assert.Equal(2, loaded.Root.Children.Count);
        Assert.Single(loaded.Root.Children, c => c.FullPath == @"C:\Alpha");
    }

    [Fact]
    public async Task SaveThenLoadPreservesEntryFields()
    {
        var result = SampleResult();
        using var temp = new TempScanFile();
        var store = new ScanStore();

        await store.SaveAsync(result, temp.Path);
        var loaded = await store.LoadAsync(temp.Path);

        var original = result.Root.Flatten().Single(e => e.FullPath == @"C:\Alpha\a.bin");
        var roundTripped = loaded.Root.Flatten().Single(e => e.FullPath == @"C:\Alpha\a.bin");

        Assert.Equal(original.Name, roundTripped.Name);
        Assert.False(roundTripped.IsDirectory);
        Assert.Equal(original.LogicalSizeBytes, roundTripped.LogicalSizeBytes);
        Assert.Equal(original.AllocatedSizeBytes, roundTripped.AllocatedSizeBytes);
        Assert.Equal(original.FileCount, roundTripped.FileCount);
        Assert.Equal(original.DirectoryCount, roundTripped.DirectoryCount);
        Assert.Equal(original.Extension, roundTripped.Extension);
        Assert.Equal(original.Attributes, roundTripped.Attributes);
        Assert.Equal(original.LastWriteTime, roundTripped.LastWriteTime);
    }

    [Fact]
    public async Task SaveThenLoadPreservesNullLastWriteTime()
    {
        var result = SampleResult();
        using var temp = new TempScanFile();
        var store = new ScanStore();

        await store.SaveAsync(result, temp.Path);
        var loaded = await store.LoadAsync(temp.Path);

        // The root in SampleResult has no LastWriteTime set.
        Assert.Null(loaded.Root.LastWriteTime);
    }

    [Fact]
    public async Task SaveThenLoadPreservesSkippedEntries()
    {
        var result = SampleResult();
        result.SkippedEntries.Add(new SkippedEntry(@"C:\Locked", "Access denied"));
        result.SkippedEntries.Add(new SkippedEntry(@"\\server\share", "Network timeout"));
        using var temp = new TempScanFile();
        var store = new ScanStore();

        await store.SaveAsync(result, temp.Path);
        var loaded = await store.LoadAsync(temp.Path);

        Assert.Equal(2, loaded.SkippedEntries.Count);
        Assert.Contains(loaded.SkippedEntries, s => s.Path == @"C:\Locked" && s.Reason == "Access denied");
        Assert.Contains(loaded.SkippedEntries, s => s.Path == @"\\server\share" && s.Reason == "Network timeout");
    }

    [Fact]
    public async Task SaveThenLoadPreservesSourceMetadata()
    {
        var result = SampleResult();
        result.RootPath = "wpd:device:storage";
        result.SourceKind = ScanSourceKind.PortableDevice;
        result.SourceId = "wpd:device:storage";
        result.DisplayRootPath = "Pixel/Internal shared storage";
        result.PortableDeviceId = "device-id";
        result.PortableStorageObjectId = "storage-object-id";
        result.PortableDeviceName = "Pixel";
        result.PortableStorageName = "Internal shared storage";
        result.Root.PortableObjectId = "storage-object-id";
        result.Root.Children[0].PortableObjectId = "folder-object-id";
        result.Root.Children[0].PortablePersistentId = "folder-persistent-id";
        result.Root.Children[0].Children[0].PortableObjectId = "file-object-id";
        result.Root.Children[0].Children[0].PortablePersistentId = "file-persistent-id";
        using var temp = new TempScanFile();
        var store = new ScanStore();

        await store.SaveAsync(result, temp.Path);
        var loaded = await store.LoadAsync(temp.Path);

        Assert.Equal(ScanSourceKind.PortableDevice, loaded.SourceKind);
        Assert.Equal("wpd:device:storage", loaded.SourceId);
        Assert.Equal("Pixel/Internal shared storage", loaded.DisplayRootPath);
        Assert.Equal("device-id", loaded.PortableDeviceId);
        Assert.Equal("storage-object-id", loaded.PortableStorageObjectId);
        Assert.Equal("Pixel", loaded.PortableDeviceName);
        Assert.Equal("Internal shared storage", loaded.PortableStorageName);
        Assert.Equal("storage-object-id", loaded.Root.PortableObjectId);
        Assert.Equal("folder-object-id", loaded.Root.Children[0].PortableObjectId);
        Assert.Equal("folder-persistent-id", loaded.Root.Children[0].PortablePersistentId);
        Assert.Equal("file-object-id", loaded.Root.Children[0].Children[0].PortableObjectId);
        Assert.Equal("file-persistent-id", loaded.Root.Children[0].Children[0].PortablePersistentId);
    }

    [Theory]
    [InlineData("onedrive", "OneDrive", "Personal", @"C:\Users\Me\OneDrive")]
    [InlineData("nextcloud", "Nextcloud", "cloud.example.test", @"C:\Users\Me\Nextcloud")]
    public async Task SaveThenLoadPreservesCloudProviderMetadata(
        string providerId,
        string providerName,
        string accountLabel,
        string rootPath)
    {
        var result = SampleResult();
        result.CloudProvider = new CloudProviderMetadata(providerId, providerName, accountLabel, rootPath);
        using var temp = new TempScanFile();
        var store = new ScanStore();

        await store.SaveAsync(result, temp.Path);
        var loaded = await store.LoadAsync(temp.Path);

        Assert.NotNull(loaded.CloudProvider);
        Assert.Equal(providerId, loaded.CloudProvider.ProviderId);
        Assert.Equal(providerName, loaded.CloudProvider.ProviderName);
        Assert.Equal(accountLabel, loaded.CloudProvider.AccountLabel);
        Assert.Equal(rootPath, loaded.CloudProvider.RootPath);
    }

    [Fact]
    public async Task LoadDefaultsSourceMetadataForLegacyScanFiles()
    {
        var result = SampleResult();
        using var temp = new TempScanFile();
        var store = new ScanStore();

        await store.SaveAsync(result, temp.Path);
        await using (var connection = new SqliteConnection($"Data Source={temp.Path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM metadata
                WHERE key IN (
                    'source_kind',
                    'source_id',
                    'display_root_path',
                    'portable_device_id',
                    'portable_storage_object_id',
                    'portable_device_name',
                    'portable_storage_name'
                );
                ALTER TABLE entries DROP COLUMN portable_object_id;
                ALTER TABLE entries DROP COLUMN portable_persistent_id;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var loaded = await store.LoadAsync(temp.Path);

        Assert.Equal(ScanSourceKind.FileSystem, loaded.SourceKind);
        Assert.Equal(result.RootPath, loaded.SourceId);
        Assert.Equal(result.RootPath, loaded.DisplayRootPath);
        Assert.Equal(string.Empty, loaded.PortableDeviceId);
        Assert.Equal(string.Empty, loaded.PortableStorageObjectId);
        Assert.Equal(string.Empty, loaded.PortableDeviceName);
        Assert.Equal(string.Empty, loaded.PortableStorageName);
        Assert.Equal(string.Empty, loaded.Root.PortableObjectId);
        Assert.Equal(string.Empty, loaded.Root.Children[0].PortableObjectId);
        Assert.Null(loaded.CloudProvider);
    }

    [Theory]
    [InlineData("onedrive", "OneDrive", "Personal", @"C:\Users\Me\OneDrive")]
    [InlineData("nextcloud", "Nextcloud", "cloud.example.test", @"C:\Users\Me\Nextcloud")]
    public async Task LoadDefaultsCloudProviderMetadataForLegacyScanFiles(
        string providerId,
        string providerName,
        string accountLabel,
        string rootPath)
    {
        var result = SampleResult();
        result.CloudProvider = new CloudProviderMetadata(providerId, providerName, accountLabel, rootPath);
        using var temp = new TempScanFile();
        var store = new ScanStore();

        await store.SaveAsync(result, temp.Path);
        await using (var connection = new SqliteConnection($"Data Source={temp.Path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM metadata
                WHERE key IN (
                    'cloud_provider_id',
                    'cloud_provider_name',
                    'cloud_provider_account_label',
                    'cloud_provider_root_path'
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var loaded = await store.LoadAsync(temp.Path);

        Assert.Null(loaded.CloudProvider);
    }

    [Fact]
    public async Task SaveOverwritesExistingFile()
    {
        using var temp = new TempScanFile();
        var store = new ScanStore();

        await store.SaveAsync(SampleResult(), temp.Path);

        var second = SampleResult();
        second.Engine = "SecondPass";
        await store.SaveAsync(second, temp.Path);

        var loaded = await store.LoadAsync(temp.Path);
        Assert.Equal("SecondPass", loaded.Engine);
    }

    [Fact]
    public async Task LoadRebuildsGlobalIndex()
    {
        var result = SampleResult();
        using var temp = new TempScanFile();
        var store = new ScanStore();

        await store.SaveAsync(result, temp.Path);
        var loaded = await store.LoadAsync(temp.Path);

        Assert.NotNull(loaded.GlobalIndex);
        var largestFile = loaded.GlobalIndex!.LargestFiles.First();
        Assert.Equal(@"C:\Alpha\a.bin", largestFile.FullPath);
    }

    private static ScanResult SampleResult()
    {
        var root = Directory(@"C:\", 195, 4, 2);

        var alpha = Directory(@"C:\Alpha", 150, 2, 0);
        alpha.Children.Add(File(@"C:\Alpha\a.bin", 80, ".bin", attributes: "Archive"));
        alpha.Children.Add(File(@"C:\Alpha\b.bin", 60, ".bin"));

        var beta = Directory(@"C:\Beta", 45, 2, 0);
        beta.Children.Add(File(@"C:\Beta\a.log", 40, ".log"));
        beta.Children.Add(File(@"C:\Beta\b.txt", 15, ".txt"));

        root.Children.Add(alpha);
        root.Children.Add(beta);

        return new ScanResult
        {
            RootPath = root.FullPath,
            Root = root,
            Engine = "Test",
            StartedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 1, 2, 3, 5, 6, TimeSpan.Zero)
        };
    }

    private static FileSystemEntry Directory(string path, long size, long fileCount, long directoryCount) => new()
    {
        Name = Path.GetFileName(path.TrimEnd('\\')),
        FullPath = path,
        IsDirectory = true,
        LogicalSizeBytes = size,
        AllocatedSizeBytes = size,
        FileCount = fileCount,
        DirectoryCount = directoryCount
    };

    private static FileSystemEntry File(string path, long size, string extension, string attributes = "") => new()
    {
        Name = Path.GetFileName(path),
        FullPath = path,
        IsDirectory = false,
        LogicalSizeBytes = size,
        AllocatedSizeBytes = size,
        FileCount = 1,
        Extension = extension,
        Attributes = attributes,
        LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    };

    private sealed class TempScanFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"custodian-test-{Guid.NewGuid():N}.custodian-scan");

        public void Dispose()
        {
            try
            {
                if (System.IO.File.Exists(Path))
                {
                    System.IO.File.Delete(Path);
                }
            }
            catch
            {
                // Best-effort cleanup; a leaked temp file must not fail the test.
            }
        }
    }
}
