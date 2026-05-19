using System.Runtime.InteropServices;
using Custodian.Core.Model;
using Microsoft.Win32.SafeHandles;

namespace Custodian.Core.Scanning;

internal sealed class NtfsRecordSizeResolver
{
    private const uint FsctlGetNtfsFileRecord = 0x00090068;
    private const ulong FileReferenceSegmentMask = 0x0000ffffffffffff;
    private const int RecordOutputBufferSize = 64 * 1024;

    private readonly SafeFileHandle _volumeHandle;
    private readonly ScanOptions _options;
    private bool _disableRecordApi;

    public NtfsRecordSizeResolver(SafeFileHandle volumeHandle, ScanOptions options)
    {
        _volumeHandle = volumeHandle;
        _options = options;
    }

    public long Attempts { get; private set; }
    public long Parsed { get; private set; }
    public long Fallbacks { get; private set; }

    public (long LogicalSize, long AllocatedSize) Resolve(string fullPath, NtfsFileRecord record)
    {
        if (!_disableRecordApi && TryResolveFromNtfsRecord(record.FileReferenceNumber, out var size))
        {
            Parsed++;
            return (size.LogicalSize, _options.CollectAllocatedSize ? size.AllocatedSize : size.LogicalSize);
        }

        Fallbacks++;
        return ResolveFromFileInfo(fullPath);
    }

    private bool TryResolveFromNtfsRecord(ulong fileReferenceNumber, out NtfsRecordSize size)
    {
        size = default;
        Attempts++;

        var segmentReference = (long)(fileReferenceNumber & FileReferenceSegmentMask);
        var input = BitConverter.GetBytes(segmentReference);
        var output = new byte[RecordOutputBufferSize];

        var ok = DeviceIoControl(
            _volumeHandle,
            FsctlGetNtfsFileRecord,
            input,
            input.Length,
            output,
            output.Length,
            out var bytesReturned,
            IntPtr.Zero);

        if (!ok)
        {
            DisableIfClearlyUnsupported();
            return false;
        }

        if (bytesReturned < 12)
        {
            return false;
        }

        var returnedReference = BitConverter.ToUInt64(output, 0) & FileReferenceSegmentMask;
        if (returnedReference != (ulong)segmentReference)
        {
            return false;
        }

        var recordLength = BitConverter.ToInt32(output, 8);
        if (recordLength <= 0 || 12 + recordLength > bytesReturned)
        {
            return false;
        }

        if (NtfsFileRecordParser.TryReadDefaultDataSize(output.AsSpan(12, recordLength), out size))
        {
            return true;
        }

        DisableIfClearlyUnsupported();
        return false;
    }

    private (long LogicalSize, long AllocatedSize) ResolveFromFileInfo(string fullPath)
    {
        var info = new FileInfo(fullPath);
        var length = info.Length;
        var allocated = _options.CollectAllocatedSize ? FileSizeUtilities.GetAllocatedSize(fullPath, length) : length;
        return (length, allocated);
    }

    private void DisableIfClearlyUnsupported()
    {
        if (Attempts >= 100 && Parsed == 0)
        {
            _disableRecordApi = true;
        }
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
