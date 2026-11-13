using System.Diagnostics;

namespace Custodian.Core.Scanning;

/// <summary>
/// Turns parsed <c>$MFT</c> records into one <see cref="NtfsFileRecord"/> per file. Records arrive in slot
/// order, and an extension record can come before or after its base record.
/// </summary>
/// <remarks>
/// A file is counted once, under one name and parent, the one <c>FSCTL_ENUM_USN_DATA</c> gives it; which link a
/// file is counted under decides which folder holds it. That is its first Win32 (or Win32 and DOS)
/// <c>$FILE_NAME</c>, looking at the base record in attribute order and then its extension records in ascending
/// record number. With no Win32 name it is the first <c>$FILE_NAME</c> that is not a DOS 8.3 alias, in the order of
/// the file's attribute list where it has one, and in the base record's attribute order where it has none.
/// <para>
/// Its size is the first extent of the unnamed <c>$DATA</c> attribute, from the base record or else the
/// lowest-numbered extension record that holds it. Extension records are tied to their file by the base record
/// reference in their header. An attribute list stored outside the <c>$MFT</c> is read from the volume only for
/// the files whose name depends on it.
/// </para>
/// </remarks>
internal sealed class MftRecordCollector
{
    // Records 0-15 are NTFS's own files ($MFT, $Bitmap, $Extend, ...), which the FSCTL_ENUM_USN_DATA path
    // does not list either. Record 5, the root folder, is kept.
    private const long FirstUserSegment = 16;
    private const long RootDirectorySegment = 5;

    // $STANDARD_INFORMATION flags that FSCTL_ENUM_USN_DATA does not report: 0x40000, which NTFS sets on a file with
    // extended attributes (winnt.h's FILE_ATTRIBUTE_EA), and the $FILE_NAME index flags 0x10000000 and 0x20000000.
    private const uint NonWin32AttributeFlags = 0x00040000 | 0x10000000 | 0x20000000;

    private readonly MftDirectReadStatistics _statistics;
    private readonly List<NtfsFileRecord?> _records = [];

    // Base records with an attribute list, and the extension records seen so far for any base record.
    private readonly Dictionary<long, PendingFile> _pending = [];

    // Base records left out for header flag 0x4; their extension records are left out with them.
    private readonly HashSet<long> _metadataFiles = [];

    public MftRecordCollector(MftDirectReadStatistics statistics)
    {
        _statistics = statistics;
    }

    public void Add(long segment, MftRawRecord record)
    {
        if (record.IsExtension)
        {
            AddExtension(segment, record);
            return;
        }

        if (segment < FirstUserSegment && segment != RootDirectorySegment)
        {
            _statistics.SystemRecordsLeftOut++;
            if (record.HasData)
            {
                _statistics.SystemBytesLeftOut += record.DataSize.LogicalSize;
            }

            return;
        }

        if (record.IsNtfsMetadataFile)
        {
            _statistics.MetadataFilesLeftOut++;
            _metadataFiles.Add(segment);
            _pending.Remove(segment);
            return;
        }

        var attributes = (FileAttributes)(record.StandardAttributes & ~NonWin32AttributeFlags);
        if (record.IsDirectory)
        {
            attributes |= FileAttributes.Directory;
        }
        else if (attributes == 0)
        {
            attributes = FileAttributes.Normal;
        }

        var file = new PendingFile
        {
            BaseSeen = true,
            Segment = segment,
            SequenceNumber = record.SequenceNumber,
            Attributes = attributes,
            LastWriteTime = ToLastWriteTime(record),
            Name = record.Name,
            Parent = record.NameParent,
            HasWin32Name = record.HasWin32Name,
            PassedOverFirstName = record.PassedOverFirstName,
            NameCount = record.NameCount,
            Size = record.HasData ? record.DataSize : null
        };

        if (!record.HasAttributeList)
        {
            _records.Add(Complete(file));
            return;
        }

        // A Win32 name in the base record decides the name, so the list's order is only kept without one.
        if (!record.HasWin32Name)
        {
            file.NameOrder = new NameOrder(
                [.. record.NameCandidates],
                record.AttributeListExtents.Count == 0 ? [.. record.AttributeListNames] : null,
                record.AttributeListExtents.Count > 0 ? [.. record.AttributeListExtents] : null,
                record.AttributeListLength,
                record.AttributeListMalformed);
        }

        if (_pending.TryGetValue(segment, out var existing))
        {
            file.Extensions = existing.Extensions;
        }

        file.Index = _records.Count;
        _records.Add(null);
        _pending[segment] = file;
    }

    /// <summary>
    /// Merges the extension records into their files and returns every file, in record order.
    /// <paramref name="readAttributeList"/> reads an attribute list stored outside the <c>$MFT</c> from its extents
    /// and byte length, or returns null when it cannot.
    /// </summary>
    public List<NtfsFileRecord> Complete(Func<IReadOnlyList<MftExtent>, long, byte[]?>? readAttributeList = null)
    {
        foreach (var file in _pending.Values)
        {
            if (!file.BaseSeen)
            {
                // The base record is not in use, could not be read, or has no attribute list, so these
                // extension records are left over from a file that no longer exists.
                _statistics.ExtensionsWithoutBase += file.Extensions?.Count ?? 0;
                continue;
            }

            // Records arrive in slot order, so the extension records are in ascending record number.
            var nameFromExtension = false;
            List<(long Segment, MftNameCandidate[] Names)>? extensionNames = null;
            foreach (var extension in file.Extensions ?? [])
            {
                if (extension.BaseSequenceNumber != file.SequenceNumber)
                {
                    _statistics.ExtensionSequenceMismatch++;
                    continue;
                }

                _statistics.ExtensionsAttached++;
                file.NameCount += extension.NameCount;

                // Same rule as within one record: the first name, unless a Win32 name comes later.
                if (extension.Name is not null && (file.Name is null || (!file.HasWin32Name && extension.HasWin32Name)))
                {
                    file.PassedOverFirstName = file.Name is not null || extension.PassedOverFirstName;
                    file.Name = extension.Name;
                    file.Parent = extension.Parent;
                    file.HasWin32Name = extension.HasWin32Name;
                    nameFromExtension = true;
                }

                if (extension.NameCandidates is { } names)
                {
                    (extensionNames ??= []).Add((extension.Segment, names));
                }

                if (file.Size is null && extension.Size is not null)
                {
                    file.Size = extension.Size;
                    _statistics.DataFromExtensions++;
                }
            }

            if (file.NameOrder is { } order && !file.HasWin32Name && file.NameCount > 1)
            {
                NameByAttributeList(file, order, extensionNames, readAttributeList, ref nameFromExtension);
            }

            if (nameFromExtension)
            {
                _statistics.NamesFromExtensions++;
            }

            _records[file.Index] = Complete(file);
        }

        _pending.Clear();
        var records = new List<NtfsFileRecord>(_records.Count);
        foreach (var record in _records)
        {
            if (record is not null)
            {
                records.Add(record);
            }
        }

        _records.Clear();
        return records;
    }

    /// <summary>
    /// Names a file with several names and no Win32 name by the first of them in its attribute list's order, as
    /// <c>FSCTL_ENUM_USN_DATA</c> does. Keeps the record-order name when the list cannot be read or names none of them.
    /// </summary>
    private void NameByAttributeList(
        PendingFile file,
        NameOrder order,
        List<(long Segment, MftNameCandidate[] Names)>? extensionNames,
        Func<IReadOnlyList<MftExtent>, long, byte[]?>? readAttributeList,
        ref bool nameFromExtension)
    {
        var entries = order.ListMalformed ? null : order.ListNames;
        if (order.ListExtents is { } extents && !order.ListMalformed)
        {
            _statistics.AttributeListsReadFromVolume++;
            var watch = Stopwatch.StartNew();
            var list = readAttributeList?.Invoke(extents, order.ListLength);
            _statistics.AttributeListReadDuration += watch.Elapsed;

            var parsed = new List<ulong>();
            entries = list is not null && MftAttributeList.TryReadFileNameEntries(list, parsed) ? [.. parsed] : null;
        }

        foreach (var entry in entries ?? [])
        {
            var segment = MftAttributeList.EntrySegment(entry);
            var instance = MftAttributeList.EntryInstance(entry);
            var names = segment == file.Segment ? order.BaseNames : extensionNames?.Find(part => part.Segment == segment).Names;

            // DOS aliases are not candidates, so an alias listed first is passed over.
            foreach (var candidate in names ?? [])
            {
                if (candidate.Instance != instance)
                {
                    continue;
                }

                _statistics.NamesFromAttributeList++;
                if (candidate.Name != file.Name || candidate.Parent != file.Parent)
                {
                    _statistics.AttributeListChangedName++;
                }

                file.Name = candidate.Name;
                file.Parent = candidate.Parent;
                nameFromExtension = segment != file.Segment;
                return;
            }
        }

        _statistics.AttributeListsNotUsed++;
    }

    private void AddExtension(long segment, MftRawRecord record)
    {
        _statistics.ExtensionRecords++;
        var baseSegment = (long)(record.BaseRecordReference & MftRawRecordParser.SegmentMask);
        if ((baseSegment < FirstUserSegment && baseSegment != RootDirectorySegment) || _metadataFiles.Contains(baseSegment))
        {
            return;
        }

        if (!_pending.TryGetValue(baseSegment, out var file))
        {
            file = new PendingFile { Segment = baseSegment };
            _pending[baseSegment] = file;
        }

        file.Extensions ??= [];
        file.Extensions.Add(new ExtensionPart(
            segment,
            (ushort)(record.BaseRecordReference >> 48),
            record.Name,
            record.NameParent,
            record.HasWin32Name,
            record.PassedOverFirstName,
            record.NameCount,
            record.NameCandidates.Count > 0 ? [.. record.NameCandidates] : null,
            record.HasData ? record.DataSize : null));
    }

    private NtfsFileRecord? Complete(PendingFile file)
    {
        if (file.Name is null)
        {
            _statistics.RecordsWithoutName++;
            return null;
        }

        var isDirectory = (file.Attributes & FileAttributes.Directory) != 0;
        if (isDirectory)
        {
            _statistics.Folders++;
        }
        else
        {
            _statistics.Files++;
            if (file.Size is null)
            {
                _statistics.FilesWithoutData++;
            }
        }

        if (file.NameCount > 1)
        {
            _statistics.RecordsWithSeveralNames++;
            if (file.HasWin32Name)
            {
                _statistics.SeveralNamesWithWin32Name++;
            }

            if (file.PassedOverFirstName)
            {
                _statistics.FirstNamePassedOver++;
            }
        }

        return new NtfsFileRecord(
            ((ulong)file.SequenceNumber << 48) | (ulong)file.Segment,
            file.Parent,
            file.Attributes,
            file.Name)
        {
            Size = isDirectory ? null : file.Size,
            LastWriteTime = file.LastWriteTime
        };
    }

    private static DateTimeOffset? ToLastWriteTime(MftRawRecord record)
    {
        if (!record.HasStandardInformation ||
            record.LastWriteFileTime <= 0 ||
            record.LastWriteFileTime > DateTime.MaxValue.ToFileTimeUtc())
        {
            return null;
        }

        return new DateTimeOffset(DateTime.FromFileTimeUtc(record.LastWriteFileTime), TimeSpan.Zero);
    }

    private sealed class PendingFile
    {
        public bool BaseSeen { get; init; }
        public long Segment { get; init; }
        public int Index { get; set; } = -1;
        public ushort SequenceNumber { get; init; }
        public FileAttributes Attributes { get; init; }
        public DateTimeOffset? LastWriteTime { get; init; }
        public string? Name { get; set; }
        public ulong Parent { get; set; }
        public bool HasWin32Name { get; set; }
        public bool PassedOverFirstName { get; set; }
        public int NameCount { get; set; }
        public NtfsRecordSize? Size { get; set; }
        public NameOrder? NameOrder { get; set; }
        public List<ExtensionPart>? Extensions { get; set; }
    }

    /// <summary>
    /// What a base record without a Win32 name keeps to order the file's names by its attribute list: the names in
    /// the base record, and the list's <c>$FILE_NAME</c> entries, or the list's extents when it is stored outside the record.
    /// </summary>
    private sealed record NameOrder(
        MftNameCandidate[] BaseNames,
        ulong[]? ListNames,
        MftExtent[]? ListExtents,
        long ListLength,
        bool ListMalformed);

    private sealed record ExtensionPart(
        long Segment,
        ushort BaseSequenceNumber,
        string? Name,
        ulong Parent,
        bool HasWin32Name,
        bool PassedOverFirstName,
        int NameCount,
        MftNameCandidate[]? NameCandidates,
        NtfsRecordSize? Size);
}
