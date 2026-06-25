using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Custodian.Core.Export;
using Custodian.Core.Formatting;
using Custodian.Core.Model;
using Custodian.Core.Portable;
using Custodian.Core.Presentation;
using Custodian.Core.Scanning;
using Custodian.Core.Storage;
using Custodian.Core.Updates;
using Custodian.Platform.Windows.Logging;
using Custodian.Platform.Windows.Services;
using Microsoft.Extensions.Logging;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Custodian.Tui;

#pragma warning disable CS0618 // Terminal.Gui TextView is sufficient for read-only summary/chart panes.

internal static class TuiApplication
{
    public static void Run(TuiLaunchOptions options)
    {
        using var app = Application.Create();
        app.Init();
        using var window = new MainView(options, app);
        app.Run(window);
    }

    private sealed class MainView : Window
    {
        private const int MaxSessionScanCacheEntries = 8;
        private static readonly ILogger Logger = AppLogging.CreateLogger(typeof(MainView).FullName!);
        private readonly DiskScanner _scanner = new();
        private readonly PortableDeviceService _portableDevices = new();
        private readonly CloudProviderDiscoveryService _cloudProviders = new();
        private readonly ScanStore _store = new();
        private readonly AppUpdateService _updates = new();
        private readonly IApplication _app;
        private readonly ObservableCollection<TargetLine> _targets = [];
        private readonly ObservableCollection<DetailLine> _details = [];
        private readonly ObservableCollection<RecycleLine> _recycleRows = [];
        private readonly Dictionary<string, CachedScan> _sessionScanCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> _sessionScanCacheOrder = new();
        private readonly Stack<FileSystemEntry> _backStack = [];
        private readonly Stack<FileSystemEntry> _forwardStack = [];
        private readonly TuiLaunchOptions _options;
        private readonly TextField _pathField;
        private readonly TextField _outputField;
        private readonly TextField _filterField;
        private readonly Button _modeButton;
        private readonly Button _allocatedButton;
        private readonly Button _detailModeButton;
        private readonly Button _chartScopeButton;
        private readonly Button _scanButton;
        private readonly Button _stopButton;
        private readonly ListView _targetList;
        private readonly ListView _detailList;
        private readonly ListView _recycleList;
        private readonly TextView _summaryText;
        private readonly TextView _chartText;
        private readonly Label _statusLabel;
        private readonly Label _progressLabel;
        private CancellationTokenSource? _activeOperation;
        private CancellationTokenSource? _recycleLoadCts;
        private ScanResult? _currentScan;
        private FileSystemEntry? _selectedEntry;
        private DetailMode _detailMode = DetailMode.Contents;
        private ChartScope _chartScope = ChartScope.SelectedFolder;
        private string _scanMode = "auto";
        private bool _collectAllocatedSize;
        private bool _recycleView;
        private bool _suppressTargetSelection;
        private bool _checkingForUpdates;

        public MainView(TuiLaunchOptions options, IApplication app)
        {
            _options = options;
            _app = app;
            _scanMode = NormalizeMode(options.Mode);
            _collectAllocatedSize = options.CollectAllocatedSize;

            Title = " Custodian TUI ";
            Width = Dim.Fill();
            Height = Dim.Fill();

            _pathField = new TextField
            {
                X = 8,
                Y = 0,
                Width = Dim.Percent(45),
                Text = options.TargetPath ?? options.ScanFilePath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            _outputField = new TextField
            {
                X = Pos.Right(_pathField) + 10,
                Y = 0,
                Width = Dim.Fill(1),
                Text = DefaultOutputPath()
            };
            _filterField = new TextField
            {
                X = 8,
                Y = 2,
                Width = Dim.Percent(35),
                Text = string.Empty
            };

            _modeButton = CommandButton("Mode: " + DisplayMode(), Pos.Right(_filterField) + 2, 2, ToggleScanMode);
            _allocatedButton = CommandButton(AllocatedLabel(), Pos.Right(_modeButton) + 1, 2, ToggleAllocated);
            _detailModeButton = CommandButton("View: Contents", Pos.Right(_allocatedButton) + 1, 2, ToggleDetailMode);
            _chartScopeButton = CommandButton("Chart: Selected", Pos.Right(_detailModeButton) + 1, 2, ToggleChartScope);

            _scanButton = CommandButton("Scan", 0, 4, () => _ = StartScanFromPathAsync(useSelectedTarget: true));
            _stopButton = CommandButton("Stop", Pos.Right(_scanButton) + 1, 4, StopActiveOperation);
            var openButton = CommandButton("Open", Pos.Right(_stopButton) + 1, 4, () => _ = OpenScanAsync(PathText()));
            var saveButton = CommandButton("Save", Pos.Right(openButton) + 1, 4, () => _ = SaveScanAsync());
            var csvButton = CommandButton("CSV", Pos.Right(saveButton) + 1, 4, () => _ = ExportAsync("csv"));
            var jsonButton = CommandButton("JSON", Pos.Right(csvButton) + 1, 4, () => _ = ExportAsync("json"));
            var applyFilterButton = CommandButton("Filter", Pos.Right(jsonButton) + 1, 4, RefreshDetails);
            var drillButton = CommandButton("Open/Drill", Pos.Right(applyFilterButton) + 1, 4, () => _ = OpenOrDrillSelectedAsync());
            var deleteButton = CommandButton("Recycle", Pos.Right(drillButton) + 1, 4, () => _ = MoveSelectedToRecycleBinAsync());
            var copyPhoneButton = CommandButton("Copy Phone", Pos.Right(deleteButton) + 1, 4, () => _ = CopyPortableSelectionAsync());

            var backButton = CommandButton("Back", 0, 6, GoBack);
            var forwardButton = CommandButton("Forward", Pos.Right(backButton) + 1, 6, GoForward);
            var upButton = CommandButton("Up", Pos.Right(forwardButton) + 1, 6, GoUp);
            var targetsButton = CommandButton("Targets", Pos.Right(upButton) + 1, 6, () => _ = LoadTargetsAsync());
            var recycleButton = CommandButton("Recycle Bin", Pos.Right(targetsButton) + 1, 6, () => _ = ShowRecycleBinAsync());
            var updateButton = CommandButton("Updates", Pos.Right(recycleButton) + 1, 6, () => _ = CheckForUpdatesAsync());
            var adminButton = CommandButton("Admin", Pos.Right(updateButton) + 1, 6, ToggleAdminLaunch);
            var quitButton = CommandButton("Quit", Pos.Right(adminButton) + 1, 6, () =>
            {
                StopActiveOperation();
                _app.RequestStop();
            });

            _targetList = new ListView
            {
                X = 0,
                Y = 8,
                Width = Dim.Percent(24),
                Height = Dim.Fill(3)
            };
            _targetList.ValueChanged += (_, _) =>
            {
                if (!_suppressTargetSelection)
                {
                    SelectTarget();
                }
            };
            _targetList.Accepting += (_, _) => SelectTarget();

            _detailList = new ListView
            {
                X = Pos.Right(_targetList) + 1,
                Y = 8,
                Width = Dim.Percent(48),
                Height = Dim.Fill(3)
            };
            _detailList.Accepting += (_, _) => _ = OpenOrDrillSelectedAsync();

            _recycleList = new ListView
            {
                X = Pos.Right(_targetList) + 1,
                Y = 8,
                Width = Dim.Percent(48),
                Height = Dim.Fill(3),
                Visible = false
            };
            _recycleList.Accepting += (_, _) => _ = RestoreSelectedRecycleEntryAsync();
            _summaryText = new TextView
            {
                X = Pos.Right(_detailList) + 1,
                Y = 8,
                Width = Dim.Fill(),
                Height = Dim.Percent(34),
                ReadOnly = true,
                Text = "No scan loaded."
            };
            _chartText = new TextView
            {
                X = Pos.Left(_summaryText),
                Y = Pos.Bottom(_summaryText) + 1,
                Width = Dim.Fill(),
                Height = Dim.Fill(3),
                ReadOnly = true,
                Text = "Run or open a scan to render charts."
            };
            _statusLabel = new Label
            {
                X = 0,
                Y = Pos.Bottom(this) - 2,
                Width = Dim.Percent(60),
                Text = "Ready."
            };
            _progressLabel = new Label
            {
                X = Pos.Right(_statusLabel) + 1,
                Y = Pos.Top(_statusLabel),
                Width = Dim.Fill(),
                Text = string.Empty
            };

            Add(
                new Label { X = 0, Y = 0, Text = "Path:" },
                _pathField,
                new Label { X = Pos.Right(_pathField) + 2, Y = 0, Text = "Output:" },
                _outputField,
                new Label { X = 0, Y = 2, Text = "Filter:" },
                _filterField,
                _modeButton,
                _allocatedButton,
                _detailModeButton,
                _chartScopeButton,
                _scanButton,
                _stopButton,
                openButton,
                saveButton,
                csvButton,
                jsonButton,
                applyFilterButton,
                drillButton,
                deleteButton,
                copyPhoneButton,
                backButton,
                forwardButton,
                upButton,
                targetsButton,
                recycleButton,
                updateButton,
                adminButton,
                quitButton,
                new Label { X = 0, Y = 7, Text = "Targets" },
                new Label { X = Pos.Left(_detailList), Y = 7, Text = "Details / Recycle Bin" },
                new Label { X = Pos.Left(_summaryText), Y = 7, Text = "Summary / Chart" },
                _targetList,
                _detailList,
                _recycleList,
                _summaryText,
                _chartText,
                _statusLabel,
                _progressLabel);

            _app.AddTimeout(TimeSpan.Zero, () =>
            {
                _ = LoadInitialAsync();
                return false;
            });
        }

        private static Button CommandButton(string text, Pos x, Pos y, Action action)
        {
            var button = new Button { X = x, Y = y, Text = text };
            button.Accepting += (_, _) => action();
            return button;
        }

        private async Task LoadInitialAsync()
        {
            try
            {
                await LoadTargetsAsync();

                if (!string.IsNullOrWhiteSpace(_options.ScanFilePath))
                {
                    await OpenScanAsync(_options.ScanFilePath);
                    return;
                }

                if (_options.AutoScan && !string.IsNullOrWhiteSpace(_options.TargetPath))
                {
                    await StartScanFromPathAsync(useSelectedTarget: false);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "TUI startup initialization failed.");
                SetStatus("Startup failed: " + ex.Message);
            }
        }

        private async Task LoadTargetsAsync()
        {
            SetStatus("Loading targets...");
            var targets = new List<TargetLine>();
            var driveTargetsTask = Task.Run(CreateDriveTargetLines);
            var cloudTargetsTask = _cloudProviders.GetTargetsAsync();
            var phoneTargetsTask = _portableDevices.GetTargetsAsync();

            try
            {
                targets.AddRange(await driveTargetsTask);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Logger.LogWarning(ex, "Unable to enumerate local drives.");
                SetStatus("Local drives unavailable: " + ex.Message);
            }

            try
            {
                var cloudTargets = await cloudTargetsTask;
                targets.AddRange(cloudTargets.Select(target => new TargetLine(
                    $"Cloud   {target.ProviderName} {target.AccountLabel} - {target.RootPath}",
                    target.RootPath,
                    null,
                    target)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Logger.LogWarning(ex, "Unable to enumerate cloud-provider targets.");
                SetStatus("Cloud targets unavailable: " + ex.Message);
            }

            try
            {
                var phoneTargets = await phoneTargetsTask;
                targets.AddRange(phoneTargets.Select(target => new TargetLine(
                    $"Phone   {target.DisplayPath} - {target.DetailText}",
                    target.DisplayPath,
                    target)));
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Unable to enumerate portable-device targets.");
                SetStatus("Phone targets unavailable: " + ex.Message);
            }

            UpdateUi(() =>
            {
                _targets.Clear();
                foreach (var target in targets)
                {
                    _targets.Add(target);
                }

                _suppressTargetSelection = true;
                try
                {
                    _targetList.SetSource<TargetLine>(_targets);
                }
                finally
                {
                    _suppressTargetSelection = false;
                }

                SetStatus($"Loaded {_targets.Count:n0} target(s).");
            });
        }

        private static IReadOnlyList<TargetLine> CreateDriveTargetLines()
        {
            var targets = new List<TargetLine>();
            foreach (var drive in DriveInfo.GetDrives().OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase))
            {
                targets.Add(CreateDriveTargetLine(drive));
            }

            return targets;
        }

        private static TargetLine CreateDriveTargetLine(DriveInfo drive)
        {
            string detail;
            try
            {
                detail = drive.IsReady
                    ? $"{drive.VolumeLabel} {SizeFormatter.Format(Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace))} used"
                    : "not ready";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Logger.LogWarning(ex, "Unable to read drive details for {Drive}.", drive.Name);
                detail = "unavailable";
            }

            return new TargetLine($"{drive.Name,-8} {detail}", drive.Name, null);
        }

        private async Task StartScanFromPathAsync(bool useSelectedTarget)
        {
            var selectedTarget = useSelectedTarget ? SelectedTarget() : null;
            var shouldUseSelectedTarget = selectedTarget is not null &&
                string.Equals(PathText(), selectedTarget.Path, StringComparison.OrdinalIgnoreCase);

            if (shouldUseSelectedTarget && selectedTarget?.PortableTarget is { } portableTarget)
            {
                await StartPortableScanAsync(portableTarget);
                return;
            }

            var path = shouldUseSelectedTarget && selectedTarget is not null ? selectedTarget.Path : PathText();
            if (string.IsNullOrWhiteSpace(path))
            {
                SetStatus("Enter a path first.");
                return;
            }

            var cloudProvider = shouldUseSelectedTarget && selectedTarget?.CloudProviderTarget is { } cloudTarget
                ? cloudTarget.ToMetadata()
                : _cloudProviders.TryMatchPath(path);
            var mode = ParseMode(_scanMode);
            var cts = await StartOperationAsync();
            await InvokeUiAsync(ShowDetailView);
            SetStatus($"Scanning {path}...");
            try
            {
                var progress = new Progress<ScanProgress>(ReportProgress);
                var result = await _scanner.ScanAsync(
                    new ScanOptions(path, mode, CollectAllocatedSize: _collectAllocatedSize, CloudProvider: cloudProvider),
                    progress,
                    cts.Token);
                if (!await IsCurrentOperationAsync(cts))
                {
                    return;
                }

                RememberScan(result, result.Root, _detailMode, _chartScope);
                if (!await QueryUiAsync(() => _recycleView))
                {
                    ApplyScan(result, result.Root);
                }

                SetStatus($"Scan complete: {SizeFormatter.Format(result.Root.LogicalSizeBytes)}, {result.Root.FileCount:n0} files.");
            }
            catch (OperationCanceledException)
            {
                Logger.LogInformation("Scan cancelled.");
                SetStatus("Scan cancelled.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Scan failed for path {Path}.", path);
                SetStatus("Scan failed: " + ex.Message);
            }
            finally
            {
                await EndOperationAsync(cts);
            }
        }

        private async Task StartPortableScanAsync(PortableDeviceTarget target)
        {
            if (!target.IsAvailable)
            {
                SetStatus(target.DetailText);
                return;
            }

            UpdateUi(() => _pathField.Text = target.DisplayPath);
            var cts = await StartOperationAsync();
            await InvokeUiAsync(ShowDetailView);
            SetStatus($"Scanning phone storage {target.DisplayPath}...");
            try
            {
                var progress = new Progress<ScanProgress>(ReportProgress);
                var result = await _portableDevices.ScanAsync(target, progress, cts.Token);
                if (!await IsCurrentOperationAsync(cts))
                {
                    return;
                }

                RememberScan(result, result.Root, _detailMode, _chartScope);
                if (!await QueryUiAsync(() => _recycleView))
                {
                    ApplyScan(result, result.Root);
                }

                SetStatus($"Phone scan complete: {SizeFormatter.Format(result.Root.LogicalSizeBytes)}, {result.Root.FileCount:n0} files.");
            }
            catch (OperationCanceledException)
            {
                Logger.LogInformation("Phone scan cancelled.");
                SetStatus("Phone scan cancelled.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Phone scan failed for target {Target}.", target.DisplayPath);
                SetStatus("Phone scan failed: " + ex.Message);
            }
            finally
            {
                await EndOperationAsync(cts);
            }
        }

        private async Task OpenScanAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                SetStatus("Enter a .custodian-scan path first.");
                return;
            }

            var cts = await StartOperationAsync();
            try
            {
                SetStatus($"Opening {path}...");
                var result = await _store.LoadAsync(path, cts.Token);
                if (!await IsCurrentOperationAsync(cts))
                {
                    return;
                }

                ApplyScan(result, result.Root);
                RememberScan(result, result.Root, _detailMode, _chartScope);
                UpdateUi(() => _pathField.Text = result.DisplayRootPathOrRoot());
                SetStatus($"Opened scan: {result.DisplayRootPathOrRoot()}.");
            }
            catch (OperationCanceledException)
            {
                Logger.LogInformation("Open scan cancelled.");
                SetStatus("Open cancelled.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Open scan failed for {Path}.", path);
                SetStatus("Open failed: " + ex.Message);
            }
            finally
            {
                await EndOperationAsync(cts);
            }
        }

        private async Task SaveScanAsync()
        {
            if (_currentScan is null)
            {
                SetStatus("No scan to save.");
                return;
            }

            var path = OutputPath(".custodian-scan");
            try
            {
                await _store.SaveAsync(_currentScan, path);
                SetStatus($"Saved {path}.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Save scan failed for {Path}.", path);
                SetStatus("Save failed: " + ex.Message);
            }
        }

        private async Task ExportAsync(string format)
        {
            if (_currentScan is null)
            {
                SetStatus("No scan to export.");
                return;
            }

            var path = OutputPath(format == "json" ? ".json" : ".csv");
            try
            {
                if (format == "json")
                {
                    await ScanExporter.ExportJsonAsync(_currentScan, path);
                }
                else
                {
                    await ScanExporter.ExportCsvAsync(_currentScan, path);
                }

                SetStatus($"Exported {path}.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Export failed for {Path}.", path);
                SetStatus("Export failed: " + ex.Message);
            }
        }

        private void ApplyScan(ScanResult result, FileSystemEntry selected)
        {
            UpdateUi(() =>
            {
                ShowDetailView();
                _currentScan = result;
                _selectedEntry = selected;
                _backStack.Clear();
                _forwardStack.Clear();
                RefreshAll();
            });
        }

        private void RefreshAll()
        {
            RefreshDetails();
        }

        private void RefreshDetails()
        {
            if (_currentScan is null || _selectedEntry is null)
            {
                _details.Clear();
                _detailList.SetSource<DetailLine>(_details);
                return;
            }

            var rows = _detailMode switch
            {
                DetailMode.LargestFiles => ReferenceEquals(_selectedEntry, _currentScan.Root)
                    ? ScanViewProjector.LargestFileRows(_currentScan)
                    : ScanViewProjector.LargestFileRows(_selectedEntry),
                DetailMode.LargestFolders => ReferenceEquals(_selectedEntry, _currentScan.Root)
                    ? ScanViewProjector.LargestFolderRows(_currentScan)
                    : ScanViewProjector.LargestFolderRows(_selectedEntry),
                DetailMode.Extensions => ReferenceEquals(_selectedEntry, _currentScan.Root)
                    ? ScanViewProjector.ExtensionRows(_currentScan)
                    : ScanViewProjector.ExtensionRows(_selectedEntry),
                _ => ScanViewProjector.ChildRows(_selectedEntry)
            };

            var filter = FilterText();
            if (!string.IsNullOrWhiteSpace(filter))
            {
                rows = rows
                    .Where(row => (row.Name ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        (row.FullPath ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        (row.Kind ?? string.Empty).Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            _details.Clear();
            foreach (var row in rows)
            {
                _details.Add(new DetailLine(row));
            }

            _detailList.SetSource<DetailLine>(_details);
            RefreshSummary();
            RefreshChart();
        }

        private void RefreshSummary()
        {
            if (_currentScan is null || _selectedEntry is null)
            {
                _summaryText.Text = "No scan loaded.";
                return;
            }

            var metrics = ScanViewProjector.SelectedSummaryMetrics(_currentScan, _selectedEntry);
            var rootMetrics = ScanViewProjector.SummaryMetrics(_currentScan);
            var breadcrumb = string.Join(" > ", ScanViewProjector.Breadcrumb(_currentScan.Root, _selectedEntry).Select(item => item.Name));
            var lines = new List<string>
            {
                _currentScan.DisplayRootPathOrRoot(),
                breadcrumb,
                string.Empty,
                "Selected",
            };
            lines.AddRange(metrics.Select(metric => $"{metric.Label,-10} {metric.Value,14}  {metric.Detail}"));
            lines.Add(string.Empty);
            lines.Add("Scan");
            lines.AddRange(rootMetrics.Select(metric => $"{metric.Label,-10} {metric.Value,14}  {metric.Detail}"));
            lines.AddRange(_currentScan.Diagnostics.Select(diagnostic => "Diagnostic  " + diagnostic));
            if (_currentScan.SkippedEntries.Count > 0)
            {
                lines.Add($"Skipped     {_currentScan.SkippedEntries.Count:n0}");
            }

            _summaryText.Text = string.Join(Environment.NewLine, lines);
        }

        private void RefreshChart()
        {
            if (_currentScan is null || _selectedEntry is null)
            {
                _chartText.Text = "Run or open a scan to render charts.";
                return;
            }

            var dataset = _chartScope switch
            {
                ChartScope.LargestFolders => ReferenceEquals(_selectedEntry, _currentScan.Root)
                    ? ScanViewProjector.LargestFoldersChart(_currentScan)
                    : ScanViewProjector.LargestFoldersChart(_selectedEntry),
                ChartScope.LargestFiles => ReferenceEquals(_selectedEntry, _currentScan.Root)
                    ? ScanViewProjector.LargestFilesChart(_currentScan)
                    : ScanViewProjector.LargestFilesChart(_selectedEntry),
                ChartScope.Extensions => ReferenceEquals(_selectedEntry, _currentScan.Root)
                    ? ScanViewProjector.ExtensionsChart(_currentScan)
                    : ScanViewProjector.ExtensionsChart(_selectedEntry),
                _ => ScanViewProjector.SelectedFolderChart(_selectedEntry)
            };
            _chartText.Text = TerminalChartRenderer.Render(dataset, width: 58);
        }

        private async Task OpenOrDrillSelectedAsync()
        {
            if (_recycleView)
            {
                await RestoreSelectedRecycleEntryAsync();
                return;
            }

            var line = SelectedDetail();
            if (line is null)
            {
                SetStatus("Select a row first.");
                return;
            }

            var entry = line.Row.Entry;
            if (entry.IsDirectory)
            {
                SelectEntry(entry, pushHistory: true);
                return;
            }

            if (_currentScan?.SourceKind != ScanSourceKind.PortableDevice && !CanUseLocalFileSystemPath(line.Row, allowRemote: true))
            {
                SetStatus("Open is only available for existing file rows.");
                return;
            }

            await OpenEntryAsync(entry, PortableExplorerOpenMode.Open);
        }

        private async Task OpenEntryAsync(FileSystemEntry entry, PortableExplorerOpenMode mode)
        {
            if (_currentScan is null)
            {
                return;
            }

            try
            {
                if (_currentScan.SourceKind == ScanSourceKind.PortableDevice)
                {
                    var result = PortableDeviceExplorerService.Open(_currentScan, entry, mode);
                    SetStatus(result.Message);
                    return;
                }

                var path = entry.FullPath;
                if (!ConfirmLaunchIfRemote(path))
                {
                    return;
                }

                if (mode == PortableExplorerOpenMode.Reveal)
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                    {
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }

                SetStatus($"Opened {path}.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Open failed for entry {EntryPath}.", entry.FullPath);
                SetStatus("Open failed: " + ex.Message);
            }

            await Task.CompletedTask;
        }

        private bool ConfirmLaunchIfRemote(string path)
        {
            if (!IsRemotePath(path))
            {
                return true;
            }

            return MessageBox.Query(
                _app,
                "Confirm Open",
                $"This item is on a network or remote location:{Environment.NewLine}{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}Opening it runs or opens the file from that remote location, which may be unsafe if the scan came from an untrusted source. Open it anyway?",
                "Open",
                "Cancel") == 0;
        }

        private static bool IsRemotePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
                (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsUnc))
            {
                return true;
            }

            try
            {
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrWhiteSpace(root))
                {
                    return false;
                }

                return new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Logger.LogWarning(ex, "Unable to classify launch path {Path}; treating it as remote.", path);
                return true;
            }
        }

        private async Task MoveSelectedToRecycleBinAsync()
        {
            if (_recycleView)
            {
                SetStatus("Select a scan row before using Recycle.");
                return;
            }

            if (_currentScan?.SourceKind == ScanSourceKind.PortableDevice)
            {
                SetStatus("Phone entries are read-only; delete is blocked.");
                return;
            }

            var line = SelectedDetail();
            if (line is null)
            {
                SetStatus("Select a file or folder first.");
                return;
            }

            if (!CanUseLocalFileSystemPath(line.Row))
            {
                SetStatus("Recycle is only available for real file and folder rows.");
                return;
            }

            if (MessageBox.Query(_app, "Recycle", $"Move '{line.Row.Name}' to the Recycle Bin?{Environment.NewLine}{line.Row.FullPath}", "Move", "Cancel") != 0)
            {
                return;
            }

            try
            {
                var result = RecycleBinService.MoveToRecycleBin(line.Row.FullPath, GetConsoleOwnerHandle());
                SetStatus(result == RecycleBinMoveResult.Completed ? "Moved to Recycle Bin." : "Recycle operation cancelled.");
                if (result == RecycleBinMoveResult.Completed)
                {
                    await InvokeUiAsync(() => RemoveEntryFromCurrentScan(line.Row.Entry));
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Recycle failed for {Path}.", line.Row.FullPath);
                SetStatus("Recycle failed: " + ex.Message);
            }
        }

        private static bool CanUseLocalFileSystemPath(DetailRow row, bool allowRemote = false)
        {
            if (string.Equals(row.Kind, "Extension", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(row.FullPath))
            {
                return false;
            }

            try
            {
                if (!Path.IsPathFullyQualified(row.FullPath))
                {
                    return false;
                }

                if (IsRemotePath(row.FullPath))
                {
                    return allowRemote;
                }

                return File.Exists(row.FullPath) || Directory.Exists(row.FullPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Logger.LogWarning(ex, "Unable to validate detail row path {Path}.", row.FullPath);
                return false;
            }
        }

        private async Task CopyPortableSelectionAsync()
        {
            if (_currentScan?.SourceKind != ScanSourceKind.PortableDevice)
            {
                SetStatus("Copy to PC is only available for phone scans.");
                return;
            }

            var line = SelectedDetail();
            if (line is null)
            {
                SetStatus("Select a phone row first.");
                return;
            }

            var destination = ResolvePortableCopyDestination(OutputText());
            if (string.IsNullOrWhiteSpace(destination))
            {
                SetStatus("Enter a PC destination folder in Output.");
                return;
            }

            var cts = await StartOperationAsync();
            try
            {
                var progress = new Progress<PortableCopyProgress>(p =>
                    ReportProgress(new ScanProgress(p.CurrentPath, p.FilesCopied, 0, p.BytesCopied, p.Message)));
                var result = await _portableDevices.CopyToPcAsync(_currentScan, [line.Row.Entry], destination, progress, cts.Token);
                SetStatus($"Copied {result.FilesCopied:n0} file(s), skipped {result.FilesSkipped:n0}.");
            }
            catch (OperationCanceledException)
            {
                Logger.LogInformation("Phone copy cancelled.");
                SetStatus("Phone copy cancelled.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Phone copy failed to destination {Destination}.", destination);
                SetStatus("Phone copy failed: " + ex.Message);
            }
            finally
            {
                await EndOperationAsync(cts);
            }
        }

        private async Task ShowRecycleBinAsync()
        {
            var loadCts = new CancellationTokenSource();
            await InvokeUiAsync(() =>
            {
                CancelRecycleLoad();
                _recycleLoadCts = loadCts;
                _recycleView = true;
                _detailList.Visible = false;
                _recycleList.Visible = true;
            });
            SetStatus("Loading Recycle Bin...");

            try
            {
                var entriesTask = RecycleBinService.GetItemsAsync(loadCts.Token);
                var usageTask = RecycleBinService.GetUsageAsync(loadCts.Token);
                await Task.WhenAll(entriesTask, usageTask);
                var entries = await entriesTask;
                var usage = await usageTask;
                if (!await QueryUiAsync(() => _recycleView && ReferenceEquals(_recycleLoadCts, loadCts)))
                {
                    return;
                }

                UpdateUi(() =>
                {
                    if (!_recycleView)
                    {
                        return;
                    }

                    _recycleRows.Clear();
                    foreach (var row in RecycleBinViewProjector.Rows(entries)
                        .OrderByDescending(row => row.DateDeletedValue)
                        .Select(row => new RecycleLine(row)))
                    {
                        _recycleRows.Add(row);
                    }

                    _recycleList.SetSource<RecycleLine>(_recycleRows);
                    _summaryText.Text = $"Recycle Bin{Environment.NewLine}{usage.ItemCount:n0} item(s){Environment.NewLine}{SizeFormatter.Format(usage.SizeBytes)}";
                    _chartText.Text = "Enter on a row restores it. Use Recycle Bin view for destructive cleanup with confirmation in the desktop app.";
                    _statusLabel.Text = Truncate($"Recycle Bin loaded: {_recycleRows.Count:n0} item(s).", 120);
                });
            }
            catch (OperationCanceledException)
            {
                SetStatus("Recycle Bin load cancelled.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Recycle Bin load failed.");
                SetStatus("Recycle Bin load failed: " + ex.Message);
            }
            finally
            {
                await InvokeUiAsync(() =>
                {
                    if (ReferenceEquals(_recycleLoadCts, loadCts))
                    {
                        _recycleLoadCts = null;
                    }

                    loadCts.Dispose();
                });
            }
        }

        private async Task RestoreSelectedRecycleEntryAsync()
        {
            var line = SelectedRecycle();
            if (line is null)
            {
                SetStatus("Select a Recycle Bin row first.");
                return;
            }

            if (MessageBox.Query(_app, "Restore", $"Restore '{line.Row.Name}'?", "Restore", "Cancel") != 0)
            {
                return;
            }

            try
            {
                var result = await RecycleBinService.RestoreAsync([line.Row.Entry]);
                SetStatus($"Restored {result.ActedOnCount:n0} item(s).");
                await ShowRecycleBinAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Recycle Bin restore failed for {Name}.", line.Row.Name);
                SetStatus("Restore failed: " + ex.Message);
            }
        }

        private async Task CheckForUpdatesAsync()
        {
            if (_checkingForUpdates)
            {
                SetStatus("Update check already running.");
                return;
            }

            _checkingForUpdates = true;
            SetStatus("Checking for updates...");
            try
            {
                var result = await _updates.CheckForUpdatesAsync();
                SetStatus(result.Status.Message);
                if ((result.Status.Kind == AppUpdateStatusKind.ReadyToRestart ||
                    result.Status.Kind == AppUpdateStatusKind.Available) &&
                    await QueryUiAsync(() => _activeOperation is not null))
                {
                    SetStatus("Finish or stop the active operation before applying updates.");
                    return;
                }

                if (result.Status.Kind == AppUpdateStatusKind.ReadyToRestart)
                {
                    await PromptInstallUpdateAsync(result, "Update ready. Install now? The TUI will close; launch it again after the update finishes.");
                    return;
                }

                if (result.Status.Kind == AppUpdateStatusKind.Available &&
                    await ShowQueryAsync("Update", result.Status.Message + Environment.NewLine + "Download now?", "Download", "Cancel") == 0)
                {
                    var cts = await StartOperationAsync(cancelExisting: false);
                    try
                    {
                        await _updates.DownloadUpdatesAsync(result, new Progress<AppUpdateStatus>(status => SetStatus(status.Message)), cts.Token);
                        await PromptInstallUpdateAsync(result, "Update downloaded. Install now? The TUI will close; launch it again after the update finishes.");
                    }
                    catch (OperationCanceledException)
                    {
                        SetStatus("Update download cancelled.");
                    }
                    finally
                    {
                        await EndOperationAsync(cts);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Update check failed.");
                SetStatus("Update check failed: " + ex.Message);
            }
            finally
            {
                _checkingForUpdates = false;
            }
        }

        private async Task PromptInstallUpdateAsync(AppUpdateCheckResult update, string message)
        {
            if (await ShowQueryAsync("Update", message, "Install", "Later") != 0)
            {
                SetStatus(update.Status.Message);
                return;
            }

            await InvokeUiAsync(() =>
            {
                _updates.ApplyUpdatesWithoutRestart(update);
                _app.RequestStop();
            });
        }

        private void ToggleAdminLaunch()
        {
            try
            {
                var enabled = !ElevationService.IsRunAsAdministratorOnLaunchEnabled();
                ElevationService.SetRunAsAdministratorOnLaunch(enabled);
                SetStatus(enabled ? "Always-run-as-admin enabled." : "Always-run-as-admin disabled.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Admin launch setting failed.");
                SetStatus("Admin setting failed: " + ex.Message);
            }
        }

        private void SelectTarget()
        {
            var target = SelectedTarget();
            if (target is null)
            {
                return;
            }

            _pathField.Text = target.Path;
            var cacheKey = target.PortableTarget?.TargetId ?? NormalizeCacheKey(target.Path);
            if (_sessionScanCache.TryGetValue(cacheKey, out var cached))
            {
                ShowDetailView();
                _currentScan = cached.Result;
                _selectedEntry = cached.Selected;
                _detailMode = cached.DetailMode;
                _chartScope = cached.ChartScope;
                _backStack.Clear();
                _forwardStack.Clear();
                RefreshButtonText();
                RefreshAll();
                SetStatus("Restored cached scan.");
            }
            else
            {
                SetStatus("Target selected. Press Scan to analyze.");
            }
        }

        private void ShowDetailView()
        {
            CancelRecycleLoad();
            _recycleView = false;
            _recycleList.Visible = false;
            _detailList.Visible = true;
        }

        private void SelectEntry(FileSystemEntry entry, bool pushHistory)
        {
            if (_selectedEntry is not null && pushHistory)
            {
                _backStack.Push(_selectedEntry);
                _forwardStack.Clear();
            }

            _selectedEntry = entry;
            RememberCurrentState();
            RefreshAll();
        }

        private void GoBack()
        {
            if (_selectedEntry is null || _backStack.Count == 0)
            {
                return;
            }

            _forwardStack.Push(_selectedEntry);
            _selectedEntry = _backStack.Pop();
            RememberCurrentState();
            RefreshAll();
        }

        private void GoForward()
        {
            if (_selectedEntry is null || _forwardStack.Count == 0)
            {
                return;
            }

            _backStack.Push(_selectedEntry);
            _selectedEntry = _forwardStack.Pop();
            RememberCurrentState();
            RefreshAll();
        }

        private void GoUp()
        {
            if (_currentScan is null || _selectedEntry is null)
            {
                return;
            }

            if (ScanViewProjector.TryFindParent(_currentScan.Root, _selectedEntry, out var parent))
            {
                SelectEntry(parent, pushHistory: true);
            }
        }

        private void ToggleDetailMode()
        {
            _detailMode = _detailMode switch
            {
                DetailMode.Contents => DetailMode.LargestFiles,
                DetailMode.LargestFiles => DetailMode.LargestFolders,
                DetailMode.LargestFolders => DetailMode.Extensions,
                _ => DetailMode.Contents
            };
            RefreshButtonText();
            RememberCurrentState();
            RefreshDetails();
        }

        private void ToggleChartScope()
        {
            _chartScope = _chartScope switch
            {
                ChartScope.SelectedFolder => ChartScope.LargestFolders,
                ChartScope.LargestFolders => ChartScope.LargestFiles,
                ChartScope.LargestFiles => ChartScope.Extensions,
                _ => ChartScope.SelectedFolder
            };
            RefreshButtonText();
            RememberCurrentState();
            RefreshChart();
        }

        private void ToggleScanMode()
        {
            _scanMode = _scanMode switch
            {
                "auto" => "recursive",
                "recursive" => "mft",
                _ => "auto"
            };
            RefreshButtonText();
        }

        private void ToggleAllocated()
        {
            _collectAllocatedSize = !_collectAllocatedSize;
            RefreshButtonText();
        }

        private void RefreshButtonText()
        {
            _modeButton.Text = "Mode: " + DisplayMode();
            _allocatedButton.Text = AllocatedLabel();
            _detailModeButton.Text = "View: " + _detailMode switch
            {
                DetailMode.LargestFiles => "Files",
                DetailMode.LargestFolders => "Folders",
                DetailMode.Extensions => "Ext",
                _ => "Contents"
            };
            _chartScopeButton.Text = "Chart: " + _chartScope switch
            {
                ChartScope.LargestFolders => "Folders",
                ChartScope.LargestFiles => "Files",
                ChartScope.Extensions => "Ext",
                _ => "Selected"
            };
        }

        private TargetLine? SelectedTarget()
            => _targetList.SelectedItem is int index && index >= 0 && index < _targets.Count
                ? _targets[index]
                : null;

        private DetailLine? SelectedDetail()
            => _detailList.SelectedItem is int index && index >= 0 && index < _details.Count
                ? _details[index]
                : null;

        private RecycleLine? SelectedRecycle()
            => _recycleList.SelectedItem is int index && index >= 0 && index < _recycleRows.Count
                ? _recycleRows[index]
                : null;

        private async Task<CancellationTokenSource> StartOperationAsync(bool cancelExisting = true)
        {
            var cts = new CancellationTokenSource();
            await InvokeUiAsync(() =>
            {
                if (cancelExisting)
                {
                    _activeOperation?.Cancel();
                }

                _activeOperation = cts;
                _scanButton.Enabled = false;
                _stopButton.Enabled = true;
            });

            return cts;
        }

        private void StopActiveOperation()
        {
            UpdateUi(() => _activeOperation?.Cancel());
        }

        private Task<bool> IsCurrentOperationAsync(CancellationTokenSource cts)
            => QueryUiAsync(() => ReferenceEquals(_activeOperation, cts));

        private void CancelRecycleLoad()
        {
            _recycleLoadCts?.Cancel();
            _recycleLoadCts = null;
        }

        private Task EndOperationAsync(CancellationTokenSource cts)
        {
            return InvokeUiAsync(() =>
            {
                var endedCurrent = ReferenceEquals(_activeOperation, cts);
                if (endedCurrent)
                {
                    _activeOperation = null;
                }

                cts.Dispose();
                if (endedCurrent || _activeOperation is null)
                {
                    _scanButton.Enabled = true;
                    _stopButton.Enabled = false;
                    _progressLabel.Text = string.Empty;
                }
            });
        }

        private void ReportProgress(ScanProgress progress)
        {
            UpdateUi(() =>
            {
                _progressLabel.Text = $"{progress.Message}: {progress.FilesSeen:n0} files, {SizeFormatter.Format(progress.BytesSeen)}";
                if (!string.IsNullOrWhiteSpace(progress.CurrentPath))
                {
                    _statusLabel.Text = Truncate(progress.CurrentPath, 90);
                }
            });
        }

        private void SetStatus(string message)
            => UpdateUi(() => _statusLabel.Text = Truncate(message, 120));

        private void UpdateUi(Action action)
        {
            _app.Invoke(action);
        }

        private Task InvokeUiAsync(Action action)
            => QueryUiAsync(() =>
            {
                action();
                return true;
            });

        private Task<T> QueryUiAsync<T>(Func<T> query)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            UpdateUi(() =>
            {
                try
                {
                    completion.TrySetResult(query());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });

            return completion.Task;
        }

        private Task<int?> ShowQueryAsync(string title, string message, params string[] buttons)
            => QueryUiAsync(() => MessageBox.Query(_app, title, message, buttons));

        private void RememberCurrentState()
        {
            if (_currentScan is not null && _selectedEntry is not null)
            {
                RememberScan(_currentScan, _selectedEntry, _detailMode, _chartScope);
            }
        }

        private void RememberScan(ScanResult result, FileSystemEntry selected, DetailMode detailMode, ChartScope chartScope)
        {
            UpdateUi(() =>
            {
                var key = ScanCacheKey(result);
                if (_sessionScanCache.ContainsKey(key))
                {
                    _sessionScanCacheOrder.Remove(key);
                }

                _sessionScanCache[key] = new CachedScan(result, selected, detailMode, chartScope);
                _sessionScanCacheOrder.AddFirst(key);
                while (_sessionScanCacheOrder.Count > MaxSessionScanCacheEntries)
                {
                    var last = _sessionScanCacheOrder.Last?.Value;
                    if (last is null)
                    {
                        break;
                    }

                    _sessionScanCache.Remove(last);
                    _sessionScanCacheOrder.RemoveLast();
                }
            });
        }

        private void RemoveEntryFromCurrentScan(FileSystemEntry entry)
        {
            if (_currentScan is null)
            {
                return;
            }

            if (ReferenceEquals(_currentScan.Root, entry))
            {
                _currentScan = null;
                _selectedEntry = null;
                RefreshAll();
                return;
            }

            if (TuiScanTreeUpdater.RemoveEntry(_currentScan, entry, ref _selectedEntry))
            {
                _backStack.Clear();
                _forwardStack.Clear();
                RememberCurrentState();
                RefreshAll();
            }
        }

        private static string ScanCacheKey(ScanResult result)
            => string.IsNullOrWhiteSpace(result.SourceId)
                ? NormalizeCacheKey(result.RootPath)
                : result.SourceId;

        private static string NormalizeCacheKey(string path)
        {
            try
            {
                return ScanPathUtility.NormalizeRoot(path);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Unable to normalize scan cache key for {Path}.", path);
                return path;
            }
        }

        private string PathText() => _pathField.Text?.ToString() ?? string.Empty;
        private string OutputText() => _outputField.Text?.ToString() ?? string.Empty;
        private string FilterText() => _filterField.Text?.ToString() ?? string.Empty;

        private static string ResolvePortableCopyDestination(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            try
            {
                if (Directory.Exists(output))
                {
                    return output;
                }

                if (File.Exists(output) || !string.IsNullOrWhiteSpace(Path.GetExtension(output)))
                {
                    return Path.GetDirectoryName(output) ?? string.Empty;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Logger.LogWarning(ex, "Unable to classify portable-copy output path {Path}.", output);
                return string.Empty;
            }

            return output;
        }

        private string OutputPath(string extension)
        {
            var output = OutputText();
            if (string.IsNullOrWhiteSpace(output))
            {
                output = DefaultOutputPath();
            }

            if (Directory.Exists(output))
            {
                var rootName = SanitizeFileName(_currentScan?.Root.Name ?? "custodian-scan");
                return Path.Combine(output, rootName + extension);
            }

            return Path.ChangeExtension(output, extension);
        }

        private static string DefaultOutputPath()
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "custodian-scan.custodian-scan");

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars().ToHashSet();
            var safe = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
            return string.IsNullOrWhiteSpace(safe) ? "custodian-scan" : safe;
        }

        private static ScanMode ParseMode(string mode)
            => NormalizeMode(mode) switch
            {
                "recursive" => ScanMode.Recursive,
                "mft" => ScanMode.Mft,
                _ => ScanMode.Auto
            };

        private static string NormalizeMode(string mode)
            => mode.ToLowerInvariant() switch
            {
                "recursive" => "recursive",
                "mft" => "mft",
                _ => "auto"
            };

        private string DisplayMode() => _scanMode switch
        {
            "recursive" => "Recursive",
            "mft" => "MFT",
            _ => "Auto"
        };

        private string AllocatedLabel() => _collectAllocatedSize ? "Allocated: On" : "Allocated: Off";

        private static string Truncate(string? text, int length)
        {
            if (text is null)
            {
                return string.Empty;
            }

            if (text.Length <= length)
            {
                return text;
            }

            return length <= 3 ? text[..length] : text[..(length - 3)] + "...";
        }

        private static IntPtr GetConsoleOwnerHandle()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return process.MainWindowHandle;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Logger.LogWarning(ex, "Unable to get current process main window handle for Recycle Bin ownership.");
            }

            return GetConsoleWindow();
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        private sealed record CachedScan(ScanResult Result, FileSystemEntry Selected, DetailMode DetailMode, ChartScope ChartScope);
    }
}

#pragma warning restore CS0618

internal static class ScanResultExtensions
{
    public static string DisplayRootPathOrRoot(this ScanResult result)
        => string.IsNullOrWhiteSpace(result.DisplayRootPath) ? result.RootPath : result.DisplayRootPath;
}
