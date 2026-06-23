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

    [Fact]
    public void BuildPlanKeepsSameNamedSelectedFoldersSeparate()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"custodian-copy-plan-{Guid.NewGuid():N}");
        var dcimCamera = Directory("Pixel/Internal shared storage/DCIM/Camera", "dcim-camera");
        var picturesCamera = Directory("Pixel/Internal shared storage/Pictures/Camera", "pictures-camera");
        var dcimPhoto = File("Pixel/Internal shared storage/DCIM/Camera/photo.jpg", "dcim-photo");
        var picturesPhoto = File("Pixel/Internal shared storage/Pictures/Camera/photo.jpg", "pictures-photo");
        dcimCamera.Children.Add(dcimPhoto);
        picturesCamera.Children.Add(picturesPhoto);

        var plan = PortableCopyPlanner.BuildPlan([dcimCamera, picturesCamera], destination);

        Assert.Empty(plan.SkippedEntries);
        Assert.Contains(plan.Items, item =>
            item.Entry == dcimPhoto &&
            item.RelativePath == Path.Combine("Camera", "photo.jpg") &&
            item.DestinationPath == Path.Combine(destination, "Camera", "photo.jpg"));
        Assert.Contains(plan.Items, item =>
            item.Entry == picturesPhoto &&
            item.RelativePath == Path.Combine("Camera (1)", "photo.jpg") &&
            item.DestinationPath == Path.Combine(destination, "Camera (1)", "photo.jpg"));
    }

    [Fact]
    public void BuildPlanPreservesEmptySelectedFoldersAndSubfolders()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"custodian-copy-plan-{Guid.NewGuid():N}");
        var albums = Directory("Pixel/Internal shared storage/Albums", "albums");
        var empty = Directory("Pixel/Internal shared storage/Albums/Empty", "empty");
        var nested = Directory("Pixel/Internal shared storage/Albums/Empty/Nested", "nested");
        empty.Children.Add(nested);
        albums.Children.Add(empty);

        var plan = PortableCopyPlanner.BuildPlan([albums], destination);

        Assert.Empty(plan.SkippedEntries);
        Assert.Contains(plan.Items, item =>
            item.IsDirectory &&
            item.Entry == albums &&
            item.RelativePath == "Albums" &&
            item.DestinationPath == Path.Combine(destination, "Albums"));
        Assert.Contains(plan.Items, item =>
            item.IsDirectory &&
            item.Entry == empty &&
            item.RelativePath == Path.Combine("Albums", "Empty") &&
            item.DestinationPath == Path.Combine(destination, "Albums", "Empty"));
        Assert.Contains(plan.Items, item =>
            item.IsDirectory &&
            item.Entry == nested &&
            item.RelativePath == Path.Combine("Albums", "Empty", "Nested") &&
            item.DestinationPath == Path.Combine(destination, "Albums", "Empty", "Nested"));
    }

    [Fact]
    public void BuildPlanReservesEmptyDirectoryDestinationsBeforePlanningFiles()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"custodian-copy-plan-{Guid.NewGuid():N}");
        var albums = Directory("Pixel/Internal shared storage/Albums", "albums");
        var empty = Directory("Pixel/Internal shared storage/Albums/A:B", "empty");
        var file = File("Pixel/Internal shared storage/Albums/A?B", "file");
        albums.Children.Add(empty);
        albums.Children.Add(file);

        var plan = PortableCopyPlanner.BuildPlan([albums], destination);

        Assert.Contains(plan.Items, item =>
            item.IsDirectory &&
            item.Entry == empty &&
            item.DestinationPath == Path.Combine(destination, "Albums", "A_B"));
        Assert.Contains(plan.Items, item =>
            !item.IsDirectory &&
            item.Entry == file &&
            item.DestinationPath == Path.Combine(destination, "Albums", "A_B (1)"));
    }

    [Fact]
    public void BuildPlanReservesNonEmptyDirectoryDestinationsBeforePlanningFiles()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"custodian-copy-plan-{Guid.NewGuid():N}");
        var albums = Directory("Pixel/Internal shared storage/Albums", "albums");
        var folder = Directory("Pixel/Internal shared storage/Albums/A:B", "folder");
        var child = File("Pixel/Internal shared storage/Albums/A:B/child.txt", "child");
        var file = File("Pixel/Internal shared storage/Albums/A?B", "file");
        folder.Children.Add(child);
        albums.Children.Add(folder);
        albums.Children.Add(file);

        var plan = PortableCopyPlanner.BuildPlan([albums], destination);

        Assert.Contains(plan.Items, item =>
            item.Entry == child &&
            item.DestinationPath == Path.Combine(destination, "Albums", "A_B", "child.txt"));
        Assert.Contains(plan.Items, item =>
            item.Entry == file &&
            item.DestinationPath == Path.Combine(destination, "Albums", "A_B (1)"));
    }

    [Fact]
    public void BuildPlanKeepsCollidingSanitizedSubfoldersSeparate()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"custodian-copy-plan-{Guid.NewGuid():N}");
        var albums = Directory("Pixel/Internal shared storage/Albums", "albums");
        var first = Directory("Pixel/Internal shared storage/Albums/A:B", "first");
        var second = Directory("Pixel/Internal shared storage/Albums/A?B", "second");
        var firstPhoto = File("Pixel/Internal shared storage/Albums/A:B/photo.jpg", "first-photo");
        var secondPhoto = File("Pixel/Internal shared storage/Albums/A?B/photo.jpg", "second-photo");
        first.Children.Add(firstPhoto);
        second.Children.Add(secondPhoto);
        albums.Children.Add(first);
        albums.Children.Add(second);

        var plan = PortableCopyPlanner.BuildPlan([albums], destination);

        Assert.Contains(plan.Items, item =>
            item.Entry == firstPhoto &&
            item.DestinationPath == Path.Combine(destination, "Albums", "A_B", "photo.jpg"));
        Assert.Contains(plan.Items, item =>
            item.Entry == secondPhoto &&
            item.DestinationPath == Path.Combine(destination, "Albums", "A_B (1)", "photo.jpg"));
    }

    [Theory]
    [InlineData("CON", "CON_")]
    [InlineData("con.txt", "con_.txt")]
    [InlineData("NUL.", "NUL_")]
    [InlineData("COM1.jpg", "COM1_.jpg")]
    [InlineData("LPT9", "LPT9_")]
    public void SanitizeFileNameRenamesWindowsReservedDeviceNames(string name, string expected)
    {
        Assert.Equal(expected, PortableCopyPlanner.SanitizeFileName(name));
    }

    [Fact]
    public void BuildPlanRenamesTopLevelFolderWhenDestinationFileConflicts()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"custodian-copy-plan-{Guid.NewGuid():N}");
        var camera = Directory("Pixel/Internal shared storage/DCIM/Camera", "camera");
        var photo = File("Pixel/Internal shared storage/DCIM/Camera/photo.jpg", "photo");
        camera.Children.Add(photo);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(destination, "Camera")
        };

        var plan = PortableCopyPlanner.BuildPlan([camera], destination, existing);

        var item = Assert.Single(plan.Items);
        Assert.Equal(photo, item.Entry);
        Assert.Equal(Path.Combine("Camera (1)", "photo.jpg"), item.RelativePath);
        Assert.Equal(Path.Combine(destination, "Camera (1)", "photo.jpg"), item.DestinationPath);
    }

    [Fact]
    public void BuildPlanRenamesTopLevelFolderWhenDestinationDirectoryExists()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"custodian-copy-plan-{Guid.NewGuid():N}");
        try
        {
            System.IO.Directory.CreateDirectory(Path.Combine(destination, "Camera"));
            var camera = Directory("Pixel/Internal shared storage/DCIM/Camera", "camera");
            var photo = File("Pixel/Internal shared storage/DCIM/Camera/photo.jpg", "photo");
            camera.Children.Add(photo);

            var plan = PortableCopyPlanner.BuildPlan([camera], destination);

            var item = Assert.Single(plan.Items);
            Assert.Equal(photo, item.Entry);
            Assert.Equal(Path.Combine("Camera (1)", "photo.jpg"), item.RelativePath);
            Assert.Equal(Path.Combine(destination, "Camera (1)", "photo.jpg"), item.DestinationPath);
        }
        finally
        {
            if (System.IO.Directory.Exists(destination))
            {
                System.IO.Directory.Delete(destination, recursive: true);
            }
        }
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
