namespace Custodian.Core.Scanning;

/// <summary>
/// Turns parsed <c>$MFT</c> records into one <see cref="NtfsFileRecord"/> per file. Records arrive in slot
/// order, and an extension record can come before or after its base record.
/// </summary>
/// <remarks>
/// A file is counted once, under one name and parent: the first <c>$FILE_NAME</c> that is not a DOS 8.3 alias,
/// taken from the base record in attribute order, or else from its extension records in ascending record
/// number. Its size is the first extent of the unnamed <c>$DATA</c> attribute, from the base record or else
/// the lowest-numbered extension record that holds it. Extension records are tied to their file by the base
/// record reference in their header, so no attribute list is read.
/// </remarks>
internal sealed class MftRecordCollector
{
    // Records 0-15 are NTFS's own files ($MFT, $Bitmap, $Extend, ...), which the FSCTL_ENUM_USN_DATA path
    // does not list either. Record 5, the root folder, is kept.
    private const long FirstUserSegment = 16;
    private const long RootDirectorySegment = 5;

    // $STANDARD_INFORMATION can carry the $FILE_NAME index flags; they are not Win32 attributes.
    private const uint NonWin32AttributeFlags = 0x10000000 | 0x20000000;

    private readonly MftDirectReadStatistics _statistics;
    private readonly List<NtfsFileRecord?> _records = [];

    // Base records with an attribute list, and the extension records seen so far for any base record.
    private readonly Dictionary<long, PendingFile> _pending = [];

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
            Name = record.FirstName,
            Parent = record.FirstNameParent,
            NameCount = record.NameCount,
            Size = record.HasData ? record.DataSize : null
        };

        if (!record.HasAttributeList)
        {
            _records.Add(Complete(file));
            return;
        }

        if (_pending.TryGetValue(segment, out var existing))
        {
            file.Extensions = existing.Extensions;
        }

        file.Index = _records.Count;
        _records.Add(null);
        _pending[segment] = file;
    }

    /// <summary>Merges the extension records into their files and returns every file, in record order.</summary>
    public List<NtfsFileRecord> Complete()
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
            foreach (var extension in file.Extensions ?? [])
            {
                if (extension.BaseSequenceNumber != file.SequenceNumber)
                {
                    _statistics.ExtensionSequenceMismatch++;
                    continue;
                }

                _statistics.ExtensionsAttached++;
                file.NameCount += extension.NameCount;
                if (file.Name is null && extension.Name is not null)
                {
                    file.Name = extension.Name;
                    file.Parent = extension.Parent;
                    _statistics.NamesFromExtensions++;
                }

                if (file.Size is null && extension.Size is not null)
                {
                    file.Size = extension.Size;
                    _statistics.DataFromExtensions++;
                }
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

    private void AddExtension(long segment, MftRawRecord record)
    {
        _statistics.ExtensionRecords++;
        var baseSegment = (long)(record.BaseRecordReference & MftRawRecordParser.SegmentMask);
        if (baseSegment < FirstUserSegment && baseSegment != RootDirectorySegment)
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
            record.FirstName,
            record.FirstNameParent,
            record.NameCount,
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
        public int NameCount { get; set; }
        public NtfsRecordSize? Size { get; set; }
        public List<ExtensionPart>? Extensions { get; set; }
    }

    private sealed record ExtensionPart(
        long Segment,
        ushort BaseSequenceNumber,
        string? Name,
        ulong Parent,
        int NameCount,
        NtfsRecordSize? Size);
}
