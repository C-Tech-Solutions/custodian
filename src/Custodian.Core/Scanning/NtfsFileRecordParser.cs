using System.Buffers.Binary;

namespace Custodian.Core.Scanning;

internal enum NtfsDataSizeReadResult
{
    Found,
    Malformed,
    NotInBaseRecord
}

internal static class NtfsFileRecordParser
{
    private const uint AttributeTypeData = 0x80;
    private const uint AttributeTypeEnd = 0xffffffff;
    private const ushort AttributeFlagCompressed = 0x0001;
    private const ushort AttributeFlagSparse = 0x8000;
    private const int NonResidentHeaderLength = 0x40;
    private const int NonResidentCompressedHeaderLength = 0x48;

    /// <summary>
    /// Reads the size of the unnamed <c>$DATA</c> attribute from a file record in the form
    /// <c>FSCTL_GET_NTFS_FILE_RECORD</c> returns it: update sequence fixups already applied,
    /// so the last two bytes of each sector hold record data, not the update sequence number.
    /// </summary>
    public static NtfsDataSizeReadResult ReadDefaultDataSize(ReadOnlySpan<byte> record, out NtfsRecordSize size)
    {
        size = default;

        if (record.Length < 48 ||
            record[0] != (byte)'F' ||
            record[1] != (byte)'I' ||
            record[2] != (byte)'L' ||
            record[3] != (byte)'E')
        {
            return NtfsDataSizeReadResult.Malformed;
        }

        var firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[20..22]);
        if (firstAttributeOffset < 24 || firstAttributeOffset >= record.Length)
        {
            return NtfsDataSizeReadResult.Malformed;
        }

        var offset = (int)firstAttributeOffset;
        while (offset + 16 <= record.Length)
        {
            var attributeType = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(offset, 4));
            if (attributeType == AttributeTypeEnd)
            {
                // The base record holds no first extent of the unnamed $DATA attribute; it lives in
                // an extension record listed in $ATTRIBUTE_LIST.
                return NtfsDataSizeReadResult.NotInBaseRecord;
            }

            var attributeLength = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(offset + 4, 4));
            if (attributeLength < 16 || attributeLength > record.Length - offset)
            {
                return NtfsDataSizeReadResult.Malformed;
            }

            var attribute = record.Slice(offset, (int)attributeLength);
            var nonResident = attribute[8] != 0;
            var nameLength = attribute[9];
            if (attributeType == AttributeTypeData && nameLength == 0)
            {
                if (!nonResident)
                {
                    return TryReadResidentDataSize(attribute, out size)
                        ? NtfsDataSizeReadResult.Found
                        : NtfsDataSizeReadResult.Malformed;
                }

                if (attribute.Length < NonResidentHeaderLength)
                {
                    return NtfsDataSizeReadResult.Malformed;
                }

                // Sizes are only valid in the first extent (lowest VCN 0). A later extent in the base
                // record means the first one is in an extension record.
                if (BinaryPrimitives.ReadInt64LittleEndian(attribute[0x10..0x18]) == 0)
                {
                    return TryReadNonResidentDataSize(attribute, out size)
                        ? NtfsDataSizeReadResult.Found
                        : NtfsDataSizeReadResult.Malformed;
                }
            }

            offset += (int)attributeLength;
        }

        return NtfsDataSizeReadResult.Malformed;
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

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x0c..0x0e]);
        var compressedOrSparse = (flags & (AttributeFlagCompressed | AttributeFlagSparse)) != 0;
        if (compressedOrSparse && attribute.Length < NonResidentCompressedHeaderLength)
        {
            return false;
        }

        // 0x28 is the space allocated for the whole stream. For compressed and sparse streams the
        // clusters actually in use are at 0x40, which only exists in their longer header.
        var allocatedSize = BinaryPrimitives.ReadInt64LittleEndian(compressedOrSparse ? attribute[0x40..0x48] : attribute[0x28..0x30]);
        var fileSize = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..0x38]);
        if (allocatedSize < 0 || fileSize < 0)
        {
            return false;
        }

        size = new NtfsRecordSize(fileSize, allocatedSize);
        return true;
    }
}
