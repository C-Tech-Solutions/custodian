using System.Buffers.Binary;
using Custodian.Core.Model;
using Custodian.Core.Scanning;

namespace Custodian.Tests;

public sealed class NtfsRecordSizeResolverTests : IDisposable
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidFunction = 1;

    private readonly string _fallbackFile = Path.Combine(Path.GetTempPath(), $"custodian-record-size-{Guid.NewGuid():N}.bin");

    public NtfsRecordSizeResolverTests()
    {
        File.WriteAllBytes(_fallbackFile, new byte[7]);
    }

    public void Dispose()
    {
        File.Delete(_fallbackFile);
    }

    [Fact]
    public void ResolveReadsSizeFromFileRecordWithoutOpeningTheFile()
    {
        var resolver = new NtfsRecordSizeResolver(
            new ScanOptions(@"C:\", ScanMode.Mft, CollectAllocatedSize: true),
            (long segment, byte[] output, out int bytesReturned, out int error) =>
            {
                error = 0;
                bytesReturned = WriteOutput(output, segment, FileRecordWithNonResidentData(logicalSize: 5_000, allocatedSize: 8_192));
                return true;
            });

        var size = resolver.Resolve(@"C:\does-not-exist\file.bin", Record(0x0003_0000_0000_002a));

        Assert.Equal((5_000L, 8_192L), size);
        Assert.Equal(1, resolver.Parsed);
        Assert.Equal(0, resolver.Fallbacks);
        Assert.Equal("NTFS record sizes parsed 1; fallbacks 0", resolver.GetDiagnostics()[0]);
    }

    [Fact]
    public void ResolveCountsEachFallbackReason()
    {
        var calls = 0;
        var resolver = new NtfsRecordSizeResolver(
            new ScanOptions(@"C:\", ScanMode.Mft),
            (long segment, byte[] output, out int bytesReturned, out int error) =>
            {
                error = 0;
                bytesReturned = 0;
                switch (calls++)
                {
                    case 0:
                        bytesReturned = WriteOutput(output, segment, FileRecordWithNonResidentData(logicalSize: 10, allocatedSize: 4_096));
                        return true;
                    case 1:
                    case 2:
                        error = ErrorAccessDenied;
                        return false;
                    case 3:
                        error = ErrorInvalidFunction;
                        return false;
                    case 4:
                        bytesReturned = WriteOutput(output, segment - 1, FileRecordWithNonResidentData(logicalSize: 10, allocatedSize: 4_096));
                        return true;
                    case 5:
                        var corrupt = FileRecordWithNonResidentData(logicalSize: 10, allocatedSize: 4_096);
                        "BAAD"u8.CopyTo(corrupt);
                        bytesReturned = WriteOutput(output, segment, corrupt);
                        return true;
                    default:
                        bytesReturned = WriteOutput(output, segment, FileRecordWithoutData());
                        return true;
                }
            });

        var sizes = Enumerable.Range(0, 7).Select(i => resolver.Resolve(_fallbackFile, Record((ulong)(100 + i)))).ToList();

        Assert.Equal((10L, 10L), sizes[0]);
        Assert.All(sizes.Skip(1), size => Assert.Equal((7L, 7L), size));
        Assert.Equal(1, resolver.Parsed);
        Assert.Equal(6, resolver.Fallbacks);
        Assert.Equal(3, resolver.CallFailures);
        Assert.Equal(1, resolver.DifferentRecordReturned);
        Assert.Equal(1, resolver.Malformed);
        Assert.Equal(1, resolver.DataNotInBaseRecord);
        Assert.Null(resolver.DisabledAfterAttempts);
        Assert.Equal(
            [
                "NTFS record sizes parsed 1; fallbacks 6",
                "NTFS record size fallbacks by reason: call failed 3 (Win32 error 5: 2, Win32 error 1: 1); " +
                "different record returned 1; record did not parse 1; no unnamed $DATA in base record 1"
            ],
            resolver.GetDiagnostics());
    }

    [Fact]
    public void ResolveSwitchesRecordPathOffAfterHundredAttemptsWithoutAParsedRecord()
    {
        var calls = 0;
        var resolver = new NtfsRecordSizeResolver(
            new ScanOptions(@"C:\", ScanMode.Mft),
            (long segment, byte[] output, out int bytesReturned, out int error) =>
            {
                calls++;
                bytesReturned = 0;
                error = ErrorInvalidFunction;
                return false;
            });

        for (var i = 0; i < 150; i++)
        {
            Assert.Equal((7L, 7L), resolver.Resolve(_fallbackFile, Record((ulong)(100 + i))));
        }

        Assert.Equal(100, calls);
        Assert.Equal(100, resolver.DisabledAfterAttempts);
        Assert.Equal(150, resolver.Fallbacks);
        Assert.Equal(
            "NTFS record size path switched off after 100 attempts without a parsed record; 50 later files used path lookups",
            resolver.GetDiagnostics()[2]);
    }

    [Fact]
    public void ResolveKeepsRecordPathOnceARecordHasParsed()
    {
        var calls = 0;
        var resolver = new NtfsRecordSizeResolver(
            new ScanOptions(@"C:\", ScanMode.Mft),
            (long segment, byte[] output, out int bytesReturned, out int error) =>
            {
                error = 0;
                bytesReturned = WriteOutput(
                    output,
                    segment,
                    calls++ == 0 ? FileRecordWithNonResidentData(logicalSize: 10, allocatedSize: 4_096) : FileRecordWithoutData());
                return true;
            });

        for (var i = 0; i < 150; i++)
        {
            resolver.Resolve(_fallbackFile, Record((ulong)(100 + i)));
        }

        Assert.Equal(150, calls);
        Assert.Null(resolver.DisabledAfterAttempts);
        Assert.Equal(2, resolver.GetDiagnostics().Count);
    }

    private static NtfsFileRecord Record(ulong fileReferenceNumber) =>
        new(fileReferenceNumber, 5, FileAttributes.Archive, "file.bin");

    private static int WriteOutput(byte[] output, long segment, byte[] record)
    {
        BinaryPrimitives.WriteInt64LittleEndian(output, segment | (7L << 48));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8, 4), record.Length);
        record.CopyTo(output, 12);
        return 12 + record.Length;
    }

    private static byte[] FileRecordWithoutData()
    {
        var record = new byte[1024];
        "FILE"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4, 2), 0x30);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20, 2), 56);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(56, 4), 0xffffffff);
        return record;
    }

    private static byte[] FileRecordWithNonResidentData(long logicalSize, long allocatedSize)
    {
        var record = FileRecordWithoutData();
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(56, 4), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(60, 4), 72);
        record[64] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(56 + 0x28, 8), allocatedSize);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(56 + 0x30, 8), logicalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(128, 4), 0xffffffff);
        return record;
    }
}
