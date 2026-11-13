using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Custodian.Core.Model;
using Microsoft.Win32.SafeHandles;

namespace Custodian.Core.Scanning;

/// <summary>
/// Reads one file record into <paramref name="output"/> in the <c>NTFS_FILE_RECORD_OUTPUT_BUFFER</c>
/// layout. Returns false with the Win32 error when the call fails.
/// </summary>
internal delegate bool NtfsFileRecordReader(long segmentReference, byte[] output, out int bytesReturned, out int win32Error);

internal sealed class NtfsRecordSizeResolver
{
    private const uint FsctlGetNtfsFileRecord = 0x00090068;
    private const ulong FileReferenceSegmentMask = 0x0000ffffffffffff;

    // NTFS_FILE_RECORD_OUTPUT_BUFFER: FileReferenceNumber (8 bytes), FileRecordLength (4 bytes), record.
    private const int OutputHeaderLength = 12;

    // File records are 1 KB, or 4 KB on volumes with 4 KB sectors. The call fails with a Win32 error
    // (reported in diagnostics) if a record does not fit.
    private const int RecordOutputBufferSize = OutputHeaderLength + 4096;
    private const int AttemptsBeforeGivingUp = 100;

    private readonly ScanOptions _options;
    private readonly NtfsFileRecordReader _readRecord;
    private readonly byte[] _output = new byte[RecordOutputBufferSize];
    private readonly Dictionary<int, long> _callFailuresByError = [];
    private bool _recordPathDisabled;

    public NtfsRecordSizeResolver(SafeFileHandle volumeHandle, ScanOptions options)
        : this(options, CreateVolumeReader(volumeHandle))
    {
    }

    internal NtfsRecordSizeResolver(ScanOptions options, NtfsFileRecordReader readRecord)
    {
        _options = options;
        _readRecord = readRecord;
    }

    public long Attempts { get; private set; }
    public long Parsed { get; private set; }
    public long Fallbacks { get; private set; }
    public long CallFailures { get; private set; }
    public long DifferentRecordReturned { get; private set; }
    public long Malformed { get; private set; }
    public long DataNotInBaseRecord { get; private set; }
    public long? DisabledAfterAttempts { get; private set; }

    public (long LogicalSize, long AllocatedSize) Resolve(string fullPath, NtfsFileRecord record)
    {
        if (!_recordPathDisabled && TryResolveFromNtfsRecord(record.FileReferenceNumber, out var size))
        {
            Parsed++;
            return (size.LogicalSize, _options.CollectAllocatedSize ? size.AllocatedSize : size.LogicalSize);
        }

        Fallbacks++;
        return ResolveFromFileInfo(fullPath);
    }

    public IReadOnlyList<string> GetDiagnostics()
    {
        var callFailures = _callFailuresByError.Count == 0
            ? string.Empty
            : " (" + string.Join(", ", _callFailuresByError
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key)
                .Select(pair => $"Win32 error {pair.Key}: {pair.Value:n0}")) + ")";

        var diagnostics = new List<string>
        {
            $"NTFS record sizes parsed {Parsed:n0}; fallbacks {Fallbacks:n0}",
            $"NTFS record size fallbacks by reason: call failed {CallFailures:n0}{callFailures}; " +
            $"different record returned {DifferentRecordReturned:n0}; record did not parse {Malformed:n0}; " +
            $"no unnamed $DATA in base record {DataNotInBaseRecord:n0}"
        };

        if (DisabledAfterAttempts is { } attempts)
        {
            diagnostics.Add(
                $"NTFS record size path switched off after {attempts:n0} attempts without a parsed record; " +
                $"{Fallbacks - (Attempts - Parsed):n0} later files used path lookups");
        }

        return diagnostics;
    }

    private bool TryResolveFromNtfsRecord(ulong fileReferenceNumber, out NtfsRecordSize size)
    {
        Attempts++;
        if (TryReadRecordSize((long)(fileReferenceNumber & FileReferenceSegmentMask), out size))
        {
            return true;
        }

        if (Parsed == 0 && Attempts >= AttemptsBeforeGivingUp)
        {
            _recordPathDisabled = true;
            DisabledAfterAttempts = Attempts;
        }

        return false;
    }

    private bool TryReadRecordSize(long segmentReference, out NtfsRecordSize size)
    {
        size = default;

        if (!_readRecord(segmentReference, _output, out var bytesReturned, out var win32Error))
        {
            CallFailures++;
            _callFailuresByError[win32Error] = _callFailuresByError.GetValueOrDefault(win32Error) + 1;
            return false;
        }

        if (bytesReturned < OutputHeaderLength)
        {
            Malformed++;
            return false;
        }

        // For a record that is not in use, NTFS returns the nearest lower record that is.
        var returnedReference = BinaryPrimitives.ReadUInt64LittleEndian(_output) & FileReferenceSegmentMask;
        if (returnedReference != (ulong)segmentReference)
        {
            DifferentRecordReturned++;
            return false;
        }

        var recordLength = BinaryPrimitives.ReadInt32LittleEndian(_output.AsSpan(8, 4));
        if (recordLength <= 0 || recordLength > bytesReturned - OutputHeaderLength)
        {
            Malformed++;
            return false;
        }

        switch (NtfsFileRecordParser.ReadDefaultDataSize(_output.AsSpan(OutputHeaderLength, recordLength), out size))
        {
            case NtfsDataSizeReadResult.Found:
                return true;
            case NtfsDataSizeReadResult.NotInBaseRecord:
                DataNotInBaseRecord++;
                return false;
            default:
                Malformed++;
                return false;
        }
    }

    private (long LogicalSize, long AllocatedSize) ResolveFromFileInfo(string fullPath)
    {
        var info = new FileInfo(fullPath);
        var length = info.Length;
        var allocated = _options.CollectAllocatedSize ? FileSizeUtilities.GetAllocatedSize(fullPath, length) : length;
        return (length, allocated);
    }

    private static NtfsFileRecordReader CreateVolumeReader(SafeFileHandle volumeHandle)
    {
        var input = new byte[sizeof(long)];
        return (long segmentReference, byte[] output, out int bytesReturned, out int win32Error) =>
        {
            BinaryPrimitives.WriteInt64LittleEndian(input, segmentReference);
            if (DeviceIoControl(volumeHandle, FsctlGetNtfsFileRecord, input, input.Length, output, output.Length, out bytesReturned, IntPtr.Zero))
            {
                win32Error = 0;
                return true;
            }

            win32Error = Marshal.GetLastPInvokeError();
            return false;
        };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
