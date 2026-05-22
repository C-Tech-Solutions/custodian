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
    private static readonly TimeSpan AutomaticUpdateCheckInterval = TimeSpan.FromHours(12);

    private readonly DiskScanner _scanner = new();
    private readonly ScanStore _store = new();
    private readonly AppUpdateService _updates = new();
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _updateCts;
    private ScanResult? _currentScan;
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
    private readonly UiSettings _settings;
    private DispatcherTimer? _scanProgressTimer;
    private DateTime _scanStarted;
    private long _scanFilesSeen;
    private long _scanBytesSeen;
    private string _scanCurrentPath = string.Empty;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly DispatcherTimer _folderJumpDebounceTimer;
    private IReadOnlyList<FolderJumpRow> _folderJumpIndex = [];
    private readonly Dictionary<DetailViewMode, IReadOnlyList<DetailRow>> _globalDetailRowsCache = [];
    private int _detailRefreshVersion;
    private IReadOnlyList<DetailRow>? _boundDetailRows;
    private bool _settingsPersistedForClose;
    private bool _isClosing;

    public ObservableCollection<DriveRow> DriveRows { get; } = [];
    public ObservableCollection<string> RecentPaths { get; } = [];
    public BulkObservableCollection<FolderNode> FolderNodes { get; } = [];
    public BulkObservableCollection<DetailRow> DetailRows { get; } = [];
    public BulkObservableCollection<ChartSlice> ChartSlices { get; } = [];
    public BulkObservableCollection<SummaryMetric> SummaryMetrics { get; } = [];
    public BulkObservableCollection<BreadcrumbItem> BreadcrumbItems { get; } = [];
    public BulkObservableCollection<FolderJumpRow> FolderJumpRows { get; } = [];

    public ICollectionView DetailRowsView { get; }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _settings = UiSettingsStore.Load();
        ApplySettingsEarly();

        DetailRowsView = CollectionViewSource.GetDefaultView(DetailRows);
        DetailRowsView.Filter = RowMatchesFilter;

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
        var driveRowsTask = LoadDriveRowsAsync();
        if (ShouldRunAutomaticUpdateCheck())
        {
            await CheckForUpdatesAsync(isAutomatic: true);
        }
        await driveRowsTask;
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
        _scanCts?.Cancel();
        _updateCts?.Cancel();
        _settingsSaveTimer.Stop();
        await PersistSettingsAsync();
        _settingsPersistedForClose = true;
        Close();
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
        var path = !string.IsNullOrWhiteSpace(_settings.LastPath)
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

    private void StopScan() => _scanCts?.Cancel();

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(isAutomatic: false);
    }

    private async Task CheckForUpdatesAsync(bool isAutomatic)
    {
        if (_updateCts is not null)
        {
            return;
        }

        _updateCts = new CancellationTokenSource();
        CheckUpdatesMenuItem.IsEnabled = false;
        ApplyUpdateStatus(AppUpdateStatusFactory.Checking());

        try
        {
            var result = await _updates.CheckForUpdatesAsync();
            MarkUpdateCheckCompleted();
            ApplyUpdateStatus(result.Status);

            switch (result.Status.Kind)
            {
                case AppUpdateStatusKind.NotInstalled:
                    if (!isAutomatic)
                    {
                        UpdateDialog.ShowInformation(this, "Updates unavailable", result.Status.Message, UpdateDialogTone.Warning);
                    }
                    break;
                case AppUpdateStatusKind.UpToDate:
                    if (!isAutomatic)
                    {
                        UpdateDialog.ShowInformation(this, "Custodian is up to date", result.Status.Message, UpdateDialogTone.Success);
                    }
                    break;
                case AppUpdateStatusKind.Available:
                    await PromptDownloadAndInstallAsync(result, _updateCts.Token);
                    break;
                case AppUpdateStatusKind.ReadyToRestart:
                    await PromptRestartForInstallAsync(result);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            UpdateFooterStatus("Updates", "Update check cancelled.");
        }
        catch (Exception ex)
        {
            var status = AppUpdateStatusFactory.Failed(ex.Message);
            ApplyUpdateStatus(status);
            if (!isAutomatic)
            {
                UpdateDialog.ShowInformation(this, "Update failed", status.Message, UpdateDialogTone.Error);
            }
        }
        finally
        {
            _updateCts.Dispose();
            _updateCts = null;
            CheckUpdatesMenuItem.IsEnabled = true;
        }
    }

    private void MarkUpdateCheckCompleted()
    {
        _settings.LastAutomaticUpdateCheckUtc = DateTime.UtcNow;
        ScheduleSettingsSave();
    }

    private bool ShouldRunAutomaticUpdateCheck()
    {
        var now = DateTime.UtcNow;
        var lastCheck = _settings.LastAutomaticUpdateCheckUtc;
        return lastCheck == DateTime.MinValue
            || lastCheck > now
            || now - lastCheck >= AutomaticUpdateCheckInterval;
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

        var progress = new Progress<AppUpdateStatus>(ApplyUpdateStatus);
        await _updates.DownloadUpdatesAsync(result, progress, cancellationToken);

        var readyStatus = AppUpdateStatusFactory.ReadyToRestart(availableVersion);
        ApplyUpdateStatus(readyStatus);

        var shouldRestart = UpdateDialog.ShowConfirmation(
            this,
            "Install update",
            $"{readyStatus.Message}\n\nRestart Custodian now to install it?",
            "Restart now",
            "Later");

        if (shouldRestart)
        {
            await ApplyUpdateAndShutdownAsync(result);
            return;
        }

        UpdateFooterStatus("Updates", "Update downloaded. Use Help > Check for Updates when you are ready to restart and install.");
    }

    private async Task PromptRestartForInstallAsync(AppUpdateCheckResult result)
    {
        var shouldRestart = UpdateDialog.ShowConfirmation(
            this,
            "Install update",
            $"{result.Status.Message}\n\nRestart Custodian now to install it?",
            "Restart now",
            "Later");

        if (shouldRestart)
        {
            await ApplyUpdateAndShutdownAsync(result);
        }
    }

    private void ApplyUpdateStatus(AppUpdateStatus status)
    {
        UpdateFooterStatus("Updates", status.Message);
    }

    private async Task ApplyUpdateAndShutdownAsync(AppUpdateCheckResult result)
    {
        _isClosing = true;
        _scanCts?.Cancel();
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
        if (_scanCts is not null) return;

        var path = PathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            UpdateFooterStatus("Choose a path to scan.", string.Empty);
            return;
        }

        _scanCts = new CancellationTokenSource();
        SetScanningState(true);
        ClearViewsForNewScan(path);
        AddRecentPath(path);
        StartLoadingAnimation();

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                _scanFilesSeen = p.FilesSeen;
                _scanBytesSeen = p.BytesSeen;
                _scanCurrentPath = string.IsNullOrWhiteSpace(p.CurrentPath) ? path : p.CurrentPath;
                LoadingPathText.Text = _scanCurrentPath;
                UpdateFooterStatus(p.Message, $"{p.FilesSeen:n0} files · {p.DirectoriesSeen:n0} folders · {SizeFormatter.Format(p.BytesSeen)}");
            });

            var mode = ModeBox.SelectedIndex switch
            {
                1 => ScanMode.Recursive,
                2 => ScanMode.Mft,
                _ => ScanMode.Auto
            };

            _currentScan = await _scanner.ScanAsync(
                new ScanOptions(path, mode, CollectAllocatedSize: AllocatedSizeBox.IsChecked == true),
                progress,
                _scanCts.Token);

            await LoadScanIntoUiAsync(_currentScan);
            ShowToast($"Scan complete: {SizeFormatter.Format(_currentScan.Root.LogicalSizeBytes)} in {_currentScan.Duration:m\\:ss}");
        }
        catch (OperationCanceledException)
        {
            UpdateFooterStatus("Cancelled", string.Empty);
            EngineBadge.Text = "Cancelled";
            EngineBadgeDot.Fill = (WpfBrush?)TryFindResource("WarningBrush") ?? WpfBrushes.Orange;
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateFooterStatus("Scan failed", ex.Message);
            EngineBadge.Text = "Failed";
            EngineBadgeDot.Fill = (WpfBrush?)TryFindResource("DangerBrush") ?? WpfBrushes.Red;
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            SetScanningState(false);
            StopLoadingAnimation();
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
                _currentScan = await _store.LoadAsync(dialog.FileName);
                PathBox.Text = _currentScan.RootPath;
                AddRecentPath(_currentScan.RootPath);
                await LoadScanIntoUiAsync(_currentScan);
                ShowToast($"Opened {Path.GetFileName(dialog.FileName)}");
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
        if (DriveList.SelectedItem is DriveRow row)
        {
            PathBox.Text = row.RootPath;
            AddRecentPath(row.RootPath);
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
            _viewMode = mode;
            await RefreshDetailsAsync(refreshContext: false);
        }, "Failed to change view");
    }

    // ============================================================
    //  Chart
    // ============================================================
    private void ChartScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChartScopeBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !Enum.TryParse(tag, out ChartScope scope)) return;
        _chartScope = scope;
        _selectedChartSourceKey = null;
        RefreshChart();
    }

    private void ChartMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse(tag, out ChartDisplayMode mode)) return;
        _chartDisplayMode = mode;
        UpdateChartModeVisibility();
        ScheduleSettingsSave();
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

    // ============================================================
    //  Theme / shortcuts / right-panel collapse
    // ============================================================
    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        ScheduleSettingsSave();
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
            WpfClipboard.SetText(string.Join(Environment.NewLine, rows.Select(r => r.FullPath)));
            ShowToast($"Copied {rows.Count:n0} path(s).");
            return;
        }
        var path = SelectedPath();
        if (path is not null) { WpfClipboard.SetText(path); ShowToast("Copied path."); }
    }

    private void CopyRows_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedDetailRows().ToList();
        if (rows.Count == 0) return;
        var builder = new StringBuilder();
        foreach (var row in rows) builder.AppendLine(row.ToString());
        WpfClipboard.SetText(builder.ToString());
        ShowToast($"Copied {rows.Count:n0} row(s).");
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
        var answer = WpfMessageBox.Show(this, $"Move to Recycle Bin?\n\n{path}", "Confirm delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            if (Directory.Exists(path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            ShowToast($"Moved to Recycle Bin: {Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WpfMessageBox.Show(this, ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ============================================================
    //  UI population
    // ============================================================
    private async Task LoadScanIntoUiAsync(ScanResult result)
    {
        ClearGlobalDetailRowsCache();

        var analysisWatch = Stopwatch.StartNew();
        var prepared = await Task.Run(() => new ScanUiPreparation(
            FolderNode.From(result.Root, Math.Max(1, result.Root.LogicalSizeBytes)),
            ScanViewProjector.FolderJumpIndex(result.Root)));

        FolderNodes.Clear();
        FolderNodes.Add(prepared.RootNode);
        _folderJumpIndex = prepared.FolderJumpIndex;
        _backStack.Clear();
        _forwardStack.Clear();

        _selectedEntry = result.Root;
        _viewMode = DetailViewMode.Contents;
        ViewContents.IsChecked = true;
        _selectedChartSourceKey = null;
        RefreshFolderJumpRows(string.Empty);
        await RefreshDetailsAsync();

        analysisWatch.Stop();
        result.PhaseTimings.RemoveAll(t => t.Name == "UI analysis preparation");
        result.PhaseTimings.Add(new ScanPhaseTiming("UI analysis preparation", analysisWatch.Elapsed));

        EngineBadge.Text = result.Engine;
        EngineBadgeDot.Fill = (WpfBrush?)TryFindResource("SuccessBrush") ?? WpfBrushes.Green;
        UpdateFooterStatus("Ready", BuildFooterDetail(result));
        UpdateEmptyStateVisibility();
        AnimateGridFadeIn();
        ScheduleSettingsSave();
    }

    private Task RefreshDetailsAsync(bool refreshContext = true)
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
            RefreshChart();
            RefreshNavigationState(null, null);
            return Task.CompletedTask;
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
                rows = GetOrCreateGlobalDetailRows(result, viewMode);
            }
            catch (Exception ex)
            {
                if (IsCurrentDetailRequest(requestVersion, result, selected, viewMode))
                {
                    DetailsGrid.IsEnabled = true;
                    ShowOperationError("View failed", ex);
                }
                return Task.CompletedTask;
            }
        }

        if (!IsCurrentDetailRequest(requestVersion, result, selected, viewMode))
        {
            return Task.CompletedTask;
        }

        DetailsGrid.IsEnabled = true;
        if (!ReferenceEquals(rows, _boundDetailRows))
        {
            ReplaceCollection(DetailRows, rows);
            _boundDetailRows = rows;
            // Granular collection notifications refresh the view. An explicit
            // Refresh is only needed to re-run the filter predicate.
            if (!string.IsNullOrEmpty(_filterText))
            {
                DetailRowsView.Refresh();
            }
        }
        UpdateFilterUiState();
        if (refreshContext)
        {
            ReplaceCollection(SummaryMetrics, ScanViewProjector.SelectedSummaryMetrics(result, selected));
            RefreshNavigationState(selected, result.Root);
            RefreshChart();
            UpdatePathDisplay(selected.FullPath);
        }
        SelectedTitleText.Text = string.IsNullOrWhiteSpace(selected.Name) ? selected.FullPath : selected.Name;
        SelectedSubText.Text = BuildSelectedSubText(viewMode, selected);
        return Task.CompletedTask;
    }

    private IReadOnlyList<DetailRow> GetOrCreateGlobalDetailRows(ScanResult result, DetailViewMode mode)
    {
        if (!_globalDetailRowsCache.TryGetValue(mode, out var rows))
        {
            rows = ProjectGlobalDetailRows(result, mode);
            _globalDetailRowsCache[mode] = rows;
        }

        return rows;
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

    private bool IsCurrentDetailRequest(int requestVersion, ScanResult result, FileSystemEntry selected, DetailViewMode mode)
    {
        return requestVersion == _detailRefreshVersion
            && ReferenceEquals(_currentScan, result)
            && ReferenceEquals(_selectedEntry ?? result.Root, selected)
            && _viewMode == mode;
    }

    private void ClearGlobalDetailRowsCache()
    {
        _globalDetailRowsCache.Clear();
        _detailRefreshVersion++;
    }

    private void RefreshChart()
    {
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
        var dataset = _chartScope switch
        {
            ChartScope.LargestFolders => ScanViewProjector.LargestFoldersChart(result),
            ChartScope.LargestFiles => ScanViewProjector.LargestFilesChart(result),
            ChartScope.Extensions => ScanViewProjector.ExtensionsChart(result),
            _ => ScanViewProjector.SelectedFolderChart(selected)
        };

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
            _chartScope = ChartScope.SelectedFolder;
            ChartScopeBox.SelectedIndex = 0;
            await NavigateToFolderAsync(folder);
            return;
        }

        await SelectDetailRowForSliceAsync(slice);
    }

    private async Task SelectDetailRowForSliceAsync(ChartSlice slice)
    {
        if (slice.Kind == ChartSliceKind.Extension)
        {
            _viewMode = DetailViewMode.Extensions;
            ViewExtensions.IsChecked = true;
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
            _viewMode = desiredView;
            switch (desiredView)
            {
                case DetailViewMode.LargestFiles: ViewLargestFiles.IsChecked = true; break;
                case DetailViewMode.LargestFolders: ViewLargestFolders.IsChecked = true; break;
                default: ViewContents.IsChecked = true; break;
            }
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
        _viewMode = DetailViewMode.Contents;
        ViewContents.IsChecked = true;
        _selectedChartSourceKey = null;
        await RefreshDetailsAsync();
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

    private void ClearViewsForNewScan(string path)
    {
        ClearGlobalDetailRowsCache();
        _currentScan = null;
        FolderNodes.Clear();
        DetailRows.Clear();
        _boundDetailRows = null;
        ChartSlices.Clear();
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

    private async Task LoadDriveRowsAsync()
    {
        try
        {
            var rows = await Task.Run(BuildDriveRows);
            DriveRows.Clear();
            foreach (var row in rows)
            {
                DriveRows.Add(row);
                AddRecentPath(row.RootPath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static IReadOnlyList<DriveRow> BuildDriveRows()
    {
        var rows = new List<DriveRow>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException ex)
        {
            Debug.WriteLine(ex);
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
            catch (IOException) { rows.Add(new DriveRow(drive.Name, drive.Name, "Not ready", "", 0)); }
            catch (UnauthorizedAccessException) { rows.Add(new DriveRow(drive.Name, drive.Name, "Access denied", "", 0)); }
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
        StopButton.IsEnabled = scanning;
        RefreshButton.IsEnabled = !scanning && _currentScan is not null;
        ProgressBar.IsIndeterminate = scanning;
    }

    private void UpdateChartModeVisibility()
    {
        TreemapHost.Visibility = _chartDisplayMode == ChartDisplayMode.Treemap ? Visibility.Visible : Visibility.Collapsed;
        PieHost.Visibility = _chartDisplayMode == ChartDisplayMode.Pie ? Visibility.Visible : Visibility.Collapsed;
        BarHost.Visibility = _chartDisplayMode == ChartDisplayMode.Bars ? Visibility.Visible : Visibility.Collapsed;
        Treemap.InvalidateVisual();
        PieChart.InvalidateVisual();
    }

    private void UpdateEmptyStateVisibility()
    {
        var hasScan = _currentScan is not null;
        WorkspaceHost.Visibility = hasScan ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateHost.Visibility = hasScan ? Visibility.Collapsed : Visibility.Visible;
    }

    // ============================================================
    //  Loading skeleton + footer
    // ============================================================
    private void StartLoadingAnimation()
    {
        WorkspaceHost.Visibility = Visibility.Collapsed;
        EmptyStateHost.Visibility = Visibility.Collapsed;
        LoadingHost.Visibility = Visibility.Visible;
        SkeletonRows.ItemsSource = Enumerable.Range(0, 12).Select(i => 0.45 - i * 0.025).ToArray();
        _scanStarted = DateTime.UtcNow;
        _scanFilesSeen = 0;
        _scanBytesSeen = 0;
        _scanProgressTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(750) };
        _scanProgressTimer.Tick += (_, _) => UpdateScanRate();
        _scanProgressTimer.Start();
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
            Debug.WriteLine($"DwmSetWindowAttribute {attributeName} failed with HRESULT 0x{result:X8}.");
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

internal enum DetailViewMode { Contents, LargestFiles, LargestFolders, Extensions }
internal enum ChartScope { SelectedFolder, LargestFolders, LargestFiles, Extensions }
internal enum ChartDisplayMode { Treemap, Pie, Bars }
