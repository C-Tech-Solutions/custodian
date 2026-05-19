namespace Custodian.Core.Model;

public sealed record ScanProgress(
    string CurrentPath,
    long FilesSeen,
    long DirectoriesSeen,
    long BytesSeen,
    string Message);
