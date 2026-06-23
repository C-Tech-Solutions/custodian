using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Custodian.Core.Model;
using Microsoft.Win32.SafeHandles;

namespace Custodian.Core.Scanning;

public sealed class MftScanProvider : IDiskScanProvider
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FsctlEnumUsnData = 0x000900b3;
    private const int BufferSize = 1024 * 1024;

    public string Name => "NTFS MFT";

    public bool CanScan(ScanOptions options, out string reason)
    {
        reason = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            reason = "MFT scans require Windows.";
            return false;
        }

        var normalizedRoot = ScanPathUtility.NormalizeRoot(options.RootPath);

        if (normalizedRoot.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = "MFT scans do not apply to UNC/network shares.";
            return false;
        }

        var root = Path.GetPathRoot(normalizedRoot);
        if (string.IsNullOrWhiteSpace(root))
        {
            reason = "No drive root could be resolved.";
            return false;
        }

        try
        {
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                reason = "Drive is not ready.";
                return false;
            }

            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Drive format is {drive.DriveFormat}, not NTFS.";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            reason = ex.Message;
            return false;
        }

        return true;
    }

    public async Task<ScanResult> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!CanScan(options, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        return await Task.Run(() => Scan(options, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static ScanResult Scan(
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var progressThrottle = new ProgressThrottle(progress);
        var rootPath = ScanPathUtility.NormalizeRoot(options.RootPath);
        var driveRoot = Path.GetPathRoot(rootPath)!;
        var volumePath = @"\\.\" + driveRoot.TrimEnd('\\');
        using var volumeHandle = OpenVolume(volumePath);
        var enumerationWatch = Stopwatch.StartNew();
        var records = EnumerateRecords(volumeHandle, volumePath, progressThrottle, cancellationToken);
        enumerationWatch.Stop();
        var skipped = new List<SkippedEntry>();
        var sizeResolver = new NtfsRecordSizeResolver(volumeHandle, options);
        var build = MftTreeBuilder.Build(records, rootPath, options, progressThrottle, skipped, cancellationToken, sizeResolver.Resolve);

        return new ScanResult
        {
            RootPath = build.Root.FullPath,
            SourceKind = ScanSourceKind.FileSystem,
            SourceId = build.Root.FullPath,
            DisplayRootPath = build.Root.FullPath,
            Engine = "NTFS MFT",
            StartedAt = started,
            CompletedAt = DateTimeOffset.UtcNow,
            Root = build.Root,
            GlobalIndex = build.GlobalIndex,
            SkippedEntries = skipped,
            PhaseTimings =
            [
                new ScanPhaseTiming("MFT enumeration", enumerationWatch.Elapsed),
                new ScanPhaseTiming("MFT tree construction", build.TreeConstructionDuration),
                new ScanPhaseTiming("Size resolution", build.SizeResolutionDuration),
                new ScanPhaseTiming("Folder aggregation", build.AggregationDuration)
            ],
            Diagnostics =
            [
                $"NTFS record sizes parsed {sizeResolver.Parsed:n0}; fallbacks {sizeResolver.Fallbacks:n0}"
            ]
        };
    }

    private static SafeFileHandle OpenVolume(string volumePath)
    {
        var handle = CreateFile(
            volumePath,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new IOException($"Could not open {volumePath} for MFT enumeration. Administrator rights may be required.");
        }

        return handle;
    }

    private static Dictionary<ulong, NtfsFileRecord> EnumerateRecords(
        SafeFileHandle handle,
        string volumePath,
        ProgressThrottle progress,
        CancellationToken cancellationToken)
    {
        var records = new Dictionary<ulong, NtfsFileRecord>();
        var input = new MftEnumDataV0
        {
            StartFileReferenceNumber = 0,
            LowUsn = 0,
            HighUsn = long.MaxValue
        };
        var output = new byte[BufferSize];
        var inputSize = Marshal.SizeOf<MftEnumDataV0>();
        var seen = 0L;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ok = DeviceIoControl(
                handle,
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
                if (error == 38)
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

                var record = ParseRecord(output.AsSpan(offset, (int)recordLength));
                if (record is not null)
                {
                    records[record.FileReferenceNumber] = record;
                    seen++;
                }

                offset += (int)recordLength;
            }

            progress.Report(volumePath, seen, 0, 0, "Enumerating MFT");
        }

        return records;
    }

    private static NtfsFileRecord? ParseRecord(ReadOnlySpan<byte> buffer)
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
}
