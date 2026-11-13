using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Custodian.Core.Scanning;

/// <summary>Fields of <c>NTFS_VOLUME_DATA_BUFFER</c> that the direct <c>$MFT</c> read depends on.</summary>
internal sealed record NtfsVolumeData(
    long TotalClusters,
    int BytesPerSector,
    int BytesPerCluster,
    int BytesPerFileRecord,
    long MftValidDataLength,
    long MftStartLcn);

/// <summary>
/// The NTFS calls the MFT engine makes against one volume. Every member needs an elevated process on a real
/// volume, so the engine's logic is tested against fakes of this interface.
/// </summary>
internal interface INtfsVolume
{
    string VolumePath { get; }

    /// <summary><c>FSCTL_GET_NTFS_VOLUME_DATA</c>. Throws <see cref="IOException"/> when the call fails.</summary>
    NtfsVolumeData GetVolumeData();

    /// <summary>
    /// <c>FSCTL_GET_RETRIEVAL_POINTERS</c> on <c>X:\$MFT</c>, opened for <c>FILE_READ_ATTRIBUTES</c> only.
    /// Returns null with the reason when it cannot be used.
    /// </summary>
    IReadOnlyList<MftExtent>? GetMftRetrievalPointers(out string? failure);

    /// <summary>
    /// <c>FSCTL_GET_NTFS_FILE_RECORD</c>, in the <c>NTFS_FILE_RECORD_OUTPUT_BUFFER</c> layout. The returned record
    /// has its update sequence fixups already applied.
    /// </summary>
    bool ReadFileRecord(long segmentReference, byte[] output, out int bytesReturned, out int win32Error);

    /// <summary>
    /// Reads raw bytes from the volume device. <paramref name="volumeOffset"/> and the length of
    /// <paramref name="destination"/> are multiples of the sector size. Throws <see cref="IOException"/> on failure.
    /// </summary>
    int Read(long volumeOffset, Span<byte> destination);

    /// <summary><c>FSCTL_ENUM_USN_DATA</c> over the whole volume: one record per file, keyed by file reference.</summary>
    Dictionary<ulong, NtfsFileRecord> EnumerateUsnRecords(ProgressThrottle progress, CancellationToken cancellationToken);
}

/// <summary>
/// A volume handle opened for reading with full sharing. Nothing here writes to the volume.
/// </summary>
internal sealed class NtfsVolume : INtfsVolume, IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileBegin = 0;
    private const uint FsctlGetNtfsVolumeData = 0x00090064;
    private const uint FsctlGetNtfsFileRecord = 0x00090068;
    private const uint FsctlGetRetrievalPointers = 0x00090073;
    private const uint FsctlEnumUsnData = 0x000900b3;
    private const int ErrorHandleEof = 38;
    private const int ErrorMoreData = 234;
    private const int EnumerationBufferSize = 1024 * 1024;

    // NTFS_VOLUME_DATA_BUFFER is 96 bytes; NTFS_EXTENDED_VOLUME_DATA may follow it.
    private const int VolumeDataLength = 96;

    private readonly SafeFileHandle _handle;
    private readonly string _driveRoot;
    private readonly byte[] _recordInput = new byte[sizeof(long)];

    private NtfsVolume(SafeFileHandle handle, string driveRoot, string volumePath)
    {
        _handle = handle;
        _driveRoot = driveRoot;
        VolumePath = volumePath;
    }

    public string VolumePath { get; }

    /// <summary>Opens <c>\\.\X:</c> for <paramref name="driveRoot"/> (<c>X:\</c>).</summary>
    public static NtfsVolume Open(string driveRoot)
    {
        var volumePath = @"\\.\" + driveRoot.TrimEnd('\\');
        var handle = CreateFile(volumePath, GenericRead, FileShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException($"Could not open {volumePath} for MFT enumeration. Administrator rights may be required.");
        }

        return new NtfsVolume(handle, driveRoot.TrimEnd('\\') + @"\", volumePath);
    }

    public NtfsVolumeData GetVolumeData()
    {
        var data = new byte[128];
        if (!DeviceIoControl(_handle, FsctlGetNtfsVolumeData, null, 0, data, data.Length, out var returned, IntPtr.Zero))
        {
            throw new IOException($"FSCTL_GET_NTFS_VOLUME_DATA failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        if (returned < VolumeDataLength)
        {
            throw new IOException($"FSCTL_GET_NTFS_VOLUME_DATA returned {returned} bytes, fewer than {VolumeDataLength}.");
        }

        return new NtfsVolumeData(
            TotalClusters: BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16)),
            BytesPerSector: BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(40)),
            BytesPerCluster: BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(44)),
            BytesPerFileRecord: BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(48)),
            MftValidDataLength: BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(56)),
            MftStartLcn: BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(64)));
    }

    public IReadOnlyList<MftExtent>? GetMftRetrievalPointers(out string? failure)
    {
        var mftPath = _driveRoot + "$MFT";
        using var mft = CreateFile(mftPath, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (mft.IsInvalid)
        {
            failure = $"opening {mftPath} failed with Win32 error {Marshal.GetLastPInvokeError()}";
            return null;
        }

        // RETRIEVAL_POINTERS_BUFFER: ExtentCount (4 bytes, padded to 8), StartingVcn (8), then
        // { NextVcn, Lcn } pairs of 8 bytes each.
        var extents = new List<MftExtent>();
        var input = new byte[sizeof(long)];
        var output = new byte[64 * 1024];
        long vcn = 0;
        while (true)
        {
            BinaryPrimitives.WriteInt64LittleEndian(input, vcn);
            var ok = DeviceIoControl(mft, FsctlGetRetrievalPointers, input, input.Length, output, output.Length, out _, IntPtr.Zero);
            var error = ok ? 0 : Marshal.GetLastPInvokeError();
            if (!ok && error != ErrorMoreData)
            {
                failure = $"FSCTL_GET_RETRIEVAL_POINTERS on {mftPath} failed with Win32 error {error}";
                return null;
            }

            var count = BinaryPrimitives.ReadInt32LittleEndian(output);
            if (count < 0 || 16 + (long)count * 16 > output.Length)
            {
                failure = $"FSCTL_GET_RETRIEVAL_POINTERS on {mftPath} returned an extent count of {count}";
                return null;
            }

            var start = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(8));
            for (var i = 0; i < count; i++)
            {
                var next = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(16 + i * 16));
                var lcn = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(24 + i * 16));
                extents.Add(new MftExtent(start, next - start, lcn));
                start = next;
            }

            if (ok || count == 0)
            {
                break;
            }

            vcn = start;
        }

        failure = null;
        return extents;
    }

    public bool ReadFileRecord(long segmentReference, byte[] output, out int bytesReturned, out int win32Error)
    {
        BinaryPrimitives.WriteInt64LittleEndian(_recordInput, segmentReference);
        if (DeviceIoControl(_handle, FsctlGetNtfsFileRecord, _recordInput, _recordInput.Length, output, output.Length, out bytesReturned, IntPtr.Zero))
        {
            win32Error = 0;
            return true;
        }

        win32Error = Marshal.GetLastPInvokeError();
        return false;
    }

    public int Read(long volumeOffset, Span<byte> destination)
    {
        if (!SetFilePointerEx(_handle, volumeOffset, out _, FileBegin))
        {
            throw new IOException($"Seeking {VolumePath} to byte {volumeOffset:n0} failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        if (!ReadFile(_handle, ref MemoryMarshal.GetReference(destination), destination.Length, out var read, IntPtr.Zero))
        {
            throw new IOException($"Reading {destination.Length:n0} bytes at byte {volumeOffset:n0} of {VolumePath} failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        }

        return read;
    }

    public Dictionary<ulong, NtfsFileRecord> EnumerateUsnRecords(ProgressThrottle progress, CancellationToken cancellationToken)
    {
        var records = new Dictionary<ulong, NtfsFileRecord>();
        var input = new MftEnumDataV0
        {
            StartFileReferenceNumber = 0,
            LowUsn = 0,
            HighUsn = long.MaxValue
        };
        var output = new byte[EnumerationBufferSize];
        var inputSize = Marshal.SizeOf<MftEnumDataV0>();
        var seen = 0L;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ok = DeviceIoControl(
                _handle,
                FsctlEnumUsnData,
                ref input,
                inputSize,
                output,
                output.Length,
                out var bytesReturned,
                IntPtr.Zero);

            if (!ok)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorHandleEof)
                {
                    break;
                }

                throw new IOException($"MFT enumeration failed with Win32 error {error}.");
            }

            if (bytesReturned <= sizeof(long))
            {
                break;
            }

            input.StartFileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(0, 8));
            var offset = 8;
            while (offset < bytesReturned)
            {
                var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(offset, 4));
                if (recordLength == 0 || offset + recordLength > bytesReturned)
                {
                    break;
                }

                var record = ParseUsnRecord(output.AsSpan(offset, (int)recordLength));
                if (record is not null)
                {
                    records[record.FileReferenceNumber] = record;
                    seen++;
                }

                offset += (int)recordLength;
            }

            progress.Report(VolumePath, seen, 0, 0, "Enumerating MFT");
        }

        return records;
    }

    public void Dispose() => _handle.Dispose();

    private static NtfsFileRecord? ParseUsnRecord(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 60)
        {
            return null;
        }

        var major = BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..6]);
        if (major is not (2 or 3 or 4))
        {
            return null;
        }

        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer[56..58]);
        var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(buffer[58..60]);
        if (nameOffset + nameLength > buffer.Length)
        {
            return null;
        }

        var name = Encoding.Unicode.GetString(buffer.Slice(nameOffset, nameLength));
        if (name is "")
        {
            return null;
        }

        return new NtfsFileRecord(
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..16]),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[16..24]),
            (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(buffer[52..56]),
            name);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftEnumDataV0
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref MftEnumDataV0 lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle hFile, ref byte lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);
}
