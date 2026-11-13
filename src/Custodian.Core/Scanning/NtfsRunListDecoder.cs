using System.Buffers.Binary;

namespace Custodian.Core.Scanning;

/// <summary>One contiguous range of a stream's clusters: VCN <see cref="Vcn"/> onwards sits at volume cluster <see cref="Lcn"/>.</summary>
internal readonly record struct MftExtent(long Vcn, long Clusters, long Lcn);

internal static class NtfsRunListDecoder
{
    private const int NonResidentHeaderLength = 0x40;

    /// <summary>
    /// Decodes the mapping pairs of one non-resident attribute into <paramref name="extents"/>. Each attribute's
    /// pairs start from LCN 0. Sparse runs are skipped, which leaves a VCN gap. Returns false when the pairs run
    /// past the attribute or a field is out of range.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> attribute, List<MftExtent> extents)
    {
        if (attribute.Length < NonResidentHeaderLength || !NtfsFileRecordParser.IsNonResident(attribute))
        {
            return false;
        }

        var vcn = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x10..0x18]);
        int position = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x20..0x22]);
        if (vcn < 0 || position >= attribute.Length)
        {
            return false;
        }

        long lcn = 0;
        while (position < attribute.Length)
        {
            var header = attribute[position++];
            if (header == 0)
            {
                return true;
            }

            var lengthSize = header & 0x0f;
            var offsetSize = header >> 4;
            if (lengthSize is 0 or > 8 || offsetSize > 8 || position + lengthSize + offsetSize > attribute.Length)
            {
                return false;
            }

            var length = ReadSigned(attribute.Slice(position, lengthSize));
            position += lengthSize;
            if (length <= 0)
            {
                return false;
            }

            if (offsetSize > 0)
            {
                lcn += ReadSigned(attribute.Slice(position, offsetSize));
                position += offsetSize;
                extents.Add(new MftExtent(vcn, length, lcn));
            }

            vcn += length;
        }

        return false;
    }

    private static long ReadSigned(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            value |= (long)bytes[i] << (8 * i);
        }

        if (bytes.Length < 8 && (bytes[^1] & 0x80) != 0)
        {
            value |= -1L << (8 * bytes.Length);
        }

        return value;
    }
}
