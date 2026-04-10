using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        public EverythingSearchResultItem(string fullPath, long sizeBytes, DateTime modifiedUtc)
        {
            FullPath = fullPath;
            FileName = System.IO.Path.GetFileName(fullPath);
            DirectoryPath = System.IO.Path.GetDirectoryName(fullPath) ?? string.Empty;
            SizeText = FormatSize(sizeBytes);
            ModifiedText = modifiedUtc == DateTime.MinValue
                ? string.Empty
                : modifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            SizeBytes = sizeBytes;
        }

        [DataGridColumn(1, DisplayName = "名称", Width = "260", IsReadOnly = true)]
        public string FileName { get; set; }

        [DataGridColumn(2, DisplayName = "路径", Width = "500", IsReadOnly = true)]
        public string DirectoryPath { get; set; }

        [DataGridColumn(3, DisplayName = "大小", Width = "110", IsReadOnly = true)]
        public string SizeText { get; set; }

        [DataGridColumn(4, DisplayName = "修改时间", Width = "160", IsReadOnly = true)]
        public string ModifiedText { get; set; }

        public string FullPath { get; set; }
        public long SizeBytes { get; set; }

        private static string FormatSize(long bytes)
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

        private readonly MftScanService _scanService = new MftScanService();
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
            _scanService.SaveAllCaches();
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

            if (forceRescan)
                _scanService.InvalidateCache();

            try
            {
                var progress = new Progress<string>(msg =>
                {
                    if (!string.IsNullOrWhiteSpace(msg))
                        StatusText.Text = msg;
                });

                _indexedCount = await _scanService.PrepareSearchIndexAsync(_roots, progress, ct).ConfigureAwait(true);

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
                var progress = new Progress<string>(msg =>
                {
                    if (!string.IsNullOrWhiteSpace(msg))
                        StatusText.Text = msg;
                });

                var queryResult = await _scanService
                    .SearchByKeywordAsync(_roots, kw, MaxDisplayedResults, progress, ct)
                    .ConfigureAwait(true);

                foreach (var item in queryResult.Results.Select(r => new EverythingSearchResultItem(r.FullPath, r.SizeBytes, r.ModifiedTimeUtc)))
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
                OpenFile(item.FullPath);
        }

        private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ResultsGrid.SelectedItem is EverythingSearchResultItem item)
                OpenFile(item.FullPath);
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

        private static void OpenFile(string fullPath)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = fullPath, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开文件：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void OpenContainingFolder(string fullPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(fullPath);
                if (dir != null && Directory.Exists(dir))
                    Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
                else
                    MessageBox.Show("目录不存在。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开文件位置：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
