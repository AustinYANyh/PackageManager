using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace MftScanner
{
    public partial class EverythingSearchWindow
    {
        private async Task StartIndexingAsync(bool forceRescan)
        {
            CancelToken(ref _indexCts);
            _indexCts = new CancellationTokenSource();
            var ct = _indexCts.Token;
            _indexReady = false;
            IndexingProgress.Visibility = Visibility.Visible;
            IndexStateBadgeText.Text = forceRescan ? "正在重建索引" : "正在建立索引";
            StatusText.Text = forceRescan ? "正在重建索引..." : "正在建立索引...";
            EmptyStateTitleText.Text = "正在准备索引";
            EmptyStateDescriptionText.Text = "索引准备完成后，你可以直接输入关键词开始搜索。";
            UpdateEmptyState();

            try
            {
                var progress = new Progress<string>(delegate(string message)
                {
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        _latestIndexStatusMessage = message;
                        StatusText.Text = message;
                    }
                });

                _indexedCount = forceRescan
                    ? await _indexService.RebuildIndexAsync(progress, ct).ConfigureAwait(true)
                    : await _indexService.BuildIndexAsync(progress, ct).ConfigureAwait(true);

                _indexReady = true;
                IndexingProgress.Visibility = Visibility.Collapsed;
                IndexStateBadgeText.Text = "索引已就绪";
                UpdateSummaryStatus();
                if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                    await ApplyFilterAsync(SearchBox.Text, false).ConfigureAwait(true);
                else
                    UpdateEmptyState();
            }
            catch (OperationCanceledException)
            {
                IndexingProgress.Visibility = Visibility.Collapsed;
                IndexStateBadgeText.Text = "索引已取消";
                StatusText.Text = "索引已取消";
            }
            catch (Exception ex)
            {
                IndexingProgress.Visibility = Visibility.Collapsed;
                IndexStateBadgeText.Text = "索引失败";
                StatusText.Text = "索引失败：" + ex.Message;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RestartSearchDebounce();
        }

        private void ScopeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressControlEvents)
                return;

            RestartSearchDebounce();
        }

        private void RestartSearchDebounce()
        {
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void DebounceTimer_Tick(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            _ = ApplyFilterAsync(SearchBox.Text, true);
        }

        private async Task ApplyFilterAsync(string keyword, bool updateHistory)
        {
            CancelToken(ref _searchCts);
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;

            _displayedResults.Clear();
            _displayedPathSet.Clear();
            _allLoadedResults.Clear();
            _loadedResultCount = 0;
            _loadedRawResultCount = 0;
            _totalMatchedCount = 0;
            _activeKeyword = BuildEffectiveKeyword(keyword);
            _isLoadingMore = false;
            _hasMoreSearchResults = false;
            _cachedKeyword = null;
            _cachedRegex = null;

            if (!_indexReady)
            {
                StatusText.Text = "正在建立索引，请稍候...";
                UpdateEmptyState();
                return;
            }

            if (string.IsNullOrWhiteSpace(_activeKeyword))
            {
                _isSearchInProgress = false;
                UpdateSelectedItemDetails(null);
                UpdateSummaryStatus();
                UpdateEmptyState();
                return;
            }

            _isSearchInProgress = true;
            IndexingProgress.Visibility = Visibility.Visible;
            StatusText.Text = "正在搜索 \"" + GetVisibleQueryText() + "\"...";
            CurrentLoadSummaryText.Text = "正在搜索";
            UpdateEmptyState();

            try
            {
                var didLoad = await LoadSearchResultsAsync(SearchBatchSize, true, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested || !didLoad)
                    return;
                if (updateHistory)
                    PushRecentSearch(_activeKeyword);
                UpdateSummaryStatus();
                UpdateEmptyState();
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
                    UpdateEmptyState();
                }

                if (_pendingRefresh && !string.IsNullOrWhiteSpace(_activeKeyword))
                {
                    _pendingRefresh = false;
                    _ = ApplyFilterAsync(_activeKeyword, false);
                }
            }
        }

        private void PushRecentSearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return;

            var existing = _recentSearches.FirstOrDefault(item => string.Equals(item.Query, query, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                _recentSearches.Remove(existing);

            _recentSearches.Insert(0, new SearchHistoryEntry { Query = query, Timestamp = DateTime.Now });
            while (_recentSearches.Count > MaxRecentSearches)
                _recentSearches.RemoveAt(_recentSearches.Count - 1);
            SaveWindowState();
        }

        private void AppendResults(SearchQueryResult result)
        {
            if (result == null || result.Results == null)
                return;

            foreach (var item in result.Results)
            {
                TryAddDisplayedResult(item.FullPath, item.IsDirectory);
            }

            _loadedResultCount = _displayedResults.Count;
            ApplyCurrentSort();
            RefreshGridColumns();
            UpdateActionButtons();
        }

        private async void ResultsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange <= 0 || _isSearchInProgress || _isLoadingMore || string.IsNullOrWhiteSpace(_activeKeyword))
                return;

            var remaining = e.ExtentHeight - e.ViewportHeight - e.VerticalOffset;
            if (remaining > LoadMoreThreshold || !_hasMoreSearchResults)
                return;

            await LoadMoreResultsAsync().ConfigureAwait(true);
        }

        private async Task LoadMoreResultsAsync()
        {
            if (_isLoadingMore || string.IsNullOrWhiteSpace(_activeKeyword))
                return;

            if (!_hasMoreSearchResults)
                return;

            _isLoadingMore = true;
            IndexingProgress.Visibility = Visibility.Visible;
            StatusText.Text = "正在继续加载 \"" + GetVisibleQueryText() + "\"...";
            try
            {
                await LoadSearchResultsAsync(SearchBatchSize, false, CancellationToken.None).ConfigureAwait(true);
            }
            catch
            {
            }
            finally
            {
                _isLoadingMore = false;
                IndexingProgress.Visibility = _isSearchInProgress ? Visibility.Visible : Visibility.Collapsed;
                UpdateSummaryStatus();
            }
        }

        private void ApplyIndexStatusChange(IndexStatusChangedEventArgs e)
        {
            _indexedCount = e.IndexedCount;
            _latestIndexStatusMessage = e.Message;
            IndexStateBadgeText.Text = e.IsBackgroundCatchUpInProgress ? "后台追平中" : "索引已就绪";
            if (e.IsBackgroundCatchUpInProgress)
                IndexingProgress.Visibility = Visibility.Visible;
            else if (!_isSearchInProgress)
                IndexingProgress.Visibility = Visibility.Collapsed;

            if (e.RequireSearchRefresh)
                RequestRefreshCurrentQuery();

            UpdateSummaryStatus();
        }

        private void ApplyIndexChange(IndexChangedEventArgs e)
        {
            if (!_indexReady || string.IsNullOrWhiteSpace(_activeKeyword) || e == null)
                return;

            var allLoadedBeforeChange = !_hasMoreSearchResults;
            var selectedPath = (ResultsGrid.SelectedItem as EverythingSearchResultItem)?.FullPath;
            var preferredSelectionPath = selectedPath;
            var oldMatches = false;
            var newMatches = false;

            switch (e.Type)
            {
                case IndexChangeType.Deleted:
                    oldMatches = MatchesCurrentQueryAndType(e.LowerName, e.FullPath, e.IsDirectory);
                    if (oldMatches && _totalMatchedCount > 0)
                        _totalMatchedCount--;
                    RemoveDisplayedResult(e.FullPath, e.LowerName);
                    break;

                case IndexChangeType.Created:
                    newMatches = MatchesCurrentQueryAndType(e.LowerName, e.FullPath, e.IsDirectory);
                    if (newMatches)
                    {
                        _totalMatchedCount++;
                        if (allLoadedBeforeChange)
                            TryAddDisplayedResult(e.FullPath, e.IsDirectory);
                    }
                    break;

                case IndexChangeType.Renamed:
                    oldMatches = MatchesCurrentQueryAndType(e.LowerName, e.OldFullPath, e.IsDirectory);
                    newMatches = MatchesCurrentQueryAndType(e.NewLowerName, e.FullPath, e.IsDirectory);

                    if (oldMatches && _totalMatchedCount > 0)
                        _totalMatchedCount--;
                    if (oldMatches || !string.IsNullOrWhiteSpace(e.OldFullPath))
                        RemoveDisplayedResult(e.OldFullPath, e.LowerName);

                    if (newMatches)
                    {
                        _totalMatchedCount++;
                        if (allLoadedBeforeChange)
                            TryAddDisplayedResult(e.FullPath, e.IsDirectory);
                    }

                    if (!string.IsNullOrWhiteSpace(selectedPath) && string.Equals(selectedPath, e.OldFullPath, StringComparison.OrdinalIgnoreCase))
                        preferredSelectionPath = e.FullPath;
                    break;
            }

            ApplyCurrentSort();
            _loadedResultCount = _displayedResults.Count;
            if (!string.IsNullOrWhiteSpace(preferredSelectionPath))
            {
                var nextSelection = _displayedResults.FirstOrDefault(item => string.Equals(item.FullPath, preferredSelectionPath, StringComparison.OrdinalIgnoreCase));
                if (nextSelection != null)
                    ResultsGrid.SelectedItem = nextSelection;
            }

            UpdateSummaryStatus();
            UpdateEmptyState();
        }

        private void RequestRefreshCurrentQuery()
        {
            if (string.IsNullOrWhiteSpace(_activeKeyword))
                return;

            if (!ShouldAutoRefreshCurrentQuery())
            {
                _pendingRefresh = false;
                return;
            }

            _pendingRefresh = true;
            _liveRefreshTimer.Stop();
            _liveRefreshTimer.Start();
        }

        private void LiveRefreshTimer_Tick(object sender, EventArgs e)
        {
            _liveRefreshTimer.Stop();
            if (_pendingRefresh && !_isSearchInProgress && !string.IsNullOrWhiteSpace(_activeKeyword) && ShouldAutoRefreshCurrentQuery())
            {
                _pendingRefresh = false;
                _ = ApplyFilterAsync(_activeKeyword, false);
            }
        }

        private bool ShouldAutoRefreshCurrentQuery()
        {
            return _displayedResults.Count == 0 && ResultsGrid.SelectedItem == null;
        }

        private void ResultsGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "FileName":
                case "DirectoryPath":
                case "TypeText":
                case "SizeText":
                case "ModifiedText":
                    break;
                default:
                    e.Cancel = true;
                    return;
            }

            RefreshGridColumns();
        }

        private void RefreshGridColumns()
        {
            if (ResultsGrid.Columns == null || ResultsGrid.Columns.Count == 0)
                return;

            foreach (var column in ResultsGrid.Columns)
            {
                var header = column.Header == null ? string.Empty : column.Header.ToString();
                if (header == "大小" || header == "修改时间")
                    column.Visibility = Visibility.Collapsed;
                else
                    column.Visibility = Visibility.Visible;
            }
        }

        private void FilterButton_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressControlEvents)
                return;

            var clickedButton = sender as ToggleButton;
            if (clickedButton == null)
                return;

            _suppressControlEvents = true;
            AllFilterButton.IsChecked = ReferenceEquals(clickedButton, AllFilterButton);
            LaunchableFilterButton.IsChecked = ReferenceEquals(clickedButton, LaunchableFilterButton);
            FolderFilterButton.IsChecked = ReferenceEquals(clickedButton, FolderFilterButton);
            ScriptFilterButton.IsChecked = ReferenceEquals(clickedButton, ScriptFilterButton);
            LogFilterButton.IsChecked = ReferenceEquals(clickedButton, LogFilterButton);
            ConfigFilterButton.IsChecked = ReferenceEquals(clickedButton, ConfigFilterButton);
            _suppressControlEvents = false;

            clickedButton.IsChecked = true;
            _currentTypeFilter = ResolveTypeFilter(clickedButton.Tag as string);
            QuerySummaryText.Text = "当前类型：" + GetTypeFilterText(_currentTypeFilter) + "；排序仅做内存重排，路径限定复用路径前缀查询";
            UpdateSummaryStatus();
            UpdateEmptyState();

            if (!string.IsNullOrWhiteSpace(_activeKeyword))
            {
                _ = ApplyFilterAsync(_activeKeyword, false);
            }
            else
            {
                StatusText.Text = "已切换类型过滤：" + GetTypeFilterText(_currentTypeFilter);
            }
        }

        private async Task<bool> LoadSearchResultsAsync(int desiredCount, bool isNewSearch, CancellationToken ct)
        {
            if (_currentTypeFilter == FileSearchTypeFilter.All)
            {
                var result = isNewSearch
                    ? await _filter.QueryAsync(_activeKeyword, SearchBatchSize, 0, ct).ConfigureAwait(true)
                    : await _indexService.SearchAsync(_activeKeyword, SearchBatchSize, _loadedRawResultCount, null, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested)
                    return false;

                _totalMatchedCount = result == null ? 0 : result.TotalMatchedCount;
                AppendResults(result);
                _loadedRawResultCount = _loadedResultCount;
                _hasMoreSearchResults = _loadedResultCount < _totalMatchedCount;
                return true;
            }

            var appendedCount = 0;
            SearchQueryResult lastResponse = null;
            while (appendedCount < desiredCount)
            {
                var response = isNewSearch && _loadedRawResultCount == 0 && appendedCount == 0
                    ? await _filter.QueryAsync(_activeKeyword, SearchBatchSize, _loadedRawResultCount, ct).ConfigureAwait(true)
                    : await _indexService.SearchAsync(_activeKeyword, SearchBatchSize, _loadedRawResultCount, null, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested)
                    return false;

                lastResponse = response;
                var pageResults = response == null ? null : response.Results;
                if (pageResults == null || pageResults.Count == 0)
                {
                    _hasMoreSearchResults = false;
                    break;
                }

                _loadedRawResultCount += pageResults.Count;
                _totalMatchedCount = response.TotalMatchedCount;
                appendedCount += AppendTypeFilteredResults(pageResults);
                _hasMoreSearchResults = response.IsTruncated;

                if (!response.IsTruncated)
                    break;
            }

            if (lastResponse == null)
            {
                _totalMatchedCount = 0;
                _hasMoreSearchResults = false;
            }

            return true;
        }

        private int AppendTypeFilteredResults(IEnumerable<ScannedFileInfo> results)
        {
            var appendedCount = 0;
            foreach (var item in results ?? Enumerable.Empty<ScannedFileInfo>())
            {
                if (!MatchesCurrentTypeFilter(item))
                    continue;

                if (TryAddDisplayedResult(item.FullPath, item.IsDirectory))
                    appendedCount++;
            }

            _loadedResultCount = _displayedResults.Count;
            ApplyCurrentSort();
            RefreshGridColumns();
            UpdateActionButtons();
            return appendedCount;
        }

        private bool MatchesCurrentTypeFilter(ScannedFileInfo item)
        {
            if (item == null)
                return false;

            var fullPath = item.FullPath ?? string.Empty;
            var extension = item.IsDirectory ? string.Empty : (Path.GetExtension(fullPath) ?? string.Empty).ToLowerInvariant();
            switch (_currentTypeFilter)
            {
                case FileSearchTypeFilter.All:
                    return true;
                case FileSearchTypeFilter.Folder:
                    return item.IsDirectory;
                case FileSearchTypeFilter.Launchable:
                    return !item.IsDirectory && EverythingSearchResultItem.IsLaunchableExtension(extension);
                case FileSearchTypeFilter.Script:
                    return !item.IsDirectory && EverythingSearchResultItem.IsScriptExtension(extension);
                case FileSearchTypeFilter.Log:
                    return !item.IsDirectory && EverythingSearchResultItem.IsLogExtension(extension);
                case FileSearchTypeFilter.Config:
                    return !item.IsDirectory && EverythingSearchResultItem.IsConfigExtension(extension);
                default:
                    return true;
            }
        }

        private bool MatchesCurrentQueryAndType(string lowerName, string fullPath, bool isDirectory)
        {
            var parsed = ParsePathScope(_activeKeyword);
            if (string.IsNullOrWhiteSpace(parsed.searchTerm))
                return false;

            if (!MatchesCurrentKeyword(lowerName, parsed.searchTerm))
                return false;

            if (!MatchesPathScope(fullPath, parsed.pathPrefix))
                return false;

            return MatchesCurrentTypeFilter(fullPath, isDirectory);
        }

        private bool MatchesCurrentTypeFilter(string fullPath, bool isDirectory)
        {
            var extension = isDirectory ? string.Empty : (Path.GetExtension(fullPath ?? string.Empty) ?? string.Empty).ToLowerInvariant();
            switch (_currentTypeFilter)
            {
                case FileSearchTypeFilter.All:
                    return true;
                case FileSearchTypeFilter.Folder:
                    return isDirectory;
                case FileSearchTypeFilter.Launchable:
                    return !isDirectory && EverythingSearchResultItem.IsLaunchableExtension(extension);
                case FileSearchTypeFilter.Script:
                    return !isDirectory && EverythingSearchResultItem.IsScriptExtension(extension);
                case FileSearchTypeFilter.Log:
                    return !isDirectory && EverythingSearchResultItem.IsLogExtension(extension);
                case FileSearchTypeFilter.Config:
                    return !isDirectory && EverythingSearchResultItem.IsConfigExtension(extension);
                default:
                    return true;
            }
        }

        private bool MatchesCurrentKeyword(string lowerName, string keyword)
        {
            var normalizedName = (lowerName ?? string.Empty).ToLowerInvariant();
            var normalizedKeyword = (keyword ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedName) || string.IsNullOrWhiteSpace(normalizedKeyword))
                return false;

            if (normalizedKeyword.StartsWith("^", StringComparison.Ordinal))
                return normalizedName.StartsWith(normalizedKeyword.Substring(1).ToLowerInvariant(), StringComparison.Ordinal);

            if (normalizedKeyword.EndsWith("$", StringComparison.Ordinal))
                return normalizedName.EndsWith(normalizedKeyword.Substring(0, normalizedKeyword.Length - 1).ToLowerInvariant(), StringComparison.Ordinal);

            var needsRegex = (normalizedKeyword.Length >= 3 && normalizedKeyword.StartsWith("/", StringComparison.Ordinal) && normalizedKeyword.EndsWith("/", StringComparison.Ordinal))
                             || normalizedKeyword.IndexOfAny(new[] { '*', '?' }) >= 0;
            if (!needsRegex)
                return normalizedName.Contains(normalizedKeyword.ToLowerInvariant());

            if (_cachedKeyword != normalizedKeyword || _cachedRegex == null)
            {
                _cachedKeyword = normalizedKeyword;
                try
                {
                    var pattern = normalizedKeyword.Length >= 3 && normalizedKeyword.StartsWith("/", StringComparison.Ordinal) && normalizedKeyword.EndsWith("/", StringComparison.Ordinal)
                        ? normalizedKeyword.Substring(1, normalizedKeyword.Length - 2)
                        : WildcardToRegex(normalizedKeyword);
                    _cachedRegex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                }
                catch
                {
                    _cachedRegex = null;
                }
            }

            if (_cachedRegex == null)
                return false;

            try
            {
                return _cachedRegex.IsMatch(lowerName ?? string.Empty);
            }
            catch
            {
                return false;
            }
        }

        private static bool MatchesPathScope(string fullPath, string pathPrefix)
        {
            if (string.IsNullOrWhiteSpace(pathPrefix))
                return true;

            if (string.IsNullOrWhiteSpace(fullPath))
                return false;

            var normalizedPrefix = pathPrefix.EndsWith("\\", StringComparison.Ordinal)
                ? pathPrefix
                : pathPrefix + "\\";
            return fullPath.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(fullPath, pathPrefix.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private static (string pathPrefix, string searchTerm) ParsePathScope(string keyword)
        {
            var kw = (keyword ?? string.Empty).Trim();
            var spaceIndex = kw.IndexOf(' ');
            if (spaceIndex <= 0)
                return (null, kw);

            var candidate = kw.Substring(0, spaceIndex);
            var isValidPath = (candidate.Length >= 3
                               && char.IsLetter(candidate[0])
                               && candidate[1] == ':'
                               && candidate[2] == '\\')
                           || candidate.StartsWith("\\", StringComparison.Ordinal);
            if (!isValidPath)
                return (null, kw);

            var term = kw.Substring(spaceIndex).TrimStart();
            return (candidate, term);
        }

        private static string WildcardToRegex(string pattern)
        {
            var escaped = Regex.Escape(pattern ?? string.Empty)
                .Replace("\\*", ".*")
                .Replace("\\?", ".");
            return "^" + escaped + "$";
        }

        private bool TryAddDisplayedResult(string fullPath, bool isDirectory)
        {
            var normalizedPath = fullPath ?? string.Empty;
            if (!_displayedPathSet.Add(normalizedPath))
                return false;

            var item = new EverythingSearchResultItem(fullPath, isDirectory);
            _allLoadedResults.Add(item);
            _displayedResults.Add(item);
            return true;
        }

        private bool RemoveDisplayedResult(string fullPath, string lowerName)
        {
            if (!string.IsNullOrWhiteSpace(fullPath) && _displayedPathSet.Remove(fullPath))
            {
                for (var i = _displayedResults.Count - 1; i >= 0; i--)
                {
                    if (!string.Equals(_displayedResults[i].FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    _allLoadedResults.Remove(_displayedResults[i]);
                    _displayedResults.RemoveAt(i);
                    return true;
                }
            }

            if (string.IsNullOrWhiteSpace(lowerName))
                return false;

            for (var i = _displayedResults.Count - 1; i >= 0; i--)
            {
                if (!string.Equals((_displayedResults[i].FileName ?? string.Empty).ToLowerInvariant(), lowerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                _displayedPathSet.Remove(_displayedResults[i].FullPath ?? string.Empty);
                _allLoadedResults.Remove(_displayedResults[i]);
                _displayedResults.RemoveAt(i);
                return true;
            }

            return false;
        }

        private string BuildEffectiveKeyword(string rawKeyword)
        {
            var keyword = (rawKeyword ?? string.Empty).Trim();
            var scopePath = GetSelectedScopePath();
            if (string.IsNullOrWhiteSpace(scopePath))
                return keyword;

            if (string.IsNullOrWhiteSpace(keyword))
                return string.Empty;

            return scopePath + " " + keyword;
        }

        private string GetVisibleQueryText()
        {
            var keyword = (SearchBox.Text ?? string.Empty).Trim();
            var scopePath = GetSelectedScopePath();
            if (string.IsNullOrWhiteSpace(scopePath))
                return keyword;

            if (string.IsNullOrWhiteSpace(keyword))
                return scopePath;

            return keyword + " @ " + scopePath;
        }

        private void PopulateQueryInputs(string combinedQuery)
        {
            var parsed = ParsePathScope(combinedQuery);
            SelectScopeOption(parsed.pathPrefix);
            SearchBox.Text = parsed.searchTerm ?? combinedQuery ?? string.Empty;
        }

        private void ApplyCurrentSort()
        {
            if (_displayedResults.Count <= 1)
            {
                if (ResultsGrid.SelectedItem == null && _displayedResults.Count > 0)
                    ResultsGrid.SelectedItem = _displayedResults[0];
                return;
            }

            IEnumerable<EverythingSearchResultItem> orderedItems = _allLoadedResults;
            switch (_currentSortKey)
            {
                case "name":
                    orderedItems = _allLoadedResults
                        .OrderBy(item => item.FileName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(item => item.DirectoryPath ?? string.Empty, StringComparer.OrdinalIgnoreCase);
                    break;
                case "path":
                    orderedItems = _allLoadedResults
                        .OrderBy(item => item.DirectoryPath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(item => item.FileName ?? string.Empty, StringComparer.OrdinalIgnoreCase);
                    break;
                case "type":
                    orderedItems = _allLoadedResults
                        .OrderBy(item => item.TypeText ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(item => item.FileName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(item => item.DirectoryPath ?? string.Empty, StringComparer.OrdinalIgnoreCase);
                    break;
            }

            var selectedPath = (ResultsGrid.SelectedItem as EverythingSearchResultItem)?.FullPath;
            var orderedList = orderedItems.ToList();
            _displayedResults.Clear();
            for (var i = 0; i < orderedList.Count; i++)
                _displayedResults.Add(orderedList[i]);

            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                var nextSelection = _displayedResults.FirstOrDefault(item => string.Equals(item.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
                if (nextSelection != null)
                {
                    ResultsGrid.SelectedItem = nextSelection;
                    return;
                }
            }

            if (ResultsGrid.SelectedItem == null && _displayedResults.Count > 0)
                ResultsGrid.SelectedItem = _displayedResults[0];
        }

        private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressControlEvents)
                return;

            var option = SortComboBox.SelectedItem as ComboOption;
            _currentSortKey = option == null ? "default" : option.Key;
            ApplyCurrentSort();
            UpdateSummaryStatus();
            UpdateEmptyState();
            SaveWindowState();
        }

        private static FileSearchTypeFilter ResolveTypeFilter(string tag)
        {
            switch (tag)
            {
                case "Launchable":
                    return FileSearchTypeFilter.Launchable;
                case "Folder":
                    return FileSearchTypeFilter.Folder;
                case "Script":
                    return FileSearchTypeFilter.Script;
                case "Log":
                    return FileSearchTypeFilter.Log;
                case "Config":
                    return FileSearchTypeFilter.Config;
                default:
                    return FileSearchTypeFilter.All;
            }
        }

        private static string GetTypeFilterText(FileSearchTypeFilter filter)
        {
            switch (filter)
            {
                case FileSearchTypeFilter.Launchable:
                    return "可启动文件";
                case FileSearchTypeFilter.Folder:
                    return "文件夹";
                case FileSearchTypeFilter.Script:
                    return "脚本";
                case FileSearchTypeFilter.Log:
                    return "日志";
                case FileSearchTypeFilter.Config:
                    return "配置";
                default:
                    return "全部";
            }
        }

        private void RebindDisplayedResults() { }
    }
}
