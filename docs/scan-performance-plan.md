# Scan performance plan

Proposed 2026-11-12, updated the same day with an elevated MFT run and an elevated diagnostic. This is the outcome of an investigation. Nothing under `src/` or `tests/` has been changed yet.

## Goal

Make full-drive scans with millions of files finish several times faster, without changing what a scan reports.

## Summary

| # | Work item | Who benefits | Measured or expected gain | Can be verified without elevation |
|---|---|---|---|---|
| 1 | Recursive engine: list each directory once, on several threads | Every scan that is not an elevated NTFS volume-root scan | `C:\`: 8 min 54 s → about 75 s (prototype), 7× | Yes |
| 2 | Post-scan UI preparation: build the folder tree lazily, take the jump index off the display path | GUI, after every scan and every opened scan file | About 8 s → under 0.5 s before results show | Yes |
| 3 | MFT engine: fix the per-file size path, which fails for every file today | Elevated volume-root scans | "Size resolution" 416 s → an estimated 15–35 s | Logic yes, real timing no |
| 4 | MFT engine: resolve file sizes on several threads | Elevated volume-root scans | About 11 s per full-drive scan. Skip it if item 5 goes ahead | No |
| 5 | Decision: read the `$MFT` directly | Elevated volume-root scans | The whole `$MFT` reads and parses in 10.4 s; a full-drive scan of an estimated 20–30 s. Has a security-footprint trade-off | No |
| 6 | Decision: memory per entry | Very large drives, small servers | About 5 GB → about 2.5 GB for 9.6M entries | Yes |
| 7 | Decision: make both engines count the same things | Anyone comparing scans | Not speed. The MFT engine leaves out 468,329 files holding 551 GB logical; the recursive engine counts a hard-linked file once per name | Partly |

Do them in this order. Items 1 and 2 are independent of each other and of the MFT work.

Until item 3 is fixed and checked, the MFT engine is the slower engine. After item 1 the recursive engine scans `C:\` in about 75 s; the MFT engine takes 8 min, and Auto mode still picks it for elevated volume-root scans. After item 3 the two should be roughly level: an estimated 45–100 s for the MFT engine. Item 5 is what makes the MFT engine clearly faster.

## Findings

Test machine: 16 logical CPUs, 64 GB RAM, SSDs, Windows 11, Defender real-time protection on. The investigating session was **not elevated**; the MFT numbers in findings 3 and 6 come from one elevated run by Colten.
Test drive `C:\`: 7.97 million files, 1.60 million folders, 9.56 million entries, 871 GB logical size, 2,516 unreadable folders.

### 1. The recursive engine is the path most scans take, and it is single-threaded

`DiskScanner` uses the MFT engine only when the process is elevated and the target is an NTFS volume root. Everything else runs `RecursiveScanProvider`: non-elevated scans, subfolders, UNC paths and cloud folders.

| Run on `C:\` | Result |
|---|---|
| Current scanner, `custodian scan C:\ --mode recursive`, first run (cold cache) | Stopped after 10 min 30 s with 5.07M of 7.97M files done |
| Current scanner, warm cache, complete | **8 min 54 s** (534 s) |
| Prototype, 8 threads, warm cache, builds the same `FileSystemEntry` tree | 72.9 s listing + 0.6 s totals + 1.2 s global index |
| Prototype, 16 threads, warm cache | 76.9 s |
| Prototype, 16 threads, first run (36% of files not yet cached) | 110 s |

The prototype and the current scanner agree on the result: 7,966,113 against 7,966,116 files and 1,598,495 against 1,598,496 folders, on a drive that was in use between the runs.

Where the time goes: 8.5 minutes into the cold run the process had used 6 min 34 s of kernel CPU time and 32 s of user CPU time. The cost is opening and listing directories, not Custodian's own code. `RecursiveScanProvider.ScanDirectory` lists every directory twice (`EnumerateDirectories`, then `EnumerateFiles`) on one thread.

Scaling on `C:\Windows` (212,569 files, 182,608 folders, warm cache):

| Variant | Time | Speed-up |
|---|---|---|
| Two listings per directory, 1 thread (today's approach) | 33.2 s | 1.0× |
| One listing per directory, 1 thread | 20.0 s | 1.7× |
| One listing per directory, 2 threads | 10.6 s | 3.1× |
| 4 threads | 5.7 s | 5.9× |
| 8 threads | 3.8 s | 8.7× |
| 16 threads | 3.2 s | 10.3× |
| 32 threads | 3.2 s | 10.3× |

Eight threads capture nearly all of the gain. On the full drive 16 threads were no faster than 8. Garbage collection is not the limit: pauses were 3.0 s of 73 s, and server GC changed nothing.

### 2. The GUI does about 8 s of work after the scan before it shows results

`MainWindow.PrepareScanUiAsync` builds two things for the whole tree. Measured on the `C:\` tree:

| Step | Time | Why |
|---|---|---|
| `FolderNode.From` | 2.8 s | Builds a node for all 1.6M folders with LINQ sorting and formatted size and percent strings, although the tree view shows a handful |
| `ScanViewProjector.FolderJumpIndex` | 5.0 s | `Flatten()` over all 9.56M entries through nested iterators, then a sort of 1.6M full paths, then a formatted row per folder |
| Same index from a folders-only walk | 0.5 s walk + 2.8 s sort + 0.5 s rows | The sort is the part that remains |

`FileSystemEntry.Flatten()` creates one iterator object per node and passes every entry up through every ancestor's iterator. Export uses it too.

### 3. The MFT engine is as slow as the recursive engine, because its fast size path never succeeds

One elevated run of `master`, `custodian scan C:\ --mode mft`, same machine and drive:

| Phase | Time |
|---|---|
| MFT enumeration (`FSCTL_ENUM_USN_DATA`) | 61.9 s |
| MFT tree construction | 2.5 s |
| Size resolution | 416.1 s |
| Folder aggregation | 0.4 s |
| **Whole scan** | **8 min 01 s** |

The same run printed `NTFS record sizes parsed 0; fallbacks 4,894,145`.

- `NtfsRecordSizeResolver` asks NTFS for each file's record with `FSCTL_GET_NTFS_FILE_RECORD` and reads the size from it. Not one of those reads succeeded.
- After 100 attempts without a success, `DisableIfClearlyUnsupported` switches the record path off for the rest of the scan. Nothing records why.
- Every file then takes the fallback, `new FileInfo(path).Length`, which opens the file by path: 85 µs per file, 416 s in total. That is the per-file kernel work the recursive engine does, so the "fast" engine needed 8 min 01 s where the recursive engine needed 8 min 54 s.
- Enumeration alone takes 62 s. The recursive prototype's whole scan takes 75 s.

**Cause, confirmed by the elevated diagnostic.** `FSCTL_GET_NTFS_FILE_RECORD` returns the record with the update sequence fixups already applied: 205,768 of 205,768 sampled records. `NtfsFileRecordParser.ApplyUpdateSequenceFixups` returns false when the last two bytes of a sector do not equal the update sequence number, so it rejects every record NTFS returns. The call itself never failed. `NtfsFileRecordParserTests` could not catch this: its records have an update sequence count of 1, and that skips the fixup code.

What the diagnostic measured on the same drive. It ran after the scan, so the `$MFT` was cached for the first four rows:

| Measurement | Result |
|---|---|
| Sizes readable once the fixup check is dropped | 199,282 of 205,768 sampled files (96.8%). The other 3.2% keep their `$DATA` attribute in an extension record |
| One `FSCTL_GET_NTFS_FILE_RECORD` per file, 64 KB buffer as today | 4.0 µs per file, 21 s for all 5.35M files |
| Same, buffer sized to the record (1,040 bytes) | 2.7 µs per file, 14 s |
| Same on 4 threads and on 8 threads, one volume handle each | 5 s and 3 s |
| `FSCTL_ENUM_USN_DATA` over the volume, counting only | 12.4 s for 7.41M records |
| Whole `$MFT` (8,570 MB) read from the volume device and parsed | 9.4 s to read at 909 MB/s, 0.9 s to parse on one thread |

Custodian's enumeration phase took 61.9 s where the bare call took 12.4 s. Part of that gap is reading the `$MFT` from disk for the first time, and part is building 7.4M record objects. The split is unknown; the next elevated scan shows the warm figure.

Smaller costs in the same code, from reading it:

- `NtfsRecordSizeResolver.TryResolveFromNtfsRecord` allocates a 64 KB output buffer and an 8-byte input array for each call. `NtfsFileRecordParser.TryReadDefaultDataSize` then copies the record again with `ToArray()`.
  Micro-benchmark of only those allocations: 1.23 s and 1,326 gen-0 collections per million files, against 0.012 s with reused buffers.
- `MftTreeBuilder.IsInsideSelectedRoot` / `IsUnderRoot` call `Path.GetFullPath` about three times per file when the request is not a volume root. Each call costs 0.31 s per million files.
- `NtfsFileRecordParser` reads the allocated size at offset 0x28 of a non-resident `$DATA` attribute. For compressed and sparse files the on-disk size is the field at offset 0x40, so the MFT engine over-reports allocated size for those files.

### 4. Memory is about 530 bytes per entry

The managed heap for the `C:\` tree was 5.13 GB with the current scanner and 5.08 GB with the prototype.

| Part | Size for 9.56M entries |
|---|---|
| `FullPath` strings | about 2.5 GB |
| Empty `Children` list allocated for every file | about 0.24 GB |
| `Attributes` strings | about 0.35 GB. The whole drive has 58 distinct values, but each entry gets its own copy from `FileAttributes.ToString()` |

### 5. "Allocated size" opens every file, and the two engines disagree

With `CollectAllocatedSize`, the recursive engine calls `GetCompressedFileSizeW` per file. For an ordinary uncompressed file that API returns the logical size, not the cluster-rounded size on disk. Checked on a 5,000-byte file: `GetCompressedFileSize` returns 5,000; the directory record's `AllocationSize` is 8,192. The MFT engine reports the cluster-rounded value, so the two engines already report different allocated sizes for the same file. Not timed at drive scale.

### 6. The two engines count different things

| `C:\` | Recursive, not elevated | MFT, elevated |
|---|---|---|
| Files | 7,966,116 | 4,831,971 |
| Folders | 1,598,496 | 1,630,098 |
| Logical size | 871 GB | 759 GB |
| Skipped entries | 57,644 | 530,503 |

The runs were hours apart on a drive in use, but that does not explain 3.1 million files. Two causes are visible in the code, and the diagnostic measured both by reading every in-use file record:

| `C:\`, from the `$MFT` | |
|---|---|
| Files | 5,347,477 |
| Names of those files | 8,857,790 |
| Files with the reparse-point attribute | 468,329, holding 551 GB logical |
| Folders | 2,061,713 |
| Logical size of all files | 1,102 GB in base records, plus 239 GB in streams that start in extension records |
| On-disk size, base records only | 557 GB |
| Used space on the volume | 783 GB of 930.5 GB |

- **Reparse-point files.** `MftTreeBuilder` skips every file that has the reparse-point attribute (line 120): 468,329 files holding 551 GB logical. It also leaves out 431,612 folders (2,061,710 enumerated, 1,630,098 reported). The recursive engine applies its reparse check to directories only; on the same drive it skipped 52,612 reparse-point directories and counted everything else. Most of these files are probably cloud placeholders, which Windows shows to ordinary programs as plain files. That last sentence is an inference, not a measurement.
- **Hard links.** `FSCTL_ENUM_USN_DATA` returns one record per file. The drive's 5.35M files have 8.86M names. The MFT engine counts a file once; the recursive engine counts it once per name.

Logical size is not disk use on this drive: 1,341 GB logical against 783 GB used.

Also in that run: 62,174 files went through size resolution and are missing from the final file count. The 530,503 skipped entries are the 468,329 reparse-point files plus those 62,174. `MftTreeBuilder` records a file as skipped when the fallback throws. A likely reason, not verified: when a file's parent directory is not in the tree and the enumeration contains no root record, `ResolveParentDirectory` attaches the file to the volume root, and the path-based fallback then fails because `C:\<name>` does not exist.

## Work items

One pull request per item. Items 1, 2 and 3 each branch from `master`. Item 4 builds on item 3.

### PR 1 — Recursive engine: one listing per directory, parallel across directories

Files: `src/Custodian.Core/Scanning/RecursiveScanProvider.cs`, `ProgressThrottle.cs`, `src/Custodian.Core/Model/ScanOptions.cs`, `src/Custodian.Cli/Program.cs`, `tests/Custodian.Tests/RecursiveScannerTests.cs`.

**Listing.** Replace the two listings with one `System.IO.Enumeration.FileSystemEnumerable<T>` per directory that returns name, attributes, length and last-write time for files and folders alike. Use `EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false }`. The defaults skip hidden and system entries and swallow access-denied errors, and both would change results.

**Test seam.** `IRecursiveScanFileSystem` shrinks to roughly this. `TestRecursiveScanFileSystem` is ported to it.

```csharp
internal readonly record struct ScanItem(string Name, FileAttributes Attributes, long Length, DateTimeOffset LastWriteTimeUtc);

internal interface IRecursiveScanFileSystem
{
    bool DirectoryExists(string path);
    IEnumerable<ScanItem> EnumerateEntries(string directoryPath);
    bool IsCloudFilesReparsePoint(string directoryPath);
    long GetAllocatedSize(string path, long fallbackLength);
}
```

**Traversal.** A work queue of directory entries and N worker tasks. A worker takes a directory, lists it once, creates the child entries, adds them to that directory's `Children`, and queues the subdirectories. Only one worker ever touches a given directory's `Children`. A pending-directory counter ends the run. A LIFO queue keeps the queue short. This is the shape the prototype used:

```csharp
var queue = new BlockingCollection<FileSystemEntry>(new ConcurrentStack<FileSystemEntry>());
var pending = 1;
queue.Add(root);
// each worker:
foreach (var directory in queue.GetConsumingEnumerable(cancellationToken))
{
    subdirectories.Clear();
    ListInto(directory, subdirectories);              // one listing; adds files and folders to directory.Children
    if (subdirectories.Count > 0)
    {
        Interlocked.Add(ref pending, subdirectories.Count);
        foreach (var child in subdirectories) queue.Add(child);
    }
    if (Interlocked.Decrement(ref pending) == 0) queue.CompleteAdding();
}
```

**After the walk.** One single-threaded post-order pass with an explicit stack computes folder sizes and counts and feeds `ScanGlobalIndexBuilder`. Measured: 0.6 s for totals and 1.2 s for the index on 9.56M entries. This removes shared mutable state from the workers and removes the recursion.

**Behaviour that must not change.**

- Child order: folders first, then files, each in listing order. Collect both kinds per directory and add folders first, so exports and saved scans stay identical.
- Reparse points, cloud placeholders and hydration-prone attributes: same decisions and same skip reasons as `ShouldSkipDirectory` and `ShouldAvoidAllocatedSizeProbe` today.
- An unreadable directory is recorded once in `SkippedEntries` with the exception message. Today it is recorded twice, once per listing: the `C:\` run reported 57,644 skipped entries, which is 52,612 reparse points plus 2 × 2,516 unreadable folders. This is the one intended change in output.
- `ScanAsync` still returns its task immediately and still throws `OperationCanceledException` on cancel.
- Root path normalisation and root name stay as `new DirectoryInfo(options.RootPath)` gives them today.

**Details to get right.**

- Degree of parallelism: `Math.Min(Environment.ProcessorCount, 8)` by default. Add `int? MaxDegreeOfParallelism = null` as the last `ScanOptions` parameter and `--threads <n>` on the CLI `scan` command. Use 1 when `options.CloudProvider` is set, so cloud scans behave exactly as they do now.
- A worker that throws must not take the process down or hang the others. Run workers as tasks, cancel a linked token on the first failure, and rethrow from the coordinating thread.
- `SkippedEntries` is filled from several threads. Collect per worker and merge, then sort by path so the result is deterministic.
- `ProgressThrottle` is not thread-safe. Workers add to shared counters with `Interlocked` once per directory, and one thread at a time passes the throttle. Reporting per directory instead of per file is enough at a 250 ms interval.
- Check the cancellation token per directory and every few thousand files inside a large directory.
- Cache `FileAttributes.ToString()` results in a shared dictionary. The whole drive has 58 distinct values.

**Tests.** Keep every existing `RecursiveScannerTests` case passing on the new seam. `ScannerSkipsFileWhenMetadataCannotBeRead` tests a failure that can no longer happen, because length now arrives with the directory record; replace it with a listing that fails part-way, where entries already returned are kept and the directory is recorded as skipped. Add: degree 1 and degree 8 give identical trees, totals, child order and skipped entries on a fake tree with a few thousand folders; an exception in one worker surfaces as a failed scan; cancellation stops all workers.

**Acceptance.**

- `custodian scan C:\ --mode recursive` reports a duration at least 4× lower than `master` on the same machine, both warm.
- On a tree that is not changing, the CSV export from the branch is byte-identical to the export from `master` (`scan <path> --mode recursive --export <file>.csv`).
- `dotnet build Custodian.slnx` has zero warnings and `dotnet test` is green.

### PR 2 — Post-scan UI preparation

Files: `src/Custodian.Core/Model/FileSystemEntry.cs`, `src/Custodian.Core/Presentation/FolderNode.cs`, `ScanViewProjector.cs`, a small new class for the jump index, and the call sites in `src/Custodian.App/MainWindow.xaml.cs` (`PrepareScanUiAsync`, the two places that assign `_folderJumpIndex`, and the jump filter). Per the project rule, new logic goes in Core or `Services/`, not in `MainWindow`.

- `FileSystemEntry.Flatten()`: rewrite with an explicit stack, same pre-order.
- `FolderNode`: build `Children` on first access instead of in `From`. The tree view binds `Children` only for the nodes it creates, and nothing in the app walks the `FolderNode` tree, so only expanded levels are ever built.
- Folder jump index: walk folders only, make the formatted size text of `FolderJumpRow` lazy, and start the build as a background task that the jump box awaits. Showing scan results must not wait for it. Keep today's ordering, by full path with `OrdinalIgnoreCase`.

**Acceptance.** The "UI analysis preparation" phase is under 0.5 s for the `C:\` scan. Folder tree and jump box behave as before. The existing `ScanViewProjectorTests` jump-index test still passes.

### PR 3 — MFT engine: make the per-file size path work

Files: `NtfsRecordSizeResolver.cs`, `NtfsFileRecordParser.cs`, `MftTreeBuilder.cs`, `MftScanProvider.cs`, and their tests.

This is a bug fix first and a clean-up second. The cause is confirmed; see finding 3.

The bug:

- In `NtfsFileRecordParser.TryReadDefaultDataSize`, parse the record as NTFS returns it and remove `ApplyUpdateSequenceFixups` from this path. The function has one caller, and it only ever passes `FSCTL_GET_NTFS_FILE_RECORD` output, which already has the fixups applied.
- Add a parser test with a record in the form NTFS returns: update sequence count 3, and sector-end bytes that hold attribute data, not the update sequence number. The existing tests use a count of 1 and never reach this code.
- Make the failure visible. Count failures by reason: the call failed (with the Win32 error), a different record came back, the record did not parse, the base record has no unnamed `$DATA`. Report the counts in the scan's `Diagnostics`, and add a diagnostic when the record path is switched off. A scan that silently takes the slow path is how this went unnoticed.
- About 3% of files keep their `$DATA` attribute in an extension record. They keep using the fallback; report how many there are. Following attribute lists is not part of this PR.
- Check what happens to a file whose parent directory is not in the tree, for example a skipped reparse-point directory. If the enumeration contains no root record, `missingParentMeansRoot` attaches such a file to the volume root. Until now the path-based fallback failed for those files and kept them out of the result; with sizes read from records they would be counted under the root. Attach a file to the root only when its parent is the root directory, skip the others with a reason, and cover both cases with tree-builder tests.

The clean-up, in the same functions:

- Allocate the `FSCTL_GET_NTFS_FILE_RECORD` input and output buffers once per resolver, and stop copying the record on every call.
- Decide "inside the selected root" once per directory instead of per file, and normalise the selected root once.
- Use the shared attribute-string cache from PR 1.
- Read the on-disk size from offset 0x40 for compressed and sparse `$DATA` attributes, with a parser test built from a synthetic record.

**Acceptance (Colten, elevated).** `NTFS record sizes parsed` is about 97% of the files resolved, and "Size resolution" is under about 40 s, down from 416 s. Totals match the `master` run apart from files that changed in between and files that were skipped before only because the fallback failed.

### PR 4 — MFT engine: resolve sizes on several threads

Measured gain: the record calls for 5.35M files drop from 14 s on one thread to 3 s on eight, about 11 s per full-drive scan. A direct read (PR 5) makes no per-file calls at all, so build this only if PR 5 is declined.

If it goes ahead: split the file records into ranges and resolve each range on its own thread. Each thread needs its own volume handle and its own resolver, because a synchronous handle serialises I/O. Sizes are written to entries created beforehand; index and totals stay single-threaded afterwards. It uses the same API as today, so the tool's footprint on the system does not change.

`MftTreeBuilder` already takes the size resolver as a delegate, so the partitioning and merge are unit-testable with the fake resolver. The handle-per-thread part can only be checked elevated.

### PR 5 — Decision for Colten: read the `$MFT` directly

Reading the `$MFT` sequentially from the volume handle and parsing records in memory replaces both the USN enumeration and the per-file calls with one sequential read of roughly 1 KB per entry (about 10 GB on this `C:\`). It also gives MFT scans last-write times, which they lack today. This is how the fastest disk analysers work.

Why it matters more than it did: after PR 3 the MFT engine should take an estimated 45–100 s for this drive, and the new recursive engine takes about 75 s. A direct read is the design that puts the MFT engine clearly ahead.

Measured by the diagnostic on this drive:

- The whole `$MFT` (8,570 MB, 8.78M record slots) was read from the volume device in 9.4 s and parsed on one thread in 0.9 s. A full-drive scan should land around 20–30 s. That is an estimate: the 10.4 s measured, plus the tree construction and aggregation the engine does today.
- `FSCTL_GET_RETRIEVAL_POINTERS` works on a handle to `C:\$MFT` opened with `FILE_READ_ATTRIBUTES` only. It returned 53 extents covering the whole file, so the extents need not be decoded from record 0.
- Every `FILE` record read this way had intact fixups, on a system volume in use. 115 slots were not `FILE` records. A reader must still tolerate both.
- There are 857,605 extension records. 169,181 of them hold the start of a file's data stream, and 2.59M file names sit in extension records. With every record in memory, the base-record reference at offset 0x20 ties each one to its file, so sizes and names need no attribute-list parsing.

Reasons to decide before building it:

- It issues raw read requests against the volume device. Endpoint-protection products watch for that pattern because credential-theft tools use it. Custodian targets locked-down environments, so this may matter more than the speed.
- None of it can be exercised without elevation, so every change needs an elevated run by Colten.
- The existing path must stay as the automatic fallback, which is why PR 3 comes first either way.

**Status (Colten, 2026-11-12):** decide after the elevated run on the PR 3 branch. That run gives the real post-fix scan time and the warm enumeration figure to weigh against the estimated 20–30 s.

### PR 6 — Decision for Colten: memory per entry

- Intern extension strings per scan. Cheap and safe; can ride along with PR 1.
- Derive `FullPath` from a parent reference and `Name` when no explicit path was set, with an `AddChild` method that sets the parent. This saves about half of the heap. It touches a type used everywhere, so it is a separate change with its own review.
- Use directory-record `AllocationSize` in the recursive engine instead of opening each file. This removes the per-file call and makes both engines report the same thing, but it changes the numbers users see for ordinary files and needs a custom directory enumerator. Decide whether that change in meaning is wanted.

### PR 7 — Decision for Colten: what a scan counts

See finding 6. Two choices, each of which changes the totals users see:

- Files with the reparse-point attribute (468,329 files, 551 GB logical on this drive): count them in the MFT engine, as the recursive engine does. Their sizes come from the file record, so nothing is opened or downloaded. If most of them are cloud placeholders, their logical size is space in the cloud, not on this disk, which is an argument for showing on-disk size alongside.
- Hard links (5.35M files with 8.86M names on this drive): count a file once (MFT engine today) or once per name (recursive engine today). Once per name overstates the space in use. Once per file makes a folder look smaller than Explorer shows it.

Until this is decided, totals from the two engines cannot be compared, and "the MFT scan matches the recursive scan" is not a usable acceptance test.

## Measuring

```powershell
dotnet build Custodian.slnx -c Release
src\Custodian.Cli\bin\Release\net10.0\Custodian.Cli.exe scan C:\ --mode recursive
```

The CLI prints `Duration`, one `Phase:` line per phase and `Diagnostic:` lines. Run each variant twice and compare the second runs, so both are warm. For a quick loop use `C:\Windows` (about 35 s today). Record before and after numbers in each PR description.

### Elevated checks (Colten)

The implementing session cannot elevate. An administrator prompt starts in `C:\Windows\System32`, so use the full path:

```powershell
E:\Projects\Custodian\src\Custodian.Cli\bin\Release\net10.0\Custodian.Cli.exe scan C:\ --mode mft
```

- `master` baseline: done 2026-11-12. The results are in findings 3 and 6.
- Run it again on the PR 3 branch, and on the PR 4 branch if that item goes ahead. Totals should match between MFT runs apart from files that changed in between.

**Diagnostic: run 2026-11-12.** `scanbench mftdiag C:\` is a throwaway tool from the investigation; it is not in the repository. It only reads: it opens the volume, makes the same NTFS calls the MFT engine makes, and reads the `$MFT` once from the volume device. Its results are in this document:

- Why the record size path fails, and what record calls and enumeration cost: finding 3.
- Reparse-point files, hard-link names and sizes: finding 6.
- Reading the `$MFT` directly: PR 5.

Still open after it: how much of Custodian's 62 s enumeration phase was a cold cache. The elevated run on the PR 3 branch answers that.

## Out of scope

- `ScanStore` save speed (one command per row). It runs only on an explicit save.
- Using the MFT engine for subfolder scans in Auto mode.
- Detecting spinning disks and lowering the thread count for them. All test drives were SSDs. `MaxDegreeOfParallelism` and `--threads` give a manual override; add detection only if a hard disk shows a regression.
- On-disk sizes of files stored by the Windows overlay filter (CompactOS). Whether the MFT engine counts such files at all is PR 7.

## Rules for the implementing session

- Follow `CLAUDE.md` in this repository: Core stays free of UI code, no new logic in `MainWindow.xaml.cs`, `AppLogging` for logging, zero-warning build, tests with new Core behaviour.
- Pull requests are opened as drafts, go through the automated review loop on the draft, and are marked ready only when the final head is clean. Never merge.
- No Plane work item exists for this yet. Do not invent an identifier; use descriptive branch names unless Colten supplies one.
- The session cannot elevate. Anything that needs the MFT engine to actually run is verified by unit tests there and by Colten's elevated run afterwards; say so in the PR description.
