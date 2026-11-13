using System.Buffers.Binary;
using Custodian.Core.Scanning;

namespace Custodian.Tests;

public sealed class NtfsFileRecordParserTests
{
    [Fact]
    public void ReadDefaultDataSizeReadsResidentDataAttribute()
    {
        var record = CreateBaseRecord();
        WriteResidentDataAttribute(record, 56, valueLength: 123);

        var result = NtfsFileRecordParser.ReadDefaultDataSize(record, out var size);

        Assert.Equal(NtfsDataSizeReadResult.Found, result);
        Assert.Equal(123, size.LogicalSize);
        Assert.Equal(123, size.AllocatedSize);
    }

    [Fact]
    public void ReadDefaultDataSizeReadsNonResidentDataAttribute()
    {
        var record = CreateBaseRecord();
        WriteNonResidentDataAttribute(record, 56, logicalSize: 456_789, allocatedSize: 512_000);

        var result = NtfsFileRecordParser.ReadDefaultDataSize(record, out var size);

        Assert.Equal(NtfsDataSizeReadResult.Found, result);
        Assert.Equal(456_789, size.LogicalSize);
        Assert.Equal(512_000, size.AllocatedSize);
    }

    [Fact]
    public void ReadDefaultDataSizeReadsRecordWithUpdateSequenceFixupsAlreadyApplied()
    {
        // FSCTL_GET_NTFS_FILE_RECORD returns records with the fixups applied: the last two bytes of
        // each 512-byte sector hold the record's own data, not the update sequence number. Lay the
        // record out so the $DATA sizes straddle the first sector end and the end marker covers the
        // second.
        const ushort updateSequenceNumber = 0x0042;
        var record = CreateBaseRecord(updateSequenceCount: 3);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x30, 2), updateSequenceNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x32, 2), 0x1111);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x34, 2), 0x2222);
        WriteAttribute(record, 56, type: 0x10, length: 104);
        WriteAttribute(record, 160, type: 0x30, length: 300);
        WriteNonResidentDataAttribute(
            record,
            460,
            logicalSize: 5_000_000_000,
            allocatedSize: 5_000_003_584,
            length: 560);
        record.AsSpan(460 + 64, 560 - 64).Fill(0x5a);

        Assert.Equal(1020, 460 + 560);
        Assert.NotEqual(updateSequenceNumber, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510, 2)));
        Assert.NotEqual(updateSequenceNumber, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(1022, 2)));

        var result = NtfsFileRecordParser.ReadDefaultDataSize(record, out var size);

        Assert.Equal(NtfsDataSizeReadResult.Found, result);
        Assert.Equal(5_000_000_000, size.LogicalSize);
        Assert.Equal(5_000_003_584, size.AllocatedSize);
    }

    [Theory]
    [InlineData((ushort)0x0001)]
    [InlineData((ushort)0x8000)]
    public void ReadDefaultDataSizeReadsOnDiskSizeOfCompressedOrSparseData(ushort flags)
    {
        var record = CreateBaseRecord();
        WriteNonResidentDataAttribute(record, 56, logicalSize: 60_000, allocatedSize: 65_536, length: 80, flags: flags);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(56 + 0x40, 8), 16_384);

        var result = NtfsFileRecordParser.ReadDefaultDataSize(record, out var size);

        Assert.Equal(NtfsDataSizeReadResult.Found, result);
        Assert.Equal(60_000, size.LogicalSize);
        Assert.Equal(16_384, size.AllocatedSize);
    }

    [Fact]
    public void ReadDefaultDataSizeRejectsCompressedDataWithoutCompressedSizeField()
    {
        var record = CreateBaseRecord();
        WriteNonResidentDataAttribute(record, 56, logicalSize: 60_000, allocatedSize: 65_536, flags: 0x0001);

        var result = NtfsFileRecordParser.ReadDefaultDataSize(record, out _);

        Assert.Equal(NtfsDataSizeReadResult.Malformed, result);
    }

    [Fact]
    public void ReadDefaultDataSizeReportsNamedDataStreamOnlyAsNotInBaseRecord()
    {
        var record = CreateBaseRecord();
        WriteResidentDataAttribute(record, 56, valueLength: 99, nameLength: 4);
        WriteEndAttribute(record, 80);

        var result = NtfsFileRecordParser.ReadDefaultDataSize(record, out _);

        Assert.Equal(NtfsDataSizeReadResult.NotInBaseRecord, result);
    }

    [Fact]
    public void ReadDefaultDataSizeReportsLaterDataExtentAsNotInBaseRecord()
    {
        var record = CreateBaseRecord();
        WriteNonResidentDataAttribute(record, 56, logicalSize: 0, allocatedSize: 0);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(56 + 0x10, 8), 1_024);

        var result = NtfsFileRecordParser.ReadDefaultDataSize(record, out _);

        Assert.Equal(NtfsDataSizeReadResult.NotInBaseRecord, result);
    }

    [Fact]
    public void ReadDefaultDataSizeRejectsRecordWithoutFileSignature()
    {
        var record = CreateBaseRecord();
        WriteResidentDataAttribute(record, 56, valueLength: 123);
        "BAAD"u8.CopyTo(record);

        var result = NtfsFileRecordParser.ReadDefaultDataSize(record, out _);

        Assert.Equal(NtfsDataSizeReadResult.Malformed, result);
    }

    [Fact]
    public void ReadDefaultDataSizeRejectsAttributeRunningPastRecordEnd()
    {
        var record = CreateBaseRecord();
        WriteAttribute(record, 56, type: 0x10, length: 2_048);

        var result = NtfsFileRecordParser.ReadDefaultDataSize(record, out _);

        Assert.Equal(NtfsDataSizeReadResult.Malformed, result);
    }

    private static byte[] CreateBaseRecord(ushort updateSequenceCount = 1)
    {
        var record = new byte[1024];
        "FILE"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4, 2), 0x30);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6, 2), updateSequenceCount);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20, 2), 56);
        WriteEndAttribute(record, 56);
        return record;
    }

    private static void WriteAttribute(byte[] record, int offset, uint type, int length)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 4, 4), (uint)length);
        if (offset + length + 4 <= record.Length)
        {
            WriteEndAttribute(record, offset + length);
        }
    }

    private static void WriteResidentDataAttribute(byte[] record, int offset, uint valueLength, byte nameLength = 0)
    {
        var length = 24;
        WriteAttribute(record, offset, 0x80, length);
        record[offset + 8] = 0;
        record[offset + 9] = nameLength;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 16, 4), valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 20, 2), 24);
    }

    private static void WriteNonResidentDataAttribute(
        byte[] record,
        int offset,
        long logicalSize,
        long allocatedSize,
        int length = 64,
        ushort flags = 0)
    {
        WriteAttribute(record, offset, 0x80, length);
        record[offset + 8] = 1;
        record[offset + 9] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 0x0c, 2), flags);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset + 0x28, 8), allocatedSize);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset + 0x30, 8), logicalSize);
    }

    private static void WriteEndAttribute(byte[] record, int offset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset, 4), 0xffffffff);
    }
}
