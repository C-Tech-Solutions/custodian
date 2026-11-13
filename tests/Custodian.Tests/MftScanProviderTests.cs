using Custodian.Core.Model;
using Custodian.Core.Scanning;

namespace Custodian.Tests;

public sealed class MftScanProviderTests
{
    private static readonly (long Clusters, long Lcn)[] Layout = [(4, 40), (3, 20), (4, 5)];

    [Fact]
    public void ScanBuildsTheTreeFromTheDirectRead()
    {
        var volume = SyntheticMft.CreateStandard(Layout).CreateVolume(4096, Layout, 64);

        var result = Scan(new ScanOptions(@"C:\", ScanMode.Mft), volume);

        Assert.Equal(MftScanProvider.DirectSourceDiagnostic, result.Diagnostics[0]);
        Assert.Contains("File sizes from $MFT records 6; path lookups 0", result.Diagnostics);
        Assert.Equal(0, volume.EnumerateCalls);
        Assert.Equal("NTFS MFT", result.Engine);

        Assert.Equal(@"C:\", result.Root.FullPath);
        Assert.Equal(SyntheticMft.StandardFileCount, result.Root.FileCount);
        Assert.Equal(2, result.Root.DirectoryCount);
        Assert.Equal(SyntheticMft.StandardLogicalBytes, result.Root.LogicalSizeBytes);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), result.Root.LastWriteTime);

        var entries = result.Root.Flatten().ToDictionary(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(5_000_000, entries[@"C:\Users\Strife\big.bin"].LogicalSizeBytes);
        Assert.Equal(7_000_000, entries[@"C:\Users\Strife\split.bin"].LogicalSizeBytes);
        Assert.Equal(1_234, entries[@"C:\Users\Strife\partial.bin"].LogicalSizeBytes);
        Assert.True(entries.ContainsKey(@"C:\Users\Strife\first.txt"));
        Assert.False(entries.ContainsKey(@"C:\Users\second.txt"));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), entries[@"C:\top.txt"].LastWriteTime);
        Assert.NotNull(entries[@"C:\Users"].LastWriteTime);

        Assert.Equal(
            [
                new SkippedEntry("$UsnJrnl", MftTreeBuilder.ParentNotEnumeratedReason),
                new SkippedEntry(@"C:\Junction", MftTreeBuilder.ReparsePointReason),
                new SkippedEntry(@"C:\Users\Strife\cloud.bin", MftTreeBuilder.ReparsePointReason)
            ],
            result.SkippedEntries.OrderBy(entry => entry.Path, StringComparer.Ordinal));

        Assert.Equal(
            ["MFT read", "MFT parse", "MFT tree construction", "File entries", "Folder aggregation"],
            result.PhaseTimings.Select(phase => phase.Name));
    }

    [Fact]
    public void ScanFallsBackToTheEnumerationWhenTheDirectReadFails()
    {
        var mft = SyntheticMft.CreateStandard(Layout, includeAttributeListFiles: false);
        var volume = mft.CreateVolume(4096, Layout, 64);
        volume.ReadFailure = new IOException("The device is not ready.");
        foreach (var (reference, record) in mft.UsnRecords())
        {
            volume.UsnRecords[reference] = record;
        }

        var result = Scan(new ScanOptions(@"C:\", ScanMode.Mft), volume);

        Assert.StartsWith(
            $"{MftScanProvider.EnumerationSourceDiagnostic}; the direct $MFT read failed after ",
            result.Diagnostics[0],
            StringComparison.Ordinal);
        Assert.EndsWith("The device is not ready.", result.Diagnostics[0], StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, line => line.StartsWith("Volume data:", StringComparison.Ordinal));
        Assert.Equal(1, volume.EnumerateCalls);
        Assert.Equal(SyntheticMft.SingleRecordFileCount, result.Root.FileCount);
        Assert.Equal(SyntheticMft.SingleRecordLogicalBytes, result.Root.LogicalSizeBytes);
        Assert.Equal(
            ["Direct MFT read (abandoned)", "MFT enumeration", "MFT tree construction", "Size resolution", "Folder aggregation"],
            result.PhaseTimings.Select(phase => phase.Name));
    }

    [Fact]
    public void ScanFallsBackWhenTheExtentsDoNotPointAtTheMft()
    {
        var mft = SyntheticMft.CreateStandard(Layout);
        var volume = mft.CreateVolume(4096, Layout, 64);
        volume.RetrievalPointers = [new MftExtent(0, 4, 20), new MftExtent(4, 3, 40), new MftExtent(7, 4, 5)];
        volume.VolumeData = volume.VolumeData with { MftStartLcn = 20 };

        var result = Scan(new ScanOptions(@"C:\", ScanMode.Mft), volume);

        Assert.Contains("not the $MFT's own record", result.Diagnostics[0]);
        Assert.Equal(1, volume.EnumerateCalls);
    }

    [Fact]
    public void ScanUsesOnlyTheEnumerationWhenTheDirectReadIsTurnedOff()
    {
        var mft = SyntheticMft.CreateStandard(Layout, includeAttributeListFiles: false);
        var volume = mft.CreateVolume(4096, Layout, 64);
        foreach (var (reference, record) in mft.UsnRecords())
        {
            volume.UsnRecords[reference] = record;
        }

        var result = Scan(new ScanOptions(@"C:\", ScanMode.Mft, UseDirectMftRead: false), volume);

        Assert.Equal($"{MftScanProvider.EnumerationSourceDiagnostic}; the direct $MFT read is turned off", result.Diagnostics[0]);
        Assert.Equal(0, volume.VolumeDataCalls);
        Assert.Equal(0, volume.ReadCalls);
        Assert.Equal(1, volume.EnumerateCalls);
        Assert.Equal(SyntheticMft.SingleRecordFileCount, result.Root.FileCount);
        Assert.Equal(SyntheticMft.SingleRecordLogicalBytes, result.Root.LogicalSizeBytes);
    }

    [Fact]
    public void DirectReadAndEnumerationAgreeOnSingleRecordFiles()
    {
        var mft = SyntheticMft.CreateStandard(Layout, includeAttributeListFiles: false);
        var direct = Scan(new ScanOptions(@"C:\", ScanMode.Mft), mft.CreateVolume(4096, Layout, 64));
        var enumerationVolume = mft.CreateVolume(4096, Layout, 64);
        foreach (var (reference, record) in mft.UsnRecords())
        {
            enumerationVolume.UsnRecords[reference] = record;
        }

        var enumerated = Scan(new ScanOptions(@"C:\", ScanMode.Mft, UseDirectMftRead: false), enumerationVolume);

        Assert.Equal(enumerated.Root.FileCount, direct.Root.FileCount);
        Assert.Equal(enumerated.Root.DirectoryCount, direct.Root.DirectoryCount);
        Assert.Equal(enumerated.Root.LogicalSizeBytes, direct.Root.LogicalSizeBytes);
        Assert.Equal(enumerated.Root.AllocatedSizeBytes, direct.Root.AllocatedSizeBytes);
        Assert.Equal(
            enumerated.Root.Flatten().Select(entry => entry.FullPath).Order(StringComparer.Ordinal),
            direct.Root.Flatten().Select(entry => entry.FullPath).Order(StringComparer.Ordinal));
        Assert.Equal(
            enumerated.SkippedEntries.OrderBy(entry => entry.Path, StringComparer.Ordinal),
            direct.SkippedEntries.OrderBy(entry => entry.Path, StringComparer.Ordinal));
    }

    [Fact]
    public void ScanReturnsARequestedSubfolder()
    {
        var volume = SyntheticMft.CreateStandard(Layout).CreateVolume(4096, Layout, 64);

        var result = Scan(new ScanOptions(@"C:\Users\Strife", ScanMode.Mft), volume);

        Assert.Equal(@"C:\Users\Strife", result.Root.FullPath);
        Assert.Equal(5, result.Root.FileCount);
        Assert.Equal(5_000_000 + 25 + 10 + 7_000_000 + 1_234, result.Root.LogicalSizeBytes);
        Assert.Equal([new SkippedEntry(@"C:\Users\Strife\cloud.bin", MftTreeBuilder.ReparsePointReason)], result.SkippedEntries);
    }

    [Fact]
    public void ScanDoesNotFallBackWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var volume = SyntheticMft.CreateStandard(Layout).CreateVolume(4096, Layout, 64);
        volume.OnRead = cancellation.Cancel;

        Assert.Throws<OperationCanceledException>(() =>
            MftScanProvider.Scan(new ScanOptions(@"C:\", ScanMode.Mft), volume, new ProgressThrottle(null), cancellation.Token, 4096));
        Assert.Equal(0, volume.EnumerateCalls);
    }

    private static ScanResult Scan(ScanOptions options, FakeNtfsVolume volume)
    {
        return MftScanProvider.Scan(options, volume, new ProgressThrottle(null), CancellationToken.None, 8192);
    }
}
