using System.ComponentModel;
using Custodian.Core.Model;
using Custodian.Core.Presentation;

namespace Custodian.Tests;

public sealed class RecycleBinViewProjectorTests
{
    [Fact]
    public void RowsDefaultToNewestDeletedFirst()
    {
        var old = Entry("old.log", @"C:\Temp", new DateTimeOffset(2026, 5, 20, 8, 0, 0, TimeSpan.Zero), 10);
        var newest = Entry("new.log", @"C:\Temp", new DateTimeOffset(2026, 5, 21, 8, 0, 0, TimeSpan.Zero), 20);

        var rows = RecycleBinViewProjector.Rows([old, newest]);

        Assert.Equal(["new.log", "old.log"], rows.Select(row => row.Name).ToList());
    }

    [Fact]
    public void FilterMatchesNameOriginalLocationTypeAndRecyclePath()
    {
        var rows = RecycleBinViewProjector.Rows(
        [
            Entry("notes.txt", @"C:\Users\Strife\Documents", DateTimeOffset.UtcNow, 10, "Text Document", @"C:\$Recycle.Bin\one"),
            Entry("archive.zip", @"D:\Backups", DateTimeOffset.UtcNow, 20, "Compressed Folder", @"C:\$Recycle.Bin\two")
        ]);

        Assert.Single(RecycleBinViewProjector.FilterRows(rows, "notes"));
        Assert.Single(RecycleBinViewProjector.FilterRows(rows, "Backups"));
        Assert.Single(RecycleBinViewProjector.FilterRows(rows, "Compressed"));
        Assert.Single(RecycleBinViewProjector.FilterRows(rows, "two"));
        Assert.Empty(RecycleBinViewProjector.FilterRows(rows, "missing"));
    }

    [Fact]
    public void SortRowsUsesNumericSize()
    {
        var rows = RecycleBinViewProjector.Rows(
        [
            Entry("small.bin", @"C:\Temp", DateTimeOffset.UtcNow, 1_024),
            Entry("large.bin", @"C:\Temp", DateTimeOffset.UtcNow, 1_048_576)
        ]);

        var sorted = RecycleBinViewProjector.SortRows(rows, RecycleBinSortColumn.Size, ListSortDirection.Descending);

        Assert.Equal(["large.bin", "small.bin"], sorted.Select(row => row.Name).ToList());
    }

    private static RecycleBinEntry Entry(
        string name,
        string originalLocation,
        DateTimeOffset dateDeleted,
        long sizeBytes,
        string itemType = "File",
        string recyclePath = @"C:\$Recycle.Bin\item")
    {
        return new RecycleBinEntry(
            name,
            originalLocation,
            dateDeleted,
            sizeBytes,
            itemType,
            recyclePath + name,
            false,
            recyclePath + name);
    }
}
