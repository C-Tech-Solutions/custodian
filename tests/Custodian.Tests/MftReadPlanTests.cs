using Custodian.Core.Scanning;

namespace Custodian.Tests;

public sealed class MftReadPlanTests
{
    private static readonly NtfsVolumeData Volume = new(
        TotalClusters: 10_000,
        BytesPerSector: 512,
        BytesPerCluster: 4096,
        BytesPerFileRecord: 1024,
        MftValidDataLength: 40 * 4096,
        MftStartLcn: 100);

    [Fact]
    public void DecodeReadsRunsWithSignedDeltasAndSkipsSparseRuns()
    {
        var attribute = new MftRecordBuilder(0)
            .NonResidentData(0, 0, lowestVcn: 4, runs: MftRuns.Encode((10, 5000), (3, null), (20, 300), (1, 70_000)))
            .Build(fixupsApplied: true)
            .AsSpan(0x38);
        var extents = new List<MftExtent>();

        Assert.True(NtfsRunListDecoder.TryDecode(attribute, extents));
        Assert.Equal(
            [new MftExtent(4, 10, 5000), new MftExtent(17, 20, 300), new MftExtent(37, 1, 70_000)],
            extents);
    }

    [Fact]
    public void DecodeRejectsRunsThatRunPastTheAttribute()
    {
        var attribute = new MftRecordBuilder(0)
            .NonResidentData(0, 0, runs: [0x88, 0x10])
            .Build(fixupsApplied: true)
            .AsSpan(0x38, 0x48);

        Assert.False(NtfsRunListDecoder.TryDecode(attribute, []));
    }

    [Fact]
    public void GeometryCheckAcceptsUsualVolumes()
    {
        Assert.Null(MftReadPlan.CheckGeometry(Volume));
        Assert.Null(MftReadPlan.CheckGeometry(Volume with { BytesPerSector = 4096, BytesPerFileRecord = 4096, BytesPerCluster = 65536 }));
    }

    [Theory]
    [InlineData(0, 4096, 1024, 100)]
    [InlineData(500, 4096, 1024, 100)]
    [InlineData(512, 256, 1024, 100)]
    [InlineData(512, 4096, 1000, 100)]
    [InlineData(512, 4096, 8192, 100)]
    [InlineData(512, 4096, 1024, 10_000)]
    [InlineData(512, 4096, 1024, -1)]
    public void GeometryCheckRejectsImplausibleValues(int sector, int cluster, int record, long mftStart)
    {
        var volume = Volume with { BytesPerSector = sector, BytesPerCluster = cluster, BytesPerFileRecord = record, MftStartLcn = mftStart };

        Assert.NotNull(MftReadPlan.CheckGeometry(volume));
    }

    [Fact]
    public void GeometryCheckRejectsTinyValidDataLength()
    {
        Assert.NotNull(MftReadPlan.CheckGeometry(Volume with { MftValidDataLength = 8 * 1024 }));
    }

    [Fact]
    public void CreateOrdersExtentsByVcnAndTrimsToValidData()
    {
        var plan = MftReadPlan.Create(
            [new MftExtent(30, 50, 7000), new MftExtent(0, 10, 100), new MftExtent(10, 20, 3000)],
            Volume);

        Assert.Equal([new MftExtent(0, 10, 100), new MftExtent(10, 20, 3000), new MftExtent(30, 10, 7000)], plan.Extents);
        Assert.Equal(40 * 4096, plan.BytesToRead);
        Assert.Equal(160, plan.RecordSlots);
    }

    [Fact]
    public void CreateReadsOnlyWholeRecordsOfTheValidData()
    {
        var plan = MftReadPlan.Create([new MftExtent(0, 64, 100)], Volume with { MftValidDataLength = 20 * 1024 + 300 });

        Assert.Equal(20 * 1024, plan.BytesToRead);
        Assert.Equal([new MftExtent(0, 5, 100)], plan.Extents);
    }

    [Fact]
    public void CreateRejectsGaps()
    {
        var error = Assert.Throws<MftDirectReadException>(() =>
            MftReadPlan.Create([new MftExtent(0, 10, 100), new MftExtent(12, 30, 3000)], Volume));

        Assert.Contains("gap at VCN 10", error.Message);
    }

    [Fact]
    public void CreateRejectsFirstExtentAwayFromTheMftStart()
    {
        var error = Assert.Throws<MftDirectReadException>(() => MftReadPlan.Create([new MftExtent(0, 40, 200)], Volume));

        Assert.Contains("says the $MFT starts at cluster 100", error.Message);
    }

    [Fact]
    public void CreateRejectsExtentsThatDoNotStartAtVcnZero()
    {
        Assert.Throws<MftDirectReadException>(() => MftReadPlan.Create([new MftExtent(1, 40, 100)], Volume));
    }

    [Fact]
    public void CreateRejectsTooFewClusters()
    {
        var error = Assert.Throws<MftDirectReadException>(() => MftReadPlan.Create([new MftExtent(0, 39, 100)], Volume));

        Assert.Contains("fewer than its valid data length", error.Message);
    }

    [Theory]
    [InlineData(9_990, 20)]
    [InlineData(-1, 20)]
    [InlineData(5_000, 0)]
    public void CreateRejectsExtentsOutsideTheVolume(long lcn, long clusters)
    {
        Assert.Throws<MftDirectReadException>(() =>
            MftReadPlan.Create([new MftExtent(0, 20, 100), new MftExtent(20, clusters, lcn)], Volume));
    }

    [Fact]
    public void CreateRejectsNoExtents()
    {
        Assert.Throws<MftDirectReadException>(() => MftReadPlan.Create([], Volume));
    }
}
