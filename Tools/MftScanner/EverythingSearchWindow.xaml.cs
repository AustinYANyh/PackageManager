using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;

namespace MftScanner
{
    public sealed class EverythingSearchResultItem
    {
        public EverythingSearchResultItem(string fullPath, long sizeBytes, DateTime modifiedUtc, bool isDirectory)
        {
            FullPath = fullPath;
            IsDirectory = isDirectory;
            FileName = System.IO.Path.GetFileName(fullPath);
            DirectoryPath = System.IO.Path.GetDirectoryName(fullPath) ?? string.Empty;
            TypeText = isDirectory ? "文件夹" : "文件";
            SizeText = isDirectory ? string.Empty : FormatSize(sizeBytes);
            ModifiedText = modifiedUtc == DateTime.MinValue
                ? string.Empty
                : modifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            SizeBytes = sizeBytes;
        }

        [DataGridColumn(1, DisplayName = "名称", Width = "260", IsReadOnly = true)]
        public string FileName { get; set; }

        [DataGridColumn(2, DisplayName = "路径", Width = "500", IsReadOnly = true)]
        public string DirectoryPath { get; set; }

        [DataGridColumn(3, DisplayName = "类型", Width = "90", IsReadOnly = true)]
        public string TypeText { get; set; }

        [DataGridColumn(4, DisplayName = "大小", Width = "110", IsReadOnly = true)]
        public string SizeText { get; set; }

        [DataGridColumn(5, DisplayName = "修改时间", Width = "160", IsReadOnly = true)]
        public string ModifiedText { get; set; }

        public string FullPath { get; set; }
        public long SizeBytes { get; set; }
        public bool IsDirectory { get; set; }

        internal static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            var units = new[] { "B", "KB", "MB", "GB", "TB" };
            var size = (double)bytes;
            var i = 0;
            while (size >= 1024d && i < units.Length - 1) { size /= 1024d; i++; }
            return i == 0 ? $"{size:F0} {units[i]}" : $"{size:F2} {units[i]}";
        }
    }

    public partial class EverythingSearchWindow : Window
    {
        private const int MaxDisplayedResults = 500;

        private readonly IndexService _indexService = new IndexService();
        private readonly IncrementalFilter _filter;
        private readonly List<ScanRoot> _roots;
        public ObservableCollection<EverythingSearchResultItem> _displayedResults { get; set; }
            = new ObservableCollection<EverythingSearchResultItem>();
        private CancellationTokenSource _indexCts;
        private CancellationTokenSource _searchCts;
        private readonly DispatcherTimer _debounceTimer;
        private bool _indexReady;
        private int _indexedCount;

        public EverythingSearchWindow()
        {
            InitializeComponent();
            ResultsGrid.ItemsSource = _displayedResults;

            _filter = new IncrementalFilter(_indexService);

            _roots = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed)
                .Select(d => new ScanRoot
                {
                    Path = d.RootDirectory.FullName,
                    DisplayName = d.Name.TrimEnd('\\', '/')
                })
                .ToList();

            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _debounceTimer.Tick += DebounceTimer_Tick;

            // 索引增量变更：直接操作结果列表，不重新搜索
            _indexService.IndexChanged += (s, e) =>
            {
                Dispatcher.BeginInvoke(new Action(() => ApplyIndexChange(e)));
            };
        }

        private void EverythingSearchWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SearchBox.Focus();
            _ = StartIndexingAsync(forceRescan: false);
        }

        private void EverythingSearchWindow_Closed(object sender, EventArgs e)
        {
            _indexCts?.Cancel();
            _indexCts?.Dispose();
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _debounceTimer.Stop();
        }

        private async Task StartIndexingAsync(bool forceRescan)
        {
            _indexCts?.Cancel();
            _indexCts?.Dispose();
            _indexCts = new CancellationTokenSource();
            var ct = _indexCts.Token;

            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            _displayedResults.Clear();
            _indexReady = false;
            _indexedCount = 0;
            IndexingProgress.Visibility = Visibility.Visible;
            StatusText.Text = "正在建立索引...";

            try
            {
                var progress = new Progress<string>(msg =>
                {
                    if (!string.IsNullOrWhiteSpace(msg))
                        StatusText.Text = msg;
                });

                _indexedCount = forceRescan
                    ? await _indexService.RebuildIndexAsync(progress, ct).ConfigureAwait(true)
                    : await _indexService.BuildIndexAsync(progress, ct).ConfigureAwait(true);

                _indexReady = true;
                IndexingProgress.Visibility = Visibility.Collapsed;
                StatusText.Text = $"{_indexedCount} 个对象";

                await ApplyFilterAsync(SearchBox.Text).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                IndexingProgress.Visibility = Visibility.Collapsed;
                StatusText.Text = "索引已取消";
            }
            catch (Exception ex)
            {
                IndexingProgress.Visibility = Visibility.Collapsed;
                StatusText.Text = $"索引失败：{ex.Message}";
            }
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void DebounceTimer_Tick(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            _ = ApplyFilterAsync(SearchBox.Text);
        }

        private async Task ApplyFilterAsync(string keyword)
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;

            _displayedResults.Clear();

            if (!_indexReady)
            {
                StatusText.Text = "正在建立索引，请稍候...";
                return;
            }

            var kw = (keyword ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(kw))
            {
                IndexingProgress.Visibility = Visibility.Collapsed;
                StatusText.Text = $"{_indexedCount} 个对象";
                return;
            }

            IndexingProgress.Visibility = Visibility.Visible;
            StatusText.Text = $"正在搜索 \"{kw}\"...";

            try
            {
                var queryResult = await _filter
                    .QueryAsync(kw, MaxDisplayedResults, ct)
                    .ConfigureAwait(true);

                foreach (var item in queryResult.Results.Select(r => new EverythingSearchResultItem(r.FullPath, r.SizeBytes, r.ModifiedTimeUtc, r.IsDirectory)))
                    _displayedResults.Add(item);

                if (_displayedResults.Count == 0)
                {
                    StatusText.Text = $"未找到匹配项（共 {_indexedCount} 个对象）";
                }
                else if (queryResult.IsTruncated)
                {
                    StatusText.Text = $"显示 {_displayedResults.Count} 个对象（共 {queryResult.TotalMatchedCount} 个，输入更多字符可继续缩小）";
                }
                else
                {
                    StatusText.Text = $"{_displayedResults.Count} 个对象（共 {queryResult.TotalIndexedCount} 个）";
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (!ct.IsCancellationRequested)
                    IndexingProgress.Visibility = Visibility.Collapsed;
            }
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && ResultsGrid.SelectedItem is EverythingSearchResultItem item)
                OpenItem(item);
        }

        private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ResultsGrid.SelectedItem is EverythingSearchResultItem item)
                OpenItem(item);
        }

        private void ResultsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ResultsGrid.SelectedItem is not EverythingSearchResultItem item)
            {
                // 无选中项时恢复计数显示
                if (_indexReady)
                    StatusText.Text = _displayedResults.Count > 0
                        ? $"{_displayedResults.Count} 个对象"
                        : $"{_indexedCount} 个对象";
                return;
            }

            try
            {
                if (item.IsDirectory)
                {
                    var di = new DirectoryInfo(item.FullPath);
                    if (!di.Exists)
                    {
                        StatusText.Text = "文件已不存在";
                        return;
                    }
                    StatusText.Text = $"修改时间：{di.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
                }
                else
                {
                    var fi = new FileInfo(item.FullPath);
                    if (!fi.Exists)
                    {
                        StatusText.Text = "文件已不存在";
                        return;
                    }
                    StatusText.Text = $"大小：{EverythingSearchResultItem.FormatSize(fi.Length)}  修改时间：{fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
                }
            }
            catch
            {
                StatusText.Text = "文件已不存在";
            }
        }

        private void OpenContainingFolder_Click(object sender, RoutedEventArgs e)
        {
            if (ResultsGrid.SelectedItem is EverythingSearchResultItem item)
                OpenContainingFolder(item.FullPath);
        }

        private void CopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (ResultsGrid.SelectedItem is EverythingSearchResultItem item)
            {
                try { Clipboard.SetText(item.FullPath); }
                catch { }
            }
        }

        private void ForceRescanButton_Click(object sender, RoutedEventArgs e)
        {
            _ = StartIndexingAsync(forceRescan: true);
        }

        /// <summary>
        /// 根据 USN 增量变更直接操作结果列表，不重新搜索。
        /// 只有当变更文件名匹配当前搜索词时才修改列表。
        /// </summary>
        private void ApplyIndexChange(IndexChangedEventArgs e)
        {
            if (!_indexReady) return;

            var kw = (SearchBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(kw)) return;

            UsnDiagLog.Write($"[UI IndexChange] type={e.Type} lowerName={e.LowerName} results={_displayedResults.Count}");

            switch (e.Type)
            {
                case IndexChangeType.Deleted:
                    for (var i = _displayedResults.Count - 1; i >= 0; i--)
                    {
                        UsnDiagLog.Write($"[UI DELETE CHECK] item.FileName={_displayedResults[i].FileName} vs e.LowerName={e.LowerName}");
                        if (string.Equals(_displayedResults[i].FileName, e.LowerName,
                                StringComparison.OrdinalIgnoreCase))
                            _displayedResults.RemoveAt(i);
                    }
                    break;

                case IndexChangeType.Created:
                    if (e.FullPath != null && MatchesCurrentKeyword(e.LowerName, kw))
                    {
                        // 避免重复添加（同一文件的 USN 事件可能触发多次）
                        var alreadyInList = false;
                        for (var i = 0; i < _displayedResults.Count; i++)
                        {
                            if (string.Equals(_displayedResults[i].FileName, e.LowerName,
                                    StringComparison.OrdinalIgnoreCase))
                            { alreadyInList = true; break; }
                        }
                        if (!alreadyInList)
                            _displayedResults.Add(new EverythingSearchResultItem(e.FullPath, 0, DateTime.MinValue, false));
                    }
                    break;

                case IndexChangeType.Renamed:
                    for (var i = _displayedResults.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(_displayedResults[i].FileName, e.LowerName,
                                StringComparison.OrdinalIgnoreCase))
                            _displayedResults.RemoveAt(i);
                    }
                    if (e.FullPath != null && e.NewLowerName != null && MatchesCurrentKeyword(e.NewLowerName, kw))
                        _displayedResults.Add(new EverythingSearchResultItem(e.FullPath, 0, DateTime.MinValue, false));
                    break;
            }
        }

        /// <summary>判断文件名小写是否匹配当前搜索词（复用 DetectMatchMode 逻辑的简化版）。</summary>
        private static bool MatchesCurrentKeyword(string lowerName, string keyword)
        {
            var kw = keyword.ToLowerInvariant();
            if (kw.StartsWith("^"))
                return lowerName.StartsWith(kw.Substring(1), StringComparison.Ordinal);
            if (kw.EndsWith("$"))
                return lowerName.EndsWith(kw.Substring(0, kw.Length - 1), StringComparison.Ordinal);
            if (kw.Length >= 3 && kw.StartsWith("/") && kw.EndsWith("/"))
            {
                try { return Regex.IsMatch(lowerName, kw.Substring(1, kw.Length - 2), RegexOptions.IgnoreCase); }
                catch { return false; }
            }
            return lowerName.Contains(kw);
        }

        private static void OpenItem(EverythingSearchResultItem item)
        {
            if (item == null) return;

            try
            {
                if (item.IsDirectory)
                {
                    if (!Directory.Exists(item.FullPath))
                    {
                        MessageBox.Show("文件夹不存在。", "错误",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                else if (!File.Exists(item.FullPath))
                {
                    MessageBox.Show("文件不存在。", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Process.Start(new ProcessStartInfo { FileName = item.FullPath, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开项目：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void OpenContainingFolder(string fullPath)
        {
            try
            {
                if (File.Exists(fullPath) || Directory.Exists(fullPath))
                    Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
                else
                    MessageBox.Show("项目不存在。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开文件位置：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
