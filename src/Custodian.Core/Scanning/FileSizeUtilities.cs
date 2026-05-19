using System.Runtime.InteropServices;

namespace Custodian.Core.Scanning;

internal static class FileSizeUtilities
{
    public static long GetAllocatedSize(string path, long fallbackLength)
    {
        var low = GetCompressedFileSize(path, out var high);
        if (low == uint.MaxValue && Marshal.GetLastPInvokeError() != 0)
        {
            return fallbackLength;
        }

        return ((long)high << 32) + low;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);
}
