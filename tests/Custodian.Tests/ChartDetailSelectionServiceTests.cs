using Custodian.App;
using Custodian.App.Services;
using Custodian.Core.Model;
using Custodian.Core.Presentation;

namespace Custodian.Tests;

public sealed class ChartDetailSelectionServiceTests
{
    [Fact]
    public void SelectedFolderEntrySlicesSelectContentsView()
        => EntrySlicesSelectMatchingDetailView(ChartScope.SelectedFolder, DetailViewMode.Contents);

    [Fact]
    public void LargestFileEntrySlicesSelectLargestFilesView()
        => EntrySlicesSelectMatchingDetailView(ChartScope.LargestFiles, DetailViewMode.LargestFiles);

    [Fact]
    public void LargestFolderEntrySlicesSelectLargestFoldersView()
        => EntrySlicesSelectMatchingDetailView(ChartScope.LargestFolders, DetailViewMode.LargestFolders);

    private static void EntrySlicesSelectMatchingDetailView(ChartScope scope, DetailViewMode expectedView)
    {
        var slice = EntrySlice(@"C:\Root\alpha.bin");

        var plan = ChartDetailSelectionService.BuildPlan([slice], scope);

        Assert.True(plan.HasActionableSelection);
        Assert.Equal(expectedView, plan.DesiredView);
        Assert.True(plan.Matches(Row(@"C:\Root\alpha.bin")));
        Assert.False(plan.Matches(Row(@"C:\Root\beta.bin")));
    }

    [Fact]
    public void ExtensionSlicesSelectExtensionViewAndMatchExtensionRows()
    {
        var slice = ExtensionSlice(".zip");

        var plan = ChartDetailSelectionService.BuildPlan([slice], ChartScope.Extensions);

        Assert.True(plan.HasActionableSelection);
        Assert.Equal(DetailViewMode.Extensions, plan.DesiredView);
        Assert.True(plan.Matches(Row(@"C:\Root\archive.zip", ".zip")));
        Assert.False(plan.Matches(Row(@"C:\Root\photo.jpg", ".jpg")));
    }

    [Fact]
    public void OtherSlicesAreNotActionable()
    {
        var plan = ChartDetailSelectionService.BuildPlan([OtherSlice()], ChartScope.SelectedFolder);

        Assert.False(plan.HasActionableSelection);
    }

    [Fact]
    public void DeleteRowsUseEntrySlicesDirectly()
    {
        var slice = EntrySlice(@"C:\Root\alpha.bin");

        var rows = ChartDetailSelectionService.BuildDeleteRows([slice]);

        var row = Assert.Single(rows);
        Assert.Equal(@"C:\Root\alpha.bin", row.FullPath);
        Assert.Same(slice.Entry, row.Entry);
    }

    [Fact]
    public void DeleteRowsIgnoreExtensionSlices()
    {
        Assert.Empty(ChartDetailSelectionService.BuildDeleteRows([ExtensionSlice(".zip")]));
    }

    [Fact]
    public void DeleteRowsIgnoreOtherSlices()
    {
        Assert.Empty(ChartDetailSelectionService.BuildDeleteRows([OtherSlice()]));
    }

    private static ChartSlice EntrySlice(string path)
    {
        var entry = new FileSystemEntry
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            LogicalSizeBytes = 10,
            AllocatedSizeBytes = 10
        };

        return Slice(path, ChartSliceKind.Entry, entry);
    }

    private static ChartSlice ExtensionSlice(string extension)
        => Slice(extension, ChartSliceKind.Extension, entry: null);

    private static ChartSlice OtherSlice()
        => Slice("other", ChartSliceKind.Other, entry: null);

    private static ChartSlice Slice(string key, ChartSliceKind kind, FileSystemEntry? entry)
        => new(
            key,
            key,
            "10 B",
            10,
            10,
            "10.0%",
            "#FFFFFF",
            kind,
            key,
            entry,
            key,
            ShowCallout: false,
            FileCategory.Other);

    private static DetailRow Row(string path, string? extension = null)
    {
        var entry = new FileSystemEntry
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            Extension = extension ?? Path.GetExtension(path),
            LogicalSizeBytes = 10,
            AllocatedSizeBytes = 10
        };

        return DetailRow.From(entry, parentBytes: 100);
    }
}
