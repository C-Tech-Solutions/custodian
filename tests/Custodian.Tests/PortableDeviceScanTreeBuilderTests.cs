using Custodian.Core.Model;
using Custodian.Core.Scanning;

namespace Custodian.Tests;

public sealed class PortableDeviceScanTreeBuilderTests
{
    [Fact]
    public void BuildAggregatesPortableTreeAndMarksSource()
    {
        var root = Directory("Pixel/Internal shared storage");
        var dcim = Directory("Pixel/Internal shared storage/DCIM");
        var camera = Directory("Pixel/Internal shared storage/DCIM/Camera");
        camera.Children.Add(File("Pixel/Internal shared storage/DCIM/Camera/photo.jpg", 120, ".jpg"));
        camera.Children.Add(File("Pixel/Internal shared storage/DCIM/Camera/video.mp4", 300, ".mp4", allocatedSize: 0));
        dcim.Children.Add(camera);
        root.Children.Add(dcim);
        root.Children.Add(File("Pixel/Internal shared storage/Download/doc.pdf", 80, ".pdf", attributes: "PortableDevice, Hidden"));

        var result = PortableDeviceScanTreeBuilder.Build(
            new PortableDeviceScanDescriptor(
                "wpd:device:storage",
                "wpd:device:storage",
                "Pixel/Internal shared storage",
                "device-id",
                "storage-object-id",
                "Pixel",
                "Internal shared storage"),
            root,
            DateTimeOffset.Parse("2026-06-22T12:00:00Z"),
            DateTimeOffset.Parse("2026-06-22T12:00:05Z"),
            [new SkippedEntry("Pixel/Internal shared storage/Android/data", "Access denied")],
            diagnostics: ["Allocated size is not available over MTP; logical size is used for allocated size."]);

        Assert.Equal(ScanSourceKind.PortableDevice, result.SourceKind);
        Assert.Equal("wpd:device:storage", result.RootPath);
        Assert.Equal("Pixel/Internal shared storage", result.DisplayRootPath);
        Assert.Equal("device-id", result.PortableDeviceId);
        Assert.Equal("storage-object-id", result.PortableStorageObjectId);
        Assert.Equal("Pixel", result.PortableDeviceName);
        Assert.Equal("Internal shared storage", result.PortableStorageName);
        Assert.Equal(500, result.Root.LogicalSizeBytes);
        Assert.Equal(500, result.Root.AllocatedSizeBytes);
        Assert.Equal(3, result.Root.FileCount);
        Assert.Equal(2, result.Root.DirectoryCount);
        Assert.Single(result.SkippedEntries);
        Assert.Contains(result.Diagnostics, text => text.Contains("MTP", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("video.mp4", result.GlobalIndex!.LargestFiles.First().Name);
    }

    private static FileSystemEntry Directory(string path) => new()
    {
        Name = path.Split('/').Last(),
        FullPath = path,
        IsDirectory = true,
        Attributes = "PortableDevice"
    };

    private static FileSystemEntry File(string path, long size, string extension, long? allocatedSize = null, string attributes = "PortableDevice") => new()
    {
        Name = path.Split('/').Last(),
        FullPath = path,
        IsDirectory = false,
        LogicalSizeBytes = size,
        AllocatedSizeBytes = allocatedSize ?? size,
        Extension = extension,
        Attributes = attributes,
        LastWriteTime = DateTimeOffset.Parse("2026-06-22T12:00:00Z")
    };
}
