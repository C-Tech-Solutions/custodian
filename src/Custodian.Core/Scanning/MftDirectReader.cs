using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Custodian.Core.Scanning;

/// <summary>Why a direct <c>$MFT</c> read was abandoned, with what it had counted so far.</summary>
internal sealed class MftDirectReadException : Exception
{
    public MftDirectReadException(string message, MftDirectReadStatistics? statistics = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Statistics = statistics;
    }

    public MftDirectReadStatistics? Statistics { get; }
}

internal sealed record MftDirectReadResult(IReadOnlyCollection<NtfsFileRecord> Records, MftDirectReadStatistics Statistics);

/// <summary>
/// Reads the <c>$MFT</c> straight from the volume device and parses its records in memory, in place of
/// <c>FSCTL_ENUM_USN_DATA</c> plus one <c>FSCTL_GET_NTFS_FILE_RECORD</c> per file. Only the <c>$MFT</c>'s own
/// clusters are read, in order and a chunk at a time, and nothing is written.
/// </summary>
internal static class MftDirectReader
{
    public const int DefaultChunkBytes = 8 * 1024 * 1024;

    // NTFS_FILE_RECORD_OUTPUT_BUFFER: FileReferenceNumber (8 bytes), FileRecordLength (4 bytes), record.
    private const int RecordOutputHeaderLength = 12;
    private const int AttributeListEntryMinimumLength = 0x1a;
    private const string MftFileName = "$MFT";
    private const int MaxRecordsInWrongSlot = 3;

    // Volume handles are not cached, so reads follow the unbuffered I/O rules: offsets, lengths and the
    // buffer's address are multiples of the sector size.
    private const int MinimumBufferAlignment = 4096;

    public static MftDirectReadResult Read(
        INtfsVolume volume,
        ProgressThrottle progress,
        CancellationToken cancellationToken,
        int chunkBytes = DefaultChunkBytes)
    {
        var statistics = new MftDirectReadStatistics();
        try
        {
            return ReadCore(volume, progress, statistics, chunkBytes, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MftDirectReadException)
        {
            throw new MftDirectReadException(ex.Message, statistics, ex);
        }
    }

    private static MftDirectReadResult ReadCore(
        INtfsVolume volume,
        ProgressThrottle progress,
        MftDirectReadStatistics statistics,
        int chunkBytes,
        CancellationToken cancellationToken)
    {
        var geometry = volume.GetVolumeData();
        statistics.Geometry = geometry;
        if (MftReadPlan.CheckGeometry(geometry) is { } implausible)
        {
            throw new MftDirectReadException($"the volume data is implausible: {implausible}", statistics);
        }

        var extents = volume.GetMftRetrievalPointers(out var retrievalFailure);
        if (extents is { Count: > 0 })
        {
            statistics.ExtentSource = "FSCTL_GET_RETRIEVAL_POINTERS";
        }
        else
        {
            statistics.ExtentSource = $"the data runs in $MFT record 0 ({retrievalFailure ?? "FSCTL_GET_RETRIEVAL_POINTERS returned no extents"})";
            extents = ReadExtentsFromRecordZero(volume, geometry, statistics);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var plan = MftReadPlan.Create(extents, geometry);
        statistics.ExtentCount = plan.Extents.Count;
        statistics.RecordSlots = plan.RecordSlots;

        var collector = new MftRecordCollector(statistics);
        StreamRecords(volume, geometry, plan, collector, statistics, progress, chunkBytes, cancellationToken);

        // An extent map that is off moves at least a cluster of records at once (4 with 4 KB clusters and
        // 1 KB records), while a lone corrupt record is just skipped. A few records in the wrong slot means the
        // map cannot be trusted, even though they are far below the unusable-slot limit.
        if (statistics.WrongRecordNumber > MaxRecordsInWrongSlot)
        {
            throw new MftDirectReadException(
                $"{statistics.WrongRecordNumber:n0} records sat in the wrong slot, so the $MFT's extent map is not trustworthy",
                statistics);
        }

        var unusable = statistics.NotFileRecord + statistics.Torn + statistics.MalformedHeader + statistics.WrongRecordNumber;
        if (unusable > plan.RecordSlots / 2)
        {
            throw new MftDirectReadException(
                $"{unusable:n0} of {plan.RecordSlots:n0} record slots did not hold a readable file record",
                statistics);
        }

        var parseWatch = Stopwatch.StartNew();
        var records = collector.Complete(new AttributeListReader(volume, geometry, cancellationToken).Read);
        parseWatch.Stop();
        statistics.ParseDuration += parseWatch.Elapsed;
        return new MftDirectReadResult(records, statistics);
    }

    private static void StreamRecords(
        INtfsVolume volume,
        NtfsVolumeData geometry,
        MftReadPlan plan,
        MftRecordCollector collector,
        MftDirectReadStatistics statistics,
        ProgressThrottle progress,
        int chunkBytes,
        CancellationToken cancellationToken)
    {
        var clusterSize = geometry.BytesPerCluster;
        var recordSize = geometry.BytesPerFileRecord;
        var chunkLength = Math.Max(clusterSize, chunkBytes / clusterSize * clusterSize);
        var buffer = AllocateAligned(chunkLength, Math.Max(MinimumBufferAlignment, geometry.BytesPerSector));

        // A record that crosses the end of a chunk (or of an extent) is put together here.
        var partial = new byte[recordSize];
        var partialLength = 0;
        var raw = new MftRawRecord();
        long slot = 0;
        long consumed = 0;
        var readWatch = new Stopwatch();
        var parseWatch = new Stopwatch();

        foreach (var extent in plan.Extents)
        {
            var extentOffset = extent.Lcn * clusterSize;
            var extentLength = extent.Clusters * clusterSize;
            for (long position = 0; position < extentLength && consumed < plan.BytesToRead; )
            {
                cancellationToken.ThrowIfCancellationRequested();

                var requested = (int)Math.Min(chunkLength, extentLength - position);
                var usable = (int)Math.Min(requested, plan.BytesToRead - consumed);
                readWatch.Start();
                var read = volume.Read(extentOffset + position, buffer.Span[..requested]);
                readWatch.Stop();
                if (read < usable)
                {
                    throw new MftDirectReadException(
                        $"reading {requested:n0} bytes at byte {extentOffset + position:n0} of {volume.VolumePath} returned {read:n0}",
                        statistics);
                }

                statistics.BytesRead += read;
                position += requested;
                consumed += usable;

                parseWatch.Start();
                var data = buffer.Span[..usable];
                var offset = 0;
                if (partialLength > 0)
                {
                    var take = Math.Min(recordSize - partialLength, usable);
                    data[..take].CopyTo(partial.AsSpan(partialLength));
                    partialLength += take;
                    offset = take;
                    if (partialLength == recordSize)
                    {
                        ProcessRecord(partial, slot++, raw, collector, statistics);
                        partialLength = 0;
                    }
                }

                for (; offset + recordSize <= usable; offset += recordSize)
                {
                    ProcessRecord(data.Slice(offset, recordSize), slot++, raw, collector, statistics);
                }

                if (offset < usable)
                {
                    data[offset..].CopyTo(partial);
                    partialLength = usable - offset;
                }

                parseWatch.Stop();

                progress.Report(
                    volume.VolumePath,
                    statistics.Files,
                    statistics.Folders,
                    0,
                    $"Reading the MFT ({consumed * 100 / plan.BytesToRead}%)");
            }
        }

        statistics.ReadDuration = readWatch.Elapsed;
        statistics.ParseDuration = parseWatch.Elapsed;
    }

    private static void ProcessRecord(
        Span<byte> record,
        long slot,
        MftRawRecord raw,
        MftRecordCollector collector,
        MftDirectReadStatistics statistics)
    {
        var accepted = false;
        switch (MftRawRecordParser.Parse(record, raw))
        {
            case MftRawRecordStatus.NotFileRecord:
                statistics.NotFileRecord++;
                break;
            case MftRawRecordStatus.Torn:
                statistics.Torn++;
                break;
            case MftRawRecordStatus.MalformedHeader:
                statistics.MalformedHeader++;
                break;
            case MftRawRecordStatus.NotInUse:
                statistics.NotInUse++;
                break;
            default:
                // Each record stores its own number. One that does not match its slot means the extents
                // put the wrong part of the volume at this position.
                if (raw.StoredRecordNumber is { } stored && stored != (uint)slot)
                {
                    statistics.WrongRecordNumber++;
                    break;
                }

                if (raw.AttributesIncomplete)
                {
                    statistics.AttributesIncomplete++;
                }

                accepted = true;
                break;
        }

        if (slot == 0 && (!accepted || raw.IsExtension || raw.Name != MftFileName))
        {
            throw new MftDirectReadException(
                "the first record read is not the $MFT's own record, so the extents do not point at the $MFT",
                statistics);
        }

        if (accepted)
        {
            collector.Add(slot, raw);
        }
    }

    /// <summary>
    /// Finds the <c>$MFT</c>'s extents from the data runs of its own file record, for when
    /// <c>FSCTL_GET_RETRIEVAL_POINTERS</c> cannot be used. A heavily fragmented <c>$MFT</c> keeps later runs in
    /// extension records that its attribute list names.
    /// </summary>
    private static List<MftExtent> ReadExtentsFromRecordZero(INtfsVolume volume, NtfsVolumeData geometry, MftDirectReadStatistics statistics)
    {
        var output = new byte[RecordOutputHeaderLength + geometry.BytesPerFileRecord];
        var extents = new List<MftExtent>();
        var extensionSegments = new List<long>();

        ReadDataRuns(volume, 0, output, extents, extensionSegments, statistics);
        foreach (var segment in extensionSegments.Distinct())
        {
            ReadDataRuns(volume, segment, output, extents, null, statistics);
        }

        if (extents.Count == 0)
        {
            throw new MftDirectReadException("$MFT record 0 holds no data runs", statistics);
        }

        return extents;
    }

    private static void ReadDataRuns(
        INtfsVolume volume,
        long segment,
        byte[] output,
        List<MftExtent> extents,
        List<long>? extensionSegments,
        MftDirectReadStatistics statistics)
    {
        if (!volume.ReadFileRecord(segment, output, out var returned, out var error))
        {
            throw new MftDirectReadException($"reading $MFT record {segment} failed with Win32 error {error}", statistics);
        }

        if (returned < RecordOutputHeaderLength ||
            (long)(BinaryPrimitives.ReadUInt64LittleEndian(output) & MftRawRecordParser.SegmentMask) != segment)
        {
            throw new MftDirectReadException($"$MFT record {segment} could not be read", statistics);
        }

        var recordLength = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(8));
        if (recordLength <= 0x18 || recordLength > returned - RecordOutputHeaderLength)
        {
            throw new MftDirectReadException($"$MFT record {segment} came back {recordLength:n0} bytes long", statistics);
        }

        // NTFS returns the record with its update sequence fixups already applied.
        var record = output.AsSpan(RecordOutputHeaderLength, recordLength);
        var bytesInUse = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]), (uint)record.Length);
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        record = record[..bytesInUse];

        while (true)
        {
            switch (NtfsFileRecordParser.NextAttribute(record, ref offset, out var type, out var attribute))
            {
                case NtfsAttributeStep.End:
                    return;
                case NtfsAttributeStep.Malformed:
                    throw new MftDirectReadException($"the attributes of $MFT record {segment} do not parse", statistics);
            }

            if (type == NtfsFileRecordParser.AttributeTypeData && NtfsFileRecordParser.IsUnnamed(attribute))
            {
                if (!NtfsRunListDecoder.TryDecode(attribute, extents))
                {
                    throw new MftDirectReadException($"the data runs in $MFT record {segment} do not decode", statistics);
                }
            }
            else if (type == NtfsFileRecordParser.AttributeTypeAttributeList && extensionSegments is not null)
            {
                if (!NtfsFileRecordParser.TryGetResidentValue(attribute, out var list))
                {
                    throw new MftDirectReadException("the $MFT's attribute list is not resident in record 0", statistics);
                }

                ReadDataSegments(list, extensionSegments, statistics);
            }
        }
    }

    private static void ReadDataSegments(ReadOnlySpan<byte> attributeList, List<long> segments, MftDirectReadStatistics statistics)
    {
        // ATTRIBUTE_LIST_ENTRY: type (4), entry length (2), name length (1), name offset (1),
        // starting VCN (8), segment reference (8), attribute id (2), name.
        for (var offset = 0; offset + AttributeListEntryMinimumLength <= attributeList.Length; )
        {
            var entry = attributeList[offset..];
            var type = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]);
            if (length < AttributeListEntryMinimumLength || length > entry.Length)
            {
                throw new MftDirectReadException("the $MFT's attribute list does not parse", statistics);
            }

            var segment = (long)(BinaryPrimitives.ReadUInt64LittleEndian(entry[0x10..]) & MftRawRecordParser.SegmentMask);
            if (type == NtfsFileRecordParser.AttributeTypeData && entry[6] == 0 && segment != 0)
            {
                segments.Add(segment);
            }

            offset += length;
        }
    }

    private static Memory<byte> AllocateAligned(int length, int alignment)
    {
        var array = GC.AllocateUninitializedArray<byte>(length + alignment, pinned: true);
        var address = Marshal.UnsafeAddrOfPinnedArrayElement(array, 0).ToInt64();
        var padding = (int)((alignment - address % alignment) % alignment);
        return array.AsMemory(padding, length);
    }

    /// <summary>
    /// Reads an attribute list stored outside the <c>$MFT</c> from the volume, one list at a time, after the
    /// <c>$MFT</c> pass. Returns null when the extents do not fit the volume or a read fails, so the caller keeps
    /// the record-order name. Cancellation is checked before each list, as it is before each chunk of the pass.
    /// </summary>
    private sealed class AttributeListReader(INtfsVolume volume, NtfsVolumeData geometry, CancellationToken cancellationToken)
    {
        private Memory<byte> _buffer;

        public byte[]? Read(IReadOnlyList<MftExtent> extents, long length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (length <= 0 || length > MftAttributeList.MaximumLength)
            {
                return null;
            }

            var clusterSize = geometry.BytesPerCluster;
            if (_buffer.IsEmpty)
            {
                _buffer = AllocateAligned(
                    (MftAttributeList.MaximumLength / clusterSize + 1) * clusterSize,
                    Math.Max(MinimumBufferAlignment, geometry.BytesPerSector));
            }

            var list = new byte[length];
            long filled = 0;
            long vcn = 0;
            try
            {
                foreach (var extent in extents)
                {
                    if (filled == length)
                    {
                        break;
                    }

                    if (extent.Vcn != vcn || extent.Lcn < 0 || extent.Lcn > geometry.TotalClusters - extent.Clusters)
                    {
                        return null;
                    }

                    // Whole clusters, as unbuffered reads need, and no more than the list still lacks.
                    var take = (int)Math.Min(extent.Clusters * clusterSize, length - filled);
                    var bytes = (take + clusterSize - 1) / clusterSize * clusterSize;
                    if (volume.Read(extent.Lcn * clusterSize, _buffer.Span[..bytes]) < take)
                    {
                        return null;
                    }

                    _buffer.Span[..take].CopyTo(list.AsSpan((int)filled));
                    filled += take;
                    vcn += extent.Clusters;
                }
            }
            catch (IOException)
            {
                return null;
            }

            return filled == length ? list : null;
        }
    }
}

/// <summary>What a direct <c>$MFT</c> read found, reported in the scan's diagnostics.</summary>
internal sealed class MftDirectReadStatistics
{
    public NtfsVolumeData? Geometry { get; set; }
    public string ExtentSource { get; set; } = "not looked up";
    public int ExtentCount { get; set; }
    public long RecordSlots { get; set; }
    public long BytesRead { get; set; }
    public TimeSpan ReadDuration { get; set; }
    public TimeSpan ParseDuration { get; set; }

    public long Files { get; set; }
    public long Folders { get; set; }
    public long ExtensionRecords { get; set; }
    public long NotInUse { get; set; }
    public long NotFileRecord { get; set; }
    public long Torn { get; set; }
    public long MalformedHeader { get; set; }
    public long WrongRecordNumber { get; set; }
    public long AttributesIncomplete { get; set; }

    public long SystemRecordsLeftOut { get; set; }
    public long SystemBytesLeftOut { get; set; }

    /// <summary>Base records from 16 up left out for header flag 0x4, NTFS's mark for its own files under <c>$Extend</c>.</summary>
    public long MetadataFilesLeftOut { get; set; }

    public long RecordsWithoutName { get; set; }
    public long RecordsWithSeveralNames { get; set; }

    /// <summary>Files and folders with several names, one of them Win32 (or Win32 and DOS).</summary>
    public long SeveralNamesWithWin32Name { get; set; }

    /// <summary>Files and folders named by a Win32 name that came after their first name.</summary>
    public long FirstNamePassedOver { get; set; }

    /// <summary>Files and folders with several names, no Win32 name and an attribute list, named in the list's order.</summary>
    public long NamesFromAttributeList { get; set; }

    /// <summary>Of <see cref="NamesFromAttributeList"/>, those whose name differs from the first one in record order.</summary>
    public long AttributeListChangedName { get; set; }

    /// <summary>Attribute lists stored outside the <c>$MFT</c> that were read from the volume.</summary>
    public long AttributeListsReadFromVolume { get; set; }

    public TimeSpan AttributeListReadDuration { get; set; }

    /// <summary>Attribute lists that could not be read or named none of the file's names, so record order was kept.</summary>
    public long AttributeListsNotUsed { get; set; }

    public long ExtensionsAttached { get; set; }
    public long NamesFromExtensions { get; set; }
    public long DataFromExtensions { get; set; }
    public long ExtensionsWithoutBase { get; set; }
    public long ExtensionSequenceMismatch { get; set; }
    public long FilesWithoutData { get; set; }

    public IReadOnlyList<string> GetDiagnostics()
    {
        var diagnostics = new List<string>();
        if (Geometry is { } geometry)
        {
            diagnostics.Add(
                $"Volume data: {geometry.BytesPerSector:n0}-byte sectors, {geometry.BytesPerCluster:n0}-byte clusters, " +
                $"{geometry.BytesPerFileRecord:n0}-byte file records; $MFT valid data {geometry.MftValidDataLength:n0} bytes, " +
                $"starting at cluster {geometry.MftStartLcn:n0}");
        }

        var seconds = ReadDuration.TotalSeconds;
        var rate = seconds > 0 ? BytesRead / seconds / (1024 * 1024) : 0;
        diagnostics.Add(
            $"$MFT read: {BytesRead:n0} bytes in {seconds:0.00} s ({rate:n0} MB/s), parsed in {ParseDuration.TotalSeconds:0.00} s; " +
            $"{ExtentCount:n0} extents from {ExtentSource}; {RecordSlots:n0} record slots");
        diagnostics.Add(
            $"$MFT records: files {Files:n0}; folders {Folders:n0}; extension {ExtensionRecords:n0}; not in use {NotInUse:n0}; " +
            $"not FILE {NotFileRecord:n0}; torn {Torn:n0}; bad header {MalformedHeader:n0}; wrong record number {WrongRecordNumber:n0}; " +
            $"attributes cut short {AttributesIncomplete:n0}; NTFS metadata files left out for header flag 0x4 {MetadataFilesLeftOut:n0}");
        diagnostics.Add(
            $"$MFT merge: NTFS system records left out {SystemRecordsLeftOut:n0} ({SystemBytesLeftOut:n0} bytes); " +
            $"in-use records without a name {RecordsWithoutName:n0}; " +
            $"extension records attached {ExtensionsAttached:n0}, with no matching base record {ExtensionsWithoutBase:n0}, " +
            $"from an older use of the base record {ExtensionSequenceMismatch:n0}; names from extension records {NamesFromExtensions:n0}; " +
            $"data sizes from extension records {DataFromExtensions:n0}; files without an unnamed $DATA {FilesWithoutData:n0}");
        diagnostics.Add(
            $"$MFT names: files and folders with several names {RecordsWithSeveralNames:n0}; of those, with a Win32 name " +
            $"{SeveralNamesWithWin32Name:n0}; named by a later Win32 name instead of the first name {FirstNamePassedOver:n0}; " +
            $"with no Win32 name, named in attribute-list order {NamesFromAttributeList:n0}, of those not the first name in " +
            $"record order {AttributeListChangedName:n0}; attribute lists read from outside the $MFT {AttributeListsReadFromVolume:n0} " +
            $"in {AttributeListReadDuration.TotalSeconds:0.00} s; lists that could not be read or used, so record order was kept " +
            $"{AttributeListsNotUsed:n0}");
        return diagnostics;
    }
}
