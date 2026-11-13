using System.Buffers.Binary;

namespace Custodian.Core.Scanning;

internal enum NtfsDataSizeReadResult
{
    Found,
    Malformed,
    NotInBaseRecord
}

internal enum NtfsDataAttributeReadResult
{
    Found,
    Malformed,

    /// <summary>A later extent of a non-resident stream; its size fields are not valid.</summary>
    NotFirstExtent
}

internal enum NtfsAttributeStep
{
    Attribute,
    End,
    Malformed
}

internal static class NtfsFileRecordParser
{
    internal const uint AttributeTypeStandardInformation = 0x10;
    internal const uint AttributeTypeAttributeList = 0x20;
    internal const uint AttributeTypeFileName = 0x30;
    internal const uint AttributeTypeData = 0x80;
    private const uint AttributeTypeEnd = 0xffffffff;
    private const ushort AttributeFlagCompressed = 0x0001;
    private const ushort AttributeFlagSparse = 0x8000;
    private const int AttributeHeaderLength = 16;
    private const int ResidentHeaderLength = 24;
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
            if (attributeType == AttributeTypeData && attribute[9] == 0)
            {
                switch (ReadDataAttributeSize(attribute, out size))
                {
                    case NtfsDataAttributeReadResult.Found:
                        return NtfsDataSizeReadResult.Found;
                    case NtfsDataAttributeReadResult.Malformed:
                        return NtfsDataSizeReadResult.Malformed;
                }

                // A later extent in the base record means the first one is in an extension record.
            }

            offset += (int)attributeLength;
        }

        return NtfsDataSizeReadResult.Malformed;
    }

    /// <summary>
    /// Steps to the attribute at <paramref name="offset"/> in a record whose fixups are applied. On
    /// <see cref="NtfsAttributeStep.Attribute"/>, <paramref name="offset"/> moves past it. Only the four-byte
    /// end marker needs to fit in the record.
    /// </summary>
    public static NtfsAttributeStep NextAttribute(
        ReadOnlySpan<byte> record,
        ref int offset,
        out uint type,
        out ReadOnlySpan<byte> attribute)
    {
        type = 0;
        attribute = default;

        if (offset < 0 || offset > record.Length - sizeof(uint))
        {
            return NtfsAttributeStep.Malformed;
        }

        type = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(offset, 4));
        if (type == AttributeTypeEnd)
        {
            return NtfsAttributeStep.End;
        }

        if (offset > record.Length - AttributeHeaderLength)
        {
            return NtfsAttributeStep.Malformed;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(offset + 4, 4));
        if (length < AttributeHeaderLength || length > record.Length - offset)
        {
            return NtfsAttributeStep.Malformed;
        }

        attribute = record.Slice(offset, (int)length);
        offset += (int)length;
        return NtfsAttributeStep.Attribute;
    }

    public static bool IsNonResident(ReadOnlySpan<byte> attribute) => attribute[8] != 0;

    public static bool IsUnnamed(ReadOnlySpan<byte> attribute) => attribute[9] == 0;

    /// <summary>Returns the value of a resident attribute, or false when its bounds do not fit the attribute.</summary>
    public static bool TryGetResidentValue(ReadOnlySpan<byte> attribute, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (attribute.Length < ResidentHeaderLength || IsNonResident(attribute))
        {
            return false;
        }

        var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..0x14]);
        var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..0x16]);
        if (valueOffset > attribute.Length || valueLength > (uint)(attribute.Length - valueOffset))
        {
            return false;
        }

        value = attribute.Slice(valueOffset, (int)valueLength);
        return true;
    }

    /// <summary>
    /// Reads the logical and on-disk size of one <c>$DATA</c> attribute. Sizes are only valid in the
    /// first extent (lowest VCN 0) of a non-resident stream.
    /// </summary>
    public static NtfsDataAttributeReadResult ReadDataAttributeSize(ReadOnlySpan<byte> attribute, out NtfsRecordSize size)
    {
        size = default;

        if (!IsNonResident(attribute))
        {
            return TryReadResidentDataSize(attribute, out size)
                ? NtfsDataAttributeReadResult.Found
                : NtfsDataAttributeReadResult.Malformed;
        }

        if (attribute.Length < NonResidentHeaderLength)
        {
            return NtfsDataAttributeReadResult.Malformed;
        }

        if (BinaryPrimitives.ReadInt64LittleEndian(attribute[0x10..0x18]) != 0)
        {
            return NtfsDataAttributeReadResult.NotFirstExtent;
        }

        return TryReadNonResidentDataSize(attribute, out size)
            ? NtfsDataAttributeReadResult.Found
            : NtfsDataAttributeReadResult.Malformed;
    }

    private static bool TryReadResidentDataSize(ReadOnlySpan<byte> attribute, out NtfsRecordSize size)
    {
        size = default;
        if (!TryGetResidentValue(attribute, out var value))
        {
            return false;
        }

        size = new NtfsRecordSize(value.Length, value.Length);
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
