using Custodian.Core.Scanning;

namespace Custodian.Tests;

public sealed class MftDirectReaderTests
{
    // 44 one-kilobyte records in 4 KB clusters: 11 clusters in three extents, later VCNs at lower LCNs.
    private static readonly (long Clusters, long Lcn)[] Layout = [(4, 40), (3, 20), (4, 5)];

    private const ulong StrifeReference = (1UL << 48) | 25;

    [Fact]
    public void ReadBuildsOneRecordPerFileFromAFragmentedMft()
    {
        var volume = SyntheticMft.CreateStandard(Layout).CreateVolume(4096, Layout, 64);
        volume.RetrievalPointers!.Reverse();

        var result = Read(volume, chunkBytes: 8192);
        var records = result.Records.ToDictionary(record => record.FileName);

        Assert.Equal(
            [".", "Users", "Strife", "big.bin", "small.txt", "first.txt", "cloud.bin", "Junction", "inside.txt", "split.bin", "partial.bin", "$UsnJrnl", "top.txt"],
            result.Records.Select(record => record.FileName));
        Assert.Empty(volume.MisalignedReads);

        Assert.Equal(SyntheticMft.RootReference, records["."].FileReferenceNumber);
        Assert.True(records["."].IsRootRecord);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), records["."].LastWriteTime);
        Assert.Equal(FileAttributes.ReadOnly | FileAttributes.Directory, records["Users"].FileAttributes);
        Assert.Equal(FileAttributes.Directory, records["Strife"].FileAttributes);
        Assert.Null(records["Strife"].Size);

        Assert.Equal(new NtfsRecordSize(5_000_000, 5_001_216), records["big.bin"].Size);
        Assert.Equal(StrifeReference, records["big.bin"].ParentFileReferenceNumber);
        Assert.Equal(new NtfsRecordSize(25, 25), records["small.txt"].Size);
        Assert.Equal(StrifeReference, records["first.txt"].ParentFileReferenceNumber);
        Assert.True(records["cloud.bin"].IsReparsePoint);
        Assert.True(records["Junction"].IsReparsePoint);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), records["top.txt"].LastWriteTime);
        Assert.Equal(SyntheticMft.RootReference, records["top.txt"].ParentFileReferenceNumber);

        // Name and data from extension record 32; the later data extent in record 42 does not replace the size.
        Assert.Equal((3UL << 48) | 33, records["split.bin"].FileReferenceNumber);
        Assert.Equal(StrifeReference, records["split.bin"].ParentFileReferenceNumber);
        Assert.Equal(new NtfsRecordSize(7_000_000, 7_000_064), records["split.bin"].Size);

        // The base record's name wins over the extension record's second name.
        Assert.Equal(StrifeReference, records["partial.bin"].ParentFileReferenceNumber);
        Assert.Equal(new NtfsRecordSize(1_234, 4_096), records["partial.bin"].Size);
    }

    [Fact]
    public void ReadCountsRecordsByKind()
    {
        var volume = SyntheticMft.CreateStandard(Layout).CreateVolume(4096, Layout, 64);

        var statistics = Read(volume).Statistics;

        Assert.Equal("FSCTL_GET_RETRIEVAL_POINTERS", statistics.ExtentSource);
        Assert.Equal(3, statistics.ExtentCount);
        Assert.Equal(44, statistics.RecordSlots);
        Assert.Equal(44 * 1024, statistics.BytesRead);
        Assert.Equal(9, statistics.Files);
        Assert.Equal(4, statistics.Folders);
        Assert.Equal(4, statistics.ExtensionRecords);
        Assert.Equal(10, statistics.NotInUse);
        Assert.Equal(1, statistics.NotFileRecord);
        Assert.Equal(1, statistics.Torn);
        Assert.Equal(0, statistics.MalformedHeader);
        Assert.Equal(0, statistics.WrongRecordNumber);
        Assert.Equal(0, statistics.AttributesIncomplete);
        Assert.Equal(15, statistics.SystemRecordsLeftOut);
        Assert.Equal(44 * 1024 + 610, statistics.SystemBytesLeftOut);
        Assert.Equal(3, statistics.ExtensionsAttached);
        Assert.Equal(1, statistics.ExtensionsWithoutBase);
        Assert.Equal(0, statistics.ExtensionSequenceMismatch);
        Assert.Equal(1, statistics.NamesFromExtensions);
        Assert.Equal(2, statistics.DataFromExtensions);
        Assert.Equal(2, statistics.RecordsWithSeveralNames);
        Assert.Equal(0, statistics.RecordsWithoutName);
        Assert.Equal(0, statistics.FilesWithoutData);
        Assert.Contains(statistics.GetDiagnostics(), line => line.StartsWith("$MFT records: files 9; folders 4;", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadJoinsRecordsThatCrossClusterExtentAndChunkBoundaries()
    {
        // 512-byte clusters: 1 KB records cross every odd extent boundary, and 1.5 KB chunks split records.
        (long Clusters, long Lcn)[] layout = [(7, 300), (30, 100), (51, 200)];
        var volume = SyntheticMft.CreateStandard(layout).CreateVolume(512, layout, 400);

        var result = Read(volume, chunkBytes: 1536);

        Assert.Equal(13, result.Records.Count);
        Assert.Equal(9, result.Statistics.Files);
        Assert.Equal(1, result.Statistics.Torn);
        Assert.Equal(1, result.Statistics.NotFileRecord);
        Assert.Equal(new NtfsRecordSize(7_000_000, 7_000_064), result.Records.Single(record => record.FileName == "split.bin").Size);
        Assert.Empty(volume.MisalignedReads);
    }

    [Fact]
    public void ReadTakesExtentsFromRecordZeroWhenRetrievalPointersFail()
    {
        var volume = SyntheticMft.CreateStandard(Layout).CreateVolume(4096, Layout, 64);
        volume.RetrievalPointers = null;
        volume.RetrievalFailure = @"opening C:\$MFT failed with Win32 error 5";

        var result = Read(volume);

        Assert.Equal(13, result.Records.Count);
        Assert.Equal(3, result.Statistics.ExtentCount);
        Assert.Contains("$MFT record 0", result.Statistics.ExtentSource);
        Assert.Contains("Win32 error 5", result.Statistics.ExtentSource);
    }

    [Fact]
    public void ReadFollowsRecordZeroAttributeListToItsExtensionRecord()
    {
        var mft = SyntheticMft.CreateStandard(Layout);
        mft.Add(0)
            .StandardInformation(FileAttributes.Hidden | FileAttributes.System)
            .AttributeList(
                (NtfsFileRecordParser.AttributeTypeStandardInformation, 0, 0),
                (NtfsFileRecordParser.AttributeTypeFileName, 0, 0),
                (NtfsFileRecordParser.AttributeTypeData, 0, 0),
                (NtfsFileRecordParser.AttributeTypeData, 16, 4))
            .FileName(SyntheticMft.RootReference, "$MFT")
            .NonResidentData(44 * 1024, 44 * 1024, runs: MftRuns.Encode((4, 40)));
        mft.Add(16).ExtensionOf(1UL << 48)
            .NonResidentData(44 * 1024, 44 * 1024, lowestVcn: 4, runs: MftRuns.Encode((3, 20), (4, 5)));
        var volume = mft.CreateVolume(4096, Layout, 64);
        volume.RetrievalPointers = [];

        var result = Read(volume);

        Assert.Equal(13, result.Records.Count);
        Assert.Equal(3, result.Statistics.ExtentCount);
        Assert.Equal(5, result.Statistics.ExtensionRecords);
    }

    [Fact]
    public void ReadRejectsExtentsThatDoNotStartAtTheMftRecord()
    {
        var mft = SyntheticMft.CreateStandard(Layout);
        mft.Add(0).StandardInformation(FileAttributes.System).FileName(SyntheticMft.RootReference, "$Bogus");
        var volume = mft.CreateVolume(4096, Layout, 64);

        var error = Assert.Throws<MftDirectReadException>(() => Read(volume));

        Assert.Contains("not the $MFT's own record", error.Message);
        Assert.Equal(4 * 4096, error.Statistics!.BytesRead);
    }

    [Fact]
    public void ReadRejectsAnMftWithFarTooFewFileRecords()
    {
        var mft = new SyntheticMft(44);
        mft.Add(0).StandardInformation(FileAttributes.System).FileName(SyntheticMft.RootReference, "$MFT");
        mft.Add(5).Sequence(5).Directory().StandardInformation(FileAttributes.System).FileName(SyntheticMft.RootReference, ".");
        var volume = mft.CreateVolume(4096, Layout, 64);

        var error = Assert.Throws<MftDirectReadException>(() => Read(volume));

        Assert.Contains("42 of 44 record slots", error.Message);
        Assert.Equal(42, error.Statistics!.NotFileRecord);
    }

    [Fact]
    public void ReadRejectsImplausibleVolumeData()
    {
        var volume = SyntheticMft.CreateStandard(Layout).CreateVolume(4096, Layout, 64);
        volume.VolumeData = volume.VolumeData with { BytesPerFileRecord = 1000 };

        var error = Assert.Throws<MftDirectReadException>(() => Read(volume));

        Assert.Contains("implausible: 1,000 bytes per file record", error.Message);
        Assert.Equal(0, volume.ReadCalls);
    }

    [Fact]
    public void ReadRejectsShortReads()
    {
        var volume = SyntheticMft.CreateStandard(Layout).CreateVolume(4096, Layout, 64);
        volume.ShortRead = 512;

        var error = Assert.Throws<MftDirectReadException>(() => Read(volume));

        Assert.Contains("returned 512", error.Message);
    }

    [Fact]
    public void ReadWrapsDeviceErrorsWithTheCountsSoFar()
    {
        var volume = SyntheticMft.CreateStandard(Layout).CreateVolume(4096, Layout, 64);
        volume.ReadFailure = new IOException("device gone");

        var error = Assert.Throws<MftDirectReadException>(() => Read(volume));

        Assert.Equal("device gone", error.Message);
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(1024, error.Statistics!.Geometry!.BytesPerFileRecord);
    }

    [Fact]
    public void ReadStopsBetweenChunksWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var volume = SyntheticMft.CreateStandard(Layout).CreateVolume(4096, Layout, 64);
        volume.OnRead = cancellation.Cancel;

        Assert.Throws<OperationCanceledException>(() =>
            MftDirectReader.Read(volume, new ProgressThrottle(null), cancellation.Token, chunkBytes: 4096));
        Assert.Equal(1, volume.ReadCalls);
    }

    [Fact]
    public void ReadSkipsRecordsStoredInTheWrongSlot()
    {
        var mft = SyntheticMft.CreateStandard(Layout);
        mft.Records[27].StoredNumber(99);
        var volume = mft.CreateVolume(4096, Layout, 64);

        var result = Read(volume);

        Assert.Equal(1, result.Statistics.WrongRecordNumber);
        Assert.DoesNotContain(result.Records, record => record.FileName == "small.txt");
    }

    [Fact]
    public void ReadIgnoresExtensionRecordsFromAnEarlierUseOfTheBaseRecord()
    {
        var mft = SyntheticMft.CreateStandard(Layout);
        mft.Add(35).ExtensionOf((7UL << 48) | 34)
            .NonResidentData(99_999, 102_400, runs: MftRuns.Encode((25, 950)));
        var volume = mft.CreateVolume(4096, Layout, 64);

        var result = Read(volume);

        Assert.Equal(1, result.Statistics.ExtensionSequenceMismatch);
        Assert.Equal(1, result.Statistics.FilesWithoutData);
        Assert.Null(result.Records.Single(record => record.FileName == "partial.bin").Size);
    }

    [Fact]
    public void ReadCountsInUseRecordsWithoutAName()
    {
        var mft = SyntheticMft.CreateStandard(Layout);
        mft.Add(43).StandardInformation(FileAttributes.Archive).ResidentData(5);
        var volume = mft.CreateVolume(4096, Layout, 64);

        var result = Read(volume);

        Assert.Equal(1, result.Statistics.RecordsWithoutName);
        Assert.Equal(13, result.Records.Count);
    }

    private static MftDirectReadResult Read(FakeNtfsVolume volume, int chunkBytes = MftDirectReader.DefaultChunkBytes)
    {
        return MftDirectReader.Read(volume, new ProgressThrottle(null), CancellationToken.None, chunkBytes);
    }
}
