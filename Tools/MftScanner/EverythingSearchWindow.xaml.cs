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
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        private const int SearchBatchSize = 500;
        private const double LoadMoreThreshold = 24d;

        private readonly IndexService _indexService = new IndexService();
        private readonly IncrementalFilter _filter;
        private readonly List<ScanRoot> _roots;
        private readonly HashSet<string> _displayedPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public ObservableCollection<EverythingSearchResultItem> _displayedResults { get; set; }
            = new ObservableCollection<EverythingSearchResultItem>();
        private CancellationTokenSource _indexCts;
        private CancellationTokenSource _searchCts;
        private readonly DispatcherTimer _debounceTimer;
        private bool _indexReady;
        private int _indexedCount;
        private string _activeKeyword = string.Empty;
        private int _totalMatchedCount;
        private int _loadedResultCount;
        private bool _isSearchInProgress;
        private bool _isLoadingMore;
        private ScrollViewer _resultsScrollViewer;
        private string _cachedKeyword;
        private Regex _cachedRegex;

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

            // 索引增量变更：直接更新当前已显示结果与命中总数，保持实时反馈
            _indexService.IndexChanged += (s, e) =>
            {
                Dispatcher.BeginInvoke(new Action(() => ApplyIndexChange(e)));
            };
        }

        private void EverythingSearchWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SearchBox.Focus();
            AttachResultsScrollViewer();
            _ = StartIndexingAsync(forceRescan: false);
        }

        private void EverythingSearchWindow_Closed(object sender, EventArgs e)
        {
            _indexCts?.Cancel();
            _indexCts?.Dispose();
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _debounceTimer.Stop();
            _indexService.Shutdown();
            if (_resultsScrollViewer != null)
                _resultsScrollViewer.ScrollChanged -= ResultsScrollViewer_ScrollChanged;
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
            _displayedPathSet.Clear();
            _indexReady = false;
            _indexedCount = 0;
            _activeKeyword = string.Empty;
            _totalMatchedCount = 0;
            _loadedResultCount = 0;
            _isSearchInProgress = false;
            _isLoadingMore = false;
            _cachedKeyword = null;
            _cachedRegex = null;
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
            _displayedPathSet.Clear();
            _activeKeyword = string.Empty;
            _totalMatchedCount = 0;
            _loadedResultCount = 0;
            _isSearchInProgress = false;
            _isLoadingMore = false;
            _cachedKeyword = null;
            _cachedRegex = null;

            if (!_indexReady)
            {
                StatusText.Text = "正在建立索引，请稍候...";
                return;
            }

            var kw = (keyword ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(kw))
            {
                IndexingProgress.Visibility = Visibility.Collapsed;
                UpdateSummaryStatus();
                return;
            }

            _activeKeyword = kw;
            _isSearchInProgress = true;
            IndexingProgress.Visibility = Visibility.Visible;
            StatusText.Text = $"正在搜索 \"{kw}\"...";

            try
            {
                var queryResult = await _filter
                    .QueryAsync(kw, SearchBatchSize, 0, ct)
                    .ConfigureAwait(true);

                if (ct.IsCancellationRequested || !string.Equals(_activeKeyword, kw, StringComparison.Ordinal))
                    return;

                AppendResults(queryResult);
                _totalMatchedCount = queryResult.TotalMatchedCount;
                _loadedResultCount = queryResult.Results.Count;
                UpdateSummaryStatus();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _isSearchInProgress = false;
                if (!ct.IsCancellationRequested)
                {
                    IndexingProgress.Visibility = Visibility.Collapsed;
                    UpdateSummaryStatus();
                }
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
                if (_isSearchInProgress)
                    return;

                if (_indexReady)
                    UpdateSummaryStatus();
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

        private async void ResultsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange <= 0)
                return;

            if (_isLoadingMore || string.IsNullOrWhiteSpace(_activeKeyword))
                return;

            if (_displayedResults.Count >= _totalMatchedCount)
                return;

            var remaining = e.ExtentHeight - e.ViewportHeight - e.VerticalOffset;
            if (remaining > LoadMoreThreshold)
                return;

            await LoadMoreResultsAsync().ConfigureAwait(true);
        }

        private async Task LoadMoreResultsAsync()
        {
            if (_isLoadingMore || string.IsNullOrWhiteSpace(_activeKeyword))
                return;

            var currentOffset = _loadedResultCount;
            if (currentOffset >= _totalMatchedCount)
                return;

            _isLoadingMore = true;
            IndexingProgress.Visibility = Visibility.Visible;
            StatusText.Text = $"正在继续加载 \"{_activeKeyword}\"...";

            try
            {
                var ct = _searchCts?.Token ?? CancellationToken.None;
                var queryResult = await _filter
                    .QueryAsync(_activeKeyword, SearchBatchSize, currentOffset, ct)
                    .ConfigureAwait(true);

                if (ct.IsCancellationRequested)
                    return;

                AppendResults(queryResult);
                _totalMatchedCount = queryResult.TotalMatchedCount;
                _loadedResultCount += queryResult.Results.Count;
                UpdateSummaryStatus();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _isLoadingMore = false;
                if (_searchCts == null || !_searchCts.IsCancellationRequested)
                {
                    IndexingProgress.Visibility = Visibility.Collapsed;
                    UpdateSummaryStatus();
                }
            }
        }

        private void AppendResults(SearchQueryResult queryResult)
        {
            foreach (var item in queryResult.Results)
            {
                TryAddDisplayedResult(item.FullPath, item.SizeBytes, item.ModifiedTimeUtc, item.IsDirectory);
            }
        }

        private void ApplyIndexChange(IndexChangedEventArgs e)
        {
            if (!_indexReady || string.IsNullOrWhiteSpace(_activeKeyword))
                return;

            var allLoadedBeforeChange = _displayedResults.Count >= _totalMatchedCount;
            var (pathPrefix, searchTerm) = ParsePathScope(_activeKeyword);
            if (string.IsNullOrWhiteSpace(searchTerm))
                return;

            switch (e.Type)
            {
                case IndexChangeType.Deleted:
                {
                    var deletedMatches = MatchesCurrentQuery(e.LowerName, e.FullPath, searchTerm, pathPrefix);
                    if (deletedMatches && _totalMatchedCount > 0)
                        _totalMatchedCount--;

                    RemoveDisplayedResult(e.FullPath, e.LowerName);
                    break;
                }
                case IndexChangeType.Created:
                {
                    if (MatchesCurrentQuery(e.LowerName, e.FullPath, searchTerm, pathPrefix))
                    {
                        _totalMatchedCount++;
                        if (allLoadedBeforeChange)
                            TryAddDisplayedResult(e.FullPath, 0, DateTime.MinValue, false);
                    }
                    break;
                }
                case IndexChangeType.Renamed:
                {
                    var oldMatches = MatchesCurrentQuery(e.LowerName, e.OldFullPath, searchTerm, pathPrefix);
                    var newMatches = MatchesCurrentQuery(e.NewLowerName, e.FullPath, searchTerm, pathPrefix);

                    if (oldMatches && _totalMatchedCount > 0)
                        _totalMatchedCount--;
                    if (oldMatches || !string.IsNullOrWhiteSpace(e.OldFullPath))
                        RemoveDisplayedResult(e.OldFullPath, e.LowerName);

                    if (newMatches)
                    {
                        _totalMatchedCount++;
                        if (allLoadedBeforeChange)
                            TryAddDisplayedResult(e.FullPath, 0, DateTime.MinValue, false);
                    }
                    break;
                }
            }

            if (allLoadedBeforeChange)
                _loadedResultCount = _displayedResults.Count;

            UpdateSummaryStatus();
        }

        private bool TryAddDisplayedResult(string fullPath, long sizeBytes, DateTime modifiedUtc, bool isDirectory)
        {
            var normalizedPath = fullPath ?? string.Empty;
            if (!_displayedPathSet.Add(normalizedPath))
                return false;

            _displayedResults.Add(new EverythingSearchResultItem(fullPath, sizeBytes, modifiedUtc, isDirectory));
            return true;
        }

        private bool RemoveDisplayedResult(string fullPath, string lowerName)
        {
            if (!string.IsNullOrWhiteSpace(fullPath) && _displayedPathSet.Remove(fullPath))
            {
                for (var i = _displayedResults.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(_displayedResults[i].FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _displayedResults.RemoveAt(i);
                        return true;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(lowerName))
                return false;

            for (var i = _displayedResults.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(_displayedResults[i].FileName, lowerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                _displayedPathSet.Remove(_displayedResults[i].FullPath ?? string.Empty);
                _displayedResults.RemoveAt(i);
                return true;
            }

            return false;
        }

        private bool MatchesCurrentQuery(string lowerName, string fullPath, string searchTerm, string pathPrefix)
        {
            if (string.IsNullOrWhiteSpace(lowerName))
                return false;

            if (!MatchesCurrentKeyword(lowerName, searchTerm))
                return false;

            if (pathPrefix == null)
                return true;

            return !string.IsNullOrWhiteSpace(fullPath)
                   && fullPath.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private bool MatchesCurrentKeyword(string lowerName, string keyword)
        {
            var kw = (keyword ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrEmpty(kw))
                return false;

            if (kw.StartsWith("^"))
                return lowerName.StartsWith(kw.Substring(1), StringComparison.Ordinal);
            if (kw.EndsWith("$"))
                return lowerName.EndsWith(kw.Substring(0, kw.Length - 1), StringComparison.Ordinal);

            var needsRegex = (kw.Length >= 3 && kw.StartsWith("/") && kw.EndsWith("/"))
                             || kw.IndexOfAny(new[] { '*', '?' }) >= 0;
            if (needsRegex)
            {
                if (_cachedKeyword != kw || _cachedRegex == null)
                {
                    _cachedKeyword = kw;
                    try
                    {
                        var pattern = (kw.Length >= 3 && kw.StartsWith("/") && kw.EndsWith("/"))
                            ? kw.Substring(1, kw.Length - 2)
                            : WildcardToRegex(kw);
                        _cachedRegex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                    }
                    catch
                    {
                        _cachedRegex = null;
                    }
                }

                if (_cachedRegex == null)
                    return false;

                try { return _cachedRegex.IsMatch(lowerName); }
                catch { return false; }
            }

            return lowerName.Contains(kw);
        }

        private static (string pathPrefix, string searchTerm) ParsePathScope(string keyword)
        {
            var kw = (keyword ?? string.Empty).Trim();
            var spaceIdx = kw.IndexOf(' ');
            if (spaceIdx <= 0)
                return (null, kw);

            var candidate = kw.Substring(0, spaceIdx);
            var isValidPath = (candidate.Length >= 3
                               && char.IsLetter(candidate[0])
                               && candidate[1] == ':'
                               && candidate[2] == '\\')
                           || candidate.StartsWith("\\");
            if (!isValidPath)
                return (null, kw);

            var normalizedPrefix = candidate.EndsWith("\\") ? candidate : candidate + "\\";
            var searchTerm = kw.Substring(spaceIdx).TrimStart();
            return (normalizedPrefix, searchTerm);
        }

        private static string WildcardToRegex(string pattern)
        {
            var sb = new System.Text.StringBuilder("^");
            foreach (var c in pattern)
            {
                switch (c)
                {
                    case '*': sb.Append(".*"); break;
                    case '?': sb.Append('.'); break;
                    default: sb.Append(Regex.Escape(c.ToString())); break;
                }
            }

            sb.Append('$');
            return sb.ToString();
        }

        private void UpdateSummaryStatus()
        {
            if (!_indexReady)
            {
                StatusText.Text = "正在建立索引，请稍候...";
                return;
            }

            if (_isSearchInProgress)
            {
                StatusText.Text = $"正在搜索 \"{_activeKeyword}\"...";
                return;
            }

            if (_isLoadingMore)
            {
                StatusText.Text = $"正在继续加载 \"{_activeKeyword}\"...";
                return;
            }

            if (string.IsNullOrWhiteSpace(_activeKeyword))
            {
                StatusText.Text = $"{_indexedCount} 个对象";
                return;
            }

            if (_totalMatchedCount <= 0 || _displayedResults.Count == 0)
            {
                StatusText.Text = $"未找到匹配项（共 {_indexedCount} 个对象）";
                return;
            }

            if (_displayedResults.Count < _totalMatchedCount)
            {
                StatusText.Text = $"已显示 {_displayedResults.Count} 个对象（共 {_totalMatchedCount} 个，滚动到底继续加载）";
                return;
            }

            StatusText.Text = $"{_displayedResults.Count} 个对象（共 {_totalMatchedCount} 个）";
        }

        private void AttachResultsScrollViewer()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_resultsScrollViewer != null)
                {
                    _resultsScrollViewer.ScrollChanged -= ResultsScrollViewer_ScrollChanged;
                    _resultsScrollViewer = null;
                }

                _resultsScrollViewer = FindDescendant<ScrollViewer>(ResultsGrid);
                if (_resultsScrollViewer != null)
                    _resultsScrollViewer.ScrollChanged += ResultsScrollViewer_ScrollChanged;
            }), DispatcherPriority.Loaded);
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
                return null;

            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T matched)
                    return matched;

                var descendant = FindDescendant<T>(child);
                if (descendant != null)
                    return descendant;
            }

            return null;
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
