using System.Buffers.Binary;

namespace Custodian.Core.Scanning;

internal static class NtfsFileRecordParser
{
    private const uint AttributeTypeData = 0x80;
    private const uint AttributeTypeEnd = 0xffffffff;

    public static bool TryReadDefaultDataSize(ReadOnlySpan<byte> fileRecord, out NtfsRecordSize size)
    {
        size = default;

        if (fileRecord.Length < 48 ||
            fileRecord[0] != (byte)'F' ||
            fileRecord[1] != (byte)'I' ||
            fileRecord[2] != (byte)'L' ||
            fileRecord[3] != (byte)'E')
        {
            return false;
        }

        var record = fileRecord.ToArray();
        if (!ApplyUpdateSequenceFixups(record))
        {
            return false;
        }

        var firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20, 2));
        if (firstAttributeOffset < 24 || firstAttributeOffset >= record.Length)
        {
            return false;
        }

        var offset = (int)firstAttributeOffset;
        while (offset + 16 <= record.Length)
        {
            var attributeType = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset, 4));
            if (attributeType == AttributeTypeEnd)
            {
                break;
            }

            var attributeLength = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset + 4, 4));
            if (attributeLength < 16 || offset + attributeLength > record.Length)
            {
                return false;
            }

            var nonResident = record[offset + 8] != 0;
            var nameLength = record[offset + 9];
            if (attributeType == AttributeTypeData && nameLength == 0)
            {
                return nonResident
                    ? TryReadNonResidentDataSize(record.AsSpan(offset, (int)attributeLength), out size)
                    : TryReadResidentDataSize(record.AsSpan(offset, (int)attributeLength), out size);
            }

            offset += (int)attributeLength;
        }

        return false;
    }

    private static bool TryReadResidentDataSize(ReadOnlySpan<byte> attribute, out NtfsRecordSize size)
    {
        size = default;
        if (attribute.Length < 24)
        {
            return false;
        }

        var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(attribute[16..20]);
        size = new NtfsRecordSize(valueLength, valueLength);
        return true;
    }

    private static bool TryReadNonResidentDataSize(ReadOnlySpan<byte> attribute, out NtfsRecordSize size)
    {
        size = default;
        if (attribute.Length < 64)
        {
            return false;
        }

        var allocatedSize = BinaryPrimitives.ReadInt64LittleEndian(attribute[40..48]);
        var fileSize = BinaryPrimitives.ReadInt64LittleEndian(attribute[48..56]);
        if (allocatedSize < 0 || fileSize < 0)
        {
            return false;
        }

        size = new NtfsRecordSize(fileSize, allocatedSize);
        return true;
    }

    private static bool ApplyUpdateSequenceFixups(byte[] record)
    {
        var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4, 2));
        var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6, 2));
        if (usaCount <= 1)
        {
            return true;
        }

        if (usaOffset + (usaCount * 2) > record.Length)
        {
            return false;
        }

        var sectors = usaCount - 1;
        if (record.Length % sectors != 0)
        {
            return false;
        }

        var sectorSize = record.Length / sectors;
        if (sectorSize < 2)
        {
            return false;
        }

        var sequenceNumber = record.AsSpan(usaOffset, 2);
        for (var i = 1; i < usaCount; i++)
        {
            var sectorEnd = (i * sectorSize) - 2;
            if (sectorEnd < 0 || sectorEnd + 2 > record.Length)
            {
                return false;
            }

            if (!record.AsSpan(sectorEnd, 2).SequenceEqual(sequenceNumber))
            {
                return false;
            }

            record[sectorEnd] = record[usaOffset + (i * 2)];
            record[sectorEnd + 1] = record[usaOffset + (i * 2) + 1];
        }

        return true;
    }
}
