using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Custodian.Core.Export;
using Custodian.Core.Formatting;
using Custodian.Core.Model;
using Custodian.Core.Presentation;
using Custodian.Core.Scanning;
using Custodian.Core.Storage;
using WinForms = System.Windows.Forms;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataGrid = System.Windows.Controls.DataGrid;
using WpfMessageBox = System.Windows.MessageBox;

namespace Custodian.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DiskScanner _scanner = new();
    private readonly ScanStore _store = new();
    private CancellationTokenSource? _scanCts;
    private ScanResult? _currentScan;
    private FileSystemEntry? _selectedEntry;
    private DetailViewMode _viewMode = DetailViewMode.Contents;

    public ObservableCollection<DriveRow> DriveRows { get; } = [];
    public ObservableCollection<string> RecentPaths { get; } = [];
    public ObservableCollection<FolderNode> FolderNodes { get; } = [];
    public ObservableCollection<DetailRow> DetailRows { get; } = [];
    public ObservableCollection<ChartRow> ChartRows { get; } = [];
    public ObservableCollection<SummaryMetric> SummaryMetrics { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        PathBox.ItemsSource = RecentPaths;
        LoadDriveRows();
        PathBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        await StartScanAsync();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
    }

    private async Task StartScanAsync()
    {
        if (_scanCts is not null)
        {
            return;
        }

        var path = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            FooterText.Text = "Choose a path to scan.";
            return;
        }

        _scanCts = new CancellationTokenSource();
        SetScanningState(true);
        ClearViews();
        AddRecentPath(path);

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                var currentPath = string.IsNullOrWhiteSpace(p.CurrentPath) ? path : p.CurrentPath;
                FooterText.Text = $"{p.Message}: {currentPath} | {p.FilesSeen:n0} files, {p.DirectoriesSeen:n0} folders | {SizeFormatter.Format(p.BytesSeen)}";
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

            LoadScanIntoUi(_currentScan);
        }
        catch (OperationCanceledException)
        {
            FooterText.Text = "Scan cancelled.";
            EngineBadge.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
            FooterText.Text = "Scan failed.";
            EngineBadge.Text = "Failed";
        }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            SetScanningState(false);
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
        if (_currentScan is null)
        {
            FooterText.Text = "Run or open a scan before saving.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Custodian scan (*.custodian-scan)|*.custodian-scan",
            FileName = "scan.custodian-scan"
        };

        if (dialog.ShowDialog(this) == true)
        {
            await _store.SaveAsync(_currentScan, dialog.FileName);
            FooterText.Text = $"Saved {dialog.FileName}";
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
            _currentScan = await _store.LoadAsync(dialog.FileName);
            PathBox.Text = _currentScan.RootPath;
            AddRecentPath(_currentScan.RootPath);
            LoadScanIntoUi(_currentScan);
        }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        await ExportAsync("CSV files (*.csv)|*.csv", "scan.csv", (result, path) => ScanExporter.ExportCsvAsync(result, path));
    }

    private async void ExportJson_Click(object sender, RoutedEventArgs e)
    {
        await ExportAsync("JSON files (*.json)|*.json", "scan.json", (result, path) => ScanExporter.ExportJsonAsync(result, path));
    }

    private async Task ExportAsync(string filter, string fileName, Func<ScanResult, string, Task> exporter)
    {
        if (_currentScan is null)
        {
            FooterText.Text = "Run or open a scan before exporting.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = filter,
            FileName = fileName
        };

        if (dialog.ShowDialog(this) == true)
        {
            await exporter(_currentScan, dialog.FileName);
            FooterText.Text = $"Exported {dialog.FileName}";
        }
    }

    private void DriveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveList.SelectedItem is DriveRow row)
        {
            PathBox.Text = row.RootPath;
            AddRecentPath(row.RootPath);
        }
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode node)
        {
            _selectedEntry = node.Entry;
            _viewMode = DetailViewMode.Contents;
            RefreshDetails();
        }
    }

    private void ViewMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse(tag, out DetailViewMode mode))
        {
            return;
        }

        _viewMode = mode;
        RefreshDetails();
    }

    private void DetailsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DetailsGrid.SelectedItem is not DetailRow row)
        {
            return;
        }

        if (row.Entry.IsDirectory)
        {
            _selectedEntry = row.Entry;
            _viewMode = DetailViewMode.Contents;
            RefreshDetails();
            return;
        }

        if (IsExistingFileSystemPath(row.FullPath))
        {
            RevealPath(row.FullPath);
        }
    }

    private void DetailsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindVisualParent<DataGridRow>((DependencyObject)e.OriginalSource);
        if (row is null)
        {
            return;
        }

        row.IsSelected = true;
        row.Focus();
    }

    private void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedPath();
        if (path is null)
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

    private void RevealSelected_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedPath();
        if (path is not null && IsExistingFileSystemPath(path))
        {
            RevealPath(path);
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedDetailRows().ToList();
        if (rows.Count > 0)
        {
            WpfClipboard.SetText(string.Join(Environment.NewLine, rows.Select(row => row.FullPath)));
            FooterText.Text = $"Copied {rows.Count:n0} path(s).";
            return;
        }

        var path = SelectedPath();
        if (path is not null)
        {
            WpfClipboard.SetText(path);
            FooterText.Text = "Copied path.";
        }
    }

    private void CopyRows_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedDetailRows().ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.AppendLine(row.ToString());
        }

        WpfClipboard.SetText(builder.ToString());
        FooterText.Text = $"Copied {rows.Count:n0} row(s).";
    }

    private async void ExportSelectionCsv_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedDetailRows().ToList();
        if (rows.Count == 0)
        {
            FooterText.Text = "Select one or more rows before exporting.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = "selection.csv"
        };

        if (dialog.ShowDialog(this) == true)
        {
            await WriteDetailRowsCsvAsync(rows, dialog.FileName);
            FooterText.Text = $"Exported {rows.Count:n0} row(s) to {dialog.FileName}";
        }
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedPath();
        if (path is null || !File.Exists(path) && !Directory.Exists(path))
        {
            FooterText.Text = "Select an existing file or folder before deleting.";
            return;
        }

        var answer = WpfMessageBox.Show(
            this,
            $"Move to Recycle Bin?\n\n{path}",
            "Confirm delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }

            FooterText.Text = $"Moved to Recycle Bin: {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WpfMessageBox.Show(this, ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadScanIntoUi(ScanResult result)
    {
        var analysisWatch = Stopwatch.StartNew();

        FolderNodes.Clear();
        FolderNodes.Add(FolderNode.From(result.Root, Math.Max(1, result.Root.LogicalSizeBytes)));

        _selectedEntry = result.Root;
        _viewMode = DetailViewMode.Contents;
        ReplaceCollection(SummaryMetrics, ScanViewProjector.SummaryMetrics(result));
        RefreshDetails();

        analysisWatch.Stop();
        result.PhaseTimings.RemoveAll(t => t.Name == "UI analysis preparation");
        result.PhaseTimings.Add(new ScanPhaseTiming("UI analysis preparation", analysisWatch.Elapsed));

        EngineBadge.Text = result.Engine;
        FooterText.Text = BuildFooterText(result);
    }

    private void RefreshDetails()
    {
        var result = _currentScan;
        if (result is null)
        {
            SelectedTitleText.Text = "No scan loaded";
            SelectedSubText.Text = "Choose a path and scan.";
            DetailRows.Clear();
            ChartRows.Clear();
            SummaryMetrics.Clear();
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
        ReplaceCollection(ChartRows, ScanViewProjector.TopChildChartRows(selected));

        SelectedTitleText.Text = string.IsNullOrWhiteSpace(selected.Name) ? selected.FullPath : selected.Name;
        SelectedSubText.Text = $"{ViewModeLabel(_viewMode)} | {selected.FullPath} | {SizeFormatter.Format(selected.LogicalSizeBytes)} logical | {selected.FileCount:n0} files, {selected.DirectoryCount:n0} folders";
    }

    private void ClearViews()
    {
        FolderNodes.Clear();
        DetailRows.Clear();
        ChartRows.Clear();
        SummaryMetrics.Clear();
        _selectedEntry = null;
        SelectedTitleText.Text = "Scanning...";
        SelectedSubText.Text = PathBox.Text;
        FooterText.Text = "Preparing scan...";
        EngineBadge.Text = "Scanning";
    }

    private void LoadDriveRows()
    {
        DriveRows.Clear();
        RecentPaths.Clear();

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
                    label,
                    drive.Name,
                    total <= 0 ? "Not ready" : $"{SizeFormatter.Format(used)} used",
                    total <= 0 ? string.Empty : $"{SizeFormatter.Format(free)} free",
                    percent));
                AddRecentPath(drive.Name);
            }
            catch (IOException)
            {
                DriveRows.Add(new DriveRow(drive.Name, drive.Name, "Not ready", string.Empty, 0));
                AddRecentPath(drive.Name);
            }
            catch (UnauthorizedAccessException)
            {
                DriveRows.Add(new DriveRow(drive.Name, drive.Name, "Access denied", string.Empty, 0));
                AddRecentPath(drive.Name);
            }
        }
    }

    private void AddRecentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || RecentPaths.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        RecentPaths.Add(path);
    }

    private IEnumerable<DetailRow> SelectedDetailRows()
    {
        return DetailsGrid.SelectedItems.OfType<DetailRow>();
    }

    private string? SelectedPath()
    {
        if (DetailsGrid.SelectedItem is DetailRow row)
        {
            return row.FullPath;
        }

        return _selectedEntry?.FullPath;
    }

    private void SetScanningState(bool scanning)
    {
        StartButton.IsEnabled = !scanning;
        StopButton.IsEnabled = scanning;
        ProgressBar.IsIndeterminate = scanning;
    }

    private static string BuildFooterText(ScanResult result)
    {
        var parts = new List<string>
        {
            $"Scan complete in {result.Duration:g}",
            $"{SizeFormatter.Format(result.Root.LogicalSizeBytes)} logical",
            $"{result.Root.FileCount:n0} files",
            $"{result.Root.DirectoryCount:n0} folders"
        };

        if (result.SkippedEntries.Count > 0)
        {
            parts.Add($"{result.SkippedEntries.Count:n0} skipped");
        }

        if (result.PhaseTimings.Count > 0)
        {
            parts.Add("Timings: " + string.Join(", ", result.PhaseTimings.Select(t => $"{t.Name} {t.Duration.TotalSeconds:0.###}s")));
        }

        if (result.Diagnostics.Count > 0)
        {
            parts.Add(string.Join(", ", result.Diagnostics));
        }

        return string.Join(" | ", parts);
    }

    private static string ViewModeLabel(DetailViewMode mode)
    {
        return mode switch
        {
            DetailViewMode.LargestFiles => "Largest files",
            DetailViewMode.LargestFolders => "Largest folders",
            DetailViewMode.Extensions => "Extensions",
            _ => "Contents"
        };
    }

    private static void RevealPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    private static bool IsExistingFileSystemPath(string path)
    {
        return Path.IsPathFullyQualified(path) && (File.Exists(path) || Directory.Exists(path));
    }

    private static async Task WriteDetailRowsCsvAsync(IEnumerable<DetailRow> rows, string path)
    {
        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("Kind,Name,LogicalSize,AllocatedSize,Percent,Files,Folders,Path");

        foreach (var row in rows)
        {
            await writer.WriteLineAsync(string.Join(
                ',',
                Csv(row.Kind),
                Csv(row.Name),
                Csv(row.LogicalSize),
                Csv(row.AllocatedSize),
                Csv(row.PercentText),
                row.FileCount,
                row.DirectoryCount,
                Csv(row.FullPath)));
        }
    }

    private static string Csv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return value;
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record DriveRow(
    string Label,
    string RootPath,
    string UsedText,
    string FreeText,
    double UsedPercent);

internal enum DetailViewMode
{
    Contents,
    LargestFiles,
    LargestFolders,
    Extensions
}
