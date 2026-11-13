using System.Buffers.Binary;
using Custodian.Core.Scanning;

namespace Custodian.Tests;

public sealed class MftRawRecordParserTests
{
    private const ulong ParentReference = (3UL << 48) | 40;

    [Fact]
    public void ParseReadsBaseRecordFields()
    {
        var lastWrite = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        var record = new MftRecordBuilder(77).Sequence(9)
            .StandardInformation(FileAttributes.Archive | FileAttributes.ReadOnly, lastWrite)
            .FileName(ParentReference, "report.docx")
            .NonResidentData(123_456, 126_976)
            .Build();

        var parsed = new MftRawRecord();
        var status = MftRawRecordParser.Parse(record, parsed);

        Assert.Equal(MftRawRecordStatus.InUse, status);
        Assert.False(parsed.IsExtension);
        Assert.False(parsed.IsDirectory);
        Assert.Equal((ushort)9, parsed.SequenceNumber);
        Assert.Equal(77u, parsed.StoredRecordNumber);
        Assert.Equal((uint)(FileAttributes.Archive | FileAttributes.ReadOnly), parsed.StandardAttributes);
        Assert.Equal(lastWrite.ToFileTimeUtc(), parsed.LastWriteFileTime);
        Assert.Equal("report.docx", parsed.FirstName);
        Assert.Equal(ParentReference, parsed.FirstNameParent);
        Assert.Equal(1, parsed.NameCount);
        Assert.True(parsed.HasData);
        Assert.Equal(new NtfsRecordSize(123_456, 126_976), parsed.DataSize);
        Assert.False(parsed.HasAttributeList);
        Assert.False(parsed.AttributesIncomplete);
    }

    [Fact]
    public void ParseRestoresBytesProtectedByTheUpdateSequenceArray()
    {
        // A long name puts name characters on the last two bytes of the first 512-byte stride, which hold
        // the update sequence number on disk.
        var name = new string('n', 200) + ".txt";
        var record = new MftRecordBuilder(30)
            .StandardInformation(FileAttributes.Archive)
            .FileName(ParentReference, name)
            .ResidentData(300)
            .Build();
        Assert.Equal(MftRecordBuilder.UpdateSequenceNumber, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510)));

        var parsed = new MftRawRecord();

        Assert.Equal(MftRawRecordStatus.InUse, MftRawRecordParser.Parse(record, parsed));
        Assert.Equal(name, parsed.FirstName);
        Assert.Equal(new NtfsRecordSize(300, 300), parsed.DataSize);
    }

    [Fact]
    public void ParseReadsFourKilobyteRecords()
    {
        var record = new MftRecordBuilder(30)
            .StandardInformation(FileAttributes.Archive)
            .FileName(ParentReference, new string('x', 250))
            .FileName(ParentReference, new string('y', 250))
            .FileName(ParentReference, new string('z', 250))
            .NonResidentData(5, 4096)
            .Build(recordSize: 4096);

        var parsed = new MftRawRecord();

        Assert.Equal(MftRawRecordStatus.InUse, MftRawRecordParser.Parse(record, parsed));
        Assert.Equal(3, parsed.NameCount);
        Assert.Equal(new NtfsRecordSize(5, 4096), parsed.DataSize);
    }

    [Fact]
    public void ParseReportsTornRecordWhenASectorTailDoesNotMatch()
    {
        var record = new MftRecordBuilder(30).StandardInformation(FileAttributes.Archive).FileName(ParentReference, "a.txt").Build();
        record[1022] ^= 0xff;

        Assert.Equal(MftRawRecordStatus.Torn, MftRawRecordParser.Parse(record, new MftRawRecord()));
    }

    [Fact]
    public void ParseReportsSlotsWithoutFileSignature()
    {
        var zeros = new byte[1024];
        var baad = new MftRecordBuilder(30).StandardInformation(FileAttributes.Archive).Build();
        "BAAD"u8.CopyTo(baad);

        Assert.Equal(MftRawRecordStatus.NotFileRecord, MftRawRecordParser.Parse(zeros, new MftRawRecord()));
        Assert.Equal(MftRawRecordStatus.NotFileRecord, MftRawRecordParser.Parse(baad, new MftRawRecord()));
    }

    [Fact]
    public void ParseReportsRecordsNotInUse()
    {
        var record = new MftRecordBuilder(30).NotInUse().StandardInformation(FileAttributes.Archive).FileName(ParentReference, "gone.txt").Build();
        var parsed = new MftRawRecord();

        Assert.Equal(MftRawRecordStatus.NotInUse, MftRawRecordParser.Parse(record, parsed));
        Assert.Null(parsed.FirstName);
    }

    [Fact]
    public void ParseReportsBadUpdateSequenceArray()
    {
        var record = new MftRecordBuilder(30).StandardInformation(FileAttributes.Archive).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 9);

        Assert.Equal(MftRawRecordStatus.MalformedHeader, MftRawRecordParser.Parse(record, new MftRawRecord()));
    }

    [Fact]
    public void ParseReportsBytesInUseBeyondTheRecord()
    {
        var record = new MftRecordBuilder(30).StandardInformation(FileAttributes.Archive).Build();
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x18), 2048);

        Assert.Equal(MftRawRecordStatus.MalformedHeader, MftRawRecordParser.Parse(record, new MftRawRecord()));
    }

    [Fact]
    public void ParseReadsExtensionRecords()
    {
        const ulong baseReference = (4UL << 48) | 900;
        var record = new MftRecordBuilder(901).ExtensionOf(baseReference)
            .FileName(ParentReference, "moved.bin")
            .NonResidentData(42_000, 45_056)
            .Build();

        var parsed = new MftRawRecord();

        Assert.Equal(MftRawRecordStatus.InUse, MftRawRecordParser.Parse(record, parsed));
        Assert.True(parsed.IsExtension);
        Assert.Equal(baseReference, parsed.BaseRecordReference);
        Assert.False(parsed.HasStandardInformation);
        Assert.Equal("moved.bin", parsed.FirstName);
        Assert.Equal(new NtfsRecordSize(42_000, 45_056), parsed.DataSize);
    }

    [Fact]
    public void ParseSkipsDosAliasesAndKeepsTheFirstOtherName()
    {
        var record = new MftRecordBuilder(30)
            .StandardInformation(FileAttributes.Archive)
            .FileName(ParentReference, "LONGNA~1.TXT", nameSpace: 2)
            .FileName(ParentReference, "long name.txt", nameSpace: 1)
            .FileName((1UL << 48) | 50, "other link.txt", nameSpace: 0)
            .ResidentData(1)
            .Build();

        var parsed = new MftRawRecord();
        MftRawRecordParser.Parse(record, parsed);

        Assert.Equal("long name.txt", parsed.FirstName);
        Assert.Equal(ParentReference, parsed.FirstNameParent);
        Assert.Equal(2, parsed.NameCount);
    }

    [Fact]
    public void ParseReadsDirectoryAndReparseFlags()
    {
        var record = new MftRecordBuilder(30).Directory()
            .StandardInformation(FileAttributes.ReparsePoint | FileAttributes.Hidden)
            .FileName(ParentReference, "Junction")
            .Build();

        var parsed = new MftRawRecord();
        MftRawRecordParser.Parse(record, parsed);

        Assert.True(parsed.IsDirectory);
        Assert.Equal((uint)(FileAttributes.ReparsePoint | FileAttributes.Hidden), parsed.StandardAttributes);
    }

    [Fact]
    public void ParseUsesOnDiskSizeForSparseAndCompressedStreams()
    {
        var record = new MftRecordBuilder(30)
            .StandardInformation(FileAttributes.SparseFile)
            .FileName(ParentReference, "sparse.vhdx")
            .NonResidentData(10_000_000, 10_002_432, onDiskSize: 65_536)
            .Build();

        var parsed = new MftRawRecord();
        MftRawRecordParser.Parse(record, parsed);

        Assert.Equal(new NtfsRecordSize(10_000_000, 65_536), parsed.DataSize);
    }

    [Fact]
    public void ParseIgnoresNamedStreamsAndLaterExtents()
    {
        var record = new MftRecordBuilder(30)
            .StandardInformation(FileAttributes.Archive)
            .FileName(ParentReference, "a.bin")
            .ResidentData(26, streamName: "Zone.Identifier")
            .NonResidentData(1_000, 4_096, lowestVcn: 7)
            .Build();

        var parsed = new MftRawRecord();
        MftRawRecordParser.Parse(record, parsed);

        Assert.False(parsed.HasData);
        Assert.False(parsed.AttributesIncomplete);
    }

    [Fact]
    public void ParseFlagsAttributesThatRunPastTheRecord()
    {
        var broken = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(broken, NtfsFileRecordParser.AttributeTypeData);
        BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(4), 4000);
        var record = new MftRecordBuilder(30)
            .StandardInformation(FileAttributes.Archive)
            .FileName(ParentReference, "a.bin")
            .RawAttribute(broken)
            .Build();

        var parsed = new MftRawRecord();

        Assert.Equal(MftRawRecordStatus.InUse, MftRawRecordParser.Parse(record, parsed));
        Assert.True(parsed.AttributesIncomplete);
        Assert.Equal("a.bin", parsed.FirstName);
        Assert.False(parsed.HasData);
    }

    [Fact]
    public void ParseAcceptsEndMarkerInTheLastBytesInUse()
    {
        var record = new MftRecordBuilder(30).StandardInformation(FileAttributes.Archive).FileName(ParentReference, "a.bin").Build();
        var endOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x18)) - 8;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x18), (uint)endOffset + 4);

        var parsed = new MftRawRecord();
        MftRawRecordParser.Parse(record, parsed);

        Assert.False(parsed.AttributesIncomplete);
        Assert.Equal("a.bin", parsed.FirstName);
    }

    [Fact]
    public void ParseResetsTheReusedResult()
    {
        var parsed = new MftRawRecord();
        MftRawRecordParser.Parse(
            new MftRecordBuilder(30).StandardInformation(FileAttributes.Archive).FileName(ParentReference, "a.bin").ResidentData(4).Build(),
            parsed);

        MftRawRecordParser.Parse(new MftRecordBuilder(31).StandardInformation(FileAttributes.Archive).Build(), parsed);

        Assert.Null(parsed.FirstName);
        Assert.False(parsed.HasData);
        Assert.Equal(0, parsed.NameCount);
    }
}
