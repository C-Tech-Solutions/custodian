using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Custodian.Core.Analysis;
using Custodian.Core.Export;
using Custodian.Core.Formatting;
using Custodian.Core.Model;
using Custodian.Core.Scanning;
using Custodian.Core.Storage;
using WinForms = System.Windows.Forms;
using WpfClipboard = System.Windows.Clipboard;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
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

    public ObservableCollection<EntryRow> CurrentRows { get; } = [];
    public ObservableCollection<EntryRow> LargestFileRows { get; } = [];
    public ObservableCollection<EntryRow> LargestFolderRows { get; } = [];
    public ObservableCollection<ExtensionRow> ExtensionRows { get; } = [];
    public ObservableCollection<TreemapRow> TreemapRows { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
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

        _scanCts = new CancellationTokenSource();
        SetScanningState(true);
        ClearViews();

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                StatusText.Text = $"{p.Message}: {p.CurrentPath}";
                CountText.Text = $"{p.FilesSeen:n0} files, {p.DirectoriesSeen:n0} folders";
                TotalText.Text = SizeFormatter.Format(p.BytesSeen);
            });

            var mode = ModeBox.SelectedIndex switch
            {
                1 => ScanMode.Recursive,
                2 => ScanMode.Mft,
                _ => ScanMode.Auto
            };
            _currentScan = await _scanner.ScanAsync(
                new ScanOptions(PathBox.Text, mode, CollectAllocatedSize: AllocatedSizeBox.IsChecked == true),
                progress,
                _scanCts.Token);
            LoadScanIntoUi(_currentScan);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Scan cancelled";
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Scan failed";
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
            SelectedPath = Directory.Exists(PathBox.Text) ? PathBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            PathBox.Text = dialog.SelectedPath;
        }
    }

    private async void SaveScan_Click(object sender, RoutedEventArgs e)
    {
        if (_currentScan is null)
        {
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
            StatusText.Text = $"Saved {dialog.FileName}";
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
            StatusText.Text = $"Exported {dialog.FileName}";
        }
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FileSystemEntry entry)
        {
            _selectedEntry = entry;
            LoadCurrentRows(entry);
        }
    }

    private void CopyRows_Click(object sender, RoutedEventArgs e)
    {
        var grid = ActiveGrid();
        if (grid?.SelectedItems.Count > 0)
        {
            var builder = new StringBuilder();
            foreach (var item in grid.SelectedItems)
            {
                builder.AppendLine(item.ToString());
            }

            WpfClipboard.SetText(builder.ToString());
        }
    }

    private void OpenPath_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedPath();
        if (path is null)
        {
            return;
        }

        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedPath();
        if (path is null || !File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        var answer = WpfMessageBox.Show(this, $"Move to Recycle Bin?\n\n{path}", "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }

            StatusText.Text = $"Moved to Recycle Bin: {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WpfMessageBox.Show(this, ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadScanIntoUi(ScanResult result)
    {
        FolderTree.ItemsSource = new[] { result.Root };
        _selectedEntry = result.Root;
        LoadCurrentRows(result.Root);
        var analysisWatch = Stopwatch.StartNew();
        LoadAnalysisRows(result);
        analysisWatch.Stop();
        result.PhaseTimings.RemoveAll(t => t.Name == "UI analysis preparation");
        result.PhaseTimings.Add(new ScanPhaseTiming("UI analysis preparation", analysisWatch.Elapsed));

        StatusText.Text = $"Scan complete in {result.Duration:g}";
        EngineText.Text = $"Engine: {result.Engine}";
        TotalText.Text = $"Size: {SizeFormatter.Format(result.Root.LogicalSizeBytes)} logical / {SizeFormatter.Format(result.Root.AllocatedSizeBytes)} allocated";
        CountText.Text = $"{result.Root.FileCount:n0} files, {result.Root.DirectoryCount:n0} folders";
        SkippedText.Text = BuildFooterText(result);
    }

    private void LoadCurrentRows(FileSystemEntry entry)
    {
        CurrentRows.Clear();
        foreach (var child in entry.Children.OrderByDescending(c => c.IsDirectory).ThenByDescending(c => c.LogicalSizeBytes))
        {
            CurrentRows.Add(EntryRow.From(child));
        }
    }

    private void LoadAnalysisRows(ScanResult result)
    {
        LargestFileRows.Clear();
        foreach (var entry in ScanAnalysis.LargestFiles(result))
        {
            LargestFileRows.Add(EntryRow.From(entry));
        }

        LargestFolderRows.Clear();
        foreach (var entry in ScanAnalysis.LargestFolders(result))
        {
            LargestFolderRows.Add(EntryRow.From(entry));
        }

        ExtensionRows.Clear();
        foreach (var extension in ScanAnalysis.ExtensionSummary(result))
        {
            ExtensionRows.Add(ExtensionRow.From(extension, result.Root.LogicalSizeBytes));
        }

        TreemapRows.Clear();
        var palette = new[] { "#2563eb", "#16a34a", "#dc2626", "#7c3aed", "#ea580c", "#0891b2", "#4f46e5", "#be123c" };
        var top = result.Root.Children.OrderByDescending(c => c.LogicalSizeBytes).Take(48).ToList();
        var max = Math.Max(1, top.FirstOrDefault()?.LogicalSizeBytes ?? 1);
        for (var i = 0; i < top.Count; i++)
        {
            var item = top[i];
            var scale = Math.Max(0.3, Math.Sqrt((double)item.LogicalSizeBytes / max));
            TreemapRows.Add(new TreemapRow(
                item.Name,
                SizeFormatter.Format(item.LogicalSizeBytes),
                140 + (220 * scale),
                70 + (120 * scale),
                new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(palette[i % palette.Length]))));
        }
    }

    private void ClearViews()
    {
        FolderTree.ItemsSource = null;
        CurrentRows.Clear();
        LargestFileRows.Clear();
        LargestFolderRows.Clear();
        ExtensionRows.Clear();
        TreemapRows.Clear();
        SkippedText.Text = string.Empty;
    }

    private static string BuildFooterText(ScanResult result)
    {
        var parts = new List<string>();
        if (result.SkippedEntries.Count > 0)
        {
            parts.Add($"{result.SkippedEntries.Count:n0} skipped entries");
        }

        if (result.PhaseTimings.Count > 0)
        {
            parts.Add("Timings: " + string.Join(", ", result.PhaseTimings.Select(t => $"{t.Name} {t.Duration.TotalSeconds:0.###}s")));
        }

        if (result.Diagnostics.Count > 0)
        {
            parts.Add(string.Join(", ", result.Diagnostics));
        }

        return string.Join(". ", parts);
    }

    private WpfDataGrid? ActiveGrid()
    {
        return (Tabs.SelectedItem as TabItem)?.Content as WpfDataGrid;
    }

    private string? SelectedPath()
    {
        if (ActiveGrid()?.SelectedItem is EntryRow row)
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record EntryRow(
    string Name,
    string Type,
    string LogicalSize,
    string AllocatedSize,
    long FileCount,
    long DirectoryCount,
    string Extension,
    string FullPath)
{
    public static EntryRow From(FileSystemEntry entry)
    {
        return new EntryRow(
            entry.Name,
            entry.IsDirectory ? "Folder" : "File",
            SizeFormatter.Format(entry.LogicalSizeBytes),
            SizeFormatter.Format(entry.AllocatedSizeBytes),
            entry.FileCount,
            entry.DirectoryCount,
            entry.Extension,
            entry.FullPath);
    }

    public override string ToString()
    {
        return $"{Type}\t{LogicalSize}\t{FullPath}";
    }
}

public sealed record ExtensionRow(
    string Extension,
    long FileCount,
    string LogicalSize,
    string AllocatedSize,
    string Share)
{
    public static ExtensionRow From(ExtensionSummary summary, long totalBytes)
    {
        var share = totalBytes <= 0 ? 0 : (double)summary.LogicalSizeBytes / totalBytes;
        return new ExtensionRow(
            summary.Extension,
            summary.FileCount,
            SizeFormatter.Format(summary.LogicalSizeBytes),
            SizeFormatter.Format(summary.AllocatedSizeBytes),
            share.ToString("P1"));
    }
}

public sealed record TreemapRow(
    string Name,
    string LogicalSize,
    double BlockWidth,
    double BlockHeight,
    System.Windows.Media.Brush Brush);
