using System.Buffers.Binary;
using System.Text;

namespace Custodian.Core.Scanning;

internal enum MftRawRecordStatus
{
    InUse,
    NotInUse,

    /// <summary>The slot does not start with the <c>FILE</c> signature.</summary>
    NotFileRecord,

    /// <summary>A sector's last two bytes do not hold the update sequence number: a partial or interrupted write.</summary>
    Torn,

    /// <summary>The header's update sequence array or attribute bounds do not fit the record.</summary>
    MalformedHeader
}

/// <summary>
/// The fields of one raw file record that the direct <c>$MFT</c> read needs. One instance is reused for
/// every record, so nothing here survives the next <see cref="MftRawRecordParser.Parse"/> call except
/// the name string.
/// </summary>
internal sealed class MftRawRecord
{
    public ushort SequenceNumber { get; set; }

    /// <summary>Header offset 0x20. Zero in a base record; the owning base record's reference in an extension record.</summary>
    public ulong BaseRecordReference { get; set; }

    // Not masked to the segment: extension records of the $MFT itself point at segment 0.
    public bool IsExtension => BaseRecordReference != 0;

    public bool IsDirectory { get; set; }

    /// <summary>The record's own number from header offset 0x2C, present in NTFS 3.1 headers.</summary>
    public uint? StoredRecordNumber { get; set; }

    public bool HasAttributeList { get; set; }

    public bool HasStandardInformation { get; set; }

    public uint StandardAttributes { get; set; }

    public long LastWriteFileTime { get; set; }

    /// <summary>The first <c>$FILE_NAME</c> that is not a DOS 8.3 alias, in attribute order.</summary>
    public string? FirstName { get; set; }

    public ulong FirstNameParent { get; set; }

    /// <summary><c>$FILE_NAME</c> attributes other than DOS 8.3 aliases: one per hard link.</summary>
    public int NameCount { get; set; }

    /// <summary>True when this record holds the unnamed <c>$DATA</c> attribute's first extent (or the resident value).</summary>
    public bool HasData { get; set; }

    public NtfsRecordSize DataSize { get; set; }

    /// <summary>The attribute walk stopped at an attribute that did not fit; fields after it are missing.</summary>
    public bool AttributesIncomplete { get; set; }

    public void Reset()
    {
        SequenceNumber = 0;
        BaseRecordReference = 0;
        IsDirectory = false;
        StoredRecordNumber = null;
        HasAttributeList = false;
        HasStandardInformation = false;
        StandardAttributes = 0;
        LastWriteFileTime = 0;
        FirstName = null;
        FirstNameParent = 0;
        NameCount = 0;
        HasData = false;
        DataSize = default;
        AttributesIncomplete = false;
    }
}

/// <summary>
/// Parses file records as they sit on disk in the <c>$MFT</c>: the last two bytes of every 512-byte stride
/// hold the update sequence number, and the real bytes are in the update sequence array. This is the
/// opposite of what <c>FSCTL_GET_NTFS_FILE_RECORD</c> returns.
/// </summary>
internal static class MftRawRecordParser
{
    internal const ulong SegmentMask = 0x0000ffffffffffff;

    // NTFS protects every 512 bytes of a record, whatever the sector size.
    private const int UpdateSequenceStride = 512;
    private const ushort FlagInUse = 0x0001;
    private const ushort FlagDirectory = 0x0002;

    // Header fields run to 0x28 (the base record reference). The NTFS 3.1 header ends at 0x30 and carries
    // the record's own number at 0x2C; the update sequence array follows the header.
    private const int HeaderFieldsLength = 0x28;
    private const int Ntfs31HeaderLength = 0x30;

    private const byte DosNamespace = 2;
    private const int StandardInformationMinimumLength = 0x24;
    private const int FileNameMinimumLength = 0x42;

    /// <summary>
    /// Checks and applies the update sequence fixups to <paramref name="record"/> in place, then reads it into
    /// <paramref name="result"/>. Records that are not in use are reported but not read further.
    /// </summary>
    public static MftRawRecordStatus Parse(Span<byte> record, MftRawRecord result)
    {
        result.Reset();

        if (record.Length < Ntfs31HeaderLength ||
            record[0] != (byte)'F' ||
            record[1] != (byte)'I' ||
            record[2] != (byte)'L' ||
            record[3] != (byte)'E')
        {
            return MftRawRecordStatus.NotFileRecord;
        }

        var status = ApplyFixups(record);
        if (status != MftRawRecordStatus.InUse)
        {
            return status;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..0x18]);
        if ((flags & FlagInUse) == 0)
        {
            return MftRawRecordStatus.NotInUse;
        }

        var updateSequenceOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..6]);
        var updateSequenceCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..8]);
        var firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..0x16]);
        var bytesInUse = BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..0x1c]);
        if (firstAttributeOffset < updateSequenceOffset + updateSequenceCount * 2 ||
            bytesInUse > (uint)record.Length ||
            firstAttributeOffset + sizeof(uint) > bytesInUse)
        {
            return MftRawRecordStatus.MalformedHeader;
        }

        result.SequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(record[0x10..0x12]);
        result.IsDirectory = (flags & FlagDirectory) != 0;
        result.BaseRecordReference = BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..0x28]);
        if (updateSequenceOffset >= Ntfs31HeaderLength)
        {
            result.StoredRecordNumber = BinaryPrimitives.ReadUInt32LittleEndian(record[0x2c..0x30]);
        }

        ReadAttributes(record[..(int)bytesInUse], firstAttributeOffset, result);
        return MftRawRecordStatus.InUse;
    }

    private static MftRawRecordStatus ApplyFixups(Span<byte> record)
    {
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..6]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(record[6..8]);
        // One entry per 512-byte stride, plus the update sequence number: a shorter array would leave sectors unchecked.
        if (count != record.Length / UpdateSequenceStride + 1 ||
            offset < HeaderFieldsLength ||
            offset + count * 2 > record.Length)
        {
            return MftRawRecordStatus.MalformedHeader;
        }

        var updateSequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(offset, 2));
        for (var i = 1; i < count; i++)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(i * UpdateSequenceStride - 2, 2)) != updateSequenceNumber)
            {
                return MftRawRecordStatus.Torn;
            }
        }

        for (var i = 1; i < count; i++)
        {
            record.Slice(offset + i * 2, 2).CopyTo(record.Slice(i * UpdateSequenceStride - 2, 2));
        }

        return MftRawRecordStatus.InUse;
    }

    private static void ReadAttributes(ReadOnlySpan<byte> record, int offset, MftRawRecord result)
    {
        while (true)
        {
            switch (NtfsFileRecordParser.NextAttribute(record, ref offset, out var type, out var attribute))
            {
                case NtfsAttributeStep.End:
                    return;
                case NtfsAttributeStep.Malformed:
                    result.AttributesIncomplete = true;
                    return;
            }

            switch (type)
            {
                case NtfsFileRecordParser.AttributeTypeStandardInformation:
                    ReadStandardInformation(attribute, result);
                    break;
                case NtfsFileRecordParser.AttributeTypeAttributeList:
                    result.HasAttributeList = true;
                    break;
                case NtfsFileRecordParser.AttributeTypeFileName:
                    ReadFileName(attribute, result);
                    break;
                case NtfsFileRecordParser.AttributeTypeData when NtfsFileRecordParser.IsUnnamed(attribute) && !result.HasData:
                    ReadData(attribute, result);
                    break;
            }
        }
    }

    private static void ReadStandardInformation(ReadOnlySpan<byte> attribute, MftRawRecord result)
    {
        if (!NtfsFileRecordParser.TryGetResidentValue(attribute, out var value) || value.Length < StandardInformationMinimumLength)
        {
            result.AttributesIncomplete = true;
            return;
        }

        result.HasStandardInformation = true;
        result.LastWriteFileTime = BinaryPrimitives.ReadInt64LittleEndian(value[0x08..0x10]);
        result.StandardAttributes = BinaryPrimitives.ReadUInt32LittleEndian(value[0x20..0x24]);
    }

    private static void ReadFileName(ReadOnlySpan<byte> attribute, MftRawRecord result)
    {
        if (!NtfsFileRecordParser.TryGetResidentValue(attribute, out var value) || value.Length < FileNameMinimumLength)
        {
            result.AttributesIncomplete = true;
            return;
        }

        var nameBytes = value[0x40] * 2;
        if (FileNameMinimumLength + nameBytes > value.Length)
        {
            result.AttributesIncomplete = true;
            return;
        }

        // A DOS 8.3 alias is a second name for the same link, not a link of its own.
        if (value[0x41] == DosNamespace || nameBytes == 0)
        {
            return;
        }

        result.NameCount++;
        if (result.FirstName is null)
        {
            result.FirstName = Encoding.Unicode.GetString(value.Slice(FileNameMinimumLength, nameBytes));
            result.FirstNameParent = BinaryPrimitives.ReadUInt64LittleEndian(value[..8]);
        }
    }

    private static void ReadData(ReadOnlySpan<byte> attribute, MftRawRecord result)
    {
        switch (NtfsFileRecordParser.ReadDataAttributeSize(attribute, out var size))
        {
            case NtfsDataAttributeReadResult.Found:
                result.HasData = true;
                result.DataSize = size;
                break;
            case NtfsDataAttributeReadResult.Malformed:
                result.AttributesIncomplete = true;
                break;
        }
    }
}
