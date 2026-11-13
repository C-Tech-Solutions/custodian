using System.Buffers.Binary;
using System.Text;
using Custodian.Core.Scanning;

namespace Custodian.Tests;

/// <summary>Builds one NTFS file record, either as it sits in the <c>$MFT</c> or as FSCTL_GET_NTFS_FILE_RECORD returns it.</summary>
internal sealed class MftRecordBuilder
{
    public const ushort UpdateSequenceNumber = 0x0007;
    private const int UpdateSequenceOffset = 0x30;

    private readonly List<byte[]> _attributes = [];
    private ushort _sequence = 1;
    private bool _inUse = true;
    private bool _directory;
    private bool _metadataFile;
    private ulong _baseReference;
    private uint? _storedNumber;
    private ushort _nextAttributeId;

    public MftRecordBuilder(long segment)
    {
        Segment = segment;
    }

    public long Segment { get; }

    public ulong Reference => ((ulong)_sequence << 48) | (ulong)Segment;

    public bool IsInUse => _inUse;

    public bool IsDirectory => _directory;

    public bool IsExtension => _baseReference != 0;

    public bool IsNtfsMetadataFile => _metadataFile;

    public uint StandardAttributes { get; private set; }

    /// <summary>Names other than DOS aliases, in attribute order.</summary>
    public List<(ulong Parent, string Name)> Names { get; } = [];

    public MftRecordBuilder Sequence(ushort sequence)
    {
        _sequence = sequence;
        return this;
    }

    public MftRecordBuilder Directory()
    {
        _directory = true;
        return this;
    }

    public MftRecordBuilder NotInUse()
    {
        _inUse = false;
        return this;
    }

    /// <summary>Sets header flag 0x4, which NTFS puts on its own files under <c>$Extend</c>.</summary>
    public MftRecordBuilder NtfsMetadataFile()
    {
        _metadataFile = true;
        return this;
    }

    /// <summary>Gives the next attribute this instance id; the ones after it count up from there.</summary>
    public MftRecordBuilder AttributeId(ushort id)
    {
        _nextAttributeId = id;
        return this;
    }

    public MftRecordBuilder ExtensionOf(ulong baseReference)
    {
        _baseReference = baseReference;
        return this;
    }

    public MftRecordBuilder StoredNumber(uint number)
    {
        _storedNumber = number;
        return this;
    }

    public MftRecordBuilder StandardInformation(FileAttributes attributes, DateTime? lastWriteUtc = null)
    {
        StandardAttributes = (uint)attributes;
        var value = new byte[0x48];
        var time = (lastWriteUtc ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToFileTimeUtc();
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(0x00), time);
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(0x08), time);
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(0x10), time);
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(0x18), time);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x20), (uint)attributes);
        return AddResident(NtfsFileRecordParser.AttributeTypeStandardInformation, value);
    }

    public MftRecordBuilder FileName(ulong parent, string name, byte nameSpace = 1)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var value = new byte[0x42 + nameBytes.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(value, parent);
        value[0x40] = (byte)name.Length;
        value[0x41] = nameSpace;
        nameBytes.CopyTo(value, 0x42);
        if (nameSpace != 2)
        {
            Names.Add((parent, name));
        }

        return AddResident(NtfsFileRecordParser.AttributeTypeFileName, value);
    }

    public MftRecordBuilder ResidentData(int length, string? streamName = null)
    {
        return AddResident(NtfsFileRecordParser.AttributeTypeData, new byte[length], streamName);
    }

    public MftRecordBuilder NonResidentData(
        long dataSize,
        long allocatedSize,
        long lowestVcn = 0,
        long? onDiskSize = null,
        byte[]? runs = null,
        string? streamName = null)
    {
        var nameBytes = streamName is null ? [] : Encoding.Unicode.GetBytes(streamName);
        var headerLength = onDiskSize is null ? 0x40 : 0x48;
        var runOffset = Align8(headerLength + nameBytes.Length);
        runs ??= [0];
        var attribute = new byte[Align8(runOffset + runs.Length)];
        WriteAttributeHeader(attribute, NtfsFileRecordParser.AttributeTypeData, nonResident: true, nameBytes, headerLength);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x0c), (ushort)(onDiskSize is null ? 0 : 0x8000));
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x10), lowestVcn);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x18), lowestVcn);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x20), (ushort)runOffset);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x28), allocatedSize);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x30), dataSize);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x38), dataSize);
        if (onDiskSize is { } onDisk)
        {
            BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x40), onDisk);
        }

        runs.CopyTo(attribute, runOffset);
        _attributes.Add(attribute);
        return this;
    }

    public MftRecordBuilder AttributeList(params (uint Type, long Segment, long StartingVcn)[] entries)
    {
        return AddResident(
            NtfsFileRecordParser.AttributeTypeAttributeList,
            EncodeAttributeList([.. entries.Select(entry => (entry.Type, entry.Segment, entry.StartingVcn, (ushort)0))]));
    }

    /// <summary>A resident attribute list whose entries name the attribute instance each one points at.</summary>
    public MftRecordBuilder AttributeList(params (uint Type, long Segment, long StartingVcn, ushort Instance)[] entries)
    {
        return AddResident(NtfsFileRecordParser.AttributeTypeAttributeList, EncodeAttributeList(entries));
    }

    /// <summary>An attribute list stored outside the record: <paramref name="length"/> bytes in the clusters <paramref name="runs"/> maps.</summary>
    public MftRecordBuilder NonResidentAttributeList(long length, byte[] runs)
    {
        const int headerLength = 0x40;
        var attribute = new byte[Align8(headerLength + runs.Length)];
        WriteAttributeHeader(attribute, NtfsFileRecordParser.AttributeTypeAttributeList, nonResident: true, [], headerLength);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x20), headerLength);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x28), (length + 4095) / 4096 * 4096);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x30), length);
        BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(0x38), length);
        runs.CopyTo(attribute, headerLength);
        _attributes.Add(attribute);
        return this;
    }

    /// <summary>The bytes of an attribute list: one 32-byte entry per attribute, with no attribute names.</summary>
    public static byte[] EncodeAttributeList(params (uint Type, long Segment, long StartingVcn, ushort Instance)[] entries)
    {
        const int entryLength = 0x20;
        var value = new byte[entries.Length * entryLength];
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = value.AsSpan(i * entryLength, entryLength);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, entries[i].Type);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], entryLength);
            entry[7] = 0x1a;
            BinaryPrimitives.WriteInt64LittleEndian(entry[0x08..], entries[i].StartingVcn);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[0x10..], (ulong)entries[i].Segment | (1UL << 48));
            BinaryPrimitives.WriteUInt16LittleEndian(entry[0x18..], entries[i].Instance);
        }

        return value;
    }

    /// <summary>Adds raw bytes in place of an attribute, for malformed records.</summary>
    public MftRecordBuilder RawAttribute(byte[] attribute)
    {
        _attributes.Add(attribute);
        return this;
    }

    /// <summary>
    /// Writes the record. In the on-disk form the last two bytes of every 512 bytes hold the update sequence
    /// number and the real bytes sit in the update sequence array; with <paramref name="fixupsApplied"/> the
    /// real bytes stay in place, as FSCTL_GET_NTFS_FILE_RECORD returns them.
    /// </summary>
    public byte[] Build(int recordSize = 1024, bool fixupsApplied = false)
    {
        var updateSequenceCount = recordSize / 512 + 1;
        var firstAttribute = Align8(UpdateSequenceOffset + updateSequenceCount * 2);
        var record = new byte[recordSize];
        "FILE"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x04), UpdateSequenceOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x06), (ushort)updateSequenceCount);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x10), _sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x12), (ushort)Math.Max(1, Names.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x14), (ushort)firstAttribute);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x16), (ushort)((_inUse ? 1 : 0) | (_directory ? 2 : 0) | (_metadataFile ? 4 : 0)));

        var offset = firstAttribute;
        foreach (var attribute in _attributes)
        {
            attribute.CopyTo(record, offset);
            offset += attribute.Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), 0xffffffff);
        offset += 8;
        if (offset > recordSize)
        {
            throw new InvalidOperationException($"Record {Segment} does not fit in {recordSize} bytes.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x18), (uint)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x1c), (uint)recordSize);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(0x20), _baseReference);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x28), _nextAttributeId);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x2c), _storedNumber ?? (uint)Segment);

        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(UpdateSequenceOffset), UpdateSequenceNumber);
        for (var i = 1; i < updateSequenceCount; i++)
        {
            var tail = record.AsSpan(i * 512 - 2, 2);
            tail.CopyTo(record.AsSpan(UpdateSequenceOffset + i * 2, 2));
            if (!fixupsApplied)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(tail, UpdateSequenceNumber);
            }
        }

        return record;
    }

    private MftRecordBuilder AddResident(uint type, byte[] value, string? name = null)
    {
        var nameBytes = name is null ? [] : Encoding.Unicode.GetBytes(name);
        var valueOffset = Align8(0x18 + nameBytes.Length);
        var attribute = new byte[Align8(valueOffset + value.Length)];
        WriteAttributeHeader(attribute, type, nonResident: false, nameBytes, 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(0x10), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x14), (ushort)valueOffset);
        value.CopyTo(attribute, valueOffset);
        _attributes.Add(attribute);
        return this;
    }

    private void WriteAttributeHeader(byte[] attribute, uint type, bool nonResident, byte[] nameBytes, int nameOffset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, type);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(4), (uint)attribute.Length);
        attribute[8] = (byte)(nonResident ? 1 : 0);
        attribute[9] = (byte)(nameBytes.Length / 2);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x0a), (ushort)nameOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(0x0e), _nextAttributeId++);
        nameBytes.CopyTo(attribute, nameOffset);
    }

    private static int Align8(int value) => (value + 7) & ~7;
}

internal static class MftRuns
{
    /// <summary>Encodes mapping pairs for (clusters, LCN) runs; a null LCN is a sparse run.</summary>
    public static byte[] Encode(params (long Clusters, long? Lcn)[] runs)
    {
        var bytes = new List<byte>();
        long previous = 0;
        foreach (var (clusters, lcn) in runs)
        {
            var length = SignedBytes(clusters);
            var delta = lcn is { } value ? SignedBytes(value - previous) : [];
            bytes.Add((byte)(length.Length | (delta.Length << 4)));
            bytes.AddRange(length);
            bytes.AddRange(delta);
            if (lcn is { } current)
            {
                previous = current;
            }
        }

        bytes.Add(0);
        return [.. bytes];
    }

    private static byte[] SignedBytes(long value)
    {
        var all = BitConverter.GetBytes(value);
        var count = 8;
        while (count > 1)
        {
            var top = all[count - 1];
            var nextSign = (all[count - 2] & 0x80) != 0;
            if ((top == 0 && !nextSign) || (top == 0xff && nextSign))
            {
                count--;
                continue;
            }

            break;
        }

        return all[..count];
    }
}

/// <summary>A volume image holding a synthetic <c>$MFT</c> laid out over chosen extents.</summary>
internal sealed class FakeNtfsVolume : INtfsVolume
{
    private const int OutputHeaderLength = 12;

    public FakeNtfsVolume(NtfsVolumeData volumeData, byte[] image)
    {
        VolumeData = volumeData;
        Image = image;
    }

    public string VolumePath => @"\\.\C:";

    public NtfsVolumeData VolumeData { get; set; }

    public byte[] Image { get; }

    public List<MftExtent>? RetrievalPointers { get; set; }

    public string? RetrievalFailure { get; set; }

    /// <summary>Records as FSCTL_GET_NTFS_FILE_RECORD returns them, fixups applied.</summary>
    public Dictionary<long, byte[]> FileRecords { get; } = [];

    public Dictionary<ulong, NtfsFileRecord> UsnRecords { get; } = [];

    public Exception? ReadFailure { get; set; }

    public int? ShortRead { get; set; }

    public Action? OnRead { get; set; }

    public int ReadCalls { get; private set; }

    public int VolumeDataCalls { get; private set; }

    public int EnumerateCalls { get; private set; }

    public List<string> MisalignedReads { get; } = [];

    public NtfsVolumeData GetVolumeData()
    {
        VolumeDataCalls++;
        return VolumeData;
    }

    public IReadOnlyList<MftExtent>? GetMftRetrievalPointers(out string? failure)
    {
        failure = RetrievalFailure;
        return RetrievalPointers;
    }

    public bool ReadFileRecord(long segmentReference, byte[] output, out int bytesReturned, out int win32Error)
    {
        if (!FileRecords.TryGetValue(segmentReference, out var record) || OutputHeaderLength + record.Length > output.Length)
        {
            bytesReturned = 0;
            win32Error = 87;
            return false;
        }

        BinaryPrimitives.WriteInt64LittleEndian(output, segmentReference);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), record.Length);
        record.CopyTo(output, OutputHeaderLength);
        bytesReturned = OutputHeaderLength + record.Length;
        win32Error = 0;
        return true;
    }

    public int Read(long volumeOffset, Span<byte> destination)
    {
        ReadCalls++;
        OnRead?.Invoke();
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        if (volumeOffset % VolumeData.BytesPerSector != 0 || destination.Length % VolumeData.BytesPerSector != 0)
        {
            MisalignedReads.Add($"{destination.Length} bytes at {volumeOffset}");
        }

        var available = (int)Math.Max(0, Math.Min(destination.Length, Image.Length - volumeOffset));
        Image.AsSpan((int)volumeOffset, available).CopyTo(destination);
        return ShortRead is { } shortRead ? Math.Min(shortRead, available) : available;
    }

    public Dictionary<ulong, NtfsFileRecord> EnumerateUsnRecords(ProgressThrottle progress, CancellationToken cancellationToken)
    {
        EnumerateCalls++;
        return new Dictionary<ulong, NtfsFileRecord>(UsnRecords);
    }
}

/// <summary>
/// A synthetic <c>$MFT</c>: one record builder per slot, written onto a fake volume over chosen extents.
/// </summary>
internal sealed class SyntheticMft
{
    public const ulong RootReference = (5UL << 48) | 5;

    private readonly SortedDictionary<long, MftRecordBuilder> _records = [];
    private readonly Dictionary<long, Action<byte[]>> _tampering = [];

    public SyntheticMft(long slots, int recordSize = 1024)
    {
        Slots = slots;
        RecordSize = recordSize;
    }

    public long Slots { get; }

    public int RecordSize { get; }

    public IReadOnlyDictionary<long, MftRecordBuilder> Records => _records;

    public MftRecordBuilder Add(long segment)
    {
        var builder = new MftRecordBuilder(segment);
        _records[segment] = builder;
        return builder;
    }

    /// <summary>Changes a slot's on-disk bytes after the record is written.</summary>
    public void Tamper(long segment, Action<byte[]> change) => _tampering[segment] = change;

    public byte[] BuildMft()
    {
        var mft = new byte[Slots * RecordSize];
        foreach (var (segment, builder) in _records)
        {
            var record = builder.Build(RecordSize);
            if (_tampering.TryGetValue(segment, out var change))
            {
                change(record);
            }

            record.CopyTo(mft, segment * RecordSize);
        }

        return mft;
    }

    /// <summary>
    /// Lays the <c>$MFT</c> out over <paramref name="layout"/>, in VCN order, on a volume of
    /// <paramref name="totalClusters"/> clusters.
    /// </summary>
    public FakeNtfsVolume CreateVolume(int clusterSize, IReadOnlyList<(long Clusters, long Lcn)> layout, long totalClusters, int sectorSize = 512)
    {
        var mft = BuildMft();
        var image = new byte[totalClusters * clusterSize];
        var extents = new List<MftExtent>();
        long vcn = 0;
        foreach (var (clusters, lcn) in layout)
        {
            var start = vcn * clusterSize;
            var length = (int)Math.Min(clusters * clusterSize, Math.Max(0, mft.Length - start));
            if (length > 0)
            {
                mft.AsSpan((int)start, length).CopyTo(image.AsSpan((int)(lcn * clusterSize)));
            }

            extents.Add(new MftExtent(vcn, clusters, lcn));
            vcn += clusters;
        }

        var volume = new FakeNtfsVolume(
            new NtfsVolumeData(totalClusters, sectorSize, clusterSize, RecordSize, mft.Length, layout[0].Lcn),
            image)
        {
            RetrievalPointers = extents
        };

        foreach (var (segment, builder) in _records)
        {
            if (builder.IsInUse)
            {
                volume.FileRecords[segment] = builder.Build(RecordSize, fixupsApplied: true);
            }
        }

        return volume;
    }

    /// <summary>
    /// What FSCTL_ENUM_USN_DATA would list for this <c>$MFT</c>: one entry per intact in-use base record from
    /// record 16 on that is not an NTFS metadata file, plus the root, under its first name. Files whose names sit only in extension records are
    /// not modelled, so use it with <see cref="CreateStandard"/> without its attribute-list files.
    /// </summary>
    public Dictionary<ulong, NtfsFileRecord> UsnRecords()
    {
        var records = new Dictionary<ulong, NtfsFileRecord>();
        foreach (var builder in _records.Values)
        {
            if (!builder.IsInUse ||
                builder.IsExtension ||
                builder.IsNtfsMetadataFile ||
                builder.Names.Count == 0 ||
                _tampering.ContainsKey(builder.Segment) ||
                (builder.Segment < 16 && builder.Segment != 5))
            {
                continue;
            }

            var attributes = (FileAttributes)builder.StandardAttributes | (builder.IsDirectory ? FileAttributes.Directory : 0);
            var (parent, name) = builder.Names[0];
            records[builder.Reference] = new NtfsFileRecord(builder.Reference, parent, attributes, name);
        }

        return records;
    }

    /// <summary>
    /// The standard test volume. Records 0-15 are NTFS's own files, 16-23 are free, and the user's files start at 24:
    /// <code>
    /// C:\                     record 5
    ///   Users\                24
    ///     Strife\             25
    ///       big.bin           26  non-resident 5,000,000 bytes, DOS alias, alternate stream
    ///       small.txt         27  resident 25 bytes
    ///       first.txt         28  10 bytes, hard-linked as Users\second.txt
    ///       cloud.bin         29  reparse point
    ///       split.bin         33  attribute list; name and data in extension record 32, later data extent in 42
    ///       partial.bin       34  attribute list; data and a second name in extension record 35
    ///   Junction\             30  reparse point folder
    ///     inside.txt          31
    ///   top.txt               41  resident 100 bytes
    /// $Extend\$UsnJrnl        39  parent is record 11, which is left out
    /// 36 torn, 37 not FILE, 38 not in use, 40 extension of the free record 38, 43 not in use
    /// </code>
    /// </summary>
    public static SyntheticMft CreateStandard(IReadOnlyList<(long Clusters, long Lcn)> layout, bool includeAttributeListFiles = true, int recordSize = 1024)
    {
        var mft = new SyntheticMft(44, recordSize);
        var mftBytes = mft.Slots * recordSize;
        var runs = MftRuns.Encode([.. layout.Select(extent => (extent.Clusters, (long?)extent.Lcn))]);
        mft.Add(0)
            .StandardInformation(FileAttributes.Hidden | FileAttributes.System)
            .FileName(RootReference, "$MFT")
            .NonResidentData(mftBytes, mftBytes, runs: runs);

        string[] systemNames = ["$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", ".", "$Bitmap", "$Boot", "$BadClus", "$Secure", "$UpCase", "$Extend"];
        for (var segment = 1; segment < 16; segment++)
        {
            if (segment == 5)
            {
                continue;
            }

            var system = mft.Add(segment).Sequence((ushort)segment).StandardInformation(FileAttributes.Hidden | FileAttributes.System);
            if (segment < systemNames.Length)
            {
                system.FileName(RootReference, systemNames[segment]).ResidentData(segment * 10);
            }

            if (segment == 11)
            {
                system.Directory();
            }
        }

        mft.Add(5).Sequence(5).Directory()
            .StandardInformation(FileAttributes.Hidden | FileAttributes.System, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc))
            .FileName(RootReference, ".");
        for (var segment = 16; segment < 24; segment++)
        {
            mft.Add(segment).NotInUse();
        }

        var users = mft.Add(24).Directory().StandardInformation(FileAttributes.ReadOnly).FileName(RootReference, "Users");
        var strife = mft.Add(25).Directory().StandardInformation(0).FileName(users.Reference, "Strife");
        mft.Add(26).StandardInformation(FileAttributes.Archive)
            .FileName(strife.Reference, "BIGBIN~1.BIN", nameSpace: 2)
            .FileName(strife.Reference, "big.bin")
            .NonResidentData(5_000_000, 5_001_216, runs: MftRuns.Encode((1222, 500)))
            .ResidentData(26, streamName: "Zone.Identifier");
        mft.Add(27).StandardInformation(FileAttributes.Archive).FileName(strife.Reference, "small.txt", nameSpace: 3).ResidentData(25);
        mft.Add(28).StandardInformation(FileAttributes.Archive)
            .FileName(strife.Reference, "first.txt")
            .FileName(users.Reference, "second.txt")
            .ResidentData(10);
        mft.Add(29).StandardInformation(FileAttributes.Archive | FileAttributes.ReparsePoint | FileAttributes.Offline)
            .FileName(strife.Reference, "cloud.bin")
            .NonResidentData(9_000_000, 0, onDiskSize: 0);
        var junction = mft.Add(30).Directory().StandardInformation(FileAttributes.ReparsePoint).FileName(RootReference, "Junction");
        mft.Add(31).StandardInformation(FileAttributes.Archive).FileName(junction.Reference, "inside.txt").ResidentData(5);

        if (includeAttributeListFiles)
        {
            var split = new MftRecordBuilder(33).Sequence(3);
            mft.Add(32).ExtensionOf(split.Reference)
                .FileName(strife.Reference, "split.bin")
                .NonResidentData(7_000_000, 7_000_064, runs: MftRuns.Encode((800, 900)));
            mft.Add(33).Sequence(3).StandardInformation(FileAttributes.Archive)
                .AttributeList(
                    (NtfsFileRecordParser.AttributeTypeStandardInformation, 33, 0),
                    (NtfsFileRecordParser.AttributeTypeFileName, 32, 0),
                    (NtfsFileRecordParser.AttributeTypeData, 32, 0),
                    (NtfsFileRecordParser.AttributeTypeData, 42, 800));
            var partial = mft.Add(34).StandardInformation(FileAttributes.Archive)
                .AttributeList((NtfsFileRecordParser.AttributeTypeData, 35, 0))
                .FileName(strife.Reference, "partial.bin");
            mft.Add(35).ExtensionOf(partial.Reference)
                .FileName(users.Reference, "partial-link.bin")
                .NonResidentData(1_234, 4_096, runs: MftRuns.Encode((1, 950)));
            mft.Add(42).ExtensionOf(split.Reference)
                .NonResidentData(7_000_000, 7_000_064, lowestVcn: 800, runs: MftRuns.Encode((909, 2000)));
            mft.Add(40).ExtensionOf((2UL << 48) | 38).FileName(strife.Reference, "orphan.bin");
        }

        mft.Add(36).StandardInformation(FileAttributes.Archive).FileName(strife.Reference, "torn.bin").ResidentData(1);
        mft.Tamper(36, record => record[1022] ^= 0xff);
        mft.Add(38).Sequence(2).NotInUse().StandardInformation(FileAttributes.Archive).FileName(strife.Reference, "deleted.txt").ResidentData(3);
        mft.Add(39).StandardInformation(FileAttributes.Hidden | FileAttributes.System)
            .FileName((11UL << 48) | 11, "$UsnJrnl")
            .NonResidentData(40_000_000, 40_000_000, runs: MftRuns.Encode((100, 3000)));
        mft.Add(41).StandardInformation(FileAttributes.Archive, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc))
            .FileName(RootReference, "top.txt")
            .ResidentData(100);
        mft.Add(43).NotInUse();

        // Record 37 stays all zeros: a slot that does not hold a FILE record.
        return mft;
    }

    /// <summary>Totals of <see cref="CreateStandard"/> with its attribute-list files, scanned from C:\.</summary>
    public const long StandardFileCount = 6;

    public const long StandardLogicalBytes = 5_000_000 + 25 + 10 + 7_000_000 + 1_234 + 100;

    /// <summary>Totals of <see cref="CreateStandard"/> without its attribute-list files, scanned from C:\.</summary>
    public const long SingleRecordFileCount = 4;

    public const long SingleRecordLogicalBytes = 5_000_000 + 25 + 10 + 100;
}
