using Custodian.Core.Model;
using Custodian.Core.Portable;

namespace Custodian.Tests;

public sealed class PortableCopyPlannerTests
{
    [Fact]
    public void BuildPlanCopiesFoldersRecursivelyAndDeduplicatesCoveredSelections()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"custodian-copy-plan-{Guid.NewGuid():N}");
        var root = Directory("Pixel/Internal shared storage", "storage");
        var dcim = Directory("Pixel/Internal shared storage/DCIM", "dcim");
        var camera = Directory("Pixel/Internal shared storage/DCIM/Camera", "camera");
        var photo = File("Pixel/Internal shared storage/DCIM/Camera/photo.jpg", "photo");
        camera.Children.Add(photo);
        dcim.Children.Add(camera);
        root.Children.Add(dcim);

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(destination, "DCIM", "Camera", "photo.jpg")
        };

        var plan = PortableCopyPlanner.BuildPlan([dcim, photo], destination, existing);

        var item = Assert.Single(plan.Items);
        Assert.Empty(plan.SkippedEntries);
        Assert.Equal(photo, item.Entry);
        Assert.Equal(Path.Combine("DCIM", "Camera", "photo.jpg"), item.RelativePath);
        Assert.Equal(Path.Combine(destination, "DCIM", "Camera", "photo (1).jpg"), item.DestinationPath);
    }

    [Fact]
    public void BuildPlanSkipsEntriesWithoutPortableIdentity()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"custodian-copy-plan-{Guid.NewGuid():N}");
        var file = new FileSystemEntry
        {
            Name = "missing.txt",
            FullPath = "Pixel/Internal shared storage/missing.txt",
            IsDirectory = false
        };

        var plan = PortableCopyPlanner.BuildPlan([file], destination);

        Assert.Empty(plan.Items);
        Assert.Single(plan.SkippedEntries);
        Assert.Contains("Rescan", plan.SkippedEntries[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPlanRejectsNullSelections()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"custodian-copy-plan-{Guid.NewGuid():N}");

        Assert.Throws<ArgumentNullException>(() => PortableCopyPlanner.BuildPlan(null!, destination));
    }

    [Fact]
    public void BuildPlanCopiesRootSelectionUnderRootFolderName()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"custodian-copy-plan-{Guid.NewGuid():N}");
        var root = Directory("Pixel/Internal shared storage", "storage");
        root.Children.Add(File("Pixel/Internal shared storage/report.txt", "report"));

        var plan = PortableCopyPlanner.BuildPlan([root], destination);

        var item = Assert.Single(plan.Items);
        Assert.Equal(Path.Combine("Internal shared storage", "report.txt"), item.RelativePath);
        Assert.Equal(Path.Combine(destination, "Internal shared storage", "report.txt"), item.DestinationPath);
    }

    private static FileSystemEntry Directory(string path, string objectId) => new()
    {
        Name = path.Split('/').Last(),
        FullPath = path,
        IsDirectory = true,
        PortableObjectId = objectId
    };

    private static FileSystemEntry File(string path, string objectId) => new()
    {
        Name = path.Split('/').Last(),
        FullPath = path,
        IsDirectory = false,
        PortableObjectId = objectId,
        PortablePersistentId = $"{objectId}-persistent"
    };
}
