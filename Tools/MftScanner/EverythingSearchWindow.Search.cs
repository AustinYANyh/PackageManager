using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private sealed class SearchLoadOutcome
        {
            public List<EverythingSearchResultItem> DisplayItems { get; set; } = new List<EverythingSearchResultItem>();
            public int TotalMatchedCount { get; set; }
            public int LoadedRawResultCount { get; set; }
            public bool HasMoreSearchResults { get; set; }
            public long HostSearchMs { get; set; }
            public long ProjectionMs { get; set; }
            public long SortMs { get; set; }
        }

        private async Task StartIndexingAsync(bool forceRescan)
        {
            CancelToken(ref _indexCts);
            _indexCts = new CancellationTokenSource();
            var ct = _indexCts.Token;
            _indexReady = false;
            IndexingProgress.Visibility = Visibility.Visible;
            IndexStateBadgeText.Text = forceRescan ? "正在重建共享索引" : "正在连接共享索引";
            StatusText.Text = forceRescan ? "正在请求共享索引重建..." : "正在连接共享索引宿主...";
            EmptyStateTitleText.Text = forceRescan ? "正在重建共享索引" : "正在连接共享索引";
            EmptyStateDescriptionText.Text = "共享索引就绪后即可直接搜索，不再回退到本地索引。";
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
            if (_suppressControlEvents || _isKeyboardScopeSelectionActive || _suppressScopeSelectionSearch)
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
            var currentSearchCts = new CancellationTokenSource();
            _searchCts = currentSearchCts;
            var ct = currentSearchCts.Token;

            _activeKeyword = BuildEffectiveKeyword(keyword);
            _isLoadingMore = false;
            _hasMoreSearchResults = false;
            _cachedKeyword = null;
            _cachedRegex = null;

            if (!_indexReady)
            {
                StatusText.Text = "共享索引正在准备，请稍候...";
                UpdateEmptyState();
                return;
            }

            if (string.IsNullOrWhiteSpace(_activeKeyword))
            {
                _isSearchInProgress = false;
                ClearDisplayedResults();
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
                var uiTotalStopwatch = Stopwatch.StartNew();
                var loadOutcome = await LoadInitialSearchResultsAsync(SearchBatchSize, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested || loadOutcome == null)
                    return;

                _totalMatchedCount = loadOutcome.TotalMatchedCount;
                _loadedRawResultCount = loadOutcome.LoadedRawResultCount;
                _hasMoreSearchResults = loadOutcome.HasMoreSearchResults;
                var bindStopwatch = Stopwatch.StartNew();
                PublishLoadedResults(loadOutcome.DisplayItems, preserveSelectionPath: null);
                bindStopwatch.Stop();

                uiTotalStopwatch.Stop();
                LogUiSearchBreakdown(loadOutcome, bindStopwatch.ElapsedMilliseconds, uiTotalStopwatch.ElapsedMilliseconds);

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
                if (ReferenceEquals(_searchCts, currentSearchCts))
                {
                    _isSearchInProgress = false;
                    IndexingProgress.Visibility = Visibility.Collapsed;
                    UpdateSummaryStatus();
                    UpdateEmptyState();
                }

                if (_pendingRefresh && !string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    _pendingRefresh = false;
                    _ = ApplyFilterAsync(SearchBox.Text, false);
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
                RequestRefreshCurrentQuery(force: true);

            UpdateSummaryStatus();
        }

        private void ApplyIndexChange(IndexChangedEventArgs e)
        {
            if (e == null)
                return;

            ApplyIndexChangesBatch(new[] { e });
        }

        private void ApplyIndexChangesBatch(IReadOnlyList<IndexChangedEventArgs> changes)
        {
            if (!_indexReady || string.IsNullOrWhiteSpace(_activeKeyword) || changes == null || changes.Count == 0)
                return;

            var allLoadedBeforeChange = !_hasMoreSearchResults;
            var preferredSelectionPath = (ResultsGrid.SelectedItem as EverythingSearchResultItem)?.FullPath;
            var resultsChanged = false;

            for (var i = 0; i < changes.Count; i++)
            {
                if (ApplyIndexChangeCore(changes[i], allLoadedBeforeChange, ref preferredSelectionPath))
                    resultsChanged = true;
            }

            if (resultsChanged)
            {
                ApplyCurrentSort(preferredSelectionPath, false);
                _loadedResultCount = _displayedResults.Count;
            }

            UpdateSummaryStatus();
            UpdateEmptyState();
        }

        private bool ApplyIndexChangeCore(IndexChangedEventArgs e, bool allLoadedBeforeChange, ref string preferredSelectionPath)
        {
            if (e == null)
                return false;

            var oldMatches = false;
            var newMatches = false;
            var resultsChanged = false;

            switch (e.Type)
            {
                case IndexChangeType.Deleted:
                    oldMatches = MatchesCurrentQueryAndType(e.LowerName, e.FullPath, e.IsDirectory);
                    if (oldMatches && _totalMatchedCount > 0)
                        _totalMatchedCount--;
                    resultsChanged = RemoveDisplayedResult(e.FullPath, e.LowerName);
                    if (!string.IsNullOrWhiteSpace(preferredSelectionPath)
                        && string.Equals(preferredSelectionPath, e.FullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        preferredSelectionPath = null;
                    }
                    break;

                case IndexChangeType.Created:
                    newMatches = MatchesCurrentQueryAndType(e.LowerName, e.FullPath, e.IsDirectory);
                    if (newMatches)
                    {
                        _totalMatchedCount++;
                        if (allLoadedBeforeChange)
                            resultsChanged = TryAddDisplayedResult(e.FullPath, e.IsDirectory);
                    }
                    break;

                case IndexChangeType.Renamed:
                    oldMatches = MatchesCurrentQueryAndType(e.LowerName, e.OldFullPath, e.IsDirectory);
                    newMatches = MatchesCurrentQueryAndType(e.NewLowerName, e.FullPath, e.IsDirectory);

                    if (oldMatches && _totalMatchedCount > 0)
                        _totalMatchedCount--;
                    if (oldMatches || !string.IsNullOrWhiteSpace(e.OldFullPath))
                        resultsChanged = RemoveDisplayedResult(e.OldFullPath, e.LowerName) || resultsChanged;

                    if (newMatches)
                    {
                        _totalMatchedCount++;
                        if (allLoadedBeforeChange)
                            resultsChanged = TryAddDisplayedResult(e.FullPath, e.IsDirectory) || resultsChanged;
                    }

                    if (!string.IsNullOrWhiteSpace(preferredSelectionPath)
                        && string.Equals(preferredSelectionPath, e.OldFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        preferredSelectionPath = newMatches ? e.FullPath : null;
                    }
                    break;
            }

            return resultsChanged;
        }

        private void RequestRefreshCurrentQuery(bool force = false)
        {
            if (string.IsNullOrWhiteSpace(_activeKeyword))
                return;

            if (!ShouldAutoRefreshCurrentQuery(force))
            {
                _pendingRefresh = false;
                _forcePendingRefresh = false;
                return;
            }

            _pendingRefresh = true;
            _forcePendingRefresh = force;
            _liveRefreshTimer.Stop();
            _liveRefreshTimer.Start();
        }

        private void LiveRefreshTimer_Tick(object sender, EventArgs e)
        {
            _liveRefreshTimer.Stop();
            if (_pendingRefresh && !_isSearchInProgress && !string.IsNullOrWhiteSpace(SearchBox.Text) && ShouldAutoRefreshCurrentQuery(_forcePendingRefresh))
            {
                _pendingRefresh = false;
                _forcePendingRefresh = false;
                _ = ApplyFilterAsync(SearchBox.Text, false);
            }
        }

        private bool ShouldAutoRefreshCurrentQuery(bool force = false)
        {
            return force || (_displayedResults.Count == 0 && ResultsGrid.SelectedItem == null);
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

            ApplyTypeFilter(ResolveTypeFilter(clickedButton.Tag as string), restoreSearchInput: false);
        }

        private void ApplyTypeFilter(FileSearchTypeFilter filter, bool restoreSearchInput)
        {
            var changed = _currentTypeFilter != filter;

            _currentTypeFilter = filter;
            UpdateTypeFilterButtonStates();
            UpdateTypeFilterKeyboardVisuals();
            QuerySummaryText.Text = "当前类型：" + GetTypeFilterText(_currentTypeFilter) + "；排序仅做内存重排，路径限定复用路径前缀查询";
            UpdateSummaryStatus();
            UpdateEmptyState();

            if (changed && !string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                _ = ApplyFilterAsync(SearchBox.Text, false);
            }
            else if (changed || string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                StatusText.Text = "已切换类型过滤：" + GetTypeFilterText(_currentTypeFilter);
            }

            if (restoreSearchInput)
                RestoreSearchBoxInputState(preserveSelection: true);
        }

        private async Task<SearchLoadOutcome> LoadInitialSearchResultsAsync(int desiredCount, CancellationToken ct)
        {
            var queryStopwatch = Stopwatch.StartNew();
            SearchQueryResult result;
            if (_currentTypeFilter == FileSearchTypeFilter.All)
            {
                result = await _filter.QueryAsync(_activeKeyword, Math.Max(desiredCount, SearchBatchSize), 0, ct).ConfigureAwait(true);
            }
            else
            {
                result = await _indexService.SearchAsync(
                    _activeKeyword,
                    Math.Max(desiredCount, SearchBatchSize),
                    0,
                    MapCurrentTypeFilter(),
                    null,
                    ct).ConfigureAwait(true);
            }

            queryStopwatch.Stop();
            if (ct.IsCancellationRequested)
            {
                return null;
            }

            return BuildSearchLoadOutcome(result, 0, queryStopwatch.ElapsedMilliseconds, null);
        }

        private async Task<bool> LoadSearchResultsAsync(int desiredCount, bool isNewSearch, CancellationToken ct)
        {
            if (isNewSearch)
            {
                var uiTotalStopwatch = Stopwatch.StartNew();
                var initialLoadOutcome = await LoadInitialSearchResultsAsync(desiredCount, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested || initialLoadOutcome == null)
                {
                    return false;
                }

                var bindStopwatch = Stopwatch.StartNew();
                PublishLoadedResults(initialLoadOutcome.DisplayItems, preserveSelectionPath: null);
                bindStopwatch.Stop();
                uiTotalStopwatch.Stop();
                LogUiSearchBreakdown(initialLoadOutcome, bindStopwatch.ElapsedMilliseconds, uiTotalStopwatch.ElapsedMilliseconds);
                return true;
            }

            var queryStopwatch = Stopwatch.StartNew();
            var result = _currentTypeFilter == FileSearchTypeFilter.All
                ? await _indexService.SearchAsync(_activeKeyword, SearchBatchSize, _loadedRawResultCount, null, ct).ConfigureAwait(true)
                : await _indexService.SearchAsync(
                    _activeKeyword,
                    Math.Max(desiredCount, SearchBatchSize),
                    _loadedRawResultCount,
                    MapCurrentTypeFilter(),
                    null,
                    ct).ConfigureAwait(true);
            queryStopwatch.Stop();
            if (ct.IsCancellationRequested)
            {
                return false;
            }

            var existingPaths = new HashSet<string>(_displayedPathSet, StringComparer.OrdinalIgnoreCase);
            var loadOutcome = BuildSearchLoadOutcome(result, _loadedRawResultCount, queryStopwatch.ElapsedMilliseconds, existingPaths);
            _totalMatchedCount = loadOutcome.TotalMatchedCount;
            _loadedRawResultCount = loadOutcome.LoadedRawResultCount;
            _hasMoreSearchResults = loadOutcome.HasMoreSearchResults;

            if (loadOutcome.DisplayItems.Count == 0)
            {
                return true;
            }

            var combinedItems = new List<EverythingSearchResultItem>(_allLoadedResults.Count + loadOutcome.DisplayItems.Count);
            combinedItems.AddRange(_allLoadedResults);
            combinedItems.AddRange(loadOutcome.DisplayItems);
            SortResultsInPlace(combinedItems);
            PublishLoadedResults(combinedItems, (ResultsGrid.SelectedItem as EverythingSearchResultItem)?.FullPath);
            return true;
        }

        private SearchLoadOutcome BuildSearchLoadOutcome(SearchQueryResult result, int currentRawOffset, long queryElapsedMs, HashSet<string> existingPaths)
        {
            var outcome = new SearchLoadOutcome
            {
                TotalMatchedCount = result == null ? 0 : result.TotalMatchedCount,
                HostSearchMs = result == null ? queryElapsedMs : Math.Max(result.HostSearchMs, 0),
                LoadedRawResultCount = currentRawOffset + (result?.Results == null ? 0 : result.Results.Count)
            };

            var projectionStopwatch = Stopwatch.StartNew();
            var pathSet = existingPaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (result != null && result.Results != null)
            {
                for (var i = 0; i < result.Results.Count; i++)
                {
                    var source = result.Results[i];
                    if (source == null || string.IsNullOrWhiteSpace(source.FullPath) || !pathSet.Add(source.FullPath))
                    {
                        continue;
                    }

                    outcome.DisplayItems.Add(new EverythingSearchResultItem(source));
                }
            }

            projectionStopwatch.Stop();
            outcome.ProjectionMs = projectionStopwatch.ElapsedMilliseconds;

            var sortStopwatch = Stopwatch.StartNew();
            SortResultsInPlace(outcome.DisplayItems);
            sortStopwatch.Stop();
            outcome.SortMs = sortStopwatch.ElapsedMilliseconds;

            if (_currentTypeFilter == FileSearchTypeFilter.All)
            {
                outcome.HasMoreSearchResults = outcome.LoadedRawResultCount < outcome.TotalMatchedCount;
            }
            else
            {
                outcome.HasMoreSearchResults = result != null && result.IsTruncated;
            }

            return outcome;
        }

        private void PublishLoadedResults(IList<EverythingSearchResultItem> items, string preserveSelectionPath)
        {
            var preferredSelectionPath = preserveSelectionPath;
            _allLoadedResults = items == null ? new List<EverythingSearchResultItem>() : new List<EverythingSearchResultItem>(items);
            _displayedPathSet.Clear();
            for (var i = 0; i < _allLoadedResults.Count; i++)
            {
                _displayedPathSet.Add(_allLoadedResults[i].FullPath ?? string.Empty);
            }

            _displayedResults.ReplaceAll(_allLoadedResults);
            _loadedResultCount = _allLoadedResults.Count;
            RefreshGridColumns();
            RestorePreferredSelection(preferredSelectionPath, forceCurrentCell: true);
            UpdateActionButtons();
        }

        private void ClearDisplayedResults()
        {
            _displayedPathSet.Clear();
            _allLoadedResults.Clear();
            _displayedResults.ReplaceAll(Array.Empty<EverythingSearchResultItem>());
            _loadedResultCount = 0;
            _loadedRawResultCount = 0;
            _totalMatchedCount = 0;
            _hasMoreSearchResults = false;
        }

        private void SortResultsInPlace(List<EverythingSearchResultItem> items)
        {
            if (items == null || items.Count <= 1)
            {
                return;
            }

            Comparison<EverythingSearchResultItem> comparison = null;
            switch (_currentSortKey)
            {
                case "name":
                    comparison = delegate(EverythingSearchResultItem left, EverythingSearchResultItem right)
                    {
                        var result = StringComparer.OrdinalIgnoreCase.Compare(left == null ? string.Empty : left.FileName ?? string.Empty, right == null ? string.Empty : right.FileName ?? string.Empty);
                        if (result != 0)
                        {
                            return result;
                        }

                        return StringComparer.OrdinalIgnoreCase.Compare(left == null ? string.Empty : left.DirectoryPath ?? string.Empty, right == null ? string.Empty : right.DirectoryPath ?? string.Empty);
                    };
                    break;
                case "path":
                    comparison = delegate(EverythingSearchResultItem left, EverythingSearchResultItem right)
                    {
                        var result = StringComparer.OrdinalIgnoreCase.Compare(left == null ? string.Empty : left.DirectoryPath ?? string.Empty, right == null ? string.Empty : right.DirectoryPath ?? string.Empty);
                        if (result != 0)
                        {
                            return result;
                        }

                        return StringComparer.OrdinalIgnoreCase.Compare(left == null ? string.Empty : left.FileName ?? string.Empty, right == null ? string.Empty : right.FileName ?? string.Empty);
                    };
                    break;
                case "type":
                    comparison = delegate(EverythingSearchResultItem left, EverythingSearchResultItem right)
                    {
                        var result = StringComparer.OrdinalIgnoreCase.Compare(left == null ? string.Empty : left.TypeText ?? string.Empty, right == null ? string.Empty : right.TypeText ?? string.Empty);
                        if (result != 0)
                        {
                            return result;
                        }

                        result = StringComparer.OrdinalIgnoreCase.Compare(left == null ? string.Empty : left.FileName ?? string.Empty, right == null ? string.Empty : right.FileName ?? string.Empty);
                        if (result != 0)
                        {
                            return result;
                        }

                        return StringComparer.OrdinalIgnoreCase.Compare(left == null ? string.Empty : left.DirectoryPath ?? string.Empty, right == null ? string.Empty : right.DirectoryPath ?? string.Empty);
                    };
                    break;
            }

            if (comparison != null)
            {
                items.Sort(comparison);
            }
        }

        private void LogUiSearchBreakdown(SearchLoadOutcome loadOutcome, long bindMs, long totalUiSearchMs)
        {
            if (loadOutcome == null)
            {
                return;
            }

            var hostSearchMs = Math.Max(loadOutcome.HostSearchMs, 0);
            var projectionMs = Math.Max(loadOutcome.ProjectionMs, 0);
            var sortMs = Math.Max(loadOutcome.SortMs, 0);
            var ipcMs = Math.Max(0, totalUiSearchMs - hostSearchMs - projectionMs - sortMs - bindMs);
            Services.LoggingService.LogIndexPerf("UI",
                $"[CTRLE UI SEARCH] keyword={Services.LoggingService.FormatPerfValue(_activeKeyword)} filter={_currentTypeFilter} " +
                $"hostSearchMs={hostSearchMs} ipcMs={ipcMs} projectionMs={projectionMs} sortMs={sortMs} bindMs={bindMs} totalUiSearchMs={totalUiSearchMs} " +
                $"matched={_totalMatchedCount} displayed={_loadedResultCount}");
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

        private SearchTypeFilter MapCurrentTypeFilter()
        {
            switch (_currentTypeFilter)
            {
                case FileSearchTypeFilter.Launchable:
                    return SearchTypeFilter.Launchable;
                case FileSearchTypeFilter.Folder:
                    return SearchTypeFilter.Folder;
                case FileSearchTypeFilter.Script:
                    return SearchTypeFilter.Script;
                case FileSearchTypeFilter.Log:
                    return SearchTypeFilter.Log;
                case FileSearchTypeFilter.Config:
                    return SearchTypeFilter.Config;
                default:
                    return SearchTypeFilter.All;
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

            _allLoadedResults.Add(new EverythingSearchResultItem(fullPath, isDirectory));
            return true;
        }

        private bool RemoveDisplayedResult(string fullPath, string lowerName)
        {
            if (!string.IsNullOrWhiteSpace(fullPath) && _displayedPathSet.Remove(fullPath))
            {
                for (var i = _allLoadedResults.Count - 1; i >= 0; i--)
                {
                    if (!string.Equals(_allLoadedResults[i].FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    _allLoadedResults.RemoveAt(i);
                    return true;
                }
            }

            if (string.IsNullOrWhiteSpace(lowerName))
                return false;

            for (var i = _allLoadedResults.Count - 1; i >= 0; i--)
            {
                if (!string.Equals((_allLoadedResults[i].FileName ?? string.Empty).ToLowerInvariant(), lowerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                _displayedPathSet.Remove(_allLoadedResults[i].FullPath ?? string.Empty);
                _allLoadedResults.RemoveAt(i);
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
            ApplyCurrentSort((ResultsGrid.SelectedItem as EverythingSearchResultItem)?.FullPath, false);
        }

        private void ApplyCurrentSort(bool forceRebind)
        {
            ApplyCurrentSort((ResultsGrid.SelectedItem as EverythingSearchResultItem)?.FullPath, forceRebind);
        }

        private void ApplyCurrentSort(string preferredSelectionPath, bool forceRebind)
        {
            if (_allLoadedResults.Count == 0)
            {
                if (ResultsGrid.SelectedItem != null)
                    ResultsGrid.SelectedItem = null;
                if (_displayedResults.Count > 0)
                    _displayedResults.ReplaceAll(Array.Empty<EverythingSearchResultItem>());
                return;
            }

            IList<EverythingSearchResultItem> orderedItems;
            if (_currentSortKey == "default")
            {
                orderedItems = _allLoadedResults;
            }
            else
            {
                var sortedItems = new List<EverythingSearchResultItem>(_allLoadedResults);
                SortResultsInPlace(sortedItems);
                orderedItems = sortedItems;
            }

            if (forceRebind || !ReferenceEquals(orderedItems, _displayedResults))
            {
                RebindDisplayedResults(orderedItems);
            }

            RestorePreferredSelection(preferredSelectionPath, true);
        }

        private void RestorePreferredSelection(string preferredSelectionPath, bool forceCurrentCell)
        {
            if (!string.IsNullOrWhiteSpace(preferredSelectionPath))
            {
                var nextSelection = _displayedResults.FirstOrDefault(item => string.Equals(item.FullPath, preferredSelectionPath, StringComparison.OrdinalIgnoreCase));
                if (nextSelection != null)
                {
                    var currentSelection = ResultsGrid.SelectedItem as EverythingSearchResultItem;
                    if (!ReferenceEquals(currentSelection, nextSelection))
                        ResultsGrid.SelectedItem = nextSelection;
                    if (forceCurrentCell)
                        EnsureCurrentCellSelection(nextSelection);
                    return;
                }
            }

            if (ResultsGrid.SelectedItem == null && _displayedResults.Count > 0)
            {
                ResultsGrid.SelectedItem = _displayedResults[0];
                EnsureCurrentCellSelection(_displayedResults[0]);
                return;
            }

            if (forceCurrentCell)
                EnsureCurrentCellSelection(ResultsGrid.SelectedItem as EverythingSearchResultItem);
        }

        private void EnsureCurrentCellSelection(EverythingSearchResultItem item)
        {
            if (item == null || ResultsGrid.Columns == null || ResultsGrid.Columns.Count == 0)
                return;

            var firstVisibleColumn = ResultsGrid.Columns.FirstOrDefault(column => column.Visibility == Visibility.Visible);
            if (firstVisibleColumn == null)
                return;

            ResultsGrid.CurrentCell = new DataGridCellInfo(item, firstVisibleColumn);
        }

        private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressControlEvents)
                return;

            var option = SortComboBox.SelectedItem as ComboOption;
            _currentSortKey = option == null ? "default" : option.Key;
            ApplyCurrentSort(true);
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

        private void RebindDisplayedResults(IList<EverythingSearchResultItem> orderedItems)
        {
            if (orderedItems == null)
                return;

            if (_displayedResults.Count == orderedItems.Count)
            {
                var isSameOrder = true;
                for (var i = 0; i < orderedItems.Count; i++)
                {
                    if (ReferenceEquals(_displayedResults[i], orderedItems[i]))
                        continue;

                    isSameOrder = false;
                    break;
                }

                if (isSameOrder)
                    return;
            }

            _displayedResults.ReplaceAll(orderedItems);
        }
    }
}

