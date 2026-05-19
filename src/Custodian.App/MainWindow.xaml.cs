using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Collections.Specialized;
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
using Custodian.App.Controls;
using Custodian.App.Services;
using Custodian.Core.Export;
using Custodian.Core.Formatting;
using Custodian.Core.Model;
using Custodian.Core.Presentation;
using Custodian.Core.Scanning;
using Custodian.Core.Storage;
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
    private readonly DiskScanner _scanner = new();
    private readonly ScanStore _store = new();
    private CancellationTokenSource? _scanCts;
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
    private readonly UiSettings _settings;
    private DispatcherTimer? _scanProgressTimer;
    private DateTime _scanStarted;
    private long _scanFilesSeen;
    private long _scanBytesSeen;
    private string _scanCurrentPath = string.Empty;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly DispatcherTimer _folderJumpDebounceTimer;
    private IReadOnlyList<FolderJumpRow> _folderJumpIndex = [];

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
        _settingsSaveTimer.Tick += (_, _) => { _settingsSaveTimer.Stop(); PersistSettings(); };
        _folderJumpDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(175)
        };
        _folderJumpDebounceTimer.Tick += (_, _) =>
        {
            _folderJumpDebounceTimer.Stop();
            RefreshFolderJumpRows(JumpBox.Text);
        };

        LoadDriveRows();
        SeedPathFromSettings();
        InstallKeyBindings();
        ApplySettingsLate();

        UpdateChartModeVisibility();
        UpdateEmptyStateVisibility();
        UpdateFilterUiState();

        SizeChanged += (_, _) => ScheduleSettingsSave();
        LocationChanged += (_, _) => ScheduleSettingsSave();
        StateChanged += (_, _) => ScheduleSettingsSave();
        Closing += (_, _) => PersistSettings();

        ThemeManager.ThemeChanged += (_, _) =>
        {
            Treemap?.InvalidateVisual();
            PieChart?.InvalidateVisual();
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ============================================================
    //  Settings
    // ============================================================
    private void ApplySettingsEarly()
    {
        // Theme has to apply before InitializeComponent finishes laying out — but
        // since this runs in the ctor before any visuals matter, calling Apply here is fine.
        ThemeManager.Apply(_settings.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase)
            ? AppTheme.Dark
            : AppTheme.Light);

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
        if (_settings.RightPanelWidth > 0)
        {
            RightCol.Width = new GridLength(_settings.RightPanelWidth);
        }
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

    private void PersistSettings()
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
        _settings.RightPanelWidth = RightCol.Width.IsAbsolute && RightCol.Width.Value > 0
            ? RightCol.Width.Value
            : _settings.RightPanelWidth;
        _settings.RightPanelCollapsed = RightPanel.Visibility != Visibility.Visible;
        _settings.ChartMode = _chartDisplayMode.ToString();
        _settings.LastPath = PathBox.Text ?? string.Empty;
        _settings.RecentPaths = RecentPaths.Take(15).ToList();
        UiSettingsStore.Save(_settings);
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
    {
        if (_backStack.Count == 0 || _selectedEntry is null) return;
        _forwardStack.Push(_selectedEntry);
        NavigateToFolder(_backStack.Pop(), addHistory: false, clearForward: false);
    }

    private void GoForward()
    {
        if (_forwardStack.Count == 0 || _selectedEntry is null) return;
        _backStack.Push(_selectedEntry);
        NavigateToFolder(_forwardStack.Pop(), addHistory: false, clearForward: false);
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
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse(tag, out DetailViewMode mode)) return;
        _viewMode = mode;
        RefreshDetails();
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

    private void Chart_SliceSelected(object sender, ChartSliceEventArgs e) => SelectChartSlice(e.Slice, drillIntoFolders: false);
    private void Chart_SliceDoubleClicked(object sender, ChartSliceEventArgs e) => SelectChartSlice(e.Slice, drillIntoFolders: true);

    private void ChartBars_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChartSelection || ChartBars.SelectedItem is not ChartSlice slice) return;
        SelectChartSlice(slice, drillIntoFolders: false);
    }

    private void ChartBars_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChartBars.SelectedItem is ChartSlice slice) SelectChartSlice(slice, drillIntoFolders: true);
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
        var row = FindVisualParent<DataGridRow>((DependencyObject)e.OriginalSource);
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
            var visible = DetailRowsView.Cast<object>().Count();
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

    private void ShowShortcuts_Click(object sender, RoutedEventArgs e) => ShowShortcuts();
    private void ShortcutClose_Click(object sender, RoutedEventArgs e) => ShortcutOverlay.Visibility = Visibility.Collapsed;
    private void ShortcutOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Only dismiss on backdrop click, not on click inside the dialog.
        if (ReferenceEquals(e.Source, ShortcutOverlay)) ShortcutOverlay.Visibility = Visibility.Collapsed;
    }
    private void ShowShortcuts() => ShortcutOverlay.Visibility = Visibility.Visible;

    private double _savedRightWidth = 350;
    private void CollapseRight_Click(object sender, RoutedEventArgs e) => CollapseRight();
    private void ExpandRight_Click(object sender, RoutedEventArgs e) => ExpandRight();
    private void CollapseRight()
    {
        if (RightCol.Width.IsAbsolute && RightCol.Width.Value > 0) _savedRightWidth = RightCol.Width.Value;
        RightPanel.Visibility = Visibility.Collapsed;
        RightPanelTab.Visibility = Visibility.Visible;
        RightCol.Width = new GridLength(40);
        ScheduleSettingsSave();
    }
    private void ExpandRight()
    {
        RightPanel.Visibility = Visibility.Visible;
        RightPanelTab.Visibility = Visibility.Collapsed;
        RightCol.Width = new GridLength(_savedRightWidth);
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
        RefreshDetails();

        analysisWatch.Stop();
        result.PhaseTimings.RemoveAll(t => t.Name == "UI analysis preparation");
        result.PhaseTimings.Add(new ScanPhaseTiming("UI analysis preparation", analysisWatch.Elapsed));

        EngineBadge.Text = result.Engine;
        EngineBadgeDot.Fill = (WpfBrush?)TryFindResource("SuccessBrush") ?? WpfBrushes.Green;
        UpdateFooterStatus("Ready", BuildFooterDetail(result));
        UpdateEmptyStateVisibility();
        AnimateGridFadeIn();
        PersistSettings();
    }

    private void RefreshDetails()
    {
        var result = _currentScan;
        if (result is null)
        {
            SelectedTitleText.Text = "No scan loaded";
            SelectedSubText.Text = "Choose a path and scan.";
            DetailRows.Clear();
            ChartSlices.Clear();
            SummaryMetrics.Clear();
            BreadcrumbItems.Clear();
            RefreshChart();
            RefreshNavigationState(null, null);
            return;
        }

        var selected = _selectedEntry ?? result.Root;
        var rows = _viewMode switch
        {
            DetailViewMode.LargestFiles => ScanViewProjector.LargestFileRows(result),
            DetailViewMode.LargestFolders => ScanViewProjector.LargestFolderRows(result),
            DetailViewMode.Extensions => ScanViewProjector.ExtensionRows(result),
            _ => ScanViewProjector.ChildRows(selected)
        };

        ReplaceCollection(DetailRows, rows);
        DetailRowsView.Refresh();
        UpdateFilterUiState();
        ReplaceCollection(SummaryMetrics, ScanViewProjector.SelectedSummaryMetrics(result, selected));
        RefreshNavigationState(selected, result.Root);
        RefreshChart();

        SelectedTitleText.Text = string.IsNullOrWhiteSpace(selected.Name) ? selected.FullPath : selected.Name;
        SelectedSubText.Text = $"{ViewModeLabel(_viewMode)} · {selected.FullPath} · {SizeFormatter.Format(selected.LogicalSizeBytes)} · {selected.FileCount:n0} files, {selected.DirectoryCount:n0} folders";
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

    private void SelectChartSlice(ChartSlice slice, bool drillIntoFolders)
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
            NavigateToFolder(folder);
            return;
        }

        SelectDetailRowForSlice(slice);
    }

    private void SelectDetailRowForSlice(ChartSlice slice)
    {
        if (slice.Kind == ChartSliceKind.Extension)
        {
            _viewMode = DetailViewMode.Extensions;
            ViewExtensions.IsChecked = true;
            RefreshDetails();
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
            RefreshDetails();
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
    {
        if (_selectedEntry is not null && string.Equals(_selectedEntry.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase)) return;
        if (addHistory && _selectedEntry is not null) _backStack.Push(_selectedEntry);
        if (clearForward) _forwardStack.Clear();

        _selectedEntry = entry;
        _viewMode = DetailViewMode.Contents;
        ViewContents.IsChecked = true;
        _selectedChartSourceKey = null;
        RefreshDetails();
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
        }
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
        _currentScan = null;
        FolderNodes.Clear();
        DetailRows.Clear();
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

    private void LoadDriveRows()
    {
        DriveRows.Clear();
        foreach (var drive in DriveInfo.GetDrives())
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

                DriveRows.Add(new DriveRow(
                    label, drive.Name,
                    total <= 0 ? "Not ready" : $"{SizeFormatter.Format(used)} used",
                    total <= 0 ? string.Empty : $"{SizeFormatter.Format(free)} free",
                    percent));
                AddRecentPath(drive.Name);
            }
            catch (IOException) { DriveRows.Add(new DriveRow(drive.Name, drive.Name, "Not ready", "", 0)); }
            catch (UnauthorizedAccessException) { DriveRows.Add(new DriveRow(drive.Name, drive.Name, "Access denied", "", 0)); }
        }
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
        if (elapsed <= 0) return;
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

    private static void RevealPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    private static bool IsExistingFileSystemPath(string path)
        => Path.IsPathFullyQualified(path) && (File.Exists(path) || Directory.Exists(path));

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
}

public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;

    public void ReplaceAll(IEnumerable<T> items)
    {
        _suppressNotifications = true;
        try
        {
            ClearItems();
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }
        finally
        {
            _suppressNotifications = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
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
