namespace Custodian.Core.Scanning;

/// <summary>The <c>$MFT</c> extents to read, in VCN order, trimmed to the record slots that hold valid data.</summary>
internal sealed record MftReadPlan(IReadOnlyList<MftExtent> Extents, long BytesToRead, long RecordSlots)
{
    private const int MinimumSectorSize = 512;
    private const int MaximumSectorSize = 64 * 1024;
    private const int MaximumClusterSize = 2 * 1024 * 1024;
    private const int MinimumRecordSize = 512;
    private const int MaximumRecordSize = 4096;

    // Records 0-15 are NTFS's own files. A $MFT holding fewer cannot belong to a working volume.
    private const int MinimumRecordSlots = 16;

    /// <summary>Returns why the volume data cannot be right, or null when it is plausible.</summary>
    public static string? CheckGeometry(NtfsVolumeData volume)
    {
        if (!IsPowerOfTwoBetween(volume.BytesPerSector, MinimumSectorSize, MaximumSectorSize))
        {
            return $"{volume.BytesPerSector:n0} bytes per sector";
        }

        if (!IsPowerOfTwoBetween(volume.BytesPerCluster, volume.BytesPerSector, MaximumClusterSize))
        {
            return $"{volume.BytesPerCluster:n0} bytes per cluster with {volume.BytesPerSector:n0}-byte sectors";
        }

        if (!IsPowerOfTwoBetween(volume.BytesPerFileRecord, MinimumRecordSize, MaximumRecordSize))
        {
            return $"{volume.BytesPerFileRecord:n0} bytes per file record";
        }

        if (volume.TotalClusters <= 0 || volume.MftStartLcn < 0 || volume.MftStartLcn >= volume.TotalClusters)
        {
            return $"the $MFT starts at cluster {volume.MftStartLcn:n0} of {volume.TotalClusters:n0}";
        }

        if (volume.MftValidDataLength < (long)MinimumRecordSlots * volume.BytesPerFileRecord ||
            volume.MftValidDataLength / volume.BytesPerCluster > volume.TotalClusters)
        {
            return $"an $MFT valid data length of {volume.MftValidDataLength:n0} bytes";
        }

        return null;
    }

    /// <summary>
    /// Orders <paramref name="extents"/> by VCN and keeps the clusters that hold the valid data length's whole
    /// records. Throws <see cref="MftDirectReadException"/> when they do not map that range onto the volume
    /// without gaps, or do not start where the volume data says the <c>$MFT</c> starts.
    /// </summary>
    public static MftReadPlan Create(IEnumerable<MftExtent> extents, NtfsVolumeData volume)
    {
        var recordSlots = volume.MftValidDataLength / volume.BytesPerFileRecord;
        var bytesToRead = recordSlots * volume.BytesPerFileRecord;
        var clustersNeeded = (bytesToRead + volume.BytesPerCluster - 1) / volume.BytesPerCluster;
        var ordered = extents.OrderBy(extent => extent.Vcn).ToList();

        if (ordered.Count == 0)
        {
            throw new MftDirectReadException("no $MFT extents were found");
        }

        if (ordered[0].Vcn != 0)
        {
            throw new MftDirectReadException($"the first $MFT extent starts at VCN {ordered[0].Vcn:n0}, not 0");
        }

        if (ordered[0].Lcn != volume.MftStartLcn)
        {
            throw new MftDirectReadException(
                $"the first $MFT extent starts at cluster {ordered[0].Lcn:n0}, but the volume data says the $MFT starts at cluster {volume.MftStartLcn:n0}");
        }

        var planned = new List<MftExtent>();
        long nextVcn = 0;
        foreach (var extent in ordered)
        {
            if (nextVcn >= clustersNeeded)
            {
                break;
            }

            if (extent.Vcn != nextVcn)
            {
                throw new MftDirectReadException($"the $MFT extents leave a gap at VCN {nextVcn:n0}");
            }

            if (extent.Clusters <= 0 ||
                extent.Lcn < 0 ||
                extent.Lcn > volume.TotalClusters - extent.Clusters)
            {
                throw new MftDirectReadException(
                    $"the $MFT extent at VCN {extent.Vcn:n0} ({extent.Clusters:n0} clusters at cluster {extent.Lcn:n0}) does not fit the volume's {volume.TotalClusters:n0} clusters");
            }

            var clusters = Math.Min(extent.Clusters, clustersNeeded - nextVcn);
            planned.Add(extent with { Clusters = clusters });
            nextVcn += clusters;
        }

        if (nextVcn < clustersNeeded)
        {
            throw new MftDirectReadException(
                $"the $MFT extents cover {nextVcn * volume.BytesPerCluster:n0} bytes, fewer than its valid data length of {volume.MftValidDataLength:n0} bytes");
        }

        return new MftReadPlan(planned, bytesToRead, recordSlots);
    }

    private static bool IsPowerOfTwoBetween(int value, int minimum, int maximum)
    {
        return value >= minimum && value <= maximum && (value & (value - 1)) == 0;
    }
}
