using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using PackageManager.Models;
using PackageManager.Services;
using PackageManager.Services.RevitCleanup;
using IOPath = System.IO.Path;

namespace PackageManager.Function.UnlockTool
{
    /// <summary>
    /// Revit 文件清理窗口。
    /// </summary>
    public partial class RevitFileCleanupWindow : Window, INotifyPropertyChanged
    {
        private const string KnownFolderDesktop = "B4BFCC3A-DB2C-424C-B029-7FE99A87C641";
        private const string KnownFolderDownloads = "374DE290-123F-4565-9164-39C4925E467B";
        private const string KnownFolderDocuments = "FDD39AD0-238F-46AF-ADB4-6C85480369C7";

        private readonly DataPersistenceService dataPersistenceService = new DataPersistenceService();
        private readonly RevitFileQueryService queryService = new RevitFileQueryService();

        private bool scanDesktop = true;
        private bool scanDownloads = true;
        private bool scanDocuments = true;
        private bool isScanning;
        private bool hasScanned;
        private string selectedCustomDirectory;
        private string summaryText = "请选择扫描范围后开始扫描。";
        private string currentProgressMessage = "请选择扫描范围后开始扫描。";
        private string currentProviderDisplayText = "本地索引";
        private long matchedFileCount;
        private long matchedTotalBytes;
        private long deletedFileCount;
        private long failedDeleteCount;
        private RevitFileQuerySourceKind currentSourceKind = RevitFileQuerySourceKind.LocalIndex;
        private CancellationTokenSource scanCancellationTokenSource;

        public RevitFileCleanupWindow()
        {
            InitializeComponent();
            DataContext = this;
            FilesGrid.ItemsSource = Files;
            LoadCustomDirectories();
            RefreshScopeText();
            RefreshSummaryText();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<string> CustomDirectories { get; } = new ObservableCollection<string>();

        public ObservableCollection<RevitCleanupFileItem> Files { get; } = new ObservableCollection<RevitCleanupFileItem>();

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

        public bool CanScan => !IsScanning && (ScanDesktop || ScanDownloads || ScanDocuments || CustomDirectories.Count > 0);

        public bool CanDeleteAll => !IsScanning && (Files.Count > 0);

        public bool CanEditCustomDirectories => !IsScanning && !string.IsNullOrWhiteSpace(SelectedCustomDirectory);

        public bool CanClearCustomDirectories => !IsScanning && (CustomDirectories.Count > 0);

        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            await StartScanAsync(false);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await StartScanAsync(true);
        }

        private void AddDirectoryButton_Click(object sender, RoutedEventArgs e)
        {
            var initialPath = CustomDirectories.LastOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
            var selectedPath = FolderPickerService.PickFolder("选择要额外扫描的目录", initialPath);
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            selectedPath = RevitCleanupPathUtility.NormalizePath(selectedPath);
            if (CustomDirectories.Any(path => string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase)))
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

            if (MessageBox.Show("确定清空所有自定义扫描目录吗？", "清理RVT", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
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

        private async void DeleteAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (Files.Count == 0)
            {
                return;
            }

            var confirm = MessageBox.Show($"确定删除当前列表中的 {Files.Count} 个文件吗？",
                                          "清理RVT",
                                          MessageBoxButton.YesNo,
                                          MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            await DeleteVisibleItemsAsync(Files.ToList(), "批量删除扫描结果");
            RefreshSummaryText();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async Task StartScanAsync(bool forceRebuildLocalIndex)
        {
            var roots = BuildScanRoots();
            if (roots.Count == 0)
            {
                MessageBox.Show("请至少选择一个默认目录，或添加一个自定义扫描目录。", "清理RVT", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            scanCancellationTokenSource?.Cancel();
            scanCancellationTokenSource?.Dispose();
            scanCancellationTokenSource = new CancellationTokenSource();

            IsScanning = true;
            hasScanned = false;
            Files.Clear();
            matchedFileCount = 0;
            matchedTotalBytes = 0;
            deletedFileCount = 0;
            failedDeleteCount = 0;
            currentProgressMessage = forceRebuildLocalIndex ? "正在准备重建本地索引..." : "正在准备索引查询...";
            currentProviderDisplayText = "本地索引";
            RefreshSummaryText();

            LoggingService.LogInfo($"{(forceRebuildLocalIndex ? "开始重建 Revit 本地索引" : "开始扫描 Revit 文件")}。Roots={string.Join(" | ", roots.Select(r => r.RootPath))}");

            try
            {
                var progress = new Progress<RevitFileQueryProgress>(info =>
                {
                    if (info != null && !string.IsNullOrWhiteSpace(info.Message))
                    {
                        currentProgressMessage = info.Message;
                        RefreshSummaryText();
                    }
                });

                var result = await queryService.QueryAsync(new RevitFileQueryOptions
                {
                    Roots = roots,
                    Extensions = new[] { ".rvt", ".rfa" },
                    ForceRebuildLocalIndex = forceRebuildLocalIndex
                }, progress, scanCancellationTokenSource.Token).ConfigureAwait(true);

                currentSourceKind = result.SourceKind;
                currentProviderDisplayText = result.ProviderDisplayText;
                Files.Clear();
                foreach (var item in result.Files.OrderByDescending(file => file.ModifiedTimeUtc))
                {
                    Files.Add(new RevitCleanupFileItem(item, DeleteSingleFileAsync));
                }

                SynchronizeMatchedSummary();
                hasScanned = true;
                currentProgressMessage = string.Empty;
                LoggingService.LogInfo($"{(forceRebuildLocalIndex ? "Revit 本地索引重建并查询完成" : "Revit 文件扫描完成")}。Source={result.ProviderDisplayText}, Matched={matchedFileCount}, Size={matchedTotalBytes}");
            }
            catch (OperationCanceledException)
            {
                LoggingService.LogWarning(forceRebuildLocalIndex ? "Revit 本地索引重建已取消。" : "Revit 文件扫描已取消。");
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "扫描 Revit 文件失败");
                MessageBox.Show($"扫描失败：{ex.Message}", "清理RVT", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsScanning = false;
                RefreshSummaryText();
            }
        }

        private async Task DeleteSingleFileAsync(RevitCleanupFileItem item)
        {
            if (item == null)
            {
                return;
            }

            var result = MessageBox.Show($"确定删除文件？\n{item.FullPath}", "清理RVT", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            var deleteResult = await DeleteFilesAsync(new[] { item.IndexedFile });
            if (deleteResult.DeletedPaths.Any(path => string.Equals(path, item.FullPath, StringComparison.OrdinalIgnoreCase)))
            {
                RemoveFilesFromView(deleteResult.DeletedPaths);
                LoggingService.LogInfo($"已删除 Revit 文件：{item.FullPath}");
            }
            else
            {
                MessageBox.Show($"删除失败：{item.FullPath}", "清理RVT", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            RefreshSummaryText();
            OnPropertyChanged(nameof(CanDeleteAll));
        }

        private async Task DeleteVisibleItemsAsync(IReadOnlyCollection<RevitCleanupFileItem> items, string logTitle)
        {
            if ((items == null) || (items.Count == 0))
            {
                return;
            }

            var deleteResult = await DeleteFilesAsync(items.Select(item => item.IndexedFile));
            RemoveFilesFromView(deleteResult.DeletedPaths);

            LoggingService.LogInfo($"{logTitle}：Success={deleteResult.DeletedPaths.Count}, Failed={deleteResult.FailedPaths.Count}");
            if (deleteResult.FailedPaths.Count > 0)
            {
                MessageBox.Show($"已删除 {deleteResult.DeletedPaths.Count} 个文件，另有 {deleteResult.FailedPaths.Count} 个删除失败，请查看日志或重试。",
                                "清理RVT",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
            }

            OnPropertyChanged(nameof(CanDeleteAll));
        }

        private async Task<DeleteResult> DeleteFilesAsync(IEnumerable<RevitIndexedFileInfo> descriptors)
        {
            var result = await Task.Run(() =>
            {
                var deletedPaths = new List<string>();
                var failedPaths = new List<string>();
                var targets = descriptors?.Where(item => item != null).ToList() ?? new List<RevitIndexedFileInfo>();

                Parallel.ForEach(targets, descriptor =>
                {
                    try
                    {
                        if (File.Exists(descriptor.FullPath))
                        {
                            File.Delete(descriptor.FullPath);
                        }

                        lock (deletedPaths)
                        {
                            deletedPaths.Add(descriptor.FullPath);
                        }

                        Interlocked.Increment(ref deletedFileCount);
                    }
                    catch (Exception ex)
                    {
                        lock (failedPaths)
                        {
                            failedPaths.Add(descriptor.FullPath);
                        }

                        Interlocked.Increment(ref failedDeleteCount);
                        LoggingService.LogError(ex, $"删除 Revit 文件失败：{descriptor.FullPath}");
                    }
                });

                return new DeleteResult
                {
                    DeletedPaths = deletedPaths,
                    FailedPaths = failedPaths
                };
            }).ConfigureAwait(true);

            if ((currentSourceKind != RevitFileQuerySourceKind.EverythingIndex) && result.DeletedPaths.Count > 0)
            {
                try
                {
                    await queryService.RemoveFilesFromIndexAsync(result.DeletedPaths, CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    LoggingService.LogError(ex, "同步本地索引删除结果失败");
                }
            }

            return result;
        }

        private void RemoveFilesFromView(IEnumerable<string> deletedPaths)
        {
            var pathSet = new HashSet<string>((deletedPaths ?? Array.Empty<string>())
                .Select(RevitCleanupPathUtility.NormalizePath)
                .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);

            if (pathSet.Count == 0)
            {
                return;
            }

            foreach (var item in Files.Where(file => pathSet.Contains(file.FullPath)).ToList())
            {
                Files.Remove(item);
            }

            SynchronizeMatchedSummary();
        }

        private List<RevitFileQueryRoot> BuildScanRoots()
        {
            var roots = new List<RevitFileQueryRoot>();

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

            foreach (var customDirectory in CustomDirectories)
            {
                AddRoot(roots, "自定义", customDirectory);
            }

            return roots.GroupBy(root => RevitCleanupPathUtility.NormalizePath(root.RootPath), StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First())
                        .ToList();
        }

        private void AddRoot(ICollection<RevitFileQueryRoot> roots, string displayName, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var normalized = RevitCleanupPathUtility.NormalizePath(path);
            if (!Directory.Exists(normalized))
            {
                LoggingService.LogWarning($"Revit 文件清理跳过不存在目录：{normalized}");
                return;
            }

            roots.Add(new RevitFileQueryRoot
            {
                DisplayName = displayName,
                RootPath = normalized
            });
        }

        private void LoadCustomDirectories()
        {
            var settings = dataPersistenceService.LoadSettings();
            var paths = settings?.RevitCleanupCustomDirectories ?? new List<string>();
            CustomDirectories.Clear();

            foreach (var path in paths.Select(RevitCleanupPathUtility.NormalizePath)
                                      .Where(path => !string.IsNullOrWhiteSpace(path))
                                      .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                CustomDirectories.Add(path);
            }

            SelectedCustomDirectory = CustomDirectories.FirstOrDefault();
            OnPropertyChanged(nameof(CanClearCustomDirectories));
        }

        private void SaveCustomDirectories()
        {
            var settings = dataPersistenceService.LoadSettings() ?? new AppSettings();
            settings.RevitCleanupCustomDirectories = CustomDirectories.Select(RevitCleanupPathUtility.NormalizePath)
                                                                     .Where(path => !string.IsNullOrWhiteSpace(path))
                                                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                                                     .ToList();
            dataPersistenceService.SaveSettings(settings);
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

            ScanScopeText = names.Count == 0
                ? "当前未选择任何扫描目录。"
                : $"当前扫描范围：{string.Join("、", names)}";
            OnPropertyChanged(nameof(ScanScopeText));
        }

        private void RefreshSummaryText()
        {
            if (IsScanning)
            {
                SummaryText = string.IsNullOrWhiteSpace(currentProgressMessage)
                    ? "正在处理索引查询..."
                    : currentProgressMessage;
                return;
            }

            if (!hasScanned)
            {
                SummaryText = "请选择扫描范围后开始扫描。";
                return;
            }

            var totalSizeText = FormatSize(matchedTotalBytes);
            SummaryText = $"来源：{currentProviderDisplayText}，命中 {matchedFileCount} 个文件，总大小 {totalSizeText}，已删除 {deletedFileCount} 个，删除失败 {failedDeleteCount} 个，当前列表剩余 {Files.Count} 个。";
        }

        private void SynchronizeMatchedSummary()
        {
            matchedFileCount = Files.Count;
            matchedTotalBytes = Files.Sum(item => item.SizeBytes);
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            scanCancellationTokenSource?.Cancel();
            scanCancellationTokenSource?.Dispose();
            scanCancellationTokenSource = null;
            base.OnClosing(e);
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        private static string GetKnownFolderPath(string folderId)
        {
            var guid = new Guid(folderId);
            var hr = SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out var pathPtr);
            if (hr != 0)
            {
                return string.Empty;
            }

            try
            {
                return Marshal.PtrToStringUni(pathPtr);
            }
            finally
            {
                if (pathPtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pathPtr);
                }
            }
        }

        public sealed class RevitCleanupFileItem
        {
            private readonly Func<RevitCleanupFileItem, Task> deleteHandler;

            internal RevitCleanupFileItem(RevitIndexedFileInfo indexedFile, Func<RevitCleanupFileItem, Task> deleteHandler)
            {
                IndexedFile = indexedFile;
                this.deleteHandler = deleteHandler;
                FileName = indexedFile.FileName;
                SourceDisplayName = indexedFile.RootDisplayName;
                SizeBytes = indexedFile.SizeBytes;
                SizeText = FormatSize(indexedFile.SizeBytes);
                ModifiedText = indexedFile.ModifiedTimeUtc == DateTime.MinValue ? string.Empty : indexedFile.ModifiedTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                DirectoryPath = IOPath.GetDirectoryName(indexedFile.FullPath) ?? string.Empty;
                FullPath = indexedFile.FullPath;
                DeleteCommand = new RelayCommand(() => _ = this.deleteHandler?.Invoke(this));
            }

            internal RevitIndexedFileInfo IndexedFile { get; }

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

            public List<ButtonConfig> ActionButtons => new List<ButtonConfig>
            {
                new ButtonConfig
                {
                    Text = "删除",
                    Width = 90,
                    Height = 26,
                    CommandProperty = nameof(DeleteCommand)
                }
            };

            public string FullPath { get; set; }

            public long SizeBytes { get; set; }
        }

        private sealed class DeleteResult
        {
            public List<string> DeletedPaths { get; set; } = new List<string>();

            public List<string> FailedPaths { get; set; } = new List<string>();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 B";
            }

            var units = new[] { "B", "KB", "MB", "GB", "TB" };
            var size = (double)bytes;
            var unitIndex = 0;
            while ((size >= 1024d) && (unitIndex < (units.Length - 1)))
            {
                size /= 1024d;
                unitIndex++;
            }

            return unitIndex == 0 ? $"{size:F0} {units[unitIndex]}" : $"{size:F2} {units[unitIndex]}";
        }
    }
}
