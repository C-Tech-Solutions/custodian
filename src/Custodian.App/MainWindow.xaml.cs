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
using Custodian.App.Services;
using Custodian.Platform.Windows.Logging;
using Custodian.Platform.Windows.Services;
using Microsoft.Extensions.Logging;
using Custodian.Core.Analysis;
using Custodian.Core.Export;
using Custodian.Core.Formatting;
using Custodian.Core.Model;
using Custodian.Core.Portable;
using Custodian.Core.Presentation;
using Custodian.Core.Scanning;
using Custodian.Core.Storage;
using Custodian.Core.Updates;
using WinForms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
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
    private static readonly TimeSpan TargetRefreshDebounceInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan FileOperationCloseTimeout = TimeSpan.FromSeconds(2);
    private static readonly ILogger Logger = AppLogging.CreateLogger(typeof(MainWindow).FullName!);
    private readonly DiskScanner _scanner = new();
    private readonly PortableDeviceService _portableDevices = new();
    private readonly CloudProviderDiscoveryService _cloudProviders = new();
    private readonly ScanStore _store = new();
    private readonly AppUpdateService _updates = new();
    private readonly MouseHistoryNavigationService _mouseHistoryNavigation;
    private readonly TargetRefreshCoordinator _targetRefreshCoordinator;
    private readonly DeviceChangeTargetRefreshService _deviceChangeTargetRefresh;
    private ActiveScanJob? _activeScan;
    private CancellationTokenSource? _activePortableCopy;
    private Task? _activePortableCopyTask;
    private bool _activeFileOperation;
    private CancellationTokenSource? _activeFileOperationCts;
    private Task? _activeFileOperationTask;
    private CancellationTokenSource? _updateCts;
    private CancellationTokenSource? _recycleBinCts;
    private bool _recycleBinCtsIsRefresh;
    private ScanResult? _currentScan;
    private string? _visibleScanKey;
    private FileSystemEntry? _selectedEntry;
    private DetailViewMode _viewMode = DetailViewMode.Contents;
    private ChartScope _chartScope = ChartScope.SelectedFolder;
    private ChartDisplayMode _chartDisplayMode = ChartDisplayMode.Treemap;
    private readonly ChartSelectionState _chartSelection = new();
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
    private readonly DispatcherTimer _targetRefreshDebounceTimer;
    private IReadOnlyList<FolderJumpRow> _folderJumpIndex = [];
    private IReadOnlyList<CloudProviderTarget> _cloudTargets = [];
    private readonly object _globalDetailRowsCacheGate = new();
    private readonly Dictionary<DetailViewMode, IReadOnlyList<DetailRow>> _globalDetailRowsCache = [];
    private int _globalDetailRowsCacheVersion;
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
    public BulkObservableCollection<TargetRow> EmptyStateTargets { get; } = [];
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
        _mouseHistoryNavigation = new MouseHistoryNavigationService(
            () => !_isRecycleBinViewActive && _backStack.Count > 0 && _selectedEntry is not null,
            GoBack,
            () => !_isRecycleBinViewActive && _forwardStack.Count > 0 && _selectedEntry is not null,
            GoForward);
        _targetRefreshCoordinator = new TargetRefreshCoordinator(RefreshTargetsCoreAsync);
        _deviceChangeTargetRefresh = new DeviceChangeTargetRefreshService(ScheduleDeviceChangeTargetRefresh);

        _settings = UiSettingsStore.Load();
        ApplySettingsEarly();

        DetailRowsView = CollectionViewSource.GetDefaultView(DetailRows);
        DetailRowsView.Filter = RowMatchesFilter;
        RecycleBinRowsView = CollectionViewSource.GetDefaultView(RecycleBinRows);
        RecycleBinRowsView.Filter = RecycleBinRowMatchesFilter;

        PathBox.ItemsSource = RecentPaths;
        PathBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler(PathBox_TextChanged));
        JumpBox.ItemsSource = FolderJumpRows;
        ChartScopeBox.SelectedIndex = 0;
        EmptyStateDrives.ItemsSource = EmptyStateTargets;

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
        _targetRefreshDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TargetRefreshDebounceInterval
        };
        _targetRefreshDebounceTimer.Tick += TargetRefreshDebounceTimer_Tick;

        SeedPathFromSettings();
        InstallKeyBindings();
        PreviewMouseUp += _mouseHistoryNavigation.HandlePreviewMouseUp;
        ApplySettingsLate();

        UpdateChartModeVisibility();
        UpdateEmptyStateVisibility();
        UpdateFilterUiState();
        RefreshDetailSortHeaders();
        ApplyRecycleBinSort();
        UpdateRecycleBinFilterUiState();
        UpdateRecycleBinActionState();
        UpdateDetailSelectionActionState();
        RefreshElevationWarning();

        SizeChanged += (_, _) => ScheduleSettingsSave();
        LocationChanged += (_, _) => ScheduleSettingsSave();
        StateChanged += (_, _) => ScheduleSettingsSave();
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        SourceInitialized += MainWindow_SourceInitialized;

        ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
        RefreshThemeMenuChecks();
        Loaded += MainWindow_Loaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await RefreshTargetsAsync(TargetRefreshReason.Startup);
        if (_settings.AutoUpdateOnStartup)
        {
            await CheckForUpdatesAsync(UpdateCheckMode.StartupAutoDownload);
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyNativeTitleBarTheme();
        _deviceChangeTargetRefresh.Attach(this);
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
        _targetRefreshDebounceTimer.Stop();
        _deviceChangeTargetRefresh.Dispose();
        SourceInitialized -= MainWindow_SourceInitialized;
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
        _activeFileOperationCts?.Cancel();
        _updateCts?.Cancel();
        _recycleBinCts?.Cancel();
        _settingsSaveTimer.Stop();
        try
        {
            await CancelActivePortableCopyAsync();
            await CancelActiveFileOperationAsync();
            await PersistSettingsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during window closing cleanup.");
        }
        finally
        {
            _settingsPersistedForClose = true;
            Close();
        }
    }

    private async Task CancelActiveFileOperationAsync()
    {
        var fileOperationTask = _activeFileOperationTask;
        _activeFileOperationCts?.Cancel();
        if (fileOperationTask is null)
        {
            return;
        }

        try
        {
            await fileOperationTask.WaitAsync(FileOperationCloseTimeout);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            Logger.LogWarning(
                "File operation did not respond to cancellation within {Timeout} during close.",
                FileOperationCloseTimeout);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "File operation ended while closing.");
        }
    }

    private async Task CancelActivePortableCopyAsync()
    {
        var copyTask = _activePortableCopyTask;
        _activePortableCopy?.Cancel();
        if (copyTask is null)
        {
            return;
        }

        try
        {
            await copyTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Phone copy ended while closing.");
        }
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
            _activeScan?.Cancellation.Cancel();
            _activeFileOperationCts?.Cancel();
            _updateCts?.Cancel();
            _recycleBinCts?.Cancel();
            await CancelActivePortableCopyAsync();
            await CancelActiveFileOperationAsync();
            await PersistSettingsAsync();

            ElevationService.RelaunchAsAdministrator(
                Environment.GetCommandLineArgs().Skip(1),
                PathBox.Text?.Trim());

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

    private void StopScan()
    {
        _activeScan?.Cancellation.Cancel();
        _activePortableCopy?.Cancel();
    }

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = PathBox.Text?.Trim() ?? string.Empty;
        if (FindFilesystemTargetForScanText(TargetRows, DriveRows, text) is { } filesystemTarget)
        {
            SetPortableScanControls(
                isPortableTarget: false,
                isCloudProviderTarget: filesystemTarget.CloudProvider is not null || TextMatchesCloudProviderPath(text));
            return;
        }

        if (TextMatchesCloudProviderPath(text))
        {
            SetPortableScanControls(isPortableTarget: false, isCloudProviderTarget: true);
            return;
        }

        if (ModeBox.IsEnabled)
        {
            return;
        }

        if (!TargetRows.Any(row =>
                row.Kind == TargetKind.PortableDevice &&
                TextMatchesTarget(text, row, allowEmpty: false)) &&
            !TextMatchesCurrentPortableScan(text))
        {
            SetPortableScanControls(isPortableTarget: false);
        }
    }

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
                        await DownloadAndPromptInstallAsync(result, _updateCts.Token, mode);
                    }
                    else
                    {
                        await PromptDownloadAndInstallAsync(result, _updateCts.Token);
                    }
                    break;
                case AppUpdateStatusKind.ReadyToRestart:
                    await PromptRestartForInstallAsync(result, result.Status, mode);
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

    private bool ShouldShowUpdateStatus(UpdateCheckMode mode, AppUpdateStatusKind statusKind)
        => mode == UpdateCheckMode.Manual
           || (CanShowBackgroundUpdateStatus()
               && statusKind is AppUpdateStatusKind.Available
                   or AppUpdateStatusKind.Downloading
                   or AppUpdateStatusKind.ReadyToRestart);

    private bool CanShowBackgroundUpdateStatus()
        => _activeScan is null &&
           _activePortableCopy is null &&
           !_activeFileOperation &&
           !_isRecycleBinViewActive;

    private void RestoreFooterStatus(string status, string detail)
    {
        if (!CanShowBackgroundUpdateStatus() || FooterStatus.Text != "Updates")
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

        await DownloadAndPromptInstallAsync(result, cancellationToken, UpdateCheckMode.Manual);
    }

    private async Task DownloadAndPromptInstallAsync(
        AppUpdateCheckResult result,
        CancellationToken cancellationToken,
        UpdateCheckMode mode)
    {
        var availableVersion = result.Status.AvailableVersion ?? "the latest version";
        var progress = new Progress<AppUpdateStatus>(status => ApplyUpdateStatusIfVisible(status, mode));
        await _updates.DownloadUpdatesAsync(result, progress, cancellationToken);

        var readyStatus = AppUpdateStatusFactory.ReadyToRestart(availableVersion);
        ApplyUpdateStatusIfVisible(readyStatus, mode);
        await PromptRestartForInstallAsync(result, readyStatus, mode);
    }

    private Task PromptRestartForInstallAsync(AppUpdateCheckResult result)
        => PromptRestartForInstallAsync(result, result.Status, UpdateCheckMode.Manual);

    private async Task PromptRestartForInstallAsync(
        AppUpdateCheckResult result,
        AppUpdateStatus status,
        UpdateCheckMode mode)
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

        if (mode == UpdateCheckMode.Manual || CanShowBackgroundUpdateStatus())
        {
            UpdateFooterStatus("Updates", "Update downloaded. Use Help > Check for Updates when you are ready to restart and install.");
        }
    }

    private void ApplyUpdateStatusIfVisible(AppUpdateStatus status, UpdateCheckMode mode)
    {
        if (mode == UpdateCheckMode.Manual || CanShowBackgroundUpdateStatus())
        {
            ApplyUpdateStatus(status);
        }
    }

    private void ApplyUpdateStatus(AppUpdateStatus status)
    {
        UpdateFooterStatus("Updates", status.Message);
    }

    private async Task ApplyUpdateAndShutdownAsync(AppUpdateCheckResult result)
    {
        _isClosing = true;
        _activeScan?.Cancellation.Cancel();
        _activeFileOperationCts?.Cancel();
        _updateCts?.Cancel();
        _settingsSaveTimer.Stop();
        await CancelActivePortableCopyAsync();
        await CancelActiveFileOperationAsync();
        await PersistSettingsAsync();
        _settingsPersistedForClose = true;
        UpdateFooterStatus("Updates", "Installing update...");
        _updates.ApplyUpdatesAndRestart(result);
        System.Windows.Application.Current.Shutdown();
    }

    private async Task StartScanAsync(PendingScan? explicitRequest = null)
    {
        if (_activeScan is not null || _activePortableCopy is not null) return;
        if (_activeFileOperation)
        {
            ShowToast("Wait for the current file operation to finish first.");
            return;
        }

        var request = explicitRequest ?? CreatePendingScan();
        if (request is null)
        {
            UpdateFooterStatus("Choose a path to scan.", string.Empty);
            return;
        }

        if (explicitRequest is not null &&
            !string.Equals(PathBox.Text?.Trim(), request.DisplayPath, StringComparison.OrdinalIgnoreCase))
        {
            UpdatePathDisplay(request.DisplayPath);
        }

        if (request.PortableTarget is { IsAvailable: false } unavailableTarget)
        {
            UpdateFooterStatus("Phone unavailable", unavailableTarget.DetailText);
            ShowToast(unavailableTarget.DetailText);
            return;
        }

        SetPortableScanControls(request.PortableTarget is not null, request.CloudProvider is not null);

        var navigationVersion = BeginNavigation();
        var scanKey = request.RootKey;
        var displayPath = request.DisplayPath;
        var cts = new CancellationTokenSource();
        var scan = new ActiveScanJob(scanKey, displayPath, cts);
        _activeScan = scan;
        _scanStarted = DateTime.UtcNow;
        _scanFilesSeen = 0;
        _scanBytesSeen = 0;
        _scanCurrentPath = displayPath;
        SetScanningState(true);
        RememberCurrentScanState();
        if (request.PortableTarget is null)
        {
            AddRecentPath(request.ScanPath);
        }

        RefreshTargetStatus(scanKey);
        if (_isRecycleBinViewActive)
        {
            await LeaveRecycleBinViewAsync();
        }

        if (TryGetCachedScan(scanKey, out var cached))
        {
            if (await RestoreCachedScanAsync(cached, navigationVersion, showToast: false))
            {
                UpdateFooterStatus("Scanning", $"Refreshing {displayPath}...");
            }
        }
        else
        {
            ClearViewsForNewScan(displayPath, scanKey);
            StartLoadingAnimation();
        }

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                _scanFilesSeen = p.FilesSeen;
                _scanBytesSeen = p.BytesSeen;
                _scanCurrentPath = string.IsNullOrWhiteSpace(p.CurrentPath) ? displayPath : p.CurrentPath;
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

            var result = request.PortableTarget is { } portableTarget
                ? await _portableDevices.ScanAsync(portableTarget, progress, cts.Token)
                : await _scanner.ScanAsync(
                    new ScanOptions(
                        request.ScanPath,
                        mode,
                        CollectAllocatedSize: AllocatedSizeBox.IsChecked == true,
                        CloudProvider: request.CloudProvider),
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
                ShowToast($"Scan cancelled: {displayPath}");
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
                ShowToast($"Scan failed: {displayPath}");
                UpdateFooterStatus("Ready", BuildFooterDetail(_currentScan));
                EngineBadge.Text = _currentScan.Engine;
                EngineBadgeDot.Fill = (WpfBrush?)TryFindResource("SuccessBrush") ?? WpfBrushes.Green;
            }
            else
            {
                ShowToast($"Scan failed: {displayPath}");
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
            RefreshTargetStatus(scanKey);
        }
    }

    private PendingScan? CreatePendingScan()
    {
        var text = PathBox.Text?.Trim() ?? string.Empty;
        if (FindFilesystemTargetForScanText(TargetRows, DriveRows, text) is { } filesystemTarget)
        {
            return new PendingScan(
                filesystemTarget.RootPath,
                filesystemTarget.DisplayPath,
                filesystemTarget.RootPath,
                null,
                filesystemTarget.CloudProvider ?? TryMatchCloudProviderPath(text));
        }

        if (DriveList.SelectedItem is TargetRow { Kind: TargetKind.PortableDevice, PortableTarget: { } portableTarget } row &&
            TextMatchesTarget(text, row, allowEmpty: true))
        {
            return new PendingScan(row.RootPath, row.DisplayPath, row.RootPath, portableTarget, null);
        }

        var portableTargetRow = TargetRows.FirstOrDefault(row =>
            row.Kind == TargetKind.PortableDevice &&
            row.PortableTarget is not null &&
            TextMatchesTarget(text, row, allowEmpty: false));
        if (portableTargetRow?.PortableTarget is { } target)
        {
            return new PendingScan(portableTargetRow.RootPath, portableTargetRow.DisplayPath, portableTargetRow.RootPath, target, null);
        }

        if (CreatePendingScanFromCurrentPortableScan(text) is { } portableScan)
        {
            return portableScan;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var scanKey = TryGetScanCacheKey(text, out var normalizedKey)
            ? normalizedKey
            : text;
        return new PendingScan(scanKey, text, text, null, TryMatchCloudProviderPath(text));
    }

    private PendingScan? CreatePendingScanFromCurrentPortableScan(string text)
    {
        if (_currentScan is not { SourceKind: ScanSourceKind.PortableDevice } currentScan ||
            !TextMatchesScanDisplayRoot(text, currentScan))
        {
            return null;
        }

        var liveTargetRow = FindPortableTargetRowForScan(TargetRows, currentScan);
        if (liveTargetRow?.PortableTarget is { } liveTarget)
        {
            return new PendingScan(liveTargetRow.RootPath, liveTargetRow.DisplayPath, liveTargetRow.RootPath, liveTarget, null);
        }

        var displayPath = DisplayRootPath(currentScan);
        var unavailableTarget = new PortableDeviceTarget(
            currentScan.RootPath,
            currentScan.PortableDeviceId,
            string.IsNullOrWhiteSpace(currentScan.PortableDeviceName) ? displayPath : currentScan.PortableDeviceName,
            currentScan.PortableStorageObjectId,
            currentScan.PortableStorageName,
            displayPath,
            null,
            null,
            IsAvailable: false,
            "Reconnect or unlock the phone and choose USB File Transfer mode.");
        return new PendingScan(currentScan.RootPath, displayPath, currentScan.RootPath, unavailableTarget, null);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        SetPortableScanControls(isPortableTarget: false);
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
            SetPortableScanControls(
                isPortableTarget: false,
                isCloudProviderTarget: _cloudProviders.TryMatchPath(dialog.SelectedPath) is not null);
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

                PathBox.Text = DisplayRootPath(loaded);
                SetPortableScanControls(loaded.SourceKind == ScanSourceKind.PortableDevice, loaded.CloudProvider is not null);
                if (loaded.SourceKind == ScanSourceKind.FileSystem)
                {
                    AddRecentPath(loaded.RootPath);
                }

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

        if (row.Kind == TargetKind.PortableDevice)
        {
            RememberCurrentScanState();

            if (_isRecycleBinViewActive)
            {
                await LeaveRecycleBinViewAsync();
            }

            PathBox.Text = row.DisplayPath;
            SetPortableScanControls(isPortableTarget: true);

            if (!row.IsAvailable)
            {
                ShowStartScanPrompt(row.DisplayPath, row.DetailText);
                UpdateFooterStatus("Phone unavailable", row.DetailText);
                return;
            }

            if (_activeScan is { } activePortableScan && IsScanActive(row.RootPath))
            {
                ShowActiveScanPage(activePortableScan);
                return;
            }

            if (TryGetCachedScan(row.RootPath, out var portableCached))
            {
                await RestoreCachedScanAsync(portableCached, targetSelectionVersion, showToast: false);
                return;
            }

            ShowStartScanPrompt(row.DisplayPath);
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
            SetPortableScanControls(isPortableTarget: false, isCloudProviderTarget: row.CloudProvider is not null);
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
        if (sender is not FrameworkElement { DataContext: TargetRow target } ||
            target.Kind is not (TargetKind.Drive or TargetKind.CloudProvider or TargetKind.PortableDevice))
        {
            return;
        }

        SelectTargetWithoutNavigation(target);
        PathBox.Text = target.Kind == TargetKind.PortableDevice ? target.DisplayPath : target.RootPath;
        SetPortableScanControls(target.Kind == TargetKind.PortableDevice, target.CloudProvider is not null);
        await StartScanAsync();
    }

    private void SelectTargetWithoutNavigation(TargetRow target)
    {
        var match = TargetRows.FirstOrDefault(row =>
            row.Kind == target.Kind &&
            string.Equals(row.RootPath, target.RootPath, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return;
        }

        _suppressTargetSelection = true;
        try
        {
            DriveList.SelectedItem = match;
            DriveList.ScrollIntoView(match);
        }
        finally
        {
            _suppressTargetSelection = false;
        }

        RefreshEmptyStateTargets();
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
            var selectedEntry = _selectedEntry;
            if (_backStack.Count == 0 || selectedEntry is null || _isRecycleBinViewActive) return;
            _forwardStack.Push(selectedEntry);
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
            var selectedEntry = _selectedEntry;
            if (_forwardStack.Count == 0 || selectedEntry is null || _isRecycleBinViewActive) return;
            _backStack.Push(selectedEntry);
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
        ClearChartSelection();
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
        => RunUiAction(() => SelectChartSliceAsync(e.Slice, drillIntoFolders: false, toggleSelection: e.IsToggleSelection), "Chart selection failed");

    private void Chart_SliceDoubleClicked(object sender, ChartSliceEventArgs e)
    {
        if (e.IsToggleSelection)
        {
            return;
        }

        RunUiAction(() => SelectChartSliceAsync(e.Slice, drillIntoFolders: true, toggleSelection: false), "Chart selection failed");
    }

    private void ChartBars_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChartSelection) return;
        RunUiAction(async () =>
        {
            var selectedSlices = ChartBars.SelectedItems.OfType<ChartSlice>().ToArray();
            _chartSelection.ReplaceWith(selectedSlices);
            ApplyChartSelectionToControls(_chartSelection.PrimarySlice(ChartSlices));
            await SelectDetailRowsForChartSelectionAsync();
            FocusActiveChartSurface();
            RememberCurrentScanState();
        }, "Chart selection failed");
    }

    private void ChartBars_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        RunUiAction(async () =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) return;
            if (ChartBars.SelectedItem is ChartSlice slice)
            {
                await SelectChartSliceAsync(slice, drillIntoFolders: true, toggleSelection: false);
            }
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
        if (!row.IsSelected)
        {
            DetailsGrid.SelectedItems.Clear();
            row.IsSelected = true;
        }

        row.Focus();
    }

    private void DetailsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateDetailSelectionActionState();

    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Handled || ChartSelectionKeyboardService.IsTextInputFocus(Keyboard.FocusedElement))
        {
            return;
        }

        if (DetailSelectionDeleteShortcutService.ResolveForChartSelection(e.Key, Keyboard.Modifiers) is not { } deleteMode ||
            !ChartSelectionKeyboardService.ShouldRouteDeleteShortcut(
                Keyboard.FocusedElement,
                ChartSurfaceHost,
                _chartSelection.Count > 0))
        {
            return;
        }

        e.Handled = true;
        RunUiAction(() => DeleteChartSelectionAsync(deleteMode), "Delete failed");
    }

    private void DetailsGrid_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (HandleSelectionDeleteShortcut(e))
        {
            return;
        }

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

    private void ChartSelection_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (_chartSelection.Count == 0)
        {
            return;
        }

        if (DetailSelectionDeleteShortcutService.ResolveForChartSelection(e.Key, Keyboard.Modifiers) is not { } deleteMode)
        {
            return;
        }

        e.Handled = true;
        RunUiAction(() => DeleteChartSelectionAsync(deleteMode), "Delete failed");
    }

    private bool HandleSelectionDeleteShortcut(WpfKeyEventArgs e)
    {
        if (DetailSelectionDeleteShortcutService.Resolve(e.Key, Keyboard.Modifiers) is not { } deleteMode)
        {
            return false;
        }

        e.Handled = true;
        RunUiAction(() => DeleteSelectedFileSystemItemsAsync(deleteMode), "Delete failed");
        return true;
    }

    private async Task DeleteChartSelectionAsync(DetailSelectionDeleteMode deleteMode)
    {
        var selectedRows = ChartDetailSelectionService.BuildDeleteRows(_chartSelection.SelectedSlices(ChartSlices));
        await SelectDetailRowsForChartSelectionAsync();
        await DeleteFileSystemItemsAsync(deleteMode, selectedRows);
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
    private void DetailsContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        UpdateDetailSelectionActionState();
        var portableVisible = CurrentScanIsPortable() ? Visibility.Visible : Visibility.Collapsed;
        var localVisible = CurrentScanIsPortable() ? Visibility.Collapsed : Visibility.Visible;
        CopyPortableToPcMenuItem.Visibility = portableVisible;
        CopyPortableToPcSeparator.Visibility = portableVisible;
        CopyToFolderMenuItem.Visibility = localVisible;
        MoveToFolderMenuItem.Visibility = localVisible;
    }

    private void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDetailRows().Take(2).Count() > 1)
        {
            ShowToast("Select one item to open.");
            return;
        }

        if (CurrentScanIsPortable())
        {
            OpenPortableSelectionInExplorer(PortableExplorerOpenMode.Open);
            return;
        }

        var path = SelectedPath();
        if (path is null) return;
        var isRemotePath = PathClassificationService.IsRemotePath(path);
        if (!ConfirmLaunchIfRemote(path, isRemotePath))
        {
            return;
        }

        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            return;
        }
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    // A loaded .custodian-scan is untrusted input: its entries' paths are attacker-
    // controllable, and a crafted file can point an entry at a remote/UNC executable.
    // Shell-executing such a file would run an attacker-hosted binary, so require explicit
    // confirmation before opening anything that is not on a local fixed/removable drive.
    private bool ConfirmLaunchIfRemote(string path, bool isRemotePath)
    {
        if (!isRemotePath)
        {
            return true;
        }

        var answer = WpfMessageBox.Show(
            this,
            $"This item is on a network or remote location:\n\n{path}\n\nOpening it runs or opens the file from that remote location, which may be unsafe if the scan came from an untrusted source. Open it anyway?",
            "Confirm opening remote item",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return answer == MessageBoxResult.Yes;
    }

    private void RevealSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDetailRows().Take(2).Count() > 1)
        {
            ShowToast("Select one item to reveal.");
            return;
        }

        if (CurrentScanIsPortable())
        {
            OpenPortableSelectionInExplorer(PortableExplorerOpenMode.Reveal);
            return;
        }

        var path = SelectedPath();
        if (path is null) return;
        if (IsExistingFileSystemPath(path)) RevealPath(path);
    }

    private async void CopySelection_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentScanIsPortable())
        {
            CopyPortableToPc_Click(sender, e);
            return;
        }

        await CopyOrMoveSelectedFileSystemItemsAsync(FileSystemOperationKind.Copy);
    }

    private async void MoveSelection_Click(object sender, RoutedEventArgs e)
        => await CopyOrMoveSelectedFileSystemItemsAsync(FileSystemOperationKind.Move);

    private async Task CopyOrMoveSelectedFileSystemItemsAsync(FileSystemOperationKind operationKind)
    {
        if (_activeScan is not null || _activePortableCopy is not null || _activeFileOperation)
        {
            ShowToast("Wait for the current operation to finish first.");
            return;
        }

        var selectedRows = SelectedDetailRows().ToList();
        var paths = DetailSelectionActionService.FileSystemPaths(selectedRows);
        if (selectedRows.Count == 0 || paths.Count == 0)
        {
            ShowToast("Select one or more file or folder rows first.");
            return;
        }

        if (paths.Count != selectedRows.Count)
        {
            ShowToast("Copy and move require real file or folder rows.");
            return;
        }

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = operationKind == FileSystemOperationKind.Copy
                ? "Choose where to copy the selected files and folders"
                : "Choose where to move the selected files and folders",
            UseDescriptionForTitle = true,
            SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        if (operationKind == FileSystemOperationKind.Move &&
            !ConfirmMoveSelection(paths.Count, dialog.SelectedPath))
        {
            return;
        }

        await RunFileSystemOperationAsync(
            operationKind,
            paths,
            dialog.SelectedPath,
            selectedRows.Select(row => row.Entry).ToArray());
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

    private async void CopyPortableToPc_Click(object sender, RoutedEventArgs e)
    {
        if (_activeScan is not null || _activePortableCopy is not null || _activeFileOperation)
        {
            ShowToast("Wait for the current operation to finish first.");
            return;
        }

        if (_currentScan is not { SourceKind: ScanSourceKind.PortableDevice } currentScan)
        {
            ShowToast("Copy to PC is only available for phone scans.");
            return;
        }

        var selectedRows = SelectedDetailRows().ToList();
        if (selectedRows.Count > 0 && !DetailSelectionActionService.AllRowsUsePortableObjectIdentity(selectedRows))
        {
            ShowToast("Copy to PC requires real phone file or folder rows.");
            return;
        }

        var entries = SelectedEntriesForPortableCopy().ToList();
        if (entries.Count == 0)
        {
            ShowToast("Select one or more phone files or folders first.");
            return;
        }

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Choose where to copy the selected phone files",
            UseDescriptionForTitle = true,
            SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _activePortableCopy = cts;
        SetScanningState(true);
        ProgressBar.IsIndeterminate = true;
        UpdateFooterStatus("Copying phone files", dialog.SelectedPath);

        try
        {
            var progress = new Progress<PortableCopyProgress>(copyProgress =>
            {
                UpdateFooterStatus(
                    copyProgress.Message,
                    $"{copyProgress.FilesCopied:n0} copied, {copyProgress.FilesSkipped:n0} skipped, {SizeFormatter.Format(copyProgress.BytesCopied)}");
            });

            var copyTask = _portableDevices.CopyToPcAsync(currentScan, entries, dialog.SelectedPath, progress, cts.Token);
            _activePortableCopyTask = copyTask;
            var result = await copyTask;
            var skippedText = result.FilesSkipped > 0 ? $", {result.FilesSkipped:n0} skipped" : string.Empty;
            ShowToast($"Copied {result.FilesCopied:n0} file(s){skippedText}.");
            UpdateFooterStatus("Ready", $"Copied to {dialog.SelectedPath}");

            if (result.SkippedEntries.Count > 0)
            {
                ShowPortableCopySkippedSummary(result);
            }
        }
        catch (OperationCanceledException)
        {
            ShowToast("Phone copy cancelled.");
            UpdateFooterStatus("Cancelled", "Phone copy cancelled.");
        }
        catch (Exception ex)
        {
            ShowOperationError("Phone copy failed", ex);
        }
        finally
        {
            if (ReferenceEquals(_activePortableCopy, cts))
            {
                _activePortableCopy = null;
                _activePortableCopyTask = null;
            }

            cts.Dispose();
            SetScanningState(false);
            if (_currentScan is not null && !_isRecycleBinViewActive)
            {
                UpdateFooterStatus("Ready", BuildFooterDetail(_currentScan));
            }
        }
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

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
        => await DeleteSelectedFileSystemItemsAsync(DetailSelectionDeleteMode.Recycle);

    private async void PermanentDeleteSelected_Click(object sender, RoutedEventArgs e)
        => await DeleteSelectedFileSystemItemsAsync(DetailSelectionDeleteMode.PermanentDelete);

    private async Task DeleteSelectedFileSystemItemsAsync(DetailSelectionDeleteMode mode)
    {
        await DeleteFileSystemItemsAsync(mode, SelectedDetailRows().ToList());
    }

    private async Task DeleteFileSystemItemsAsync(
        DetailSelectionDeleteMode mode,
        IReadOnlyCollection<DetailRow> selectedRows)
    {
        var command = DetailSelectionDeleteCommandService.Build(
            mode,
            selectedRows,
            CurrentScanIsPortable(),
            _activeScan is not null || _activePortableCopy is not null || _activeFileOperation);

        if (!command.CanExecute)
        {
            if (command.BlockReason == DetailSelectionDeleteBlockReason.PortableScan)
            {
                ShowPortableDeviceModificationBlockedMessage();
                return;
            }

            ShowToast(command.ToastMessage ?? "Selection cannot be deleted.");
            return;
        }

        var answer = command.DefaultResult is { } defaultResult
            ? WpfMessageBox.Show(
                this,
                command.ConfirmationMessage,
                command.ConfirmationTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                defaultResult)
            : WpfMessageBox.Show(
                this,
                command.ConfirmationMessage,
                command.ConfirmationTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        await RunFileSystemOperationAsync(
            command.OperationKind,
            command.Paths,
            destinationFolder: null,
            selectedRows.Select(row => row.Entry).ToArray());
    }

    private bool ConfirmMoveSelection(int count, string destinationFolder)
    {
        var answer = WpfMessageBox.Show(
            this,
            $"Move {count:n0} selected item(s) to:\n\n{destinationFolder}\n\nWindows may prompt if names collide or access is denied.",
            "Confirm Move",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return answer == MessageBoxResult.Yes;
    }

    private async Task RunFileSystemOperationAsync(
        FileSystemOperationKind operationKind,
        IReadOnlyCollection<string> paths,
        string? destinationFolder,
        IReadOnlyCollection<FileSystemEntry> sourceEntries)
    {
        var originatingScan = _isRecycleBinViewActive ? null : _currentScan;
        var cts = new CancellationTokenSource();
        _activeFileOperationCts = cts;
        _activeFileOperation = true;
        UpdateDetailSelectionActionState();
        var operationText = operationKind switch
        {
            FileSystemOperationKind.Copy => "Copying selected items",
            FileSystemOperationKind.Move => "Moving selected items",
            FileSystemOperationKind.PermanentDelete => "Deleting selected items permanently",
            _ => "Moving selected items to Recycle Bin"
        };
        UpdateFooterStatus(operationText, destinationFolder ?? $"{paths.Count:n0} selected item(s)");

        try
        {
            var ownerHandle = new WindowInteropHelper(this).Handle;
            var operationTask = operationKind switch
            {
                FileSystemOperationKind.Copy => FileSystemOperationService.CopyToFolderAsync(paths, destinationFolder!, ownerHandle, cts.Token),
                FileSystemOperationKind.Move => FileSystemOperationService.MoveToFolderAsync(paths, destinationFolder!, ownerHandle, cts.Token),
                FileSystemOperationKind.PermanentDelete => FileSystemOperationService.DeletePermanentlyAsync(paths, ownerHandle, cts.Token),
                _ => FileSystemOperationService.MoveToRecycleBinAsync(paths, ownerHandle, cts.Token)
            };
            _activeFileOperationTask = operationTask;

            var result = await operationTask;
            if (!_isClosing)
            {
                ShowFileSystemOperationResult(operationKind, result, destinationFolder);
                await ApplyFileSystemOperationScanMutationAsync(
                    operationKind,
                    result,
                    sourceEntries,
                    originatingScan,
                    destinationFolder);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
            {
                ShowToast("File operation cancelled.");
                UpdateFooterStatus("Cancelled", "File operation cancelled.");
            }
        }
        catch (Exception ex)
        {
            if (_isClosing)
            {
                Logger.LogWarning(ex, "{OperationKind} operation ended during shutdown.", operationKind);
            }
            else
            {
                Logger.LogError(ex, "{OperationKind} operation failed.", operationKind);
                ShowOperationError(FileSystemOperationTitle(operationKind) + " failed", ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_activeFileOperationCts, cts))
            {
                _activeFileOperationCts = null;
                _activeFileOperationTask = null;
            }

            cts.Dispose();
            _activeFileOperation = false;
            if (!_isClosing)
            {
                UpdateDetailSelectionActionState();
                if (_currentScan is not null && !_isRecycleBinViewActive)
                {
                    UpdateFooterStatus("Ready", BuildFooterDetail(_currentScan));
                }
            }
        }
    }

    private async Task ApplyFileSystemOperationScanMutationAsync(
        FileSystemOperationKind operationKind,
        FileSystemOperationBatchResult result,
        IReadOnlyCollection<FileSystemEntry> sourceEntries,
        ScanResult? originatingScan,
        string? destinationFolder)
    {
        if (originatingScan is null)
        {
            return;
        }

        var entriesToRemove = FileSystemOperationScanMutationService.RemovedEntriesFor(
            operationKind,
            result,
            sourceEntries,
            originatingScan,
            destinationFolder);
        if (entriesToRemove.Count == 0)
        {
            return;
        }

        var selectedEntry = ReferenceEquals(_currentScan, originatingScan) && !_isRecycleBinViewActive
            ? _selectedEntry
            : null;
        var update = ScanTreeUpdater.RemoveEntries(originatingScan, entriesToRemove, selectedEntry);
        if (!update.Changed)
        {
            return;
        }

        if (ReferenceEquals(_currentScan, originatingScan) && !_isRecycleBinViewActive)
        {
            _selectedEntry = update.SelectedEntry ?? originatingScan.Root;
        }

        await RefreshScanAfterTreeMutationAsync(originatingScan, destinationFolder);
    }

    private void ShowFileSystemOperationResult(
        FileSystemOperationKind operationKind,
        FileSystemOperationBatchResult result,
        string? destinationFolder)
    {
        var title = FileSystemOperationTitle(operationKind);
        var destinationText = string.IsNullOrWhiteSpace(destinationFolder) ? string.Empty : $" to {destinationFolder}";
        if (!result.HasIssues)
        {
            ShowToast($"{title} complete: {result.CompletedCount:n0} item(s){destinationText}.");
            return;
        }

        var preview = string.Join(
            Environment.NewLine,
            result.Failures.Take(8).Select(failure => $"{failure.Path}: {failure.Reason}"));
        var more = result.Failures.Count > 8
            ? $"{Environment.NewLine}...and {result.Failures.Count - 8:n0} more failure(s)."
            : string.Empty;
        var summary = result.IndeterminateCount > 0
            ? $"Windows reported that the staged batch was cancelled or partially aborted. Exact per-item completion is unavailable after a shell batch cancellation. Check the source or destination folder for the final item state.{Environment.NewLine}{Environment.NewLine}{result.IndeterminateCount:n0} staged item(s) have indeterminate completion state."
            : result.CancelledCount > 0
                ? $"{result.CancelledCount:n0} item(s) were cancelled or skipped by Windows."
                : $"{result.CompletedCount:n0} of {result.RequestedCount:n0} item(s) completed.";
        var message = $"{title} completed with issues.{Environment.NewLine}{Environment.NewLine}{summary}";
        if (!string.IsNullOrWhiteSpace(preview))
        {
            message += $"{Environment.NewLine}{Environment.NewLine}{preview}{more}";
        }

        WpfMessageBox.Show(this, message, title + " completed with issues", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string FileSystemOperationTitle(FileSystemOperationKind operationKind)
        => operationKind switch
        {
            FileSystemOperationKind.Copy => "Copy",
            FileSystemOperationKind.Move => "Move",
            FileSystemOperationKind.PermanentDelete => "Permanent delete",
            _ => "Recycle Bin move"
        };

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

    private async Task RefreshScanAfterTreeMutationAsync(ScanResult result, string? destinationFolder)
    {
        ClearGlobalDetailRowsCacheFor(result);
        var isVisibleScan = ReferenceEquals(_currentScan, result) && !_isRecycleBinViewActive;
        if (isVisibleScan)
        {
            ResetProjectionRequests();
        }

        var preparation = await PrepareScanUiAsync(result);
        var stillVisibleScan = ReferenceEquals(_currentScan, result) && !_isRecycleBinViewActive;
        if (stillVisibleScan && !IsEntryReachableFromRoot(result.Root, _selectedEntry ?? result.Root))
        {
            _selectedEntry = result.Root;
        }

        if (_visibleCachedScan is { } visible && ReferenceEquals(visible.Result, result))
        {
            visible.Preparation = preparation;
            if (stillVisibleScan)
            {
                visible.State = CaptureCurrentScanState(result);
            }
        }

        if (TryGetScanCacheKey(result.RootPath, out var key) &&
            _sessionScanCache.TryGetValue(key, out var cached) &&
            ReferenceEquals(cached.Result, result))
        {
            cached.Preparation = preparation;
            if (stillVisibleScan)
            {
                cached.State = CaptureCurrentScanState(result);
            }
        }

        await RefreshTargetUsageAfterTreeMutationAsync(result, destinationFolder);
        var visibleAfterTargetRefresh = ReferenceEquals(_currentScan, result) && !_isRecycleBinViewActive;
        if (!visibleAfterTargetRefresh)
        {
            return;
        }

        ReplaceCollection(FolderNodes, [preparation.RootNode]);
        _folderJumpIndex = preparation.FolderJumpIndex;
        _backStack.Clear();
        _forwardStack.Clear();
        RefreshFolderJumpRows(JumpBox.Text ?? string.Empty);
        await RefreshDetailsAsync();
        UpdateFooterStatus("Ready", BuildFooterDetail(result));
    }

    private async Task RefreshTargetUsageAfterTreeMutationAsync(ScanResult result, string? destinationFolder)
    {
        var freshDriveRows = await Task.Run(BuildDriveRows);
        var selectedTargetBeforeRefresh = DriveList.SelectedItem as TargetRow;
        IReadOnlyList<string> refreshedDriveRoots;
        var wasSuppressingTargetSelection = _suppressTargetSelection;
        _suppressTargetSelection = true;
        try
        {
            refreshedDriveRoots = TargetUsageRefreshService.RefreshDriveTargetsForPaths(
                TargetRows,
                DriveRows,
                freshDriveRows,
                [result.RootPath, destinationFolder],
                IsScanCached,
                IsScanActive);
        }
        finally
        {
            _suppressTargetSelection = wasSuppressingTargetSelection;
        }

        RefreshTargetStatus(result.RootPath);
        foreach (var rootPath in refreshedDriveRoots)
        {
            RefreshTargetStatus(rootPath);
        }

        RestoreSelectedTargetRow(selectedTargetBeforeRefresh);
        RefreshEmptyStateTargets();
    }

    private void RestoreSelectedTargetRow(TargetRow? previousSelectedTarget)
    {
        if (previousSelectedTarget is null)
        {
            return;
        }

        var replacement = TargetMatchingService.FindEquivalentTargetRow(TargetRows, previousSelectedTarget);
        if (replacement is null || ReferenceEquals(DriveList.SelectedItem, replacement))
        {
            return;
        }

        var wasSuppressingTargetSelection = _suppressTargetSelection;
        _suppressTargetSelection = true;
        try
        {
            DriveList.SelectedItem = replacement;
        }
        finally
        {
            _suppressTargetSelection = wasSuppressingTargetSelection;
        }
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
        if (IsPortableTargetId(rootPath))
        {
            key = rootPath;
            return true;
        }

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

    private bool CurrentScanIsPortable()
        => _currentScan?.SourceKind == ScanSourceKind.PortableDevice;

    private void OpenPortableSelectionInExplorer(PortableExplorerOpenMode mode)
    {
        if (_currentScan is not { SourceKind: ScanSourceKind.PortableDevice } currentScan)
        {
            return;
        }

        var entry = SelectedEntry();
        if (entry is null)
        {
            ShowToast("Select a phone file or folder first.");
            return;
        }

        try
        {
            var result = PortableDeviceExplorerService.Open(currentScan, entry, mode);
            ShowPortableExplorerResult(result, entry, mode);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to open portable device item in Explorer.");
            ShowPortableExplorerResult(
                PortableExplorerOpenResult.Failed(ex.Message),
                entry,
                mode);
        }
    }

    private void ShowPortableExplorerResult(
        PortableExplorerOpenResult result,
        FileSystemEntry entry,
        PortableExplorerOpenMode mode)
    {
        switch (result.Kind)
        {
            case PortableExplorerOpenResultKind.OpenedExactItem:
                ShowToast(BuildPortableExplorerExactMessage(entry, mode));
                break;
            case PortableExplorerOpenResultKind.OpenedParent:
                ShowToast("Could not open that phone item. Opened the nearest available folder.");
                break;
            case PortableExplorerOpenResultKind.OpenedStorageRoot:
                ShowToast("Could not open that phone item. Opened the phone storage root.");
                break;
            case PortableExplorerOpenResultKind.OpenedThisPc:
                ShowToast("Could not open that phone item. Opened This PC; open the phone from there.");
                break;
            default:
                ShowToast("Could not open that phone item. Unlock the phone and choose USB File Transfer mode.");
                break;
        }
    }

    private static string BuildPortableExplorerExactMessage(FileSystemEntry entry, PortableExplorerOpenMode mode)
    {
        if (entry.IsDirectory)
        {
            return "Opened phone folder in Explorer.";
        }

        return mode == PortableExplorerOpenMode.Reveal
            ? "Revealed phone file in Explorer."
            : "Opened phone file.";
    }

    private IEnumerable<FileSystemEntry> SelectedEntriesForPortableCopy()
        => PortableCopyEntriesFromSelectedRows(SelectedDetailRows());

    internal static IReadOnlyList<FileSystemEntry> PortableCopyEntriesFromSelectedRows(IEnumerable<DetailRow> selectedRows)
        => selectedRows.Select(row => row.Entry).ToList();

    private void ShowPortableCopySkippedSummary(PortableCopyResult result)
    {
        var preview = string.Join(
            Environment.NewLine,
            result.SkippedEntries.Take(8).Select(entry => $"{entry.Path}: {entry.Reason}"));
        var suffix = result.SkippedEntries.Count > 8
            ? $"{Environment.NewLine}{Environment.NewLine}...and {result.SkippedEntries.Count - 8:n0} more."
            : string.Empty;
        WpfMessageBox.Show(
            this,
            $"{result.SkippedEntries.Count:n0} phone item(s) could not be copied.{Environment.NewLine}{Environment.NewLine}{preview}{suffix}",
            "Phone copy completed with skips",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ShowPortableDeviceModificationBlockedMessage()
        => ShowToast("Portable device items can be opened in Explorer and copied to the PC, but not deleted or modified on the phone.");

    private static bool IsPortableTargetId(string? value)
        => value is not null && value.StartsWith("wpd:", StringComparison.OrdinalIgnoreCase);

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
                if (target.Kind is not (TargetKind.Drive or TargetKind.CloudProvider or TargetKind.PortableDevice) ||
                    !TryGetScanCacheKey(target.RootPath, out var targetKey) ||
                    !string.Equals(key, targetKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var replacement = target.Kind switch
                {
                    TargetKind.PortableDevice when target.PortableTarget is { } portableTarget => TargetRow.FromPortable(
                        portableTarget,
                        scanCached: IsScanCached(portableTarget.TargetId),
                        scanActive: IsScanActive(portableTarget.TargetId)),
                    TargetKind.CloudProvider when target.CloudProviderTarget is { } cloudTarget => TargetRow.FromCloudProvider(
                        cloudTarget,
                        scanCached: IsScanCached(cloudTarget.RootPath),
                        scanActive: IsScanActive(cloudTarget.RootPath)),
                    TargetKind.Drive when DriveRows.FirstOrDefault(row =>
                        string.Equals(row.RootPath, target.RootPath, StringComparison.OrdinalIgnoreCase)) is { } drive => TargetRow.FromDrive(
                            drive,
                            scanCached: IsScanCached(drive.RootPath),
                            scanActive: IsScanActive(drive.RootPath)),
                    _ => target
                };
                if (!TargetRowsEquivalent(target, replacement))
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

    private static bool TargetRowsEquivalent(TargetRow left, TargetRow right)
        => EqualityComparer<TargetRow>.Default.Equals(left, right) &&
            EqualityComparer<PortableDeviceTarget?>.Default.Equals(left.PortableTarget, right.PortableTarget) &&
            EqualityComparer<CloudProviderTarget?>.Default.Equals(left.CloudProviderTarget, right.CloudProviderTarget) &&
            EqualityComparer<CloudProviderMetadata?>.Default.Equals(left.CloudProvider, right.CloudProvider) &&
            left.IsCloudDrive == right.IsCloudDrive;

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
        ClearChartSelection();
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
        SelectedSubText.Text = BuildSelectedStatusText(viewMode, selected);
    }

    private IReadOnlyList<DetailRow> GetOrCreateGlobalDetailRows(
        ScanResult result,
        DetailViewMode mode,
        Dictionary<DetailViewMode, IReadOnlyList<DetailRow>> cache)
    {
        var cacheVersion = GetGlobalDetailRowsCacheVersion();
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
            if (cacheVersion != _globalDetailRowsCacheVersion)
            {
                return rows;
            }

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

    private int GetGlobalDetailRowsCacheVersion()
    {
        lock (_globalDetailRowsCacheGate)
        {
            return _globalDetailRowsCacheVersion;
        }
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
            _globalDetailRowsCacheVersion++;
            _globalDetailRowsCache.Clear();
        }

        ResetProjectionRequests();
    }

    private void ClearGlobalDetailRowsCacheFor(ScanResult result)
    {
        lock (_globalDetailRowsCacheGate)
        {
            _globalDetailRowsCacheVersion++;
            _globalDetailRowsCache.Clear();
            if (_visibleCachedScan is { } visible && ReferenceEquals(visible.Result, result))
            {
                visible.GlobalDetailRowsCache.Clear();
            }

            if (TryGetScanCacheKey(result.RootPath, out var key) &&
                _sessionScanCache.TryGetValue(key, out var cached) &&
                ReferenceEquals(cached.Result, result))
            {
                cached.GlobalDetailRowsCache.Clear();
            }
        }
    }

    private void ResetProjectionRequests()
    {
        _detailRefreshVersion++;
        _chartRefreshVersion++;
    }

    private static bool IsEntryReachableFromRoot(FileSystemEntry root, FileSystemEntry entry)
        => root.Flatten().Any(candidate => ReferenceEquals(candidate, entry));

    private async Task RefreshChartAsync()
    {
        var requestVersion = ++_chartRefreshVersion;
        var result = _currentScan;
        if (result is null)
        {
            ChartSlices.Clear();
            ChartTitleText.Text = "Disk distribution";
            ChartTotalText.Text = "Run a scan to render chart data.";
            ClearChartSelection();
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
        var wasSuppressing = _suppressChartSelection;
        _suppressChartSelection = true;
        try
        {
            ReplaceCollection(ChartSlices, dataset.Slices);
            ChartTitleText.Text = dataset.Title;
            ChartTotalText.Text = dataset.HasOther
                ? $"{dataset.TotalSize} · top {Math.Min(12, dataset.Slices.Count)} + other"
                : $"{dataset.TotalSize} · {dataset.Slices.Count:n0} item(s)";

            _chartSelection.PruneTo(ChartSlices);
            ApplyChartSelectionToControls(_chartSelection.PrimarySlice(ChartSlices));
        }
        finally
        {
            _suppressChartSelection = wasSuppressing;
        }
    }

    private async Task SelectChartSliceAsync(ChartSlice slice, bool drillIntoFolders, bool toggleSelection)
    {
        if (toggleSelection)
        {
            _chartSelection.Toggle(slice);
            drillIntoFolders = false;
        }
        else
        {
            _chartSelection.SelectSingle(slice);
        }

        ApplyChartSelectionToControls(slice);

        if (!toggleSelection && drillIntoFolders && slice.Entry is { IsDirectory: true } folder)
        {
            SetChartScope(ChartScope.SelectedFolder);
            await NavigateToFolderAsync(folder);
            return;
        }

        await SelectDetailRowsForChartSelectionAsync();
        FocusActiveChartSurface();
        RememberCurrentScanState();
    }

    private void FocusActiveChartSurface()
    {
        _ = _chartDisplayMode switch
        {
            ChartDisplayMode.Pie => Keyboard.Focus(PieChart),
            ChartDisplayMode.Bars => Keyboard.Focus(ChartBars),
            _ => Keyboard.Focus(Treemap)
        };
    }

    private void ClearChartSelection()
    {
        _chartSelection.Clear();
        ApplyChartSelectionToControls(scrollTo: null);
    }

    private void ApplyChartSelectionToControls(ChartSlice? scrollTo)
    {
        var selectedKeys = _chartSelection.SourceKeys.ToArray();
        var selectedSlices = _chartSelection.SelectedSlices(ChartSlices);
        var selectedSliceSet = selectedSlices.ToHashSet();
        var primarySlice = scrollTo is not null && _chartSelection.IsSelected(scrollTo)
            ? scrollTo
            : _chartSelection.PrimarySlice(ChartSlices);

        var wasSuppressing = _suppressChartSelection;
        _suppressChartSelection = true;
        try
        {
            PieChart.SelectedSlice = primarySlice;
            PieChart.SelectedSourceKeys = selectedKeys;
            Treemap.SelectedSlice = primarySlice;
            Treemap.SelectedSourceKeys = selectedKeys;
            ChartBars.SelectedItems.Clear();
            foreach (var slice in ChartSlices.Where(selectedSliceSet.Contains))
            {
                ChartBars.SelectedItems.Add(slice);
            }

            if (primarySlice is not null)
            {
                ChartBars.ScrollIntoView(primarySlice);
            }
        }
        finally
        {
            _suppressChartSelection = wasSuppressing;
        }

        ChartSelectionText.Text = ChartSelectionState.SelectionText(selectedSlices);
        PieChart.InvalidateVisual();
        Treemap.InvalidateVisual();
    }

    private async Task SelectDetailRowsForChartSelectionAsync()
    {
        var selectionPlan = ChartDetailSelectionService.BuildPlan(_chartSelection.SelectedSlices(ChartSlices), _chartScope);
        if (!selectionPlan.HasActionableSelection)
        {
            DetailsGrid.SelectedItems.Clear();
            UpdateDetailSelectionActionState();
            return;
        }

        if (_viewMode != selectionPlan.DesiredView)
        {
            SetViewMode(selectionPlan.DesiredView);
            await RefreshDetailsAsync(refreshContext: false);
        }

        var matchingRowCount = DetailRows.Count(selectionPlan.Matches);
        var selectedRowCount = SelectDetailRows(selectionPlan.Matches, focusGrid: false);
        if (selectedRowCount < matchingRowCount && !string.IsNullOrEmpty(_filterText))
        {
            ClearFilter();
            SelectDetailRows(selectionPlan.Matches, focusGrid: false);
        }
    }

    private void SelectDetailRow(Func<DetailRow, bool> predicate)
        => SelectDetailRows(predicate);

    private int SelectDetailRows(Func<DetailRow, bool> predicate, bool focusGrid = true)
    {
        DetailsGrid.SelectedItems.Clear();
        DetailRow? firstRow = null;
        var selectedCount = 0;
        foreach (var row in DetailRowsView.Cast<object>().OfType<DetailRow>().Where(predicate))
        {
            DetailsGrid.SelectedItems.Add(row);
            selectedCount++;
            firstRow ??= row;
        }

        if (firstRow is null)
        {
            UpdateDetailSelectionActionState();
            return selectedCount;
        }

        DetailsGrid.ScrollIntoView(firstRow);
        if (focusGrid)
        {
            DetailsGrid.Focus();
        }

        return selectedCount;
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
        ClearChartSelection();
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

        if (ScanViewProjector.TryFindParent(root, entry, out parent))
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
        ClearChartSelection();
        _selectedEntry = null;
        SelectedTitleText.Text = "Scanning...";
        SelectedSubText.Text = "Preparing scan...";
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

    private void ShowStartScanPrompt(string path, string? detail = null)
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
        ClearChartSelection();
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
        EmptyStateDetailText.Text = detail ?? $"No session scan is loaded for {path}. Start a scan to inspect this target.";
        SelectedTitleText.Text = "No scan loaded";
        SelectedSubText.Text = path;
        ChartTitleText.Text = "Disk distribution";
        ChartTotalText.Text = "Start a scan to render chart data.";
        ChartSelectionText.Text = "Select a slice to locate it in the grid.";
        UpdateFooterStatus("Ready", detail ?? $"Start a scan for {path}");
        EngineBadge.Text = "Ready";
        EngineBadgeDot.Fill = (WpfBrush?)TryFindResource("MutedBrush") ?? WpfBrushes.Gray;
        RefreshNavigationState(null, null);
        UpdateEmptyStateVisibility();
    }

    private async Task RefreshTargetsAsync(TargetRefreshReason reason, WpfButton? button = null)
    {
        if (_isClosing)
        {
            return;
        }

        if (button is not null)
        {
            button.IsEnabled = false;
        }

        try
        {
            await _targetRefreshCoordinator.RequestRefreshAsync(reason);
        }
        finally
        {
            if (button is not null)
            {
                button.IsEnabled = true;
            }
        }
    }

    private async Task RefreshTargetsCoreAsync(TargetRefreshReason reason)
    {
        try
        {
            Logger.LogDebug("Refreshing target list because {Reason}.", reason);
            var previousPathText = PathBox.Text?.Trim() ?? string.Empty;
            var previousSelectedTarget = DriveList.SelectedItem as TargetRow;
            var driveRowsTask = Task.Run(BuildDriveRows);
            var cloudTargetsTask = _cloudProviders.GetTargetsAsync();
            var portableTargetsTask = _portableDevices.GetTargetsAsync();
            await Task.WhenAll(driveRowsTask, cloudTargetsTask, portableTargetsTask);
            var rows = await driveRowsTask;
            var cloudTargets = await cloudTargetsTask;
            var portableTargets = await portableTargetsTask;
            _cloudTargets = cloudTargets;
            DriveRows.Clear();
            TargetRows.Clear();
            TargetRows.Add(TargetRow.RecycleBin());
            foreach (var row in rows)
            {
                DriveRows.Add(row);
                if (!row.IsCloudDrive)
                {
                    TargetRows.Add(TargetRow.FromDrive(
                        row,
                        scanCached: IsScanCached(row.RootPath),
                        scanActive: IsScanActive(row.RootPath)));
                    AddRecentPath(row.RootPath);
                }
            }

            if (ShowCloudTargetsBox?.IsChecked != false)
            {
                CloudTargetRowService.AddVisibleCloudTargetRows(
                    TargetRows,
                    DriveRows,
                    _cloudTargets,
                    IsScanCached,
                    IsScanActive,
                    AddRecentPath);
            }

            foreach (var target in portableTargets)
            {
                TargetRows.Add(TargetRow.FromPortable(
                    target,
                    scanCached: IsScanCached(target.TargetId),
                    scanActive: IsScanActive(target.TargetId)));
            }

            RefreshEmptyStateTargets();
            await RefreshRecycleBinTargetUsageAsync();
            if (_isClosing)
            {
                return;
            }

            if (!RepairLocalVolumeProjectionSelection(rows, previousPathText, previousSelectedTarget))
            {
                await RestoreTargetSelectionAfterRefreshAsync(previousSelectedTarget, previousPathText);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to refresh drive and target list.");
        }
    }

    private void ShowCloudTargets_Changed(object sender, RoutedEventArgs e)
    {
        if (TargetRows.Count == 0 && _cloudTargets.Count == 0)
        {
            return;
        }

        if (ShowCloudTargetsBox?.IsChecked != false)
        {
            CloudTargetRowService.AddVisibleCloudTargetRows(
                TargetRows,
                DriveRows,
                _cloudTargets,
                IsScanCached,
                IsScanActive,
                AddRecentPath);
        }
        else
        {
            _suppressTargetSelection = true;
            try
            {
                var driveList = DriveList;
                if (driveList?.SelectedItem is TargetRow selected && CloudTargetRowService.IsCloudFilteredTarget(selected))
                {
                    driveList.SelectedItem = null;
                }

                CloudTargetRowService.RemoveCloudTargetRows(TargetRows);
            }
            finally
            {
                _suppressTargetSelection = false;
            }
        }

        RefreshEmptyStateTargets();
    }

    private bool RepairLocalVolumeProjectionSelection(
        IReadOnlyList<DriveRow> driveRows,
        string previousPathText,
        TargetRow? previousSelectedTarget)
    {
        if (previousSelectedTarget?.Kind != TargetKind.PortableDevice)
        {
            return false;
        }

        var repairDrive = FindLocalVolumeProjectionRepairDrive(driveRows, previousPathText, previousSelectedTarget);
        if (repairDrive is null)
        {
            return false;
        }

        var target = TargetRows.FirstOrDefault(row =>
            row.Kind == TargetKind.Drive &&
            string.Equals(row.RootPath, repairDrive.RootPath, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        _suppressTargetSelection = true;
        try
        {
            DriveList.SelectedItem = target;
        }
        finally
        {
            _suppressTargetSelection = false;
        }

        PathBox.Text = repairDrive.RootPath;
        SetPortableScanControls(isPortableTarget: false);
        if (_activeScan is null && _currentScan is null && !_isRecycleBinViewActive)
        {
            ShowStartScanPrompt(repairDrive.RootPath);
        }

        return true;
    }

    private async Task<bool> RestoreTargetSelectionAfterRefreshAsync(TargetRow? previousTarget, string previousPathText)
    {
        if (previousTarget is null)
        {
            return false;
        }

        var replacement = TargetMatchingService.FindEquivalentTargetRow(TargetRows, previousTarget);
        if (replacement is null)
        {
            return false;
        }

        var currentPathText = PathBox.Text?.Trim() ?? string.Empty;
        var canRestorePathFromSnapshot = string.Equals(currentPathText, previousPathText, StringComparison.OrdinalIgnoreCase) &&
            TextMatchesTarget(previousPathText, previousTarget, allowEmpty: true);
        if (!canRestorePathFromSnapshot)
        {
            return false;
        }

        _suppressTargetSelection = true;
        try
        {
            DriveList.SelectedItem = replacement;
            DriveList.ScrollIntoView(replacement);
        }
        finally
        {
            _suppressTargetSelection = false;
        }

        var restoredPathText = replacement.Kind == TargetKind.PortableDevice
            ? IsPortableDisplaySubpath(previousPathText, previousTarget.DisplayPath)
                ? previousPathText
                : replacement.DisplayPath
            : replacement.RootPath;
        if (replacement.Kind != TargetKind.RecycleBin)
        {
            PathBox.Text = restoredPathText;
            SetPortableScanControls(
                replacement.Kind == TargetKind.PortableDevice,
                replacement.CloudProvider is not null);
        }

        if (previousTarget.PortableTarget is { } previousPortable &&
            replacement.PortableTarget is { } currentPortable &&
            previousPortable.IsAvailable != currentPortable.IsAvailable)
        {
            await SelectTargetAsync(replacement, BeginNavigation());
            if (replacement.Kind == TargetKind.PortableDevice &&
                IsPortableDisplaySubpath(previousPathText, previousTarget.DisplayPath))
            {
                PathBox.Text = restoredPathText;
            }
        }

        return true;
    }
    internal static DriveRow? FindLocalVolumeProjectionRepairDrive(
        IEnumerable<DriveRow> driveRows,
        string previousPathText,
        TargetRow? previousSelectedTarget)
    {
        var rows = driveRows as IReadOnlyList<DriveRow> ?? driveRows.ToList();
        var repairDrive = FindDriveByVolumeLabel(rows, previousPathText);
        if (repairDrive is null && previousSelectedTarget?.Kind == TargetKind.PortableDevice)
        {
            repairDrive = FindDriveByVolumeLabel(rows, previousSelectedTarget.DisplayPath) ??
                FindDriveByVolumeLabel(rows, previousSelectedTarget.Label);
        }

        return repairDrive;
    }

    internal static DriveRow? FindDriveByVolumeLabel(IEnumerable<DriveRow> driveRows, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        var exactLabelMatch = driveRows.FirstOrDefault(row =>
            string.Equals(row.Label, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exactLabelMatch is not null)
        {
            return exactLabelMatch;
        }

        if (Path.IsPathFullyQualified(trimmed))
        {
            return null;
        }

        return driveRows.FirstOrDefault(row =>
            row.Label.EndsWith(" " + trimmed, StringComparison.OrdinalIgnoreCase));
    }

    internal static TargetRow? FindFilesystemTargetForScanText(
        IEnumerable<TargetRow> targetRows,
        IEnumerable<DriveRow> driveRows,
        string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var targets = targetRows as IReadOnlyList<TargetRow> ?? targetRows.ToList();
        var driveTarget = targets.FirstOrDefault(row =>
            row.Kind is TargetKind.Drive or TargetKind.CloudProvider &&
            (string.Equals(row.RootPath, text, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(row.DisplayPath, text, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(row.Label, text, StringComparison.OrdinalIgnoreCase)));
        if (driveTarget is not null)
        {
            return driveTarget;
        }

        var drive = FindDriveByVolumeLabel(driveRows, text);
        if (drive is null)
        {
            return null;
        }

        return targets.FirstOrDefault(row =>
            row.Kind == TargetKind.Drive &&
            string.Equals(row.RootPath, drive.RootPath, StringComparison.OrdinalIgnoreCase)) ??
            TargetRow.FromDrive(drive);
    }

    private async void RefreshTargets_Click(object sender, RoutedEventArgs e)
    {
        await RefreshTargetsAsync(TargetRefreshReason.Manual, sender as WpfButton);
    }

    private void ScheduleDeviceChangeTargetRefresh()
    {
        if (_isClosing || !IsLoaded)
        {
            return;
        }

        _targetRefreshDebounceTimer.Stop();
        _targetRefreshDebounceTimer.Start();
    }

    private void TargetRefreshDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _targetRefreshDebounceTimer.Stop();
        RunUiAction(
            () => RefreshTargetsAsync(TargetRefreshReason.DeviceChange),
            "Target refresh failed");
    }

    private void RefreshEmptyStateTargets()
    {
        EmptyStateTargets.ReplaceAll(TargetRows.Where(row =>
            row.Kind is TargetKind.Drive or TargetKind.CloudProvider or TargetKind.PortableDevice));
    }

    private static bool TextMatchesTarget(string text, TargetRow row, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return allowEmpty;
        }

        return string.Equals(text, row.DisplayPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, row.RootPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, row.Label, StringComparison.OrdinalIgnoreCase) ||
            IsPortableDisplaySubpath(text, row.DisplayPath);
    }

    private bool TextMatchesCurrentPortableScan(string text)
        => _currentScan is { SourceKind: ScanSourceKind.PortableDevice } currentScan &&
            TextMatchesScanDisplayRoot(text, currentScan);

    private static bool TextMatchesScanDisplayRoot(string text, ScanResult scan)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var displayRoot = DisplayRootPath(scan);
        return string.Equals(text, displayRoot, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, scan.RootPath, StringComparison.OrdinalIgnoreCase) ||
            IsPortableDisplaySubpath(text, displayRoot);
    }

    internal static bool PortableTargetMatchesScan(PortableDeviceTarget target, ScanResult scan)
        => PortableTargetMatchesScanExactly(target, scan) ||
            PortableTargetMatchesScanByName(target, scan);

    internal static TargetRow? FindPortableTargetRowForScan(IEnumerable<TargetRow> targetRows, ScanResult scan)
    {
        var candidates = targetRows
            .Where(row => row.Kind == TargetKind.PortableDevice && row.PortableTarget is not null)
            .ToList();
        return candidates.FirstOrDefault(row => PortableTargetMatchesScanExactly(row.PortableTarget!, scan)) ??
            candidates.FirstOrDefault(row => PortableTargetMatchesScanByName(row.PortableTarget!, scan));
    }

    private static bool PortableTargetMatchesScanExactly(PortableDeviceTarget target, ScanResult scan)
    {
        if (!string.Equals(target.DeviceId, scan.PortableDeviceId, StringComparison.Ordinal))
        {
            return false;
        }

        return (!string.IsNullOrWhiteSpace(scan.PortableStorageObjectId) &&
                string.Equals(target.StorageObjectId, scan.PortableStorageObjectId, StringComparison.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(scan.SourceId) &&
                string.Equals(target.TargetId, scan.SourceId, StringComparison.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(scan.RootPath) &&
                string.Equals(target.TargetId, scan.RootPath, StringComparison.Ordinal));
    }

    private static bool PortableTargetMatchesScanByName(PortableDeviceTarget target, ScanResult scan)
    {
        if (!string.Equals(target.DeviceId, scan.PortableDeviceId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(scan.PortableStorageName) &&
            string.Equals(target.StorageName, scan.PortableStorageName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var displayRoot = DisplayRootPath(scan);
        return !string.IsNullOrWhiteSpace(displayRoot) &&
            string.Equals(target.DisplayPath, displayRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPortableDisplaySubpath(string text, string displayRoot)
    {
        if (string.IsNullOrWhiteSpace(displayRoot))
        {
            return false;
        }

        var normalizedText = NormalizePortableDisplayPath(text);
        var normalizedRoot = NormalizePortableDisplayPath(displayRoot);
        return normalizedText.Length > normalizedRoot.Length &&
            normalizedText.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            normalizedText[normalizedRoot.Length] == '/';
    }

    private static string NormalizePortableDisplayPath(string path)
    {
        return string.Join(
            '/',
            path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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
                var volumeLabel = drive.IsReady ? drive.VolumeLabel : string.Empty;
                var label = !string.IsNullOrWhiteSpace(volumeLabel)
                    ? $"{drive.Name} {volumeLabel}"
                    : drive.Name;

                rows.Add(new DriveRow(
                    label, drive.Name,
                    total <= 0 ? "Not ready" : $"{SizeFormatter.Format(used)} used",
                    total <= 0 ? string.Empty : $"{SizeFormatter.Format(free)} free",
                    percent,
                    CloudTargetRowService.IsCloudDriveVolumeLabel(volumeLabel)));
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

    private void UpdateDetailSelectionActionState()
    {
        if (!IsInitialized || DetailsGrid is null)
        {
            return;
        }

        var rows = SelectedDetailRows().ToList();
        var allRowsUseFileSystemPaths = DetailSelectionActionService.AllRowsUseFileSystemPathSyntax(rows);
        var state = DetailSelectionActionService.Build(
            rows,
            CurrentScanIsPortable(),
            allRowsUseFileSystemPaths,
            _activeScan is not null || _activePortableCopy is not null || _activeFileOperation);

        SelectionActionsBar.Visibility = Visibility.Visible;
        SelectionStatusText.Text = state.SelectionText;
        OpenSelectionButton.IsEnabled = state.CanOpen;
        RevealSelectionButton.IsEnabled = state.CanReveal;
        CopyPathSelectionButton.IsEnabled = state.CanCopyPath;
        CopySelectionButton.IsEnabled = state.CanCopy;
        CopySelectionButton.Content = state.CopyText;
        CopySelectionButton.ToolTip = state.CopyToolTip;
        MoveSelectionButton.IsEnabled = state.CanMove;
        MoveSelectionButton.ToolTip = state.MoveToolTip;
        ExportSelectionButton.IsEnabled = state.CanExport;
        DeleteSelectionButton.IsEnabled = state.CanDelete;
        DeleteSelectionButton.ToolTip = state.DeleteToolTip;
        PermanentDeleteSelectionButton.IsEnabled = state.CanPermanentDelete;
        PermanentDeleteSelectionButton.ToolTip = state.PermanentDeleteToolTip;

        OpenSelectedMenuItem.IsEnabled = state.CanOpen;
        RevealSelectedMenuItem.IsEnabled = state.CanReveal;
        CopyToFolderMenuItem.IsEnabled = state.CanCopy && !CurrentScanIsPortable();
        MoveToFolderMenuItem.IsEnabled = state.CanMove;
        CopyPortableToPcMenuItem.IsEnabled = state.CanCopy && CurrentScanIsPortable();
        CopyPathMenuItem.IsEnabled = state.CanCopyPath;
        CopyRowsMenuItem.IsEnabled = state.CanCopyRows;
        ExportSelectionMenuItem.IsEnabled = state.CanExport;
        DeleteSelectedMenuItem.IsEnabled = state.CanDelete;
        PermanentDeleteSelectedMenuItem.IsEnabled = state.CanPermanentDelete;
    }

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
        UpdateDetailSelectionActionState();
    }

    private CloudProviderMetadata? TryMatchCloudProviderPath(string text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !CloudProviderDiscoveryService.TryNormalizeRoot(text, out var normalizedPath))
        {
            return null;
        }

        var cloudDrive = DriveRows
            .Where(row => row.IsCloudDrive && CloudProviderDiscoveryService.IsNormalizedPathWithinRoot(normalizedPath, row.RootPath))
            .OrderByDescending(row => row.RootPath.Length)
            .FirstOrDefault();
        if (cloudDrive is not null)
        {
            return CloudTargetRowService.CloudProviderMetadataForDrive(cloudDrive);
        }

        var cloudTarget = _cloudTargets
            .Where(target => CloudProviderDiscoveryService.IsNormalizedPathWithinRoot(normalizedPath, target.RootPath))
            .OrderByDescending(target => target.RootPath.Length)
            .FirstOrDefault();
        if (cloudTarget is not null)
        {
            return cloudTarget.ToMetadata();
        }

        return _cloudProviders.TryMatchPath(text);
    }

    private bool TextMatchesCloudProviderPath(string text)
        => TryMatchCloudProviderPath(text) is not null;

    private void SetPortableScanControls(bool isPortableTarget, bool isCloudProviderTarget = false)
    {
        ModeBox.IsEnabled = !isPortableTarget && !isCloudProviderTarget;
        AllocatedSizeBox.IsEnabled = !isPortableTarget && !isCloudProviderTarget;
        if (isPortableTarget)
        {
            ModeBox.SelectedIndex = 0;
            AllocatedSizeBox.IsChecked = false;
            ModeBox.ToolTip = "MTP phone scans use portable-device metadata enumeration.";
            AllocatedSizeBox.ToolTip = "Allocated size is not available over MTP.";
        }
        else if (isCloudProviderTarget)
        {
            ModeBox.SelectedIndex = 1;
            AllocatedSizeBox.IsChecked = false;
            ModeBox.ToolTip = "Cloud provider scans use recursive mode to avoid placeholder hydration.";
            AllocatedSizeBox.ToolTip = "Allocated size is disabled for cloud provider scans.";
        }
        else
        {
            ModeBox.ToolTip = "Scan engine";
            AllocatedSizeBox.ToolTip = "Include on-disk allocated size";
        }
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

    private static string DisplayRootPath(ScanResult result)
        => string.IsNullOrWhiteSpace(result.DisplayRootPath) ? result.RootPath : result.DisplayRootPath;

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

    private static string BuildSelectedStatusText(DetailViewMode mode, FileSystemEntry selected)
    {
        return $"{ViewModeLabel(mode)} | {SizeFormatter.Format(selected.LogicalSizeBytes)} | {selected.FileCount:n0} files, {selected.DirectoryCount:n0} folders";
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
                CsvFieldFormatter.Format(row.Kind),
                CsvFieldFormatter.Format(row.Name),
                CsvFieldFormatter.Format(row.LogicalSize),
                CsvFieldFormatter.Format(row.AllocatedSize),
                CsvFieldFormatter.Format(row.PercentText),
                row.FileCount,
                row.DirectoryCount,
                CsvFieldFormatter.Format(row.FullPath)));
        }
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
        public ScanUiPreparation Preparation { get; set; } = preparation;
        public Dictionary<DetailViewMode, IReadOnlyList<DetailRow>> GlobalDetailRowsCache { get; } = [];
    }

    private sealed record CachedScanState(string SelectedPath, DetailViewMode ViewMode, ChartScope ChartScope);

    private sealed record PendingScan(
        string RootKey,
        string DisplayPath,
        string ScanPath,
        PortableDeviceTarget? PortableTarget,
        CloudProviderMetadata? CloudProvider);

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
    string Label,
    string RootPath,
    string UsedText,
    string FreeText,
    double UsedPercent,
    bool IsCloudDrive = false,
    CloudProviderMetadata? CloudProvider = null);

public sealed record TargetRow(
    TargetKind Kind,
    string Label,
    string RootPath,
    string DisplayPath,
    bool IsAvailable,
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

    internal PortableDeviceTarget? PortableTarget { get; init; }
    internal CloudProviderTarget? CloudProviderTarget { get; init; }
    internal CloudProviderMetadata? CloudProvider { get; init; }
    internal bool IsCloudDrive { get; init; }

    public static TargetRow RecycleBin()
        => new(
            TargetKind.RecycleBin,
            "Recycle Bin",
            string.Empty,
            string.Empty,
            true,
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
            string.Empty,
            true,
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
            string.Empty,
            true,
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
            row.RootPath,
            true,
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
            scanActive || scanCached ? Visibility.Visible : Visibility.Collapsed)
        {
            IsCloudDrive = row.IsCloudDrive,
            CloudProvider = CloudTargetRowService.CloudProviderMetadataForDrive(row)
        };

    internal static TargetRow FromCloudProvider(CloudProviderTarget target, bool scanCached = false, bool scanActive = false)
        => new(
            TargetKind.CloudProvider,
            string.IsNullOrWhiteSpace(target.AccountLabel)
                ? target.ProviderName
                : $"{target.ProviderName} - {target.AccountLabel}",
            target.RootPath,
            target.RootPath,
            true,
            "Ready",
            target.DetailText,
            0,
            "\uE753",
            "#2563EB",
            Visibility.Collapsed,
            Visibility.Visible,
            scanActive ? "\uE895" : scanCached ? "\uE930" : string.Empty,
            scanActive ? "Scanning" : scanCached ? "Scanned" : string.Empty,
            scanActive ? "#3B82F6" : scanCached ? "#10B981" : TransparentBrush,
            scanActive || scanCached ? Visibility.Visible : Visibility.Collapsed)
        {
            CloudProviderTarget = target,
            CloudProvider = target.ToMetadata()
        };

    internal static TargetRow FromPortable(PortableDeviceTarget target, bool scanCached = false, bool scanActive = false)
    {
        var hasCapacity = target.CapacityBytes is > 0;
        var free = Math.Max(0, target.FreeBytes ?? 0);
        var used = hasCapacity ? Math.Max(0, target.CapacityBytes!.Value - free) : 0;
        var percent = hasCapacity ? (double)used / target.CapacityBytes!.Value * 100 : 0;
        var usedText = target.IsAvailable
            ? hasCapacity ? $"{SizeFormatter.Format(used)} used" : "Ready"
            : "Unavailable";
        var detailText = target.IsAvailable
            ? hasCapacity ? $"{SizeFormatter.Format(free)} free" : target.DetailText
            : target.DetailText;

        var label = target.IsAvailable
            ? string.Join(" ", new[] { target.DeviceName, target.StorageName }.Where(part => !string.IsNullOrWhiteSpace(part)))
            : target.DeviceName;

        return new TargetRow(
            TargetKind.PortableDevice,
            label,
            target.TargetId,
            target.DisplayPath,
            target.IsAvailable,
            usedText,
            detailText,
            percent,
            "\uE8EA",
            target.IsAvailable ? "#0D9488" : "#94A3B8",
            hasCapacity ? Visibility.Visible : Visibility.Collapsed,
            hasCapacity ? Visibility.Collapsed : Visibility.Visible,
            scanActive ? "\uE895" : scanCached ? "\uE930" : string.Empty,
            scanActive ? "Scanning" : scanCached ? "Scanned" : string.Empty,
            scanActive ? "#3B82F6" : scanCached ? "#10B981" : TransparentBrush,
            scanActive || scanCached ? Visibility.Visible : Visibility.Collapsed)
        {
            PortableTarget = target
        };
    }

    private static string ItemCountText(long count)
        => count == 1 ? "1 item" : $"{count:n0} items";
}

public enum TargetKind
{
    Drive,
    CloudProvider,
    PortableDevice,
    RecycleBin
}

internal enum DetailViewMode { Contents, LargestFiles, LargestFolders, Extensions }
internal enum ChartScope { SelectedFolder, LargestFolders, LargestFiles, Extensions }
internal enum ChartDisplayMode { Treemap, Pie, Bars }
internal enum UpdateCheckMode { Manual, StartupAutoDownload }
internal enum DetailSortColumn { Name, Kind, LogicalSize, Percent, AllocatedSize, FileCount, DirectoryCount, FullPath }
