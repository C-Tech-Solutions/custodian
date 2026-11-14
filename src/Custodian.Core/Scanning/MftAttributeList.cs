using System.Buffers.Binary;

namespace Custodian.Core.Scanning;

/// <summary>Reads the <c>$FILE_NAME</c> entries of an NTFS attribute list.</summary>
internal static class MftAttributeList
{
    /// <summary>An attribute list never grows past 256 KB.</summary>
    public const int MaximumLength = 256 * 1024;

    // ATTRIBUTE_LIST_ENTRY: type (4), entry length (2), name length (1), name offset (1), starting VCN (8),
    // segment reference (8), attribute instance id (2), name.
    private const int EntryMinimumLength = 0x1a;

    /// <summary>
    /// Appends each <c>$FILE_NAME</c> entry of <paramref name="list"/>, in list order, as
    /// <see cref="PackEntry"/> packs it. Returns false when an entry's length does not fit the list.
    /// </summary>
    public static bool TryReadFileNameEntries(ReadOnlySpan<byte> list, List<ulong> entries)
    {
        for (var offset = 0; offset + EntryMinimumLength <= list.Length; )
        {
            var entry = list[offset..];
            var length = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]);
            if (length < EntryMinimumLength || length > entry.Length)
            {
                return false;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) == NtfsFileRecordParser.AttributeTypeFileName)
            {
                var segment = (long)(BinaryPrimitives.ReadUInt64LittleEndian(entry[0x10..]) & MftRawRecordParser.SegmentMask);
                entries.Add(PackEntry(segment, BinaryPrimitives.ReadUInt16LittleEndian(entry[0x18..])));
            }

            offset += length;
        }

        return true;
    }

    /// <summary>The record's segment in the high 48 bits, the attribute's instance id in the low 16.</summary>
    public static ulong PackEntry(long segment, ushort instance) => ((ulong)segment << 16) | instance;

    public static long EntrySegment(ulong entry) => (long)(entry >> 16);

    public static ushort EntryInstance(ulong entry) => (ushort)entry;
}
