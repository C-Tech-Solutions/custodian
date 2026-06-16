using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Interop;
using Custodian.App.Controls;
using Custodian.App.Logging;
using Custodian.App.Services;
using Microsoft.Extensions.Logging;
using Custodian.Core.Export;
using Custodian.Core.Formatting;
using Custodian.Core.Model;
using Custodian.Core.Presentation;
using Custodian.Core.Scanning;
using Custodian.Core.Storage;
using Custodian.Core.Updates;
using WinForms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMessageBox = System.Windows.MessageBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Custodian.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const double DefaultRightPanelWidth = 350;
    private const double CollapsedRightPanelWidth = 36;
    private const int MaxSessionScanCacheEntries = 8;
    private const string DefaultEmptyStateTitle = "Scan a folder to get started";
    private const string DefaultEmptyStateDetail = "Pick a drive or folder, then press Scan. You can also drag a folder onto this window.";

    private static readonly TimeSpan StartupUpdateCheckTimeout = TimeSpan.FromSeconds(15);
    private static readonly ILogger Logger = AppLogging.CreateLogger(typeof(MainWindow).FullName!);
    private readonly DiskScanner _scanner = new();
    private readonly ScanStore _store = new();
    private readonly AppUpdateService _updates = new();
    private ActiveScanJob? _activeScan;
    private CancellationTokenSource? _updateCts;
    private CancellationTokenSource? _recycleBinCts;
    private bool _recycleBinCtsIsRefresh;
    private ScanResult? _currentScan;
    private string? _visibleScanKey;
    private FileSystemEntry? _selectedEntry;
    private DetailViewMode _viewMode = DetailViewMode.Contents;
    private ChartScope _chartScope = ChartScope.SelectedFolder;
    private ChartDisplayMode _chartDisplayMode = ChartDisplayMode.Treemap;
    private string? _selectedChartSourceKey;
    private bool _suppressChartSelection;
    private bool _suppressJumpSelection;
    private readonly Stack<FileSystemEntry> _backStack = new();
    private readonly Stack<FileSystemEntry> _forwardStack = new();
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private readonly Dictionary<string, CachedScan> _sessionScanCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _sessionScanCacheOrder = new();
    private readonly UiSettings _settings;
    private readonly string? _launchPath;
    private DispatcherTimer? _scanProgressTimer;
    private DateTime _scanStarted;
    private long _scanFilesSeen;
    private long _scanBytesSeen;
    private string _scanCurrentPath = string.Empty;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly DispatcherTimer _folderJumpDebounceTimer;
    private IReadOnlyList<FolderJumpRow> _folderJumpIndex = [];
    private readonly object _globalDetailRowsCacheGate = new();
    private readonly Dictionary<DetailViewMode, IReadOnlyList<DetailRow>> _globalDetailRowsCache = [];
    private CachedScan? _visibleCachedScan;
    private int _detailRefreshVersion;
    private int _chartRefreshVersion;
    private int _targetSelectionVersion;
    private IReadOnlyList<DetailRow>? _boundDetailRows;
    private DetailSortColumn? _detailSortColumn;
    private ListSortDirection _detailSortDirection = ListSortDirection.Ascending;
    private bool _isRecycleBinViewActive;
    private bool _suppressTargetSelection;
    private bool _suppressChartScopeSelection;
    private string _recycleBinFilterText = string.Empty;
    private RecycleBinSortColumn _recycleBinSortColumn = RecycleBinSortColumn.DateDeleted;
    private ListSortDirection _recycleBinSortDirection = ListSortDirection.Descending;
    private long _recycleBinShellItemCount;
    private bool _settingsPersistedForClose;
    private bool _isClosing;

    public ObservableCollection<DriveRow> DriveRows { get; } = [];
    public ObservableCollection<TargetRow> TargetRows { get; } = [];
    public ObservableCollection<string> RecentPaths { get; } = [];
    public BulkObservableCollection<FolderNode> FolderNodes { get; } = [];
    public BulkObservableCollection<DetailRow> DetailRows { get; } = [];
    public BulkObservableCollection<ChartSlice> ChartSlices { get; } = [];
    public BulkObservableCollection<SummaryMetric> SummaryMetrics { get; } = [];
    public BulkObservableCollection<BreadcrumbItem> BreadcrumbItems { get; } = [];
    public BulkObservableCollection<FolderJumpRow> FolderJumpRows { get; } = [];
    public BulkObservableCollection<RecycleBinRow> RecycleBinRows { get; } = [];

    public ICollectionView DetailRowsView { get; }
    public ICollectionView RecycleBinRowsView { get; }

    public MainWindow(string? launchPath = null)
    {
        InitializeComponent();
        DataContext = this;
        _launchPath = launchPath;

        _settings = UiSettingsStore.Load();
        ApplySettingsEarly();

        DetailRowsView = CollectionViewSource.GetDefaultView(DetailRows);
        DetailRowsView.Filter = RowMatchesFilter;
        RecycleBinRowsView = CollectionViewSource.GetDefaultView(RecycleBinRows);
        RecycleBinRowsView.Filter = RecycleBinRowMatchesFilter;

        PathBox.ItemsSource = RecentPaths;
        JumpBox.ItemsSource = FolderJumpRows;
        ChartScopeBox.SelectedIndex = 0;
        EmptyStateDrives.ItemsSource = DriveRows;

        _settingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _settingsSaveTimer.Tick += SettingsSaveTimer_Tick;
        _folderJumpDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(175)
        };
        _folderJumpDebounceTimer.Tick += (_, _) =>
        {
            _folderJumpDebounceTimer.Stop();
            RefreshFolderJumpRows(JumpBox.Text);
        };

        SeedPathFromSettings();
        InstallKeyBindings();
        ApplySettingsLate();

        UpdateChartModeVisibility();
        UpdateEmptyStateVisibility();
        UpdateFilterUiState();
        RefreshDetailSortHeaders();
        ApplyRecycleBinSort();
        UpdateRecycleBinFilterUiState();
        UpdateRecycleBinActionState();
        RefreshElevationWarning();

        SizeChanged += (_, _) => ScheduleSettingsSave();
        LocationChanged += (_, _) => ScheduleSettingsSave();
        StateChanged += (_, _) => ScheduleSettingsSave();
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        SourceInitialized += (_, _) => ApplyNativeTitleBarTheme();

        ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
        RefreshThemeMenuChecks();
        Loaded += MainWindow_Loaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await LoadDriveRowsAsync();
        if (_settings.AutoUpdateOnStartup)
        {
            await CheckForUpdatesAsync(UpdateCheckMode.StartupAutoDownload);
        }
    }

    private void ThemeManager_ThemeChanged(object? sender, AppTheme theme)
    {
        ApplyNativeTitleBarTheme();
        RefreshThemeMenuChecks();
        Treemap?.InvalidateVisual();
        PieChart?.InvalidateVisual();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
        Closed -= MainWindow_Closed;
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_settingsPersistedForClose)
        {
            return;
        }

        e.Cancel = true;
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        IsEnabled = false;
        _activeScan?.Cancellation.Cancel();
        _updateCts?.Cancel();
        _recycleBinCts?.Cancel();
        _settingsSaveTimer.Stop();
        await PersistSettingsAsync();
        _settingsPersistedForClose = true;
        Close();
    }

    private void RefreshElevationWarning()
    {
        var isElevated = ElevationService.IsRunningAsAdministrator();
        ElevationWarningBanner.Visibility = isElevated ? Visibility.Collapsed : Visibility.Visible;
        RelaunchAsAdminButton.IsEnabled = !isElevated;
        AutoRelaunchAdminMenuItem.IsChecked = IsRunAsAdministratorOnLaunchEnabled();
    }

    private bool IsRunAsAdministratorOnLaunchEnabled()
    {
        try
        {
            return ElevationService.IsRunAsAdministratorOnLaunchEnabled();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to read administrator launch setting.");
            return _settings.AutoRelaunchAsAdministrator;
        }
    }

    private async void RelaunchAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        RelaunchAsAdminButton.IsEnabled = false;
        IsEnabled = false;
        try
        {
            _isClosing = true;
            _settingsSaveTimer.Stop();
            await PersistSettingsAsync();

            ElevationService.RelaunchAsAdministrator(
                Environment.GetCommandLineArgs().Skip(1),
                PathBox.Text?.Trim());

            _activeScan?.Cancellation.Cancel();
            _updateCts?.Cancel();
            _recycleBinCts?.Cancel();
            _settingsPersistedForClose = true;
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex) when (ElevationService.IsElevationCancelled(ex))
        {
            _isClosing = false;
            IsEnabled = true;
            ShowToast("Administrator relaunch cancelled.");
            RefreshElevationWarning();
        }
        catch (Exception ex)
        {
            _isClosing = false;
            Logger.LogError(ex, "Failed to relaunch Custodian as administrator.");
            IsEnabled = true;
            RelaunchAsAdminButton.IsEnabled = true;
            ShowOperationError("Relaunch as administrator failed", ex);
        }
    }

    // ============================================================
    //  Settings
    // ============================================================
    private void ApplySettingsEarly()
    {
        // Theme has to apply before InitializeComponent finishes laying out — but
        // since this runs in the ctor before any visuals matter, calling Apply here is fine.
        ThemeManager.Apply(ThemeManager.ParseOrDefault(_settings.Theme));

        if (_settings.WindowWidth > 400) Width = _settings.WindowWidth;
        if (_settings.WindowHeight > 400) Height = _settings.WindowHeight;
        if (!double.IsNaN(_settings.WindowLeft)) Left = _settings.WindowLeft;
        if (!double.IsNaN(_settings.WindowTop)) Top = _settings.WindowTop;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void ApplySettingsLate()
    {
        if (_settings.LeftPanelWidth > 0)
        {
            LeftCol.Width = new GridLength(_settings.LeftPanelWidth);
        }
        _savedRightWidth = _settings.RightPanelWidth > CollapsedRightPanelWidth
            ? _settings.RightPanelWidth
            : DefaultRightPanelWidth;
        RightCol.Width = new GridLength(_savedRightWidth);
        if (_settings.RightPanelCollapsed)
        {
            CollapseRight();
        }

        AutoRelaunchAdminMenuItem.IsChecked = IsRunAsAdministratorOnLaunchEnabled();
        AutoUpdateOnStartupMenuItem.IsChecked = _settings.AutoUpdateOnStartup;

        _chartDisplayMode = _settings.ChartMode switch
        {
            "Pie" => ChartDisplayMode.Pie,
            "Bars" => ChartDisplayMode.Bars,
            _ => ChartDisplayMode.Treemap
        };
        TreemapChartModeButton.IsChecked = _chartDisplayMode == ChartDisplayMode.Treemap;
        PieChartModeButton.IsChecked = _chartDisplayMode == ChartDisplayMode.Pie;
        BarsChartModeButton.IsChecked = _chartDisplayMode == ChartDisplayMode.Bars;

        foreach (var path in _settings.RecentPaths.Take(15))
        {
            AddRecentPath(path);
        }
    }

    private void SeedPathFromSettings()
    {
        var path = !string.IsNullOrWhiteSpace(_launchPath)
            ? _launchPath
            : !string.IsNullOrWhiteSpace(_settings.LastPath)
            ? _settings.LastPath
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        PathBox.Text = path;
    }

    private void ScheduleSettingsSave()
    {
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private async void SettingsSaveTimer_Tick(object? sender, EventArgs e)
    {
        _settingsSaveTimer.Stop();
        await PersistSettingsAsync();
    }

    private void CaptureSettings()
    {
        _settings.Theme = ThemeManager.Current.ToString();
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
        }
        _settings.LeftPanelWidth = LeftCol.Width.IsAbsolute ? LeftCol.Width.Value : 320;
        if (RightPanel.Visibility == Visibility.Visible
            && RightCol.Width.IsAbsolute
            && RightCol.Width.Value > 0)
        {
            _savedRightWidth = RightCol.Width.Value;
        }
        _settings.RightPanelWidth = _savedRightWidth;
        _settings.RightPanelCollapsed = RightPanel.Visibility != Visibility.Visible;
        _settings.ChartMode = _chartDisplayMode.ToString();
        _settings.LastPath = PathBox.Text ?? string.Empty;
        _settings.AutoRelaunchAsAdministrator = AutoRelaunchAdminMenuItem.IsChecked;
        _settings.AutoUpdateOnStartup = AutoUpdateOnStartupMenuItem.IsChecked;
        _settings.RecentPaths = RecentPaths.Take(15).ToList();
    }

    private async Task PersistSettingsAsync()
    {
        CaptureSettings();
        await UiSettingsStore.SaveAsync(_settings);
    }

    // ============================================================
    //  Keyboard shortcuts
    // ============================================================
    private void InstallKeyBindings()
    {
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => _ = StartScanAsync()), new KeyGesture(Key.F5)));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => StopScan()), new KeyGesture(Key.Escape)));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => OpenScan_Click(this, new RoutedEventArgs())), new KeyGesture(Key.O, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => SaveScan_Click(this, new RoutedEventArgs())), new KeyGesture(Key.S, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => ExportCsv_Click(this, new RoutedEventArgs())), new KeyGesture(Key.E, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => { PathBox.Focus(); (PathBox.Template?.FindName("PART_EditableTextBox", PathBox) as WpfTextBox)?.SelectAll(); }), new KeyGesture(Key.L, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => { FilterBox.Focus(); FilterBox.SelectAll(); }), new KeyGesture(Key.F, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => { JumpBox.Focus(); JumpBox.IsDropDownOpen = true; }), new KeyGesture(Key.K, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => { ThemeManager.Toggle(); ScheduleSettingsSave(); }), new KeyGesture(Key.T, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => ShowShortcuts()), new KeyGesture(Key.OemQuestion, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => GoBack()), new KeyGesture(Key.Left, ModifierKeys.Alt)));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => GoForward()), new KeyGesture(Key.Right, ModifierKeys.Alt)));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => GoUp()), new KeyGesture(Key.Up, ModifierKeys.Alt)));
    }

    private sealed class RelayCommand(Action<object?> execute) : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute(parameter);
#pragma warning disable CS0067
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
    }

    // ============================================================
    //  Scan
    // ============================================================
    private async void Start_Click(object sender, RoutedEventArgs e) => await StartScanAsync();
    private void Stop_Click(object sender, RoutedEventArgs e) => StopScan();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await StartScanAsync();

    private void StopScan() => _activeScan?.Cancellation.Cancel();

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(UpdateCheckMode.Manual);
    }

    private void AutoUpdateOnStartup_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoUpdateOnStartup = AutoUpdateOnStartupMenuItem.IsChecked;
        ScheduleSettingsSave();
    }

    private async Task CheckForUpdatesAsync(UpdateCheckMode mode)
    {
        if (_isClosing || _updateCts is not null)
        {
            return;
        }

        var previousFooterStatus = FooterStatus.Text;
        var previousFooterDetail = FooterDetail.Text;
        _updateCts = new CancellationTokenSource();
        if (mode == UpdateCheckMode.StartupAutoDownload)
        {
            _updateCts.CancelAfter(StartupUpdateCheckTimeout);
        }

        CheckUpdatesMenuItem.IsEnabled = false;
        if (mode == UpdateCheckMode.Manual)
        {
            ApplyUpdateStatus(AppUpdateStatusFactory.Checking());
        }

        try
        {
            var result = await _updates.CheckForUpdatesAsync().WaitAsync(_updateCts.Token);
            if (mode == UpdateCheckMode.StartupAutoDownload)
            {
                _updateCts.CancelAfter(Timeout.InfiniteTimeSpan);
            }

            if (ShouldShowUpdateStatus(mode, result.Status.Kind))
            {
                ApplyUpdateStatus(result.Status);
            }

            switch (result.Status.Kind)
            {
                case AppUpdateStatusKind.NotInstalled:
                    if (mode == UpdateCheckMode.Manual)
                    {
                        UpdateDialog.ShowInformation(this, "Updates unavailable", result.Status.Message, UpdateDialogTone.Warning);
                    }
                    else
                    {
                        RestoreFooterStatus(previousFooterStatus, previousFooterDetail);
                    }
                    break;
                case AppUpdateStatusKind.UpToDate:
                    if (mode == UpdateCheckMode.Manual)
                    {
                        UpdateDialog.ShowInformation(this, "Custodian is up to date", result.Status.Message, UpdateDialogTone.Success);
                    }
                    else
                    {
                        RestoreFooterStatus(previousFooterStatus, previousFooterDetail);
                    }
                    break;
                case AppUpdateStatusKind.Available:
                    if (mode == UpdateCheckMode.StartupAutoDownload)
                    {
                        await DownloadAndPromptInstallAsync(result, _updateCts.Token);
                    }
                    else
                    {
                        await PromptDownloadAndInstallAsync(result, _updateCts.Token);
                    }
                    break;
                case AppUpdateStatusKind.ReadyToRestart:
                    await PromptRestartForInstallAsync(result);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            if (mode == UpdateCheckMode.Manual)
            {
                UpdateFooterStatus("Updates", "Update check cancelled.");
            }
            else
            {
                RestoreFooterStatus(previousFooterStatus, previousFooterDetail);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Update check failed (mode={UpdateCheckMode}).", mode);
            var status = AppUpdateStatusFactory.Failed(ex.Message);
            if (mode == UpdateCheckMode.Manual)
            {
                ApplyUpdateStatus(status);
                UpdateDialog.ShowInformation(this, "Update failed", status.Message, UpdateDialogTone.Error);
            }
            else
            {
                RestoreFooterStatus(previousFooterStatus, previousFooterDetail);
            }
        }
        finally
        {
            _updateCts?.Dispose();
            _updateCts = null;
            CheckUpdatesMenuItem.IsEnabled = true;
        }
    }

    private static bool ShouldShowUpdateStatus(UpdateCheckMode mode, AppUpdateStatusKind statusKind)
        => mode == UpdateCheckMode.Manual
           || statusKind is AppUpdateStatusKind.Available
               or AppUpdateStatusKind.Downloading
               or AppUpdateStatusKind.ReadyToRestart;

    private void RestoreFooterStatus(string status, string detail)
    {
        if (_activeScan is not null || _isRecycleBinViewActive || FooterStatus.Text != "Updates")
        {
            return;
        }

        FooterStatus.Text = status;
        FooterDetail.Text = detail;
    }

    private async Task PromptDownloadAndInstallAsync(AppUpdateCheckResult result, CancellationToken cancellationToken)
    {
        var availableVersion = result.Status.AvailableVersion ?? "the latest version";
        var shouldDownload = UpdateDialog.ShowConfirmation(
            this,
            "Update available",
            $"{result.Status.Message}\n\nDownload it now? Custodian will ask before restarting to install it.",
            "Download",
            "Not now");

        if (!shouldDownload)
        {
            UpdateFooterStatus("Updates", $"Update available: Custodian {availableVersion}.");
            return;
        }

        await DownloadAndPromptInstallAsync(result, cancellationToken);
    }

    private async Task DownloadAndPromptInstallAsync(AppUpdateCheckResult result, CancellationToken cancellationToken)
    {
        var availableVersion = result.Status.AvailableVersion ?? "the latest version";
        var progress = new Progress<AppUpdateStatus>(ApplyUpdateStatus);
        await _updates.DownloadUpdatesAsync(result, progress, cancellationToken);

        var readyStatus = AppUpdateStatusFactory.ReadyToRestart(availableVersion);
        ApplyUpdateStatus(readyStatus);
        await PromptRestartForInstallAsync(result, readyStatus);
    }

    private Task PromptRestartForInstallAsync(AppUpdateCheckResult result)
        => PromptRestartForInstallAsync(result, result.Status);

    private async Task PromptRestartForInstallAsync(AppUpdateCheckResult result, AppUpdateStatus status)
    {
        var shouldRestart = UpdateDialog.ShowConfirmation(
            this,
            "Install update",
            $"{status.Message}\n\nRestart Custodian now to install it?",
            "Restart now",
            "Later");

        if (shouldRestart)
        {
            await ApplyUpdateAndShutdownAsync(result);
            return;
        }

        UpdateFooterStatus("Updates", "Update downloaded. Use Help > Check for Updates when you are ready to restart and install.");
    }

    private void ApplyUpdateStatus(AppUpdateStatus status)
    {
        UpdateFooterStatus("Updates", status.Message);
    }

    private async Task ApplyUpdateAndShutdownAsync(AppUpdateCheckResult result)
    {
        _isClosing = true;
        _activeScan?.Cancellation.Cancel();
        _updateCts?.Cancel();
        _settingsSaveTimer.Stop();
        await PersistSettingsAsync();
        _settingsPersistedForClose = true;
        UpdateFooterStatus("Updates", "Installing update...");
        _updates.ApplyUpdatesAndRestart(result);
        System.Windows.Application.Current.Shutdown();
    }

    private async Task StartScanAsync()
    {
        if (_activeScan is not null) return;

        var path = PathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            UpdateFooterStatus("Choose a path to scan.", string.Empty);
            return;
        }

        var navigationVersion = BeginNavigation();
        var scanKey = TryGetScanCacheKey(path, out var normalizedKey)
            ? normalizedKey
            : path;
        var cts = new CancellationTokenSource();
        var scan = new ActiveScanJob(scanKey, path, cts);
        _activeScan = scan;
        _scanStarted = DateTime.UtcNow;
        _scanFilesSeen = 0;
        _scanBytesSeen = 0;
        _scanCurrentPath = path;
        SetScanningState(true);
        RememberCurrentScanState();
        AddRecentPath(path);
        RefreshTargetStatus(path);
        if (_isRecycleBinViewActive)
        {
            await LeaveRecycleBinViewAsync();
        }

        if (TryGetCachedScan(path, out var cached))
        {
            if (await RestoreCachedScanAsync(cached, navigationVersion, showToast: false))
            {
                UpdateFooterStatus("Scanning", $"Refreshing {path}...");
            }
        }
        else
        {
            ClearViewsForNewScan(path, scanKey);
            StartLoadingAnimation();
        }

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                _scanFilesSeen = p.FilesSeen;
                _scanBytesSeen = p.BytesSeen;
                _scanCurrentPath = string.IsNullOrWhiteSpace(p.CurrentPath) ? path : p.CurrentPath;
                LoadingPathText.Text = _scanCurrentPath;
                if (IsShowingActiveScan(scan))
                {
                    UpdateFooterStatus(p.Message, $"{p.FilesSeen:n0} files, {p.DirectoriesSeen:n0} folders, {SizeFormatter.Format(p.BytesSeen)}");
                }
            });

            var mode = ModeBox.SelectedIndex switch
            {
                1 => ScanMode.Recursive,
                2 => ScanMode.Mft,
                _ => ScanMode.Auto
            };

            var result = await _scanner.ScanAsync(
                new ScanOptions(path, mode, CollectAllocatedSize: AllocatedSizeBox.IsChecked == true),
                progress,
                cts.Token);

            var completed = await CacheScanResultAsync(result, scanKey);
            var shouldShowCompletedScan = ShouldShowCompletedScan(scanKey);
            if (shouldShowCompletedScan)
            {
                await RestoreCachedScanAsync(completed, _targetSelectionVersion, showToast: false);
            }
            else if (_currentScan is not null && !_isRecycleBinViewActive)
            {
                UpdateFooterStatus("Ready", BuildFooterDetail(_currentScan));
            }

            ShowToast($"Scan complete: {SizeFormatter.Format(result.Root.LogicalSizeBytes)} in {result.Duration:m\\:ss}");
        }
        catch (OperationCanceledException)
        {
            if (ShouldShowCompletedScan(scanKey) && _currentScan is null)
            {
                UpdateFooterStatus("Cancelled", string.Empty);
                EngineBadge.Text = "Cancelled";
                EngineBadgeDot.Fill = (WpfBrush?)TryFindResource("WarningBrush") ?? WpfBrushes.Orange;
            }
            else if (_currentScan is not null && !_isRecycleBinViewActive)
            {
                UpdateFooterStatus("Ready", BuildFooterDetail(_currentScan));
                EngineBadge.Text = _currentScan.Engine;
                EngineBadgeDot.Fill = (WpfBrush?)TryFindResource("SuccessBrush") ?? WpfBrushes.Green;
            }
            else
            {
                ShowToast($"Scan cancelled: {path}");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Scan failed.");
            if (ShouldShowCompletedScan(scanKey))
            {
                WpfMessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateFooterStatus("Scan failed", ex.Message);
                EngineBadge.Text = "Failed";
                EngineBadgeDot.Fill = (WpfBrush?)TryFindResource("DangerBrush") ?? WpfBrushes.Red;
            }
            else if (_currentScan is not null && !_isRecycleBinViewActive)
            {
                ShowToast($"Scan failed: {path}");
                UpdateFooterStatus("Ready", BuildFooterDetail(_currentScan));
                EngineBadge.Text = _currentScan.Engine;
                EngineBadgeDot.Fill = (WpfBrush?)TryFindResource("SuccessBrush") ?? WpfBrushes.Green;
            }
            else
            {
                ShowToast($"Scan failed: {path}");
            }
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(_activeScan, scan))
            {
                _activeScan = null;
                SetScanningState(false);
                StopLoadingAnimation();
            }
            RefreshTargetStatus(path);
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Choose a folder to scan",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(PathBox.Text)
                ? PathBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            PathBox.Text = dialog.SelectedPath;
            AddRecentPath(dialog.SelectedPath);
        }
    }

    private async void SaveScan_Click(object sender, RoutedEventArgs e)
    {
        if (_currentScan is null) { ShowToast("Run or open a scan first."); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Custodian scan (*.custodian-scan)|*.custodian-scan",
            FileName = "scan.custodian-scan"
        };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                await _store.SaveAsync(_currentScan, dialog.FileName);
                ShowToast($"Saved {Path.GetFileName(dialog.FileName)}");
            }
            catch (Exception ex)
            {
                ShowOperationError("Save failed", ex);
            }
        }
    }

    private void ApplyNativeTitleBarTheme()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var useDarkMode = ThemeManager.UsesDarkChrome ? 1 : 0;
        SetDwmAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDarkMode, nameof(DwmwaUseImmersiveDarkMode));

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var caption = ThemeManager.CaptionColor;
        var captionColor = ColorRef(caption.R, caption.G, caption.B);
        SetDwmAttribute(hwnd, DwmwaCaptionColor, ref captionColor, nameof(DwmwaCaptionColor));

        var captionText = ThemeManager.CaptionTextColor;
        var textColor = ColorRef(captionText.R, captionText.G, captionText.B);
        SetDwmAttribute(hwnd, DwmwaTextColor, ref textColor, nameof(DwmwaTextColor));
    }

    private async void OpenScan_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Custodian scan (*.custodian-scan)|*.custodian-scan"
        };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                RememberCurrentScanState();
                var navigationVersion = BeginNavigation();
                var loaded = await _store.LoadAsync(dialog.FileName);
                var cached = await CacheScanResultAsync(loaded);
                if (!IsCurrentNavigation(navigationVersion))
                {
                    return;
                }

                if (_isRecycleBinViewActive)
                {
                    await LeaveRecycleBinViewAsync();
                }

                PathBox.Text = loaded.RootPath;
                AddRecentPath(loaded.RootPath);
                var restored = await RestoreCachedScanAsync(cached, navigationVersion, showToast: false);
                if (restored)
                {
                    ShowToast($"Opened {Path.GetFileName(dialog.FileName)}");
                }
            }
            catch (Exception ex)
            {
                ShowOperationError("Open failed", ex);
            }
        }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
        => await ExportAsync("CSV files (*.csv)|*.csv", "scan.csv", (r, p) => ScanExporter.ExportCsvAsync(r, p));

    private async void ExportJson_Click(object sender, RoutedEventArgs e)
        => await ExportAsync("JSON files (*.json)|*.json", "scan.json", (r, p) => ScanExporter.ExportJsonAsync(r, p));

    private async Task ExportAsync(string filter, string fileName, Func<ScanResult, string, Task> exporter)
    {
        if (_currentScan is null) { ShowToast("Run or open a scan first."); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = fileName };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                await exporter(_currentScan, dialog.FileName);
                ShowToast($"Exported {Path.GetFileName(dialog.FileName)}");
            }
            catch (Exception ex)
            {
                ShowOperationError("Export failed", ex);
            }
        }
    }

    // ============================================================
    //  Navigation
    // ============================================================
    private void DriveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTargetSelection)
        {
            return;
        }

        if (DriveList.SelectedItem is not TargetRow row)
        {
            return;
        }

        var targetSelectionVersion = BeginNavigation();
        RunUiAction(() => SelectTargetAsync(row, targetSelectionVersion), "Target selection failed");
    }

    private async Task SelectTargetAsync(TargetRow row, int targetSelectionVersion)
    {
        if (row.Kind == TargetKind.RecycleBin)
        {
            RememberCurrentScanState();
            await ShowRecycleBinAsync(targetSelectionVersion);
            return;
        }

        if (!string.IsNullOrWhiteSpace(row.RootPath))
        {
            RememberCurrentScanState();

            if (_isRecycleBinViewActive)
            {
                await LeaveRecycleBinViewAsync();
            }

            PathBox.Text = row.RootPath;
            AddRecentPath(row.RootPath);

            if (_activeScan is { } activeScan && IsScanActive(row.RootPath))
            {
                ShowActiveScanPage(activeScan);
                return;
            }

            if (TryGetCachedScan(row.RootPath, out var cached))
            {
                await RestoreCachedScanAsync(cached, targetSelectionVersion, showToast: false);
                return;
            }

            ShowStartScanPrompt(row.RootPath);
        }
    }

    private async void EmptyStateDrive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
        {
            PathBox.Text = path;
            await StartScanAsync();
        }
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode node) NavigateToFolder(node.Entry);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => GoForward();
    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();

    private void GoBack()
        => RunUiAction(GoBackAsync, "Navigation failed");

    private async Task GoBackAsync()
    {
        await _navigationGate.WaitAsync();
        try
        {
            if (_backStack.Count == 0 || _selectedEntry is null) return;
            _forwardStack.Push(_selectedEntry);
            await NavigateToFolderCoreAsync(_backStack.Pop(), addHistory: false, clearForward: false);
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    private void GoForward()
        => RunUiAction(GoForwardAsync, "Navigation failed");

    private async Task GoForwardAsync()
    {
        await _navigationGate.WaitAsync();
        try
        {
            if (_forwardStack.Count == 0 || _selectedEntry is null) return;
            _backStack.Push(_selectedEntry);
            await NavigateToFolderCoreAsync(_forwardStack.Pop(), addHistory: false, clearForward: false);
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    private void GoUp()
    {
        if (_selectedEntry is not null && TryGetParent(_selectedEntry, out var parent))
        {
            NavigateToFolder(parent);
        }
    }

    private void Breadcrumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BreadcrumbItem item })
        {
            NavigateToFolder(item.Entry);
        }
    }

    private void JumpBox_KeyUp(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _folderJumpDebounceTimer.Stop();
            RefreshFolderJumpRows(JumpBox.Text);
            if (JumpBox.SelectedItem is FolderJumpRow selected) { NavigateToFolder(selected.Entry); return; }
            if (FolderJumpRows.FirstOrDefault() is { } first) NavigateToFolder(first.Entry);
            return;
        }
        if (e.Key is Key.Up or Key.Down or Key.Escape or Key.Tab) return;

        ScheduleFolderJumpRefresh();
    }

    private void JumpBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressJumpSelection || JumpBox.SelectedItem is not FolderJumpRow row) return;
        NavigateToFolder(row.Entry);
    }

    private void ViewMode_Click(object sender, RoutedEventArgs e)
    {
        RunUiAction(async () =>
        {
            if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse(tag, out DetailViewMode mode)) return;
            SetViewMode(mode);
            await RefreshDetailsAsync(refreshContext: false);
            RememberCurrentScanState();
        }, "Failed to change view");
    }

    // ============================================================
    //  Chart
    // ============================================================
    private void ChartScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChartScopeSelection) return;
        if (ChartScopeBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !Enum.TryParse(tag, out ChartScope scope)) return;
        _chartScope = scope;
        _selectedChartSourceKey = null;
        RunUiAction(RefreshChartAsync, "Chart refresh failed");
        RememberCurrentScanState();
    }

    private void ChartMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse(tag, out ChartDisplayMode mode)) return;
        _chartDisplayMode = mode;
        UpdateChartModeVisibility();
        ScheduleSettingsSave();
    }

    private void ResetPieZoom_Click(object sender, RoutedEventArgs e)
    {
        ResetPieZoom();
    }

    private void ResetPieZoom()
    {
        if (PieZoomSlider is not null)
        {
            PieZoomSlider.Value = 1;
        }
    }

    private void Chart_SliceSelected(object sender, ChartSliceEventArgs e)
        => RunUiAction(() => SelectChartSliceAsync(e.Slice, drillIntoFolders: false), "Chart selection failed");

    private void Chart_SliceDoubleClicked(object sender, ChartSliceEventArgs e)
        => RunUiAction(() => SelectChartSliceAsync(e.Slice, drillIntoFolders: true), "Chart selection failed");

    private void ChartBars_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RunUiAction(async () =>
        {
            if (_suppressChartSelection || ChartBars.SelectedItem is not ChartSlice slice) return;
            await SelectChartSliceAsync(slice, drillIntoFolders: false);
        }, "Chart selection failed");
    }

    private void ChartBars_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        RunUiAction(async () =>
        {
            if (ChartBars.SelectedItem is ChartSlice slice) await SelectChartSliceAsync(slice, drillIntoFolders: true);
        }, "Chart selection failed");
    }

    // ============================================================
    //  Grid
    // ============================================================
    private void DetailsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DetailsGrid.SelectedItem is not DetailRow row) return;
        ActivateRow(row);
    }

    private void DetailsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindVisualParent<System.Windows.Controls.ListViewItem>((DependencyObject)e.OriginalSource);
        if (row is null) return;
        row.IsSelected = true;
        row.Focus();
    }

    private void DetailsGrid_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter && DetailsGrid.SelectedItem is DetailRow row)
        {
            ActivateRow(row);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Back)
        {
            GoUp();
            e.Handled = true;
        }
    }

    private void ActivateRow(DetailRow row)
    {
        if (row.Entry.IsDirectory) { NavigateToFolder(row.Entry); return; }
        if (IsExistingFileSystemPath(row.FullPath)) RevealPath(row.FullPath);
    }

    // ============================================================
    //  Filter
    // ============================================================
    private string _filterText = string.Empty;

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filterText = FilterBox.Text ?? string.Empty;
        DetailRowsView.Refresh();
        UpdateFilterUiState();
    }

    private void FilterBox_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearFilter();
            e.Handled = true;
        }
    }

    private void FilterClear_Click(object sender, RoutedEventArgs e) => ClearFilter();

    private void ClearFilter()
    {
        FilterBox.Clear();
        _filterText = string.Empty;
        DetailRowsView.Refresh();
        UpdateFilterUiState();
    }

    private bool RowMatchesFilter(object obj)
    {
        if (string.IsNullOrEmpty(_filterText)) return true;
        if (obj is not DetailRow row) return false;
        return row.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase)
            || row.FullPath.Contains(_filterText, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateFilterUiState()
    {
        var hasText = !string.IsNullOrEmpty(_filterText);
        FilterPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        FilterClearButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        if (hasText)
        {
            var total = DetailRows.Count;
            var visible = DetailRowsView is System.Collections.ICollection collection
                ? collection.Count
                : DetailRowsView.Cast<object>().Count();
            FilterCountText.Text = $"{visible:n0} of {total:n0}";
        }
        else
        {
            FilterCountText.Text = string.Empty;
        }
    }

    private void DetailsColumnHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }
            || !Enum.TryParse(tag, out DetailSortColumn column))
        {
            return;
        }

        if (_detailSortColumn == column)
        {
            _detailSortDirection = _detailSortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            _detailSortColumn = column;
            _detailSortDirection = DefaultSortDirection(column);
        }

        ApplyDetailSort();
        e.Handled = true;
    }

    private void ApplyDetailSort()
    {
        if (DetailRowsView is ListCollectionView listView)
        {
            listView.CustomSort = _detailSortColumn is { } column
                ? new DetailRowComparer(column, _detailSortDirection)
                : null;
        }
        else
        {
            DetailRowsView.Refresh();
        }

        RefreshDetailSortHeaders();
        UpdateFilterUiState();
    }

    private void RefreshDetailSortHeaders()
    {
        SetDetailSortHeader(NameColumnHeader, "Name", DetailSortColumn.Name);
        SetDetailSortHeader(KindColumnHeader, "Type", DetailSortColumn.Kind);
        SetDetailSortHeader(LogicalSizeColumnHeader, "Size", DetailSortColumn.LogicalSize);
        SetDetailSortHeader(PercentColumnHeader, "Share", DetailSortColumn.Percent);
        SetDetailSortHeader(AllocatedSizeColumnHeader, "Allocated", DetailSortColumn.AllocatedSize);
        SetDetailSortHeader(FileCountColumnHeader, "Files", DetailSortColumn.FileCount);
        SetDetailSortHeader(DirectoryCountColumnHeader, "Folders", DetailSortColumn.DirectoryCount);
        SetDetailSortHeader(FullPathColumnHeader, "Path", DetailSortColumn.FullPath);
    }

    private void SetDetailSortHeader(TextBlock header, string label, DetailSortColumn column)
    {
        if (_detailSortColumn == column)
        {
            header.Text = $"{label} {(_detailSortDirection == ListSortDirection.Ascending ? "^" : "v")}";
            header.Foreground = (WpfBrush?)TryFindResource("AccentBrush") ?? header.Foreground;
        }
        else
        {
            header.Text = label;
            header.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private static ListSortDirection DefaultSortDirection(DetailSortColumn column)
    {
        return column is DetailSortColumn.LogicalSize
            or DetailSortColumn.Percent
            or DetailSortColumn.AllocatedSize
            or DetailSortColumn.FileCount
            or DetailSortColumn.DirectoryCount
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
    }

    // ============================================================
    //  Theme / shortcuts / right-panel collapse
    // ============================================================
    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        ScheduleSettingsSave();
    }

    private void AutoRelaunchAdmin_Click(object sender, RoutedEventArgs e)
    {
        var enabled = AutoRelaunchAdminMenuItem.IsChecked;
        try
        {
            ElevationService.SetRunAsAdministratorOnLaunch(enabled);
            _settings.AutoRelaunchAsAdministrator = enabled;
            ScheduleSettingsSave();
            AutoRelaunchAdminMenuItem.IsChecked = IsRunAsAdministratorOnLaunchEnabled();
            ShowToast(enabled
                ? "Custodian will open as administrator next time."
                : "Custodian will no longer always open as administrator.");
        }
        catch (Exception ex)
        {
            AutoRelaunchAdminMenuItem.IsChecked = IsRunAsAdministratorOnLaunchEnabled();
            ShowOperationError("Administrator launch setting failed", ex);
        }
    }

    private void ThemeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: AppTheme theme })
        {
            ThemeManager.Apply(theme);
            ScheduleSettingsSave();
        }

        RefreshThemeMenuChecks();
    }

    private void RefreshThemeMenuChecks()
    {
        if (ThemeSelectorMenu.Items.Count == 0
            || ThemeSelectorMenu.Items[0] is not System.Windows.Controls.MenuItem themeMenuItem)
        {
            return;
        }

        foreach (var item in themeMenuItem.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            if (item.Tag is AppTheme theme)
            {
                item.IsChecked = theme == ThemeManager.Current;
            }
        }
    }

    private void ShowShortcuts_Click(object sender, RoutedEventArgs e) => ShowShortcuts();
    private void ShortcutClose_Click(object sender, RoutedEventArgs e) => ShortcutOverlay.Visibility = Visibility.Collapsed;
    private void ShortcutOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Only dismiss on backdrop click, not on click inside the dialog.
        if (ReferenceEquals(e.Source, ShortcutOverlay)) ShortcutOverlay.Visibility = Visibility.Collapsed;
    }
    private void ShowShortcuts() => ShortcutOverlay.Visibility = Visibility.Visible;

    private double _savedRightWidth = DefaultRightPanelWidth;
    private void CollapseRight_Click(object sender, RoutedEventArgs e) => CollapseRight();
    private void ExpandRight_Click(object sender, RoutedEventArgs e) => ExpandRight();
    private void CollapseRight()
    {
        if (RightPanel.Visibility == Visibility.Visible
            && RightCol.Width.IsAbsolute
            && RightCol.Width.Value > 0)
        {
            _savedRightWidth = RightCol.Width.Value;
        }
        RightPanel.Visibility = Visibility.Collapsed;
        RightPanelTab.Visibility = Visibility.Visible;
        RightCol.Width = new GridLength(CollapsedRightPanelWidth);
        ScheduleSettingsSave();
    }
    private void ExpandRight()
    {
        RightPanel.Visibility = Visibility.Visible;
        RightPanelTab.Visibility = Visibility.Collapsed;
        var restoredWidth = _savedRightWidth > CollapsedRightPanelWidth
            ? _savedRightWidth
            : DefaultRightPanelWidth;
        RightCol.Width = new GridLength(restoredWidth);
        ScheduleSettingsSave();
    }

    // ============================================================
    //  Drag and drop
    // ============================================================
    private void Window_DragOver(object sender, WpfDragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(WpfDataFormats.FileDrop) ? WpfDragDropEffects.Copy : WpfDragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, WpfDragEventArgs e)
    {
        if (!e.Data.GetDataPresent(WpfDataFormats.FileDrop)) return;
        if (e.Data.GetData(WpfDataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        var path = paths.FirstOrDefault(p => Directory.Exists(p)) ?? paths[0];
        if (string.IsNullOrWhiteSpace(path)) return;
        PathBox.Text = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        await StartScanAsync();
    }

    // ============================================================
    //  Recycle Bin
    // ============================================================
    private async void ShowRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        await ShowRecycleBinAsync(BeginNavigation());
    }

    private async Task ShowRecycleBinAsync(int navigationVersion)
    {
        RememberCurrentScanState();
        _isRecycleBinViewActive = true;
        StopLoadingAnimation();
        UpdateEmptyStateVisibility();
        UpdateFooterStatus("Recycle Bin", "Loading Recycle Bin items...");
        await RefreshRecycleBinAsync(navigationVersion);
    }

    private async void BackToScan_Click(object sender, RoutedEventArgs e)
        => await LeaveRecycleBinViewAsync(advanceNavigation: true, restoreVisibleCachedScan: true);

    private async Task LeaveRecycleBinViewAsync(bool advanceNavigation = false, bool restoreVisibleCachedScan = false)
    {
        var navigationVersion = advanceNavigation ? BeginNavigation() : _targetSelectionVersion;

        _isRecycleBinViewActive = false;
        _recycleBinCts?.Cancel();
        UpdateEmptyStateVisibility();
        if (restoreVisibleCachedScan &&
            TryGetVisibleCachedScan(out var cached) &&
            !ReferenceEquals(_currentScan, cached.Result) &&
            await RestoreCachedScanAsync(cached, navigationVersion, showToast: false))
        {
            return;
        }

        if (_currentScan is not null)
        {
            UpdateFooterStatus("Ready", BuildFooterDetail(_currentScan));
        }
        else
        {
            UpdateFooterStatus("Ready", "Choose a path and scan, or open a saved scan.");
        }
    }

    private async void RefreshRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRecycleBinAsync(_targetSelectionVersion);
    }

    private async Task RefreshRecycleBinAsync(int navigationVersion)
    {
        if (_recycleBinCts is not null)
        {
            if (!_recycleBinCtsIsRefresh)
            {
                return;
            }

            _recycleBinCts.Cancel();
        }

        using var cts = new CancellationTokenSource();
        _recycleBinCts = cts;
        _recycleBinCtsIsRefresh = true;
        SetRecycleBinBusy(true, "Loading Recycle Bin items...");
        try
        {
            var entriesTask = RecycleBinService.GetItemsAsync(cts.Token);
            var usageTask = RecycleBinService.GetUsageAsync(cts.Token);
            await Task.WhenAll(entriesTask, usageTask);
            if (!IsActiveRecycleBinRefresh(navigationVersion, cts))
            {
                return;
            }

            var entries = await entriesTask;
            var usage = await usageTask;
            ReplaceCollection(RecycleBinRows, RecycleBinViewProjector.Rows(entries));
            _recycleBinShellItemCount = Math.Max(0, usage.ItemCount);
            ApplyRecycleBinSort();
            UpdateRecycleBinFilterUiState();
            UpdateRecycleBinTargetUsage(usage.SizeBytes, usage.ItemCount);
            var countText = RecycleBinItemCountText(RecycleBinRows.Count);
            var totalSize = SizeFormatter.Format(usage.SizeBytes);
            var shellCountText = RecycleBinItemCountText(_recycleBinShellItemCount);
            if (RecycleBinRows.Count == 0)
            {
                RecycleBinStatusText.Text = _recycleBinShellItemCount == 0
                    ? "The Recycle Bin is empty."
                    : $"Windows reports {shellCountText} using {totalSize}, but no readable item metadata was loaded.";
                UpdateFooterStatus("Recycle Bin", _recycleBinShellItemCount == 0 ? "Empty." : $"{shellCountText} reported by Windows.");
            }
            else
            {
                var skippedCount = Math.Max(0, _recycleBinShellItemCount - RecycleBinRows.Count);
                var skippedText = skippedCount > 0 ? $" ({RecycleBinItemCountText(skippedCount)} unreadable)" : string.Empty;
                RecycleBinStatusText.Text = $"{countText}{skippedText} using {totalSize} in the Windows Recycle Bin.";
                UpdateFooterStatus("Recycle Bin", $"{countText}{skippedText} using {totalSize} loaded.");
            }
        }
        catch (OperationCanceledException)
        {
            if (IsActiveRecycleBinRefresh(navigationVersion, cts))
            {
                UpdateFooterStatus("Recycle Bin", "Recycle Bin refresh cancelled.");
            }
        }
        catch (Exception ex)
        {
            if (!IsActiveRecycleBinRefresh(navigationVersion, cts))
            {
                return;
            }

            _recycleBinShellItemCount = 0;
            RecycleBinGrid.SelectedItems.Clear();
            RecycleBinRows.Clear();
            RecycleBinRowsView.Refresh();
            UpdateRecycleBinFilterUiState();
            RecycleBinStatusText.Text = "Recycle Bin refresh failed.";
            ShowOperationError("Recycle Bin failed", ex);
            UpdateFooterStatus("Recycle Bin", "Recycle Bin refresh failed.");
        }
        finally
        {
            if (ReferenceEquals(_recycleBinCts, cts))
            {
                _recycleBinCts = null;
                _recycleBinCtsIsRefresh = false;
                if (IsCurrentRecycleBinNavigation(navigationVersion))
                {
                    SetRecycleBinBusy(false);
                }
            }
        }
    }

    private async void RestoreRecycleBinSelected_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRecycleBinRows();
        if (rows.Count == 0)
        {
            ShowToast("Select one or more Recycle Bin items first.");
            return;
        }

        var countText = RecycleBinItemCountText(rows.Count);
        if (!UpdateDialog.ShowConfirmation(
            this,
            "Restore Recycle Bin items",
            $"Restore {countText} to the original location Windows recorded for each item?",
            "Restore",
            "Cancel",
            subtitle: "Recycle Bin"))
        {
            return;
        }

        await RunRecycleBinOperationAsync(
            "Restoring Recycle Bin items...",
            async token => BuildRecycleBinOperationMessage(
                "Restored",
                await RecycleBinService.RestoreAsync(rows.Select(row => row.Entry).ToList(), token)),
            "Restore failed");
    }

    private async void DeleteRecycleBinSelected_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRecycleBinRows();
        if (rows.Count == 0)
        {
            ShowToast("Select one or more Recycle Bin items first.");
            return;
        }

        var countText = RecycleBinItemCountText(rows.Count);
        if (!UpdateDialog.ShowConfirmation(
            this,
            "Delete permanently",
            $"Permanently delete {countText} from the Recycle Bin?\n\nThis cannot be undone.",
            "Delete permanently",
            "Cancel",
            UpdateDialogTone.Error,
            "Recycle Bin"))
        {
            return;
        }

        await RunRecycleBinOperationAsync(
            "Deleting Recycle Bin items...",
            async token => BuildRecycleBinOperationMessage(
                "Permanently deleted",
                await RecycleBinService.DeletePermanentlyAsync(rows.Select(row => row.Entry).ToList(), token)),
            "Permanent delete failed");
    }

    private async void EmptyRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        if (_recycleBinShellItemCount == 0)
        {
            ShowToast("The Recycle Bin is already empty.");
            return;
        }

        var countText = RecycleBinItemCountText(_recycleBinShellItemCount);
        if (!UpdateDialog.ShowConfirmation(
            this,
            "Empty Recycle Bin",
            $"Permanently delete all {countText} in the Recycle Bin?\n\nThis cannot be undone.",
            "Empty Recycle Bin",
            "Cancel",
            UpdateDialogTone.Error,
            "Recycle Bin"))
        {
            return;
        }

        await RunRecycleBinOperationAsync(
            "Emptying Recycle Bin...",
            async token =>
            {
                await RecycleBinService.EmptyAsync(new WindowInteropHelper(this).Handle, token);
                return "Emptied the Recycle Bin.";
            },
            "Empty Recycle Bin failed");
    }

    private void OpenRecycleBinExplorer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RecycleBinService.OpenInExplorer();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowOperationError("Open Recycle Bin failed", ex);
        }
    }

    private async Task RunRecycleBinOperationAsync(
        string busyMessage,
        Func<CancellationToken, Task<string>> operation,
        string errorTitle)
    {
        if (_recycleBinCts is not null)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _recycleBinCts = cts;
        _recycleBinCtsIsRefresh = false;
        SetRecycleBinBusy(true, busyMessage);
        var refreshAfterOperation = true;
        try
        {
            var successMessage = await operation(cts.Token);
            ShowToast(successMessage);
            await Task.Delay(250);
        }
        catch (OperationCanceledException)
        {
            refreshAfterOperation = false;
            UpdateFooterStatus("Recycle Bin", "Recycle Bin operation cancelled.");
        }
        catch (Exception ex)
        {
            ShowOperationError(errorTitle, ex);
        }
        finally
        {
            if (ReferenceEquals(_recycleBinCts, cts))
            {
                _recycleBinCts = null;
                _recycleBinCtsIsRefresh = false;
                SetRecycleBinBusy(false);
            }
        }

        if (refreshAfterOperation)
        {
            await RefreshRecycleBinAsync(_targetSelectionVersion);
        }
    }

    private static string BuildRecycleBinOperationMessage(string completedVerb, RecycleBinOperationResult result)
    {
        if (result.ActedOnCount == 0)
        {
            return result.SkippedCount == 0
                ? "No Recycle Bin items were selected."
                : $"No selected Recycle Bin items were available. {RecycleBinItemCountText(result.SkippedCount)} skipped.";
        }

        var message = $"{completedVerb} {RecycleBinItemCountText(result.ActedOnCount)}.";
        if (result.SkippedCount > 0)
        {
            message += $" {RecycleBinItemCountText(result.SkippedCount)} skipped because no longer available.";
        }

        return message;
    }

    private void RecycleBinFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _recycleBinFilterText = RecycleBinFilterBox.Text ?? string.Empty;
        RecycleBinRowsView.Refresh();
        UpdateRecycleBinFilterUiState();
    }

    private void RecycleBinFilterBox_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearRecycleBinFilter();
            e.Handled = true;
        }
    }

    private void RecycleBinFilterClear_Click(object sender, RoutedEventArgs e)
    {
        ClearRecycleBinFilter();
    }

    private void ClearRecycleBinFilter()
    {
        RecycleBinFilterBox.Clear();
        _recycleBinFilterText = string.Empty;
        RecycleBinRowsView.Refresh();
        UpdateRecycleBinFilterUiState();
    }

    private bool RecycleBinRowMatchesFilter(object obj)
    {
        return obj is RecycleBinRow row
            && RecycleBinViewProjector.RowMatchesFilter(row, _recycleBinFilterText);
    }

    private void RecycleBinColumnHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }
            || !Enum.TryParse(tag, out RecycleBinSortColumn column))
        {
            return;
        }

        if (_recycleBinSortColumn == column)
        {
            _recycleBinSortDirection = _recycleBinSortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            _recycleBinSortColumn = column;
            _recycleBinSortDirection = RecycleBinViewProjector.DefaultSortDirection(column);
        }

        ApplyRecycleBinSort();
        e.Handled = true;
    }

    private void ApplyRecycleBinSort()
    {
        if (RecycleBinRowsView is ListCollectionView listView)
        {
            listView.CustomSort = new RecycleBinRowComparer(_recycleBinSortColumn, _recycleBinSortDirection);
        }
        else
        {
            RecycleBinRowsView.Refresh();
        }

        RefreshRecycleBinSortHeaders();
        UpdateRecycleBinFilterUiState();
    }

    private void RefreshRecycleBinSortHeaders()
    {
        SetRecycleBinSortHeader(RecycleBinNameHeader, "Name", RecycleBinSortColumn.Name);
        SetRecycleBinSortHeader(RecycleBinOriginalLocationHeader, "Original Location", RecycleBinSortColumn.OriginalLocation);
        SetRecycleBinSortHeader(RecycleBinDateDeletedHeader, "Date Deleted", RecycleBinSortColumn.DateDeleted);
        SetRecycleBinSortHeader(RecycleBinSizeHeader, "Size", RecycleBinSortColumn.Size);
        SetRecycleBinSortHeader(RecycleBinTypeHeader, "Type", RecycleBinSortColumn.ItemType);
        SetRecycleBinSortHeader(RecycleBinPathHeader, "Recycle Path", RecycleBinSortColumn.RecyclePath);
    }

    private void SetRecycleBinSortHeader(TextBlock header, string label, RecycleBinSortColumn column)
    {
        if (_recycleBinSortColumn == column)
        {
            header.Text = $"{label} {(_recycleBinSortDirection == ListSortDirection.Ascending ? "^" : "v")}";
            header.Foreground = (WpfBrush?)TryFindResource("AccentBrush") ?? header.Foreground;
        }
        else
        {
            header.Text = label;
            header.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private void RecycleBinGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateRecycleBinActionState();
    }

    private void RecycleBinGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenRecycleBinExplorer_Click(sender, e);
    }

    private void RecycleBinGrid_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeleteRecycleBinSelected_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            RestoreRecycleBinSelected_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            RefreshRecycleBin_Click(sender, e);
            e.Handled = true;
        }
    }

    private void CopyRecycleBinOriginalPath_Click(object sender, RoutedEventArgs e)
    {
        CopyRecycleBinText(rows => rows.Select(row => Path.Combine(row.OriginalLocation, row.Name)), "original path");
    }

    private void CopyRecycleBinRecyclePath_Click(object sender, RoutedEventArgs e)
    {
        CopyRecycleBinText(rows => rows.Select(row => row.RecyclePath), "recycle path");
    }

    private void CopyRecycleBinText(Func<IReadOnlyList<RecycleBinRow>, IEnumerable<string>> textFactory, string label)
    {
        var rows = SelectedRecycleBinRows();
        if (rows.Count == 0)
        {
            ShowToast("Select one or more Recycle Bin items first.");
            return;
        }

        CopyTextToClipboard(
            string.Join(Environment.NewLine, textFactory(rows)),
            $"Copied {rows.Count:n0} {label}(s).");
    }

    private IReadOnlyList<RecycleBinRow> SelectedRecycleBinRows()
        => RecycleBinGrid.SelectedItems.OfType<RecycleBinRow>().ToList();

    private void SetRecycleBinBusy(bool busy, string? message = null)
    {
        RecycleBinGrid.IsEnabled = !busy;
        RecycleBinRefreshButton.IsEnabled = !busy;
        RecycleBinLoadingOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(message))
        {
            RecycleBinLoadingText.Text = message;
            RecycleBinStatusText.Text = message;
            UpdateFooterStatus("Recycle Bin", message);
        }

        UpdateRecycleBinActionState();
    }

    private void UpdateRecycleBinActionState()
    {
        var busy = _recycleBinCts is not null;
        var hasSelection = RecycleBinGrid.SelectedItems.Count > 0;
        RecycleBinRestoreButton.IsEnabled = !busy && hasSelection;
        RecycleBinDeleteButton.IsEnabled = !busy && hasSelection;
        RecycleBinEmptyButton.IsEnabled = !busy && _recycleBinShellItemCount > 0;
    }

    private void UpdateRecycleBinFilterUiState()
    {
        var hasText = !string.IsNullOrEmpty(_recycleBinFilterText);
        RecycleBinFilterPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        RecycleBinFilterClearButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        if (hasText)
        {
            var total = RecycleBinRows.Count;
            var visible = RecycleBinRowsView is System.Collections.ICollection collection
                ? collection.Count
                : RecycleBinRowsView.Cast<object>().Count();
            RecycleBinFilterCountText.Text = $"{visible:n0} of {total:n0}";
        }
        else
        {
            RecycleBinFilterCountText.Text = string.Empty;
        }
    }

    // ============================================================
    //  Selection actions (open / reveal / copy / export / delete)
    // ============================================================
    private void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedPath();
        if (path is null) return;
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            return;
        }
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void RevealSelected_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedPath();
        if (path is not null && IsExistingFileSystemPath(path)) RevealPath(path);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedDetailRows().ToList();
        if (rows.Count > 0)
        {
            CopyTextToClipboard(
                string.Join(Environment.NewLine, rows.Select(r => r.FullPath)),
                $"Copied {rows.Count:n0} path(s).");
            return;
        }
        var path = SelectedPath();
        if (path is not null)
        {
            CopyTextToClipboard(path, "Copied path.");
        }
    }

    private void CopyRows_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedDetailRows().ToList();
        if (rows.Count == 0) return;
        var builder = new StringBuilder();
        foreach (var row in rows) builder.AppendLine(row.ToString());
        CopyTextToClipboard(builder.ToString(), $"Copied {rows.Count:n0} row(s).");
    }

    private void CopyTextToClipboard(string text, string successMessage)
    {
        try
        {
            WpfClipboard.SetText(text);
            ShowToast(successMessage);
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            Logger.LogWarning(ex, "Failed to copy text to clipboard.");
            ShowToast("Clipboard is currently busy. Please try again.");
        }
    }

    private async void ExportSelectionCsv_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedDetailRows().ToList();
        if (rows.Count == 0) { ShowToast("Select one or more rows first."); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = "selection.csv" };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                await WriteDetailRowsCsvAsync(rows, dialog.FileName);
                ShowToast($"Exported {rows.Count:n0} row(s).");
            }
            catch (Exception ex)
            {
                ShowOperationError("Export failed", ex);
            }
        }
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedPath();
        if (path is null || (!File.Exists(path) && !Directory.Exists(path)))
        {
            ShowToast("Select an existing file or folder.");
            return;
        }

        var targetEntry = SelectedEntry();
        var sizeLine = targetEntry is not null && targetEntry.LogicalSizeBytes > 0
            ? $"{Environment.NewLine}{Environment.NewLine}Size: {SizeFormatter.Format(targetEntry.LogicalSizeBytes)}"
            : string.Empty;

        var answer = WpfMessageBox.Show(
            this,
            $"Move to Recycle Bin?\n\n{path}{sizeLine}\n\nCustodian will ask Windows to recycle this item, not permanently delete it. If Windows warns that it cannot use the Recycle Bin, cancel the operation.",
            "Confirm Recycle Bin move",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            var result = RecycleBinService.MoveToRecycleBin(path, new WindowInteropHelper(this).Handle);
            if (result == RecycleBinMoveResult.Cancelled)
            {
                ShowToast("Recycle Bin move cancelled.");
                return;
            }

            ShowToast($"Moved to Recycle Bin: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Recycle Bin move failed for {Path}.", path);
            WpfMessageBox.Show(this, ex.Message, "Recycle Bin move failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private FileSystemEntry? SelectedEntry()
    {
        if (DetailsGrid.SelectedItem is DetailRow row) return row.Entry;
        return _selectedEntry;
    }

    // ============================================================
    //  Session scan cache
    // ============================================================
    private void RememberCurrentScanState()
    {
        if (_currentScan is null || _visibleCachedScan is null)
        {
            return;
        }

        _visibleCachedScan.State = CaptureCurrentScanState(_currentScan);
        RefreshTargetStatus(_currentScan.RootPath);
    }

    private CachedScanState CaptureCurrentScanState(ScanResult result)
    {
        return new CachedScanState(
            (_selectedEntry ?? result.Root).FullPath,
            _viewMode,
            _chartScope);
    }

    private async Task<CachedScan> CacheScanResultAsync(ScanResult result, string? preferredKey = null)
    {
        var key = preferredKey
            ?? (TryGetScanCacheKey(result.RootPath, out var resultKey) ? resultKey : result.RootPath);
        _sessionScanCache.TryGetValue(key, out var previous);

        var state = string.Equals(_visibleScanKey, key, StringComparison.OrdinalIgnoreCase) && _currentScan is not null
            ? CaptureCurrentScanState(_currentScan)
            : previous?.State ?? new CachedScanState(result.Root.FullPath, DetailViewMode.Contents, ChartScope.SelectedFolder);
        var preparation = await PrepareScanUiAsync(result);
        var cached = new CachedScan(key, result, state, preparation);
        _sessionScanCache[key] = cached;
        MarkSessionScanCacheKeyUsed(key, enforceLimit: true);
        RefreshTargetStatus(result.RootPath);
        return cached;
    }

    private bool TryGetCachedScan(string rootPath, out CachedScan cached)
    {
        if (TryGetScanCacheKey(rootPath, out var key) &&
            _sessionScanCache.TryGetValue(key, out cached!))
        {
            MarkSessionScanCacheKeyUsed(key, enforceLimit: false);
            return true;
        }

        cached = default!;
        return false;
    }

    private bool TryGetVisibleCachedScan(out CachedScan cached)
    {
        if (!string.IsNullOrWhiteSpace(_visibleScanKey) &&
            _sessionScanCache.TryGetValue(_visibleScanKey, out cached!))
        {
            MarkSessionScanCacheKeyUsed(_visibleScanKey, enforceLimit: false);
            return true;
        }

        cached = default!;
        return false;
    }

    private void MarkSessionScanCacheKeyUsed(string key, bool enforceLimit)
    {
        for (var node = _sessionScanCacheOrder.First; node is not null; node = node.Next)
        {
            if (string.Equals(node.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                _sessionScanCacheOrder.Remove(node);
                break;
            }
        }

        _sessionScanCacheOrder.AddLast(key);

        if (enforceLimit)
        {
            TrimSessionScanCache();
        }
    }

    private void TrimSessionScanCache()
    {
        while (_sessionScanCacheOrder.Count > MaxSessionScanCacheEntries)
        {
            var evictedNode = _sessionScanCacheOrder.First;
            if (evictedNode is null)
            {
                return;
            }

            var evictedKey = evictedNode.Value;
            _sessionScanCacheOrder.RemoveFirst();
            if (string.Equals(evictedKey, _visibleScanKey, StringComparison.OrdinalIgnoreCase))
            {
                _sessionScanCacheOrder.AddLast(evictedKey);
                continue;
            }

            if (_sessionScanCache.Remove(evictedKey, out var evicted))
            {
                RefreshTargetStatus(evicted.Result.RootPath);
            }
        }
    }

    private bool IsScanCached(string rootPath)
        => TryGetScanCacheKey(rootPath, out var key) && _sessionScanCache.ContainsKey(key);

    private bool IsScanActive(string rootPath)
        => TryGetScanCacheKey(rootPath, out var key) &&
            string.Equals(_activeScan?.RootKey, key, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetScanCacheKey(string rootPath, out string key)
    {
        try
        {
            key = ScanPathUtility.NormalizeRoot(rootPath);
            return true;
        }
        catch (Exception)
        {
            key = string.Empty;
            return false;
        }
    }

    private static FileSystemEntry ResolveCachedSelection(ScanResult result, CachedScanState? restoreState)
    {
        if (!string.IsNullOrWhiteSpace(restoreState?.SelectedPath) &&
            ScanViewProjector.TryFindDirectoryByPath(result.Root, restoreState.SelectedPath, out var selected))
        {
            return selected;
        }

        return result.Root;
    }

    private async Task<ScanUiPreparation> PrepareScanUiAsync(ScanResult result)
    {
        return await Task.Run(() => new ScanUiPreparation(
            FolderNode.From(result.Root, Math.Max(1, result.Root.LogicalSizeBytes)),
            ScanViewProjector.FolderJumpIndex(result.Root)));
    }

    private bool ShouldShowCompletedScan(string scanKey)
    {
        return !_isRecycleBinViewActive &&
            (string.IsNullOrWhiteSpace(_visibleScanKey) ||
            string.Equals(_visibleScanKey, scanKey, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsShowingActiveScan(ActiveScanJob scan)
    {
        return _currentScan is null &&
            !_isRecycleBinViewActive &&
            string.Equals(_visibleScanKey, scan.RootKey, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> RestoreCachedScanAsync(CachedScan cached, int navigationVersion, bool showToast = true)
    {
        var restored = await LoadScanIntoUiAsync(cached, navigationVersion);
        if (!restored)
        {
            return false;
        }

        if (showToast)
        {
            ShowToast($"Restored cached scan: {cached.Result.RootPath}");
        }

        return true;
    }

    private int BeginNavigation()
        => ++_targetSelectionVersion;

    private bool IsCurrentNavigation(int navigationVersion)
        => navigationVersion == _targetSelectionVersion;

    private bool IsCurrentRecycleBinNavigation(int navigationVersion)
        => IsCurrentNavigation(navigationVersion) && _isRecycleBinViewActive;

    private bool IsActiveRecycleBinRefresh(int navigationVersion, CancellationTokenSource cts)
        => IsCurrentRecycleBinNavigation(navigationVersion) &&
            _recycleBinCtsIsRefresh &&
            ReferenceEquals(_recycleBinCts, cts);

    private bool IsCurrentScanRestore(string rootKey, int navigationVersion)
    {
        if (!IsCurrentNavigation(navigationVersion) ||
            _isRecycleBinViewActive ||
            !string.Equals(_visibleScanKey, rootKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void RefreshTargetStatus(string rootPath)
    {
        if (!TryGetScanCacheKey(rootPath, out var key))
        {
            return;
        }

        var selectedRootPath = (DriveList.SelectedItem as TargetRow)?.RootPath;
        TargetRow? selectedReplacement = null;

        _suppressTargetSelection = true;
        try
        {
            for (var i = 0; i < TargetRows.Count; i++)
            {
                var target = TargetRows[i];
                if (target.Kind != TargetKind.Drive ||
                    !TryGetScanCacheKey(target.RootPath, out var targetKey) ||
                    !string.Equals(key, targetKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var drive = DriveRows.FirstOrDefault(row => string.Equals(row.RootPath, target.RootPath, StringComparison.OrdinalIgnoreCase));
                if (drive is null)
                {
                    continue;
                }

                var replacement = TargetRow.FromDrive(
                    drive,
                    scanCached: IsScanCached(drive.RootPath),
                    scanActive: IsScanActive(drive.RootPath));
                if (!EqualityComparer<TargetRow>.Default.Equals(target, replacement))
                {
                    TargetRows[i] = replacement;

                    if (!string.IsNullOrWhiteSpace(selectedRootPath) &&
                        string.Equals(selectedRootPath, replacement.RootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedReplacement = replacement;
                    }
                }

                break;
            }

            if (selectedReplacement is not null)
            {
                DriveList.SelectedItem = selectedReplacement;
            }
        }
        finally
        {
            _suppressTargetSelection = false;
        }
    }

    private void SetViewMode(DetailViewMode mode)
    {
        _viewMode = mode;
        switch (mode)
        {
            case DetailViewMode.LargestFiles:
                ViewLargestFiles.IsChecked = true;
                break;
            case DetailViewMode.LargestFolders:
                ViewLargestFolders.IsChecked = true;
                break;
            case DetailViewMode.Extensions:
                ViewExtensions.IsChecked = true;
                break;
            default:
                ViewContents.IsChecked = true;
                break;
        }
    }

    private void SetChartScope(ChartScope scope)
    {
        _chartScope = scope;
        _suppressChartScopeSelection = true;
        try
        {
            ChartScopeBox.SelectedIndex = scope switch
            {
                ChartScope.LargestFolders => 1,
                ChartScope.LargestFiles => 2,
                ChartScope.Extensions => 3,
                _ => 0
            };
        }
        finally
        {
            _suppressChartScopeSelection = false;
        }
    }

    // ============================================================
    //  UI population
    // ============================================================
    private async Task<bool> LoadScanIntoUiAsync(CachedScan cached, int navigationVersion)
    {
        if (!IsCurrentNavigation(navigationVersion) || _isRecycleBinViewActive)
        {
            return false;
        }

        ResetProjectionRequests();

        var analysisWatch = Stopwatch.StartNew();
        var result = cached.Result;
        _currentScan = result;
        _visibleScanKey = cached.RootKey;
        _visibleCachedScan = cached;
        StopLoadingAnimation();
        ResetPieZoom();

        FolderNodes.Clear();
        FolderNodes.Add(cached.Preparation.RootNode);
        _folderJumpIndex = cached.Preparation.FolderJumpIndex;
        _backStack.Clear();
        _forwardStack.Clear();

        _selectedEntry = ResolveCachedSelection(result, cached.State);
        SetViewMode(cached.State.ViewMode);
        SetChartScope(cached.State.ChartScope);
        _selectedChartSourceKey = null;
        RefreshFolderJumpRows(string.Empty);
        await RefreshDetailsAsync();
        if (!IsCurrentScanRestore(cached.RootKey, navigationVersion))
        {
            return false;
        }

        analysisWatch.Stop();
        result.PhaseTimings.RemoveAll(t => t.Name == "UI analysis preparation");
        result.PhaseTimings.Add(new ScanPhaseTiming("UI analysis preparation", analysisWatch.Elapsed));

        EngineBadge.Text = result.Engine;
        EngineBadgeDot.Fill = (WpfBrush?)TryFindResource("SuccessBrush") ?? WpfBrushes.Green;
        UpdateFooterStatus("Ready", BuildFooterDetail(result));
        UpdateEmptyStateVisibility();
        AnimateGridFadeIn();
        ScheduleSettingsSave();
        return true;
    }

    private async Task RefreshDetailsAsync(bool refreshContext = true)
    {
        var requestVersion = ++_detailRefreshVersion;
        var result = _currentScan;
        if (result is null)
        {
            SelectedTitleText.Text = "No scan loaded";
            SelectedSubText.Text = "Choose a path and scan.";
            DetailsGrid.IsEnabled = true;
            DetailRows.Clear();
            _boundDetailRows = null;
            ChartSlices.Clear();
            SummaryMetrics.Clear();
            BreadcrumbItems.Clear();
            await RefreshChartAsync();
            RefreshNavigationState(null, null);
            return;
        }

        var selected = _selectedEntry ?? result.Root;
        var viewMode = _viewMode;
        IReadOnlyList<DetailRow> rows;
        if (viewMode == DetailViewMode.Contents)
        {
            rows = ScanViewProjector.ChildRows(selected);
        }
        else
        {
            try
            {
                if (ReferenceEquals(selected, result.Root))
                {
                    var globalRowsCache = GlobalDetailRowsCacheFor(result);
                    rows = await Task.Run(() => GetOrCreateGlobalDetailRows(result, viewMode, globalRowsCache));
                }
                else
                {
                    rows = await Task.Run(() => ProjectScopedDetailRows(selected, viewMode));
                }
            }
            catch (Exception ex)
            {
                if (IsCurrentDetailRequest(requestVersion, result, selected, viewMode))
                {
                    DetailsGrid.IsEnabled = true;
                    ShowOperationError("View failed", ex);
                }
                return;
            }
        }

        if (!IsCurrentDetailRequest(requestVersion, result, selected, viewMode))
        {
            return;
        }

        DetailsGrid.IsEnabled = true;
        if (!ReferenceEquals(rows, _boundDetailRows))
        {
            ReplaceCollection(DetailRows, rows);
            _boundDetailRows = rows;
            // Granular collection notifications refresh the view. An explicit
            // Refresh is only needed to re-run filter/sort view logic.
            if (!string.IsNullOrEmpty(_filterText) || _detailSortColumn is not null)
            {
                DetailRowsView.Refresh();
            }
        }
        UpdateFilterUiState();
        if (refreshContext)
        {
            ReplaceCollection(SummaryMetrics, ScanViewProjector.SelectedSummaryMetrics(result, selected));
            RefreshNavigationState(selected, result.Root);
            await RefreshChartAsync();
            UpdatePathDisplay(selected.FullPath);
        }
        SelectedTitleText.Text = string.IsNullOrWhiteSpace(selected.Name) ? selected.FullPath : selected.Name;
        SelectedSubText.Text = BuildSelectedSubText(viewMode, selected);
    }

    private IReadOnlyList<DetailRow> GetOrCreateGlobalDetailRows(
        ScanResult result,
        DetailViewMode mode,
        Dictionary<DetailViewMode, IReadOnlyList<DetailRow>> cache)
    {
        lock (_globalDetailRowsCacheGate)
        {
            if (cache.TryGetValue(mode, out var cachedRows))
            {
                return cachedRows;
            }
        }

        var rows = ProjectGlobalDetailRows(result, mode);
        lock (_globalDetailRowsCacheGate)
        {
            if (cache.TryGetValue(mode, out var cachedRows))
            {
                return cachedRows;
            }

            cache[mode] = rows;
            return rows;
        }
    }

    private Dictionary<DetailViewMode, IReadOnlyList<DetailRow>> GlobalDetailRowsCacheFor(ScanResult result)
    {
        if (_visibleCachedScan is { } visible && ReferenceEquals(visible.Result, result))
        {
            return visible.GlobalDetailRowsCache;
        }

        if (TryGetScanCacheKey(result.RootPath, out var key) &&
            _sessionScanCache.TryGetValue(key, out var cached) &&
            ReferenceEquals(cached.Result, result))
        {
            return cached.GlobalDetailRowsCache;
        }

        return _globalDetailRowsCache;
    }

    private static IReadOnlyList<DetailRow> ProjectGlobalDetailRows(ScanResult result, DetailViewMode mode)
    {
        return mode switch
        {
            DetailViewMode.LargestFiles => ScanViewProjector.LargestFileRows(result),
            DetailViewMode.LargestFolders => ScanViewProjector.LargestFolderRows(result),
            DetailViewMode.Extensions => ScanViewProjector.ExtensionRows(result),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }

    private static IReadOnlyList<DetailRow> ProjectScopedDetailRows(FileSystemEntry selected, DetailViewMode mode)
    {
        return mode switch
        {
            DetailViewMode.LargestFiles => ScanViewProjector.LargestFileRows(selected),
            DetailViewMode.LargestFolders => ScanViewProjector.LargestFolderRows(selected),
            DetailViewMode.Extensions => ScanViewProjector.ExtensionRows(selected),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }

    private bool IsCurrentDetailRequest(int requestVersion, ScanResult result, FileSystemEntry selected, DetailViewMode mode)
    {
        return requestVersion == _detailRefreshVersion
            && ReferenceEquals(_currentScan, result)
            && ReferenceEquals(_selectedEntry ?? result.Root, selected)
            && _viewMode == mode;
    }

    private void ClearGlobalDetailRowsCache()
    {
        lock (_globalDetailRowsCacheGate)
        {
            _globalDetailRowsCache.Clear();
        }

        ResetProjectionRequests();
    }

    private void ResetProjectionRequests()
    {
        _detailRefreshVersion++;
        _chartRefreshVersion++;
    }

    private async Task RefreshChartAsync()
    {
        var requestVersion = ++_chartRefreshVersion;
        var result = _currentScan;
        if (result is null)
        {
            ChartSlices.Clear();
            ChartTitleText.Text = "Disk distribution";
            ChartTotalText.Text = "Run a scan to render chart data.";
            ChartSelectionText.Text = "Select a slice to locate it in the grid.";
            PieChart.SelectedSlice = null;
            PieChart.InvalidateVisual();
            Treemap.SelectedSlice = null;
            Treemap.InvalidateVisual();
            return;
        }

        var selected = _selectedEntry ?? result.Root;
        var scope = _chartScope;
        ChartDataset dataset;
        try
        {
            dataset = await Task.Run(() => ProjectChartDataset(result, selected, scope));
        }
        catch (Exception ex)
        {
            if (IsCurrentChartRequest(requestVersion, result, selected, scope))
            {
                ShowOperationError("Chart refresh failed", ex);
            }

            return;
        }

        if (!IsCurrentChartRequest(requestVersion, result, selected, scope))
        {
            return;
        }

        ApplyChartDataset(dataset);
    }

    private static ChartDataset ProjectChartDataset(ScanResult result, FileSystemEntry selected, ChartScope scope)
    {
        return scope switch
        {
            ChartScope.LargestFolders => ReferenceEquals(selected, result.Root)
                ? ScanViewProjector.LargestFoldersChart(result)
                : ScanViewProjector.LargestFoldersChart(selected),
            ChartScope.LargestFiles => ReferenceEquals(selected, result.Root)
                ? ScanViewProjector.LargestFilesChart(result)
                : ScanViewProjector.LargestFilesChart(selected),
            ChartScope.Extensions => ReferenceEquals(selected, result.Root)
                ? ScanViewProjector.ExtensionsChart(result)
                : ScanViewProjector.ExtensionsChart(selected),
            _ => ScanViewProjector.SelectedFolderChart(selected)
        };
    }

    private bool IsCurrentChartRequest(int requestVersion, ScanResult result, FileSystemEntry selected, ChartScope scope)
    {
        return requestVersion == _chartRefreshVersion
            && ReferenceEquals(_currentScan, result)
            && ReferenceEquals(_selectedEntry ?? result.Root, selected)
            && _chartScope == scope;
    }

    private void ApplyChartDataset(ChartDataset dataset)
    {
        ReplaceCollection(ChartSlices, dataset.Slices);
        ChartTitleText.Text = dataset.Title;
        ChartTotalText.Text = dataset.HasOther
            ? $"{dataset.TotalSize} · top {Math.Min(12, dataset.Slices.Count)} + other"
            : $"{dataset.TotalSize} · {dataset.Slices.Count:n0} item(s)";

        var selectedSlice = ChartSlices.FirstOrDefault(s => string.Equals(s.SourceKey, _selectedChartSourceKey, StringComparison.Ordinal));
        _suppressChartSelection = true;
        PieChart.SelectedSlice = selectedSlice;
        Treemap.SelectedSlice = selectedSlice;
        ChartBars.SelectedItem = selectedSlice;
        if (selectedSlice is not null)
        {
            ChartBars.ScrollIntoView(selectedSlice);
            ChartSelectionText.Text = $"{selectedSlice.Label}: {selectedSlice.FormattedSize} ({selectedSlice.PercentText})";
        }
        else
        {
            ChartSelectionText.Text = "Select a slice to locate it in the grid.";
        }
        _suppressChartSelection = false;
        PieChart.InvalidateVisual();
        Treemap.InvalidateVisual();
    }

    private async Task SelectChartSliceAsync(ChartSlice slice, bool drillIntoFolders)
    {
        _selectedChartSourceKey = slice.SourceKey;
        ChartSelectionText.Text = $"{slice.Label}: {slice.FormattedSize} ({slice.PercentText})";

        _suppressChartSelection = true;
        PieChart.SelectedSlice = slice;
        Treemap.SelectedSlice = slice;
        ChartBars.SelectedItem = slice;
        ChartBars.ScrollIntoView(slice);
        _suppressChartSelection = false;
        PieChart.InvalidateVisual();
        Treemap.InvalidateVisual();

        if (slice.Kind == ChartSliceKind.Other) return;

        if (drillIntoFolders && slice.Entry is { IsDirectory: true } folder)
        {
            SetChartScope(ChartScope.SelectedFolder);
            await NavigateToFolderAsync(folder);
            return;
        }

        await SelectDetailRowForSliceAsync(slice);
        RememberCurrentScanState();
    }

    private async Task SelectDetailRowForSliceAsync(ChartSlice slice)
    {
        if (slice.Kind == ChartSliceKind.Extension)
        {
            SetViewMode(DetailViewMode.Extensions);
            await RefreshDetailsAsync(refreshContext: false);
            SelectDetailRow(row => string.Equals(row.Extension, slice.SourceKey, StringComparison.OrdinalIgnoreCase));
            return;
        }
        if (slice.Entry is null) return;

        var desiredView = _chartScope switch
        {
            ChartScope.LargestFiles => DetailViewMode.LargestFiles,
            ChartScope.LargestFolders => DetailViewMode.LargestFolders,
            _ => DetailViewMode.Contents
        };
        if (_viewMode != desiredView)
        {
            SetViewMode(desiredView);
            await RefreshDetailsAsync(refreshContext: false);
        }
        SelectDetailRow(row => string.Equals(row.FullPath, slice.Entry.FullPath, StringComparison.OrdinalIgnoreCase));
    }

    private void SelectDetailRow(Func<DetailRow, bool> predicate)
    {
        var row = DetailRows.FirstOrDefault(predicate);
        if (row is null) return;
        DetailsGrid.SelectedItem = row;
        DetailsGrid.ScrollIntoView(row);
        DetailsGrid.Focus();
    }

    private void NavigateToFolder(FileSystemEntry entry, bool addHistory = true, bool clearForward = true)
        => RunUiAction(() => NavigateToFolderAsync(entry, addHistory, clearForward), "Navigation failed");

    private async Task NavigateToFolderAsync(FileSystemEntry entry, bool addHistory = true, bool clearForward = true)
    {
        await _navigationGate.WaitAsync();
        try
        {
            await NavigateToFolderCoreAsync(entry, addHistory, clearForward);
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    private async Task NavigateToFolderCoreAsync(FileSystemEntry entry, bool addHistory = true, bool clearForward = true)
    {
        if (_selectedEntry is not null && string.Equals(_selectedEntry.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase)) return;
        if (addHistory && _selectedEntry is not null) _backStack.Push(_selectedEntry);
        if (clearForward) _forwardStack.Clear();

        _selectedEntry = entry;
        SetViewMode(DetailViewMode.Contents);
        _selectedChartSourceKey = null;
        await RefreshDetailsAsync();
        RememberCurrentScanState();
        AnimateGridFadeIn();
    }

    private void RefreshNavigationState(FileSystemEntry? selected, FileSystemEntry? root)
    {
        BackButton.IsEnabled = _backStack.Count > 0;
        ForwardButton.IsEnabled = _forwardStack.Count > 0;
        UpButton.IsEnabled = selected is not null && TryGetParent(selected, out _);

        BreadcrumbItems.Clear();
        if (selected is not null && root is not null)
        {
            ReplaceCollection(BreadcrumbItems, ScanViewProjector.Breadcrumb(root, selected));
            ScrollBreadcrumbsToEnd();
        }
    }

    private void UpdatePathDisplay(string path)
    {
        PathBox.Text = path;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (PathBox.Template?.FindName("PART_EditableTextBox", PathBox) is WpfTextBox textBox)
            {
                textBox.CaretIndex = textBox.Text.Length;
                textBox.ScrollToEnd();
            }
        }), DispatcherPriority.ContextIdle);
    }

    private void ScrollBreadcrumbsToEnd()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            BreadcrumbScrollViewer.UpdateLayout();
            BreadcrumbScrollViewer.ScrollToRightEnd();
        }), DispatcherPriority.ContextIdle);
    }

    private bool TryGetParent(FileSystemEntry entry, out FileSystemEntry parent)
    {
        var root = _currentScan?.Root;
        if (root is null || string.Equals(root.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            parent = null!;
            return false;
        }

        var normalized = entry.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentPath = Path.GetDirectoryName(normalized);
        if (!string.IsNullOrWhiteSpace(parentPath) && ScanViewProjector.TryFindDirectoryByPath(root, parentPath, out parent))
        {
            return true;
        }

        parent = null!;
        return false;
    }

    private void RefreshFolderJumpRows(string query)
    {
        _suppressJumpSelection = true;
        ReplaceCollection(FolderJumpRows, ScanViewProjector.FolderJumpRows(_folderJumpIndex, query));
        JumpBox.IsDropDownOpen = FolderJumpRows.Count > 0 && JumpBox.IsKeyboardFocusWithin;
        _suppressJumpSelection = false;
    }

    private void ScheduleFolderJumpRefresh()
    {
        _folderJumpDebounceTimer.Stop();
        _folderJumpDebounceTimer.Start();
    }

    private void ClearViewsForNewScan(string path, string scanKey)
    {
        _isRecycleBinViewActive = false;
        RecycleBinHost.Visibility = Visibility.Collapsed;
        _visibleCachedScan = null;
        _visibleScanKey = scanKey;
        ResetEmptyStateText();
        ClearGlobalDetailRowsCache();
        _currentScan = null;
        FolderNodes.Clear();
        DetailRows.Clear();
        _boundDetailRows = null;
        ChartSlices.Clear();
        ResetPieZoom();
        SummaryMetrics.Clear();
        BreadcrumbItems.Clear();
        FolderJumpRows.Clear();
        _folderJumpIndex = [];
        _backStack.Clear();
        _forwardStack.Clear();
        _selectedChartSourceKey = null;
        _selectedEntry = null;
        SelectedTitleText.Text = "Scanning...";
        SelectedSubText.Text = path;
        UpdateFooterStatus("Scanning", "Preparing...");
        EngineBadge.Text = "Scanning";
        EngineBadgeDot.Fill = (WpfBrush?)TryFindResource("AccentBrush") ?? WpfBrushes.DodgerBlue;
        ChartTitleText.Text = "Disk distribution";
        ChartTotalText.Text = "Run a scan to render chart data.";
        ChartSelectionText.Text = "Select a slice to locate it in the grid.";
        RefreshNavigationState(null, null);
        UpdateEmptyStateVisibility();
    }

    private void ResetEmptyStateText()
    {
        EmptyStateTitleText.Text = DefaultEmptyStateTitle;
        EmptyStateDetailText.Text = DefaultEmptyStateDetail;
    }

    private void ShowActiveScanPage(ActiveScanJob scan)
    {
        ClearViewsForNewScan(scan.DisplayPath, scan.RootKey);
        LoadingPathText.Text = string.IsNullOrWhiteSpace(_scanCurrentPath)
            ? scan.DisplayPath
            : _scanCurrentPath;
        StartLoadingAnimation(resetProgress: false);
        UpdateFooterStatus("Scanning", BuildActiveScanDetail());
    }

    private string BuildActiveScanDetail()
    {
        if (_scanFilesSeen <= 0 && _scanBytesSeen <= 0)
        {
            return "Preparing...";
        }

        return $"{_scanFilesSeen:n0} files, {SizeFormatter.Format(_scanBytesSeen)}";
    }

    private void ShowStartScanPrompt(string path)
    {
        _isRecycleBinViewActive = false;
        RecycleBinHost.Visibility = Visibility.Collapsed;
        StopLoadingAnimation();
        _visibleCachedScan = null;
        _visibleScanKey = TryGetScanCacheKey(path, out var promptKey)
            ? promptKey
            : path;
        ClearGlobalDetailRowsCache();
        _currentScan = null;
        _selectedEntry = null;
        _selectedChartSourceKey = null;
        _folderJumpIndex = [];
        _backStack.Clear();
        _forwardStack.Clear();

        FolderNodes.Clear();
        DetailRows.Clear();
        _boundDetailRows = null;
        ChartSlices.Clear();
        ResetPieZoom();
        SummaryMetrics.Clear();
        BreadcrumbItems.Clear();
        FolderJumpRows.Clear();

        EmptyStateTitleText.Text = "Start Scan";
        EmptyStateDetailText.Text = $"No session scan is loaded for {path}. Start a scan to inspect this drive.";
        SelectedTitleText.Text = "No scan loaded";
        SelectedSubText.Text = path;
        ChartTitleText.Text = "Disk distribution";
        ChartTotalText.Text = "Start a scan to render chart data.";
        ChartSelectionText.Text = "Select a slice to locate it in the grid.";
        UpdateFooterStatus("Ready", $"Start a scan for {path}");
        EngineBadge.Text = "Ready";
        EngineBadgeDot.Fill = (WpfBrush?)TryFindResource("MutedBrush") ?? WpfBrushes.Gray;
        RefreshNavigationState(null, null);
        UpdateEmptyStateVisibility();
    }

    private async Task LoadDriveRowsAsync()
    {
        try
        {
            var rows = await Task.Run(BuildDriveRows);
            DriveRows.Clear();
            TargetRows.Clear();
            TargetRows.Add(TargetRow.RecycleBin());
            foreach (var row in rows)
            {
                DriveRows.Add(row);
                TargetRows.Add(TargetRow.FromDrive(
                    row,
                    scanCached: IsScanCached(row.RootPath),
                    scanActive: IsScanActive(row.RootPath)));
                AddRecentPath(row.RootPath);
            }

            await RefreshRecycleBinTargetUsageAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to refresh drive and target list.");
        }
    }

    private async Task RefreshRecycleBinTargetUsageAsync()
    {
        try
        {
            var usage = await RecycleBinService.GetUsageAsync();
            UpdateRecycleBinTargetUsage(usage.SizeBytes, usage.ItemCount);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Recycle Bin usage unavailable.");
            ReplaceTargetRow(TargetKind.RecycleBin, TargetRow.RecycleBinUnavailable());
        }
    }

    private void UpdateRecycleBinTargetUsage(long sizeBytes, long itemCount)
    {
        ReplaceTargetRow(
            TargetKind.RecycleBin,
            TargetRow.RecycleBin(sizeBytes, itemCount));
    }

    private void ReplaceTargetRow(TargetKind kind, TargetRow row)
    {
        var index = TargetRows
            .Select((target, targetIndex) => new { target, targetIndex })
            .FirstOrDefault(item => item.target.Kind == kind)
            ?.targetIndex;
        if (index is null)
        {
            if (kind == TargetKind.RecycleBin)
            {
                TargetRows.Insert(0, row);
            }
            else
            {
                TargetRows.Add(row);
            }
            return;
        }

        TargetRows[index.Value] = row;
    }

    private static IReadOnlyList<DriveRow> BuildDriveRows()
    {
        var rows = new List<DriveRow>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to enumerate drives.");
            return rows;
        }

        foreach (var drive in drives)
        {
            try
            {
                var total = drive.IsReady ? drive.TotalSize : 0;
                var free = drive.IsReady ? drive.AvailableFreeSpace : 0;
                var used = Math.Max(0, total - free);
                var percent = total <= 0 ? 0 : (double)used / total * 100;
                var label = drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? $"{drive.Name} {drive.VolumeLabel}"
                    : drive.Name;

                rows.Add(new DriveRow(
                    label, drive.Name,
                    total <= 0 ? "Not ready" : $"{SizeFormatter.Format(used)} used",
                    total <= 0 ? string.Empty : $"{SizeFormatter.Format(free)} free",
                    percent));
            }
            catch (IOException ex)
            {
                Logger.LogWarning(ex, "Drive {Drive} not ready.", drive.Name);
                rows.Add(new DriveRow(drive.Name, drive.Name, "Not ready", "", 0));
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.LogWarning(ex, "Access denied enumerating drive {Drive}.", drive.Name);
                rows.Add(new DriveRow(drive.Name, drive.Name, "Access denied", "", 0));
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Drive {Drive} unavailable.", drive.Name);
                rows.Add(new DriveRow(drive.Name, drive.Name, "Unavailable", "", 0));
            }
        }

        return rows;
    }

    private void AddRecentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (RecentPaths.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))) return;
        RecentPaths.Add(path);
    }

    private IEnumerable<DetailRow> SelectedDetailRows() => DetailsGrid.SelectedItems.OfType<DetailRow>();

    private string? SelectedPath()
    {
        if (DetailsGrid.SelectedItem is DetailRow row) return row.FullPath;
        return _selectedEntry?.FullPath;
    }

    private void SetScanningState(bool scanning)
    {
        StartButton.IsEnabled = !scanning;
        EmptyStateStartButton.IsEnabled = !scanning;
        StopButton.IsEnabled = scanning;
        RefreshButton.IsEnabled = !scanning && _currentScan is not null;
        ProgressBar.IsIndeterminate = scanning;
    }

    private void UpdateChartModeVisibility()
    {
        TreemapHost.Visibility = _chartDisplayMode == ChartDisplayMode.Treemap ? Visibility.Visible : Visibility.Collapsed;
        PieHost.Visibility = _chartDisplayMode == ChartDisplayMode.Pie ? Visibility.Visible : Visibility.Collapsed;
        PieZoomControls.Visibility = _chartDisplayMode == ChartDisplayMode.Pie ? Visibility.Visible : Visibility.Collapsed;
        BarHost.Visibility = _chartDisplayMode == ChartDisplayMode.Bars ? Visibility.Visible : Visibility.Collapsed;
        Treemap.InvalidateVisual();
        PieChart.InvalidateVisual();
    }

    private void UpdateEmptyStateVisibility()
    {
        if (_isRecycleBinViewActive)
        {
            RecycleBinHost.Visibility = Visibility.Visible;
            WorkspaceHost.Visibility = Visibility.Collapsed;
            EmptyStateHost.Visibility = Visibility.Collapsed;
            return;
        }

        RecycleBinHost.Visibility = Visibility.Collapsed;
        var hasScan = _currentScan is not null;
        WorkspaceHost.Visibility = hasScan ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateHost.Visibility = hasScan ? Visibility.Collapsed : Visibility.Visible;
    }

    // ============================================================
    //  Loading skeleton + footer
    // ============================================================
    private void StartLoadingAnimation(bool resetProgress = true)
    {
        RecycleBinHost.Visibility = Visibility.Collapsed;
        WorkspaceHost.Visibility = Visibility.Collapsed;
        EmptyStateHost.Visibility = Visibility.Collapsed;
        LoadingHost.Visibility = Visibility.Visible;
        SkeletonRows.ItemsSource = Enumerable.Range(0, 12).Select(i => 0.45 - i * 0.025).ToArray();
        LoadingPathText.Text = string.IsNullOrWhiteSpace(_scanCurrentPath) ? "Preparing..." : _scanCurrentPath;
        if (resetProgress)
        {
            _scanStarted = DateTime.UtcNow;
            _scanFilesSeen = 0;
            _scanBytesSeen = 0;
            _scanProgressTimer?.Stop();
            _scanProgressTimer = null;
        }

        if (_scanProgressTimer is null)
        {
            _scanProgressTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(750) };
            _scanProgressTimer.Tick += (_, _) => UpdateScanRate();
            _scanProgressTimer.Start();
        }
    }

    private void StopLoadingAnimation()
    {
        LoadingHost.Visibility = Visibility.Collapsed;
        _scanProgressTimer?.Stop();
        _scanProgressTimer = null;
        FooterRate.Text = string.Empty;
        FooterEta.Text = string.Empty;
        UpdateEmptyStateVisibility();
    }

    private void UpdateScanRate()
    {
        var elapsed = (DateTime.UtcNow - _scanStarted).TotalSeconds;
        if (elapsed < 0.1) return;

        var filesPerSec = _scanFilesSeen / elapsed;
        var bytesPerSec = _scanBytesSeen / elapsed;
        FooterRate.Text = $"{filesPerSec:n0} files/s · {SizeFormatter.Format((long)bytesPerSec)}/s";
    }

    private void UpdateFooterStatus(string status, string detail)
    {
        FooterStatus.Text = status;
        FooterDetail.Text = detail;
    }

    private static string BuildFooterDetail(ScanResult result)
    {
        var parts = new List<string>
        {
            $"{SizeFormatter.Format(result.Root.LogicalSizeBytes)} · {result.Root.FileCount:n0} files · {result.Root.DirectoryCount:n0} folders",
            $"in {result.Duration:g}"
        };
        if (result.SkippedEntries.Count > 0) parts.Add($"{result.SkippedEntries.Count:n0} skipped");
        return string.Join(" · ", parts);
    }

    private void AnimateGridFadeIn()
    {
        var animation = new DoubleAnimation(0.5, 1.0, TimeSpan.FromMilliseconds(140));
        DetailsGrid.BeginAnimation(OpacityProperty, animation);
    }

    // ============================================================
    //  Toast
    // ============================================================
    private void ShowToast(string message)
    {
        var border = new Border
        {
            Background = (WpfBrush?)TryFindResource("TextStrong") ?? WpfBrushes.Black,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 9, 14, 9),
            Margin = new Thickness(0, 6, 0, 0),
            Opacity = 0,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.22 }
        };
        var text = new TextBlock
        {
            Text = message,
            Foreground = (WpfBrush?)TryFindResource("SurfaceRaised") ?? WpfBrushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12
        };
        border.Child = text;
        ToastHost.Children.Add(border);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromSeconds(2.6) };
        var sb = new Storyboard();
        Storyboard.SetTarget(fadeIn, border); Storyboard.SetTargetProperty(fadeIn, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(fadeOut, border); Storyboard.SetTargetProperty(fadeOut, new PropertyPath(OpacityProperty));
        sb.Children.Add(fadeIn); sb.Children.Add(fadeOut);
        sb.Completed += (_, _) => ToastHost.Children.Remove(border);
        sb.Begin();
    }

    private void ShowOperationError(string title, Exception ex)
    {
        Logger.LogError(ex, "{Title}", title);
        WpfMessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void RunUiAction(Func<Task> action, string title)
    {
        _ = RunUiActionAsync(action, title);
    }

    private async Task RunUiActionAsync(Func<Task> action, string title)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowOperationError(title, ex);
        }
    }

    // ============================================================
    //  Static helpers
    // ============================================================
    private static string ViewModeLabel(DetailViewMode mode) => mode switch
    {
        DetailViewMode.LargestFiles => "Largest files",
        DetailViewMode.LargestFolders => "Largest folders",
        DetailViewMode.Extensions => "Extensions",
        _ => "Contents"
    };

    private static string BuildSelectedSubText(DetailViewMode mode, FileSystemEntry selected)
    {
        return $"{ViewModeLabel(mode)} · {selected.FullPath} · {SizeFormatter.Format(selected.LogicalSizeBytes)} · {selected.FileCount:n0} files, {selected.DirectoryCount:n0} folders";
    }

    private static string RecycleBinItemCountText(long count)
        => count == 1 ? "1 item" : $"{count:n0} items";

    private static void RevealPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    private static bool IsExistingFileSystemPath(string path)
        => Path.IsPathFullyQualified(path) && (File.Exists(path) || Directory.Exists(path));

    private static int ColorRef(byte red, byte green, byte blue)
        => red | (green << 8) | (blue << 16);

    private static void SetDwmAttribute(IntPtr hwnd, int attribute, ref int value, string attributeName)
    {
        var result = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        if (result != 0)
        {
            Logger.LogDebug("DwmSetWindowAttribute {Attribute} failed with HRESULT 0x{Result:X8}.", attributeName, result);
        }
    }

    private static async Task WriteDetailRowsCsvAsync(IEnumerable<DetailRow> rows, string path)
    {
        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("Kind,Name,LogicalSize,AllocatedSize,Percent,Files,Folders,Path");
        foreach (var row in rows)
        {
            await writer.WriteLineAsync(string.Join(',',
                Csv(row.Kind), Csv(row.Name), Csv(row.LogicalSize), Csv(row.AllocatedSize),
                Csv(row.PercentText), row.FileCount, row.DirectoryCount, Csv(row.FullPath)));
        }
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return value;
    }

    private static void ReplaceCollection<T>(BulkObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.ReplaceAll(items);
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class CachedScan(
        string rootKey,
        ScanResult result,
        CachedScanState state,
        ScanUiPreparation preparation)
    {
        public string RootKey { get; } = rootKey;
        public ScanResult Result { get; } = result;
        public CachedScanState State { get; set; } = state;
        public ScanUiPreparation Preparation { get; } = preparation;
        public Dictionary<DetailViewMode, IReadOnlyList<DetailRow>> GlobalDetailRowsCache { get; } = [];
    }

    private sealed record CachedScanState(string SelectedPath, DetailViewMode ViewMode, ChartScope ChartScope);

    private sealed record ActiveScanJob(string RootKey, string DisplayPath, CancellationTokenSource Cancellation);

    private sealed class DetailRowComparer(DetailSortColumn column, ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is not DetailRow left) return direction == ListSortDirection.Ascending ? -1 : 1;
            if (y is not DetailRow right) return direction == ListSortDirection.Ascending ? 1 : -1;

            var result = column switch
            {
                DetailSortColumn.Name => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name),
                DetailSortColumn.Kind => StringComparer.CurrentCultureIgnoreCase.Compare(left.Kind, right.Kind),
                DetailSortColumn.LogicalSize => left.Entry.LogicalSizeBytes.CompareTo(right.Entry.LogicalSizeBytes),
                DetailSortColumn.Percent => left.Percent.CompareTo(right.Percent),
                DetailSortColumn.AllocatedSize => left.Entry.AllocatedSizeBytes.CompareTo(right.Entry.AllocatedSizeBytes),
                DetailSortColumn.FileCount => left.FileCount.CompareTo(right.FileCount),
                DetailSortColumn.DirectoryCount => left.DirectoryCount.CompareTo(right.DirectoryCount),
                DetailSortColumn.FullPath => StringComparer.CurrentCultureIgnoreCase.Compare(left.FullPath, right.FullPath),
                _ => 0
            };

            if (result == 0 && column != DetailSortColumn.FullPath)
            {
                result = StringComparer.CurrentCultureIgnoreCase.Compare(left.FullPath, right.FullPath);
            }

            return direction == ListSortDirection.Ascending ? Math.Sign(result) : -Math.Sign(result);
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);
}

public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private const int ResetNotificationThreshold = 128;
    private bool _suppressNotifications;

    public void ReplaceAll(IEnumerable<T> items)
    {
        var newItems = items as IReadOnlyList<T> ?? items.ToList();
        var oldCount = Count;

        var prefix = 0;
        while (prefix < oldCount
            && prefix < newItems.Count
            && EqualityComparer<T>.Default.Equals(Items[prefix], newItems[prefix]))
        {
            prefix++;
        }

        if (prefix == oldCount && prefix == newItems.Count)
        {
            return;
        }

        var oldSuffix = oldCount - 1;
        var newSuffix = newItems.Count - 1;
        while (oldSuffix >= prefix
            && newSuffix >= prefix
            && EqualityComparer<T>.Default.Equals(Items[oldSuffix], newItems[newSuffix]))
        {
            oldSuffix--;
            newSuffix--;
        }

        var oldChangedCount = oldSuffix >= prefix ? oldSuffix - prefix + 1 : 0;
        var newChangedCount = newSuffix >= prefix ? newSuffix - prefix + 1 : 0;
        var changedCount = oldChangedCount + newChangedCount;

        if (changedCount > ResetNotificationThreshold)
        {
            ReplaceWithReset(newItems, oldCount);
            return;
        }

        if (oldChangedCount == newChangedCount)
        {
            for (var i = 0; i < newChangedCount; i++)
            {
                SetItem(prefix + i, newItems[prefix + i]);
            }

            return;
        }

        for (var i = 0; i < oldChangedCount; i++)
        {
            RemoveItem(prefix);
        }

        for (var i = 0; i < newChangedCount; i++)
        {
            InsertItem(prefix + i, newItems[prefix + i]);
        }
    }

    private void ReplaceWithReset(IReadOnlyList<T> newItems, int oldCount)
    {
        _suppressNotifications = true;
        try
        {
            ClearItems();
            foreach (var item in newItems)
            {
                InsertItem(Count, item);
            }
        }
        finally
        {
            _suppressNotifications = false;
        }

        if (Count != oldCount)
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        }

        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotifications)
        {
            base.OnCollectionChanged(e);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotifications)
        {
            base.OnPropertyChanged(e);
        }
    }
}

public sealed record ScanUiPreparation(FolderNode RootNode, IReadOnlyList<FolderJumpRow> FolderJumpIndex);

public sealed record DriveRow(
    string Label, string RootPath, string UsedText, string FreeText, double UsedPercent);

public sealed record TargetRow(
    TargetKind Kind,
    string Label,
    string RootPath,
    string UsedText,
    string DetailText,
    double UsedPercent,
    string Icon,
    string IconBrush,
    Visibility UsageVisibility,
    Visibility DetailVisibility,
    string ScanStatusIcon,
    string ScanStatusText,
    string ScanStatusBrush,
    Visibility ScanStatusVisibility)
{
    private const string TransparentBrush = "#00000000";

    public static TargetRow RecycleBin()
        => new(
            TargetKind.RecycleBin,
            "Recycle Bin",
            string.Empty,
            string.Empty,
            "Calculating usage...",
            0,
            "\uE74D",
            "#F59E0B",
            Visibility.Collapsed,
            Visibility.Visible,
            string.Empty,
            string.Empty,
            TransparentBrush,
            Visibility.Collapsed);

    public static TargetRow RecycleBin(long sizeBytes, long itemCount)
        => new(
            TargetKind.RecycleBin,
            "Recycle Bin",
            string.Empty,
            SizeFormatter.Format(sizeBytes),
            itemCount == 0 ? "Windows Recycle Bin - empty" : $"Windows Recycle Bin - {ItemCountText(itemCount)}",
            0,
            "\uE74D",
            "#F59E0B",
            Visibility.Collapsed,
            Visibility.Visible,
            string.Empty,
            string.Empty,
            TransparentBrush,
            Visibility.Collapsed);

    public static TargetRow RecycleBinUnavailable()
        => new(
            TargetKind.RecycleBin,
            "Recycle Bin",
            string.Empty,
            "Unavailable",
            "Windows Recycle Bin",
            0,
            "\uE74D",
            "#F59E0B",
            Visibility.Collapsed,
            Visibility.Visible,
            string.Empty,
            string.Empty,
            TransparentBrush,
            Visibility.Collapsed);

    public static TargetRow FromDrive(DriveRow row, bool scanCached = false, bool scanActive = false)
        => new(
            TargetKind.Drive,
            row.Label,
            row.RootPath,
            row.UsedText,
            row.FreeText,
            row.UsedPercent,
            "\uEDA2",
            "#3B82F6",
            Visibility.Visible,
            Visibility.Collapsed,
            scanActive ? "\uE895" : scanCached ? "\uE930" : string.Empty,
            scanActive ? "Scanning" : scanCached ? "Scanned" : string.Empty,
            scanActive ? "#3B82F6" : scanCached ? "#10B981" : TransparentBrush,
            scanActive || scanCached ? Visibility.Visible : Visibility.Collapsed);

    private static string ItemCountText(long count)
        => count == 1 ? "1 item" : $"{count:n0} items";
}

public enum TargetKind
{
    Drive,
    RecycleBin
}

internal enum DetailViewMode { Contents, LargestFiles, LargestFolders, Extensions }
internal enum ChartScope { SelectedFolder, LargestFolders, LargestFiles, Extensions }
internal enum ChartDisplayMode { Treemap, Pie, Bars }
internal enum UpdateCheckMode { Manual, StartupAutoDownload }
internal enum DetailSortColumn { Name, Kind, LogicalSize, Percent, AllocatedSize, FileCount, DirectoryCount, FullPath }
