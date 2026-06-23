using Custodian.Core.Model;
using Custodian.Core.Portable;

namespace Custodian.Tests;

public sealed class PortableExplorerNavigatorTests
{
    [Theory]
    [InlineData("Pixel/Internal storage", "")]
    [InlineData("Pixel/Internal storage/DCIM", "DCIM")]
    [InlineData("Pixel/Internal storage/DCIM/Camera", "DCIM|Camera")]
    [InlineData("Pixel/Internal storage/DCIM/Camera/photo.jpg", "DCIM|Camera|photo.jpg")]
    [InlineData("Pixel/Internal storage/Name_With_Slash/file.txt", "Name_With_Slash|file.txt")]
    public void GetRelativeSegmentsDerivesPortablePathBelowStorageRoot(string entryPath, string expected)
    {
        var result = ScanResult();
        var entry = Directory(entryPath);

        var segments = PortableExplorerNavigator.GetRelativeSegments(result, entry);

        Assert.Equal(expected, string.Join('|', segments));
    }

    [Fact]
    public void GetRelativeSegmentsRejectsNullArguments()
    {
        var result = ScanResult();
        var entry = Directory("Pixel/Internal storage/DCIM");

        Assert.Throws<ArgumentNullException>(() => PortableExplorerNavigator.GetRelativeSegments(null!, entry));
        Assert.Throws<ArgumentNullException>(() => PortableExplorerNavigator.GetRelativeSegments(result, null!));
    }

    [Fact]
    public void TryGetRelativeSegmentsRejectsEntryOutsideStorageRoot()
    {
        var result = ScanResult();
        var entry = File(".jpg", string.Empty);

        var matched = PortableExplorerNavigator.TryGetRelativeSegments(result, entry, out var segments);

        Assert.False(matched);
        Assert.Empty(segments);
    }

    [Fact]
    public void OpenStorageRootSelectedOpensStorageRoot()
    {
        var (thisPc, _, storage) = BuildTree();
        var entry = Directory("Pixel/Internal storage");

        var result = PortableExplorerNavigator.Open(ScanResult(), entry, thisPc, PortableExplorerOpenMode.Open);

        Assert.Equal(PortableExplorerOpenResultKind.OpenedExactItem, result.Kind);
        Assert.Equal(1, storage.OpenCount);
    }

    [Fact]
    public void OpenExactFolderOpensThatFolder()
    {
        var (thisPc, _, _) = BuildTree(out var dcim);
        var entry = Directory("Pixel/Internal storage/DCIM");

        var result = PortableExplorerNavigator.Open(ScanResult(), entry, thisPc, PortableExplorerOpenMode.Open);

        Assert.Equal(PortableExplorerOpenResultKind.OpenedExactItem, result.Kind);
        Assert.Equal(1, dcim.OpenCount);
    }

    [Fact]
    public void OpenExactFileInvokesDefaultVerb()
    {
        var (thisPc, _, _) = BuildTree(out _, out _, out var photo);
        var entry = File("Pixel/Internal storage/DCIM/Camera/photo.jpg", "photo-object");

        var result = PortableExplorerNavigator.Open(ScanResult(), entry, thisPc, PortableExplorerOpenMode.Open);

        Assert.Equal(PortableExplorerOpenResultKind.OpenedExactItem, result.Kind);
        Assert.Equal(1, photo.InvokeDefaultCount);
    }

    [Fact]
    public void OpenUsesPortableObjectIdentityForFinalItemWhenAvailable()
    {
        var (thisPc, _, _) = BuildTree(out _, out _, out var photo);
        photo.Name = "renamed.jpg";
        photo.IdentityText = "shell://phone/object-photo";
        var entry = File("Pixel/Internal storage/DCIM/Camera/photo.jpg", "object-photo");

        var result = PortableExplorerNavigator.Open(ScanResult(), entry, thisPc, PortableExplorerOpenMode.Open);

        Assert.Equal(PortableExplorerOpenResultKind.OpenedExactItem, result.Kind);
        Assert.Equal(1, photo.InvokeDefaultCount);
    }

    [Fact]
    public void OpenPrefersPortableObjectIdentityBeforeNameMatchForFinalItem()
    {
        var (thisPc, _, _) = BuildTree(out _, out var camera, out var photo);
        var wrongPhoto = new FakeExplorerNode("photo.jpg", isFolder: false)
        {
            IdentityText = "shell://phone/wrong-photo"
        };
        camera.Children.Insert(0, wrongPhoto);
        var entry = File("Pixel/Internal storage/DCIM/Camera/photo.jpg", "photo-object");

        var result = PortableExplorerNavigator.Open(ScanResult(), entry, thisPc, PortableExplorerOpenMode.Open);

        Assert.Equal(PortableExplorerOpenResultKind.OpenedExactItem, result.Kind);
        Assert.Equal(0, wrongPhoto.InvokeDefaultCount);
        Assert.Equal(1, photo.InvokeDefaultCount);
    }

    [Fact]
    public void RevealExactFileSelectsTheItem()
    {
        var (thisPc, _, _) = BuildTree(out _, out _, out var photo);
        var entry = File("Pixel/Internal storage/DCIM/Camera/photo.jpg", "photo-object");

        var result = PortableExplorerNavigator.Open(ScanResult(), entry, thisPc, PortableExplorerOpenMode.Reveal);

        Assert.Equal(PortableExplorerOpenResultKind.OpenedExactItem, result.Kind);
        Assert.Equal(1, photo.SelectCount);
    }

    [Fact]
    public void MissingFileOpensNearestParent()
    {
        var (thisPc, _, _) = BuildTree(out var dcim);
        var entry = File("Pixel/Internal storage/DCIM/Missing/photo.jpg", "missing-object");

        var result = PortableExplorerNavigator.Open(ScanResult(), entry, thisPc, PortableExplorerOpenMode.Reveal);

        Assert.Equal(PortableExplorerOpenResultKind.OpenedParent, result.Kind);
        Assert.Equal(1, dcim.OpenCount);
    }

    [Fact]
    public void MissingTopLevelParentOpensStorageRoot()
    {
        var (thisPc, _, storage) = BuildTree();
        var entry = File("Pixel/Internal storage/Pictures/photo.jpg", "missing-object");

        var result = PortableExplorerNavigator.Open(ScanResult(), entry, thisPc, PortableExplorerOpenMode.Reveal);

        Assert.Equal(PortableExplorerOpenResultKind.OpenedStorageRoot, result.Kind);
        Assert.Equal(1, storage.OpenCount);
    }

    [Fact]
    public void MissingPhoneOpensThisPc()
    {
        var thisPc = new FakeExplorerNode("This PC");
        var entry = Directory("Pixel/Internal storage/DCIM");

        var result = PortableExplorerNavigator.Open(ScanResult(), entry, thisPc, PortableExplorerOpenMode.Open);

        Assert.Equal(PortableExplorerOpenResultKind.OpenedThisPc, result.Kind);
        Assert.Equal(1, thisPc.OpenCount);
    }

    [Fact]
    public void OpenRejectsEntryOutsideScanTreeBeforeMissingPhoneFallback()
    {
        var thisPc = new FakeExplorerNode("This PC");
        var entry = File(".jpg", string.Empty);

        var result = PortableExplorerNavigator.Open(ScanResult(), entry, thisPc, PortableExplorerOpenMode.Open);

        Assert.Equal(PortableExplorerOpenResultKind.Failed, result.Kind);
        Assert.Equal(0, thisPc.OpenCount);
    }

    [Fact]
    public void OpenRejectsEntryOutsideScanTree()
    {
        var (thisPc, _, storage) = BuildTree();
        var entry = File(".jpg", string.Empty);

        var result = PortableExplorerNavigator.Open(ScanResult(), entry, thisPc, PortableExplorerOpenMode.Open);

        Assert.Equal(PortableExplorerOpenResultKind.Failed, result.Kind);
        Assert.Equal(0, storage.OpenCount);
    }

    [Fact]
    public void OpenPrefersExactDeviceMatchBeforeSubstringMatch()
    {
        var thisPc = new FakeExplorerNode("This PC");
        var pixel8 = new FakeExplorerNode("Pixel 8");
        var pixel8Storage = new FakeExplorerNode("Internal storage");
        var pixel8Dcim = new FakeExplorerNode("DCIM");
        var pixel = new FakeExplorerNode("Pixel");
        var pixelStorage = new FakeExplorerNode("Internal storage");
        var pixelDcim = new FakeExplorerNode("DCIM");
        thisPc.Children.Add(pixel8);
        thisPc.Children.Add(pixel);
        pixel8.Children.Add(pixel8Storage);
        pixel8Storage.Children.Add(pixel8Dcim);
        pixel.Children.Add(pixelStorage);
        pixelStorage.Children.Add(pixelDcim);
        var entry = Directory("Pixel/Internal storage/DCIM");

        var result = PortableExplorerNavigator.Open(ScanResult(), entry, thisPc, PortableExplorerOpenMode.Open);

        Assert.Equal(PortableExplorerOpenResultKind.OpenedExactItem, result.Kind);
        Assert.Equal(0, pixel8Dcim.OpenCount);
        Assert.Equal(1, pixelDcim.OpenCount);
    }

    private static ScanResult ScanResult() => new()
    {
        DisplayRootPath = "Pixel/Internal storage",
        PortableDeviceName = "Pixel",
        PortableStorageName = "Internal storage"
    };

    private static FileSystemEntry Directory(string path) => new()
    {
        Name = path.Split(['/', '\\']).Last(),
        FullPath = path,
        IsDirectory = true
    };

    private static FileSystemEntry File(string path, string objectId) => new()
    {
        Name = path.Split(['/', '\\']).Last(),
        FullPath = path,
        IsDirectory = false,
        PortableObjectId = objectId
    };

    private static (FakeExplorerNode ThisPc, FakeExplorerNode Device, FakeExplorerNode Storage)
        BuildTree()
        => BuildTree(out _, out _, out _);

    private static (FakeExplorerNode ThisPc, FakeExplorerNode Device, FakeExplorerNode Storage)
        BuildTree(out FakeExplorerNode dcim)
        => BuildTree(out dcim, out _, out _);

    private static (FakeExplorerNode ThisPc, FakeExplorerNode Device, FakeExplorerNode Storage)
        BuildTree(out FakeExplorerNode dcim, out FakeExplorerNode camera, out FakeExplorerNode photo)
    {
        var thisPc = new FakeExplorerNode("This PC");
        var device = new FakeExplorerNode("Pixel");
        var storage = new FakeExplorerNode("Internal storage");
        dcim = new FakeExplorerNode("DCIM");
        camera = new FakeExplorerNode("Camera");
        photo = new FakeExplorerNode("photo.jpg", isFolder: false)
        {
            IdentityText = "shell://phone/photo-object"
        };

        thisPc.Children.Add(device);
        device.Children.Add(storage);
        storage.Children.Add(dcim);
        dcim.Children.Add(camera);
        camera.Children.Add(photo);

        return (thisPc, device, storage);
    }

    private sealed class FakeExplorerNode(string name, bool isFolder = true) : IPortableExplorerNode
    {
        public string Name { get; set; } = name;
        public string IdentityText { get; set; } = name;
        public bool IsFolder { get; set; } = isFolder;
        public List<IPortableExplorerNode> Children { get; } = [];
        public int OpenCount { get; private set; }
        public int InvokeDefaultCount { get; private set; }
        public int SelectCount { get; private set; }
        public bool OpenSucceeds { get; set; } = true;
        public bool InvokeDefaultSucceeds { get; set; } = true;
        public bool SelectSucceeds { get; set; } = true;

        public IReadOnlyList<IPortableExplorerNode> GetChildren() => Children;

        public bool TryOpen()
        {
            OpenCount++;
            return OpenSucceeds;
        }

        public bool TryInvokeDefault()
        {
            InvokeDefaultCount++;
            return InvokeDefaultSucceeds;
        }

        public bool TrySelectInExplorer()
        {
            SelectCount++;
            return SelectSucceeds;
        }
    }
}
