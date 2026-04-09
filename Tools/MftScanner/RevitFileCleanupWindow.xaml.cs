using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using Newtonsoft.Json;
using IOPath = System.IO.Path;

namespace MftScanner;

public sealed class RevitCleanupFileItem
{
    private readonly Func<RevitCleanupFileItem, Task> deleteHandler;

    internal RevitCleanupFileItem(ScannedFileInfo info, Func<RevitCleanupFileItem, Task> deleteHandler)
    {
        this.deleteHandler = deleteHandler;
        FileName = info.FileName;
        SourceDisplayName = info.RootDisplayName ?? string.Empty;
        SizeBytes = info.SizeBytes;
        SizeText = FormatSize(info.SizeBytes);
        ModifiedText = info.ModifiedTimeUtc == DateTime.MinValue
                           ? string.Empty
                           : info.ModifiedTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        DirectoryPath = IOPath.GetDirectoryName(info.FullPath) ?? string.Empty;
        FullPath = info.FullPath;
        DeleteCommand = new RelayCommand(() => _ = this.deleteHandler?.Invoke(this));
    }

    [DataGridColumn(1, DisplayName = "文件名", Width = "220", IsReadOnly = true)]
    public string FileName { get; set; }

    [DataGridColumn(2, DisplayName = "来源", Width = "100", IsReadOnly = true)]
    public string SourceDisplayName { get; set; }

    [DataGridColumn(3, DisplayName = "大小", Width = "120", IsReadOnly = true)]
    public string SizeText { get; set; }

    [DataGridColumn(4, DisplayName = "修改时间", Width = "170", IsReadOnly = true)]
    public string ModifiedText { get; set; }

    [DataGridColumn(5, DisplayName = "目录", Width = "430", IsReadOnly = true)]
    public string DirectoryPath { get; set; }

    [DataGridMultiButton(nameof(ActionButtons), 6, DisplayName = "操作", Width = "110", ButtonSpacing = 10)]
    public string Actions { get; set; }

    public ICommand DeleteCommand { get; }

    public List<ButtonConfig> ActionButtons => new()
    {
        new ButtonConfig
        {
            Text = "删除",
            Width = 90,
            Height = 26,
            CommandProperty = nameof(DeleteCommand),
        },
    };

    public string FullPath { get; set; }

    public long SizeBytes { get; set; }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var size = (double)bytes;
        var i = 0;
        while ((size >= 1024d) && (i < (units.Length - 1)))
        {
            size /= 1024d;
            i++;
        }

        return i == 0 ? $"{size:F0} {units[i]}" : $"{size:F2} {units[i]}";
    }

    public sealed class DeleteResult
    {
        public List<string> DeletedPaths { get; set; } = new();

        public List<string> FailedPaths { get; set; } = new();
    }
}

public partial class RevitFileCleanupWindow : Window, INotifyPropertyChanged
{
    private static readonly string SettingsPath = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                                                 "PackageManager",
                                                                 "revit_cleanup_settings.json");

    private readonly MftScanService scanService = new();

    private bool scanDesktop = true;

    private bool scanDownloads = true;

    private bool scanDocuments = true;

    private bool isScanning;

    private bool hasScanned;

    private string selectedCustomDirectory;

    private string summaryText = "请选择扫描范围后开始扫描。";

    private string currentProgressMessage;

    private long matchedFileCount;

    private long matchedTotalBytes;

    private long deletedFileCount;

    private long failedDeleteCount;

    private TimeSpan scanElapsed;

    private CancellationTokenSource scanCts;

    public RevitFileCleanupWindow()
    {
        InitializeComponent();
        DataContext = this;
        LoadCustomDirectories();
        RefreshScopeText();
        RefreshSummaryText();
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public ObservableCollection<string> CustomDirectories { get; } = new();

    public ObservableCollection<RevitCleanupFileItem> Files { get; } = new();

    public bool ScanDesktop
    {
        get => scanDesktop;

        set
        {
            if (SetProperty(ref scanDesktop, value))
            {
                RefreshScopeText();
                OnPropertyChanged(nameof(CanScan));
            }
        }
    }

    public bool ScanDownloads
    {
        get => scanDownloads;

        set
        {
            if (SetProperty(ref scanDownloads, value))
            {
                RefreshScopeText();
                OnPropertyChanged(nameof(CanScan));
            }
        }
    }

    public bool ScanDocuments
    {
        get => scanDocuments;

        set
        {
            if (SetProperty(ref scanDocuments, value))
            {
                RefreshScopeText();
                OnPropertyChanged(nameof(CanScan));
            }
        }
    }

    public bool IsScanning
    {
        get => isScanning;

        private set
        {
            if (SetProperty(ref isScanning, value))
            {
                OnPropertyChanged(nameof(CanScan));
                OnPropertyChanged(nameof(CanDeleteAll));
                OnPropertyChanged(nameof(CanEditCustomDirectories));
                OnPropertyChanged(nameof(CanClearCustomDirectories));
                OnPropertyChanged(nameof(BusyVisibility));
            }
        }
    }

    public string SelectedCustomDirectory
    {
        get => selectedCustomDirectory;

        set
        {
            if (SetProperty(ref selectedCustomDirectory, value))
            {
                OnPropertyChanged(nameof(CanEditCustomDirectories));
            }
        }
    }

    public string SummaryText
    {
        get => summaryText;

        private set => SetProperty(ref summaryText, value);
    }

    public string ScanScopeText { get; private set; } = string.Empty;

    public Visibility BusyVisibility => IsScanning ? Visibility.Visible : Visibility.Collapsed;

    public bool CanScan => !IsScanning && (ScanDesktop || ScanDownloads || ScanDocuments || (CustomDirectories.Count > 0));

    public bool CanDeleteAll => !IsScanning && (Files.Count > 0);

    public bool CanEditCustomDirectories => !IsScanning && !string.IsNullOrWhiteSpace(SelectedCustomDirectory);

    public bool CanClearCustomDirectories => !IsScanning && (CustomDirectories.Count > 0);

    protected virtual void OnPropertyChanged([CallerMemberName] string name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        scanCts?.Cancel();
        scanCts?.Dispose();
        scanCts = null;
        scanService.SaveAllCaches();
        base.OnClosing(e);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    private static string GetKnownFolderPath(string folderId)
    {
        var guid = new Guid(folderId);
        if (SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out var ptr) != 0)
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var size = (double)bytes;
        var i = 0;
        while ((size >= 1024d) && (i < (units.Length - 1)))
        {
            size /= 1024d;
            i++;
        }

        return i == 0 ? $"{size:F0} {units[i]}" : $"{size:F2} {units[i]}";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes >= 1)
        {
            return $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒";
        }

        return elapsed.TotalSeconds >= 1
            ? $"{elapsed.TotalSeconds:F1} 秒"
            : $"{elapsed.TotalMilliseconds:F0} ms";
    }

    // 与主项目 RevitCleanupPathUtility.NormalizePath 逻辑一致，正确处理盘符根目录（E: → E:\）
    private static string NormalizeDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();

        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Za-z]:$"))
        {
            return trimmed + IOPath.DirectorySeparatorChar;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Za-z]:[\\/]$"))
        {
            return trimmed.Substring(0, 2) + IOPath.DirectorySeparatorChar;
        }

        try
        {
            var full = IOPath.GetFullPath(trimmed);
            var root = IOPath.GetPathRoot(full);
            if (!string.IsNullOrWhiteSpace(root) && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return full.TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        await StartScanAsync();
    }

    private async void ForceRescanButton_Click(object sender, RoutedEventArgs e)
    {
        scanService.InvalidateCache();
        await StartScanAsync();
    }

    private async void DeleteAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (Files.Count == 0)
        {
            return;
        }

        if (MessageBox.Show($"确定删除当前列表中的 {Files.Count} 个文件吗？",
                            "清理RVT",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await DeleteVisibleItemsAsync(Files.ToList());
        RefreshSummaryText();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AddDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要额外扫描的目录",
            CheckFileExists = false,
            CheckPathExists = true,
            FileName = "选择文件夹",
            Filter = "文件夹 (*.folder)|*.folder",
            ValidateNames = false,
        };
        var initial = CustomDirectories.LastOrDefault(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p));
        if (!string.IsNullOrWhiteSpace(initial))
        {
            dialog.InitialDirectory = initial;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var selectedPath = NormalizeDirectoryPath(IOPath.GetDirectoryName(dialog.FileName));
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        if (CustomDirectories.Any(p => string.Equals(p, selectedPath, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("该目录已在扫描列表中。", "清理RVT", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        CustomDirectories.Add(selectedPath);
        SelectedCustomDirectory = selectedPath;
        SaveCustomDirectories();
        RefreshScopeText();
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanClearCustomDirectories));
    }

    private void RemoveDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedCustomDirectory))
        {
            return;
        }

        CustomDirectories.Remove(SelectedCustomDirectory);
        SelectedCustomDirectory = CustomDirectories.FirstOrDefault();
        SaveCustomDirectories();
        RefreshScopeText();
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanClearCustomDirectories));
    }

    private void ClearDirectoriesButton_Click(object sender, RoutedEventArgs e)
    {
        if (CustomDirectories.Count == 0)
        {
            return;
        }

        if (MessageBox.Show("确定清空所有自定义扫描目录吗？",
                            "清理RVT",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        CustomDirectories.Clear();
        SelectedCustomDirectory = null;
        SaveCustomDirectories();
        RefreshScopeText();
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanClearCustomDirectories));
    }

    private async Task StartScanAsync()
    {
        var roots = BuildScanRoots();
        if (roots.Count == 0)
        {
            MessageBox.Show("请至少选择一个默认目录，或添加一个自定义扫描目录。",
                            "清理RVT",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
            return;
        }

        scanCts?.Cancel();
        scanCts?.Dispose();
        scanCts = new CancellationTokenSource();

        IsScanning = true;
        hasScanned = false;
        Files.Clear();
        matchedFileCount = 0;
        matchedTotalBytes = 0;
        deletedFileCount = 0;
        failedDeleteCount = 0;
        scanElapsed = TimeSpan.Zero;
        currentProgressMessage = "正在准备扫描...";
        RefreshSummaryText();

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var progress = new Progress<string>(msg =>
            {
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    currentProgressMessage = msg;
                    RefreshSummaryText();
                }
            });

            var results = await scanService.ScanAsync(roots, new[] { ".rvt", ".rfa" }, progress, scanCts.Token)
                                           .ConfigureAwait(true);

            stopwatch.Stop();
            scanElapsed = stopwatch.Elapsed;

            Files.Clear();
            foreach (var item in results)
            {
                Files.Add(new RevitCleanupFileItem(item, DeleteSingleFileAsync));
            }

            SynchronizeMatchedSummary();
            hasScanned = true;
            currentProgressMessage = string.Empty;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            MessageBox.Show($"扫描失败：{ex.Message}", "清理RVT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsScanning = false;
            RefreshSummaryText();
            OnPropertyChanged(nameof(CanDeleteAll));
        }
    }

    private async Task DeleteSingleFileAsync(RevitCleanupFileItem item)
    {
        if (item == null)
        {
            return;
        }

        if (MessageBox.Show($"确定删除文件？\n{item.FullPath}",
                            "清理RVT",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var result = await DeleteFilesAsync(new[] { item.FullPath });
        if (result.DeletedPaths.Any(p => string.Equals(p, item.FullPath, StringComparison.OrdinalIgnoreCase)))
        {
            RemoveFilesFromView(result.DeletedPaths);
        }
        else
        {
            MessageBox.Show($"删除失败：{item.FullPath}", "清理RVT", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshSummaryText();
        OnPropertyChanged(nameof(CanDeleteAll));
    }

    private async Task DeleteVisibleItemsAsync(IReadOnlyCollection<RevitCleanupFileItem> items)
    {
        if ((items == null) || (items.Count == 0))
        {
            return;
        }

        var result = await DeleteFilesAsync(items.Select(f => f.FullPath));
        RemoveFilesFromView(result.DeletedPaths);

        if (result.FailedPaths.Count > 0)
        {
            MessageBox.Show($"已删除 {result.DeletedPaths.Count} 个文件，另有 {result.FailedPaths.Count} 个删除失败。",
                            "清理RVT",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
        }

        OnPropertyChanged(nameof(CanDeleteAll));
    }

    private Task<RevitCleanupFileItem.DeleteResult> DeleteFilesAsync(IEnumerable<string> paths)
    {
        return Task.Run(() =>
        {
            var deletedPaths = new List<string>();
            var failedPaths = new List<string>();
            var targets = paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();

            Parallel.ForEach(targets,
                             path =>
                             {
                                 try
                                 {
                                     if (File.Exists(path))
                                     {
                                         File.Delete(path);
                                     }

                                     lock (deletedPaths)
                                     {
                                         deletedPaths.Add(path);
                                     }

                                     Interlocked.Increment(ref deletedFileCount);
                                 }
                                 catch
                                 {
                                     lock (failedPaths)
                                     {
                                         failedPaths.Add(path);
                                     }

                                     Interlocked.Increment(ref failedDeleteCount);
                                 }
                             });

            return new RevitCleanupFileItem.DeleteResult
            {
                DeletedPaths = deletedPaths,
                FailedPaths = failedPaths,
            };
        });
    }

    private void RemoveFilesFromView(IEnumerable<string> deletedPaths)
    {
        var pathSet = new HashSet<string>((deletedPaths ?? Array.Empty<string>())
                                          .Where(p => !string.IsNullOrWhiteSpace(p)),
                                          StringComparer.OrdinalIgnoreCase);

        if (pathSet.Count == 0)
        {
            return;
        }

        foreach (var item in Files.Where(f => pathSet.Contains(f.FullPath)).ToList())
        {
            Files.Remove(item);
        }

        SynchronizeMatchedSummary();
    }

    private void SynchronizeMatchedSummary()
    {
        matchedFileCount = Files.Count;
        matchedTotalBytes = Files.Sum(f => f.SizeBytes);
    }

    private List<ScanRoot> BuildScanRoots()
    {
        var roots = new List<ScanRoot>();
        if (ScanDesktop)
        {
            AddRoot(roots, "桌面", GetKnownFolderPath(KnownFolderDesktop));
        }

        if (ScanDownloads)
        {
            AddRoot(roots, "下载", GetKnownFolderPath(KnownFolderDownloads));
        }

        if (ScanDocuments)
        {
            AddRoot(roots, "文档", GetKnownFolderPath(KnownFolderDocuments));
        }

        foreach (var dir in CustomDirectories)
        {
            AddRoot(roots, "自定义", dir);
        }

        return roots.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
    }

    private void AddRoot(ICollection<ScanRoot> roots, string displayName, string path)
    {
        var normalized = NormalizeDirectoryPath(path);
        if (!string.IsNullOrWhiteSpace(normalized) && Directory.Exists(normalized))
        {
            roots.Add(new ScanRoot { Path = normalized, DisplayName = displayName });
        }
    }

    private void LoadCustomDirectories()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

            var json = File.ReadAllText(SettingsPath);
            var dirs = JsonConvert.DeserializeObject<List<string>>(json);
            if (dirs == null)
            {
                return;
            }

            foreach (var d in dirs.Select(NormalizeDirectoryPath)
                                  .Where(p => !string.IsNullOrWhiteSpace(p))
                                  .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                CustomDirectories.Add(d);
            }

            SelectedCustomDirectory = CustomDirectories.FirstOrDefault();
        }
        catch
        {
        }
    }

    private void SaveCustomDirectories()
    {
        try
        {
            Directory.CreateDirectory(IOPath.GetDirectoryName(SettingsPath));
            File.WriteAllText(SettingsPath,
                              JsonConvert.SerializeObject(CustomDirectories.Select(NormalizeDirectoryPath)
                                                                           .Where(p => !string.IsNullOrWhiteSpace(p))
                                                                           .Distinct(StringComparer.OrdinalIgnoreCase)
                                                                           .ToList()));
        }
        catch
        {
        }
    }

    private void RefreshScopeText()
    {
        var names = new List<string>();
        if (ScanDesktop)
        {
            names.Add("桌面");
        }

        if (ScanDownloads)
        {
            names.Add("下载");
        }

        if (ScanDocuments)
        {
            names.Add("文档");
        }

        if (CustomDirectories.Count > 0)
        {
            names.Add($"自定义 {CustomDirectories.Count} 个");
        }

        ScanScopeText = names.Count == 0 ? "当前未选择任何扫描目录。" : $"当前扫描范围：{string.Join("、", names)}";
        OnPropertyChanged(nameof(ScanScopeText));
    }

    private void RefreshSummaryText()
    {
        if (IsScanning)
        {
            SummaryText = string.IsNullOrWhiteSpace(currentProgressMessage) ? "正在扫描..." : currentProgressMessage;
            return;
        }

        if (!hasScanned)
        {
            SummaryText = "请选择扫描范围后开始扫描。";
            return;
        }

        SummaryText = $"来源：MFT索引，命中 {matchedFileCount} 个文件，总大小 {FormatSize(matchedTotalBytes)}，" +
                      $"已删除 {deletedFileCount} 个，删除失败 {failedDeleteCount} 个，当前列表剩余 {Files.Count} 个，" +
                      $"扫描耗时 {FormatElapsed(scanElapsed)}。";
    }

    private const string KnownFolderDesktop = "B4BFCC3A-DB2C-424C-B029-7FE99A87C641";

    private const string KnownFolderDownloads = "374DE290-123F-4565-9164-39C4925E467B";

    private const string KnownFolderDocuments = "FDD39AD0-238F-46AF-ADB4-6C85480369C7";
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action execute;

    public RelayCommand(Action execute) { this.execute = execute; }

    public event EventHandler CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;

        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object parameter)
    {
        return true;
    }

    public void Execute(object parameter)
    {
        execute();
    }
}