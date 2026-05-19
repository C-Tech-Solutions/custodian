using System.Buffers.Binary;
using Custodian.Core.Scanning;

namespace Custodian.Tests;

public sealed class NtfsFileRecordParserTests
{
    [Fact]
    public void TryReadDefaultDataSizeReadsResidentDataAttribute()
    {
        var record = CreateBaseRecord();
        WriteResidentDataAttribute(record, 56, valueLength: 123);

        var ok = NtfsFileRecordParser.TryReadDefaultDataSize(record, out var size);

        Assert.True(ok);
        Assert.Equal(123, size.LogicalSize);
        Assert.Equal(123, size.AllocatedSize);
    }

    [Fact]
    public void TryReadDefaultDataSizeReadsNonResidentDataAttribute()
    {
        var record = CreateBaseRecord();
        WriteNonResidentDataAttribute(record, 56, logicalSize: 456_789, allocatedSize: 512_000);

        var ok = NtfsFileRecordParser.TryReadDefaultDataSize(record, out var size);

        Assert.True(ok);
        Assert.Equal(456_789, size.LogicalSize);
        Assert.Equal(512_000, size.AllocatedSize);
    }

    [Fact]
    public void TryReadDefaultDataSizeIgnoresNamedDataStreams()
    {
        var record = CreateBaseRecord();
        WriteResidentDataAttribute(record, 56, valueLength: 99, nameLength: 4);
        WriteEndAttribute(record, 80);

        var ok = NtfsFileRecordParser.TryReadDefaultDataSize(record, out _);

        Assert.False(ok);
    }

    private static byte[] CreateBaseRecord()
    {
        var record = new byte[1024];
        record[0] = (byte)'F';
        record[1] = (byte)'I';
        record[2] = (byte)'L';
        record[3] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4, 2), 0x30);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20, 2), 56);
        WriteEndAttribute(record, 56);
        return record;
    }

    private static void WriteResidentDataAttribute(byte[] record, int offset, uint valueLength, byte nameLength = 0)
    {
        var length = 24;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset, 4), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 4, 4), (uint)length);
        record[offset + 8] = 0;
        record[offset + 9] = nameLength;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 16, 4), valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 20, 2), 24);
        WriteEndAttribute(record, offset + length);
    }

    private static void WriteNonResidentDataAttribute(byte[] record, int offset, long logicalSize, long allocatedSize)
    {
        var length = 64;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset, 4), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 4, 4), (uint)length);
        record[offset + 8] = 1;
        record[offset + 9] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset + 40, 8), allocatedSize);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset + 48, 8), logicalSize);
        WriteEndAttribute(record, offset + length);
    }

    private static void WriteEndAttribute(byte[] record, int offset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset, 4), 0xffffffff);
    }
}
