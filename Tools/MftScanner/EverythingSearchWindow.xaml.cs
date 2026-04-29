using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MftScanner
{
    public partial class EverythingSearchWindow : Window
    {
        private static readonly SolidColorBrush TypeFilterKeyboardHighlightBackground = new SolidColorBrush(Color.FromRgb(0xF5, 0xEB, 0xC8));
        private static readonly SolidColorBrush TypeFilterKeyboardHighlightBorderBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xBC, 0x71));
        private static readonly SolidColorBrush TypeFilterKeyboardHighlightForeground = new SolidColorBrush(Color.FromRgb(0x8B, 0x6A, 0x1B));

        private const int SearchBatchSize = 500;
        private const int DisplayBatchSize = 200;
        private const double LoadMoreThreshold = 24d;
        private const int MaxRecentSearches = 12;

        private readonly ISharedIndexService _indexService;
        private readonly IncrementalFilter _filter;
        private readonly ReplaceableObservableCollection<EverythingSearchResultItem> _displayedResults = new ReplaceableObservableCollection<EverythingSearchResultItem>();
        private readonly ObservableCollection<SearchHistoryEntry> _recentSearches = new ObservableCollection<SearchHistoryEntry>();
        private readonly DispatcherTimer _debounceTimer;
        private readonly DispatcherTimer _liveRefreshTimer;
        private readonly DispatcherTimer _indexChangeFlushTimer;
        private readonly SearchWindowStateStore _stateStore = new SearchWindowStateStore();
        private readonly Dictionary<string, FileMetadata> _metadataCache = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _displayedPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _locallyDeletedPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<ComboOption> _scopeOptions = new ObservableCollection<ComboOption>();
        private readonly ConcurrentQueue<IndexChangedEventArgs> _pendingIndexChanges = new ConcurrentQueue<IndexChangedEventArgs>();

        private CancellationTokenSource _indexCts;
        private CancellationTokenSource _searchCts;
        private ScrollViewer _resultsScrollViewer;
        private List<EverythingSearchResultItem> _allLoadedResults = new List<EverythingSearchResultItem>();
        private string _activeKeyword = string.Empty;
        private int _searchGeneration;
        private int _totalMatchedCount;
        private int _loadedResultCount;
        private int _loadedRawResultCount;
        private bool _isSearchInProgress;
        private bool _isLoadingMore;
        private bool _hasMoreSearchResults;
        private bool _indexReady;
        private int _indexedCount;
        private bool _hideInsteadOfClose = true;
        private bool _allowProcessExit;
        private bool _pendingRefresh;
        private bool _forcePendingRefresh;
        private string _latestIndexStatusMessage = string.Empty;
        private ContainsBucketStatus _latestContainsBucketStatus = ContainsBucketStatus.Empty;
        private string _cachedKeyword;
        private Regex _cachedRegex;
        private bool _suppressControlEvents;
        private bool _isApplyingPendingIndexChanges;
        private FileSearchTypeFilter _currentTypeFilter = FileSearchTypeFilter.All;
        private string _currentSortKey = "default";
        private bool _isKeyboardScopeSelectionActive;
        private bool _suppressScopeSelectionSearch;
        private bool _skipScopeDropDownClosedRestore;
        private string _scopeSelectionOriginalValue = string.Empty;
        private int _scopeSearchSelectionStart;
        private int _scopeSearchSelectionLength;
        private int _scopeSearchCaretIndex;
        private bool _isTypeFilterKeyboardMode;
        private int _typeFilterKeyboardIndex;
        private FileSearchTypeFilter _typeFilterKeyboardOriginalType = FileSearchTypeFilter.All;

        public EverythingSearchWindow()
            : this(SharedIndexServiceFactory.Create("CtrlE.SearchUi"))
        {
        }

        public EverythingSearchWindow(ISharedIndexService indexService)
        {
            _indexService = indexService ?? throw new ArgumentNullException(nameof(indexService));
            InitializeComponent();
            ResultsGrid.ItemsSource = _displayedResults;
            RecentSearchList.ItemsSource = _recentSearches;
            _filter = new IncrementalFilter(_indexService);
            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _debounceTimer.Tick += DebounceTimer_Tick;
            _liveRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _liveRefreshTimer.Tick += LiveRefreshTimer_Tick;
            _indexChangeFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _indexChangeFlushTimer.Tick += IndexChangeFlushTimer_Tick;

            _indexService.IndexChanged += delegate(object sender, IndexChangedEventArgs e)
            {
                Dispatcher.BeginInvoke(new Action(delegate { EnqueueIndexChange(e); }), DispatcherPriority.Background);
            };
            _indexService.IndexStatusChanged += delegate(object sender, IndexStatusChangedEventArgs e)
            {
                Dispatcher.BeginInvoke(new Action(delegate { ApplyIndexStatusChange(e); }));
            };

            ConfigureStaticControls();
            LoadWindowState();
            UpdateActionButtons();
            UpdateSelectedItemDetails(null);
            UpdateEmptyState();
            UpdateSummaryStatus();
        }

        public void FocusSearchBoxAndSelectAll()
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        private void BeginTypeFilterKeyboardMode()
        {
            if (_isTypeFilterKeyboardMode)
                return;

            CaptureSearchBoxInputState();
            _typeFilterKeyboardOriginalType = _currentTypeFilter;
            _typeFilterKeyboardIndex = GetTypeFilterIndex(_currentTypeFilter);
            _isTypeFilterKeyboardMode = true;
            UpdateTypeFilterKeyboardVisuals();
            StatusText.Text = "类型筛选模式：左右选择，Enter确认，Esc取消";
        }

        private void CommitTypeFilterKeyboardMode()
        {
            if (!_isTypeFilterKeyboardMode)
                return;

            var filter = GetTypeFilterByIndex(_typeFilterKeyboardIndex);
            _isTypeFilterKeyboardMode = false;
            UpdateTypeFilterKeyboardVisuals();
            ApplyTypeFilter(filter, restoreSearchInput: true);
        }

        private void CancelTypeFilterKeyboardMode(bool restoreSearchInput)
        {
            if (!_isTypeFilterKeyboardMode)
                return;

            _isTypeFilterKeyboardMode = false;
            _typeFilterKeyboardIndex = GetTypeFilterIndex(_typeFilterKeyboardOriginalType);
            UpdateTypeFilterKeyboardVisuals();

            if (restoreSearchInput)
                RestoreSearchBoxInputState(preserveSelection: true);
        }

        private void MoveTypeFilterKeyboardSelection(int delta)
        {
            if (!_isTypeFilterKeyboardMode)
                return;

            _typeFilterKeyboardIndex = Math.Max(0, Math.Min(GetTypeFilterButtons().Length - 1, _typeFilterKeyboardIndex + delta));
            UpdateTypeFilterKeyboardVisuals();
        }

        private void JumpTypeFilterKeyboardSelection(bool toEnd)
        {
            if (!_isTypeFilterKeyboardMode)
                return;

            _typeFilterKeyboardIndex = toEnd ? GetTypeFilterButtons().Length - 1 : 0;
            UpdateTypeFilterKeyboardVisuals();
        }

        private ToggleButton[] GetTypeFilterButtons()
        {
            return new[]
            {
                AllFilterButton,
                LaunchableFilterButton,
                FolderFilterButton,
                ScriptFilterButton,
                LogFilterButton,
                ConfigFilterButton
            };
        }

        private FileSearchTypeFilter GetTypeFilterByIndex(int index)
        {
            switch (index)
            {
                case 1:
                    return FileSearchTypeFilter.Launchable;
                case 2:
                    return FileSearchTypeFilter.Folder;
                case 3:
                    return FileSearchTypeFilter.Script;
                case 4:
                    return FileSearchTypeFilter.Log;
                case 5:
                    return FileSearchTypeFilter.Config;
                default:
                    return FileSearchTypeFilter.All;
            }
        }

        private int GetTypeFilterIndex(FileSearchTypeFilter filter)
        {
            switch (filter)
            {
                case FileSearchTypeFilter.Launchable:
                    return 1;
                case FileSearchTypeFilter.Folder:
                    return 2;
                case FileSearchTypeFilter.Script:
                    return 3;
                case FileSearchTypeFilter.Log:
                    return 4;
                case FileSearchTypeFilter.Config:
                    return 5;
                default:
                    return 0;
            }
        }

        private void UpdateTypeFilterButtonStates()
        {
            _suppressControlEvents = true;
            AllFilterButton.IsChecked = _currentTypeFilter == FileSearchTypeFilter.All;
            LaunchableFilterButton.IsChecked = _currentTypeFilter == FileSearchTypeFilter.Launchable;
            FolderFilterButton.IsChecked = _currentTypeFilter == FileSearchTypeFilter.Folder;
            ScriptFilterButton.IsChecked = _currentTypeFilter == FileSearchTypeFilter.Script;
            LogFilterButton.IsChecked = _currentTypeFilter == FileSearchTypeFilter.Log;
            ConfigFilterButton.IsChecked = _currentTypeFilter == FileSearchTypeFilter.Config;
            _suppressControlEvents = false;
        }

        private void UpdateTypeFilterKeyboardVisuals()
        {
            var buttons = GetTypeFilterButtons();
            for (var i = 0; i < buttons.Length; i++)
            {
                var button = buttons[i];
                if (button == null)
                    continue;

                button.ClearValue(Control.BackgroundProperty);
                button.ClearValue(Control.BorderBrushProperty);
                button.ClearValue(Control.ForegroundProperty);

                if (!_isTypeFilterKeyboardMode || i != _typeFilterKeyboardIndex)
                    continue;

                button.Background = TypeFilterKeyboardHighlightBackground;
                button.BorderBrush = TypeFilterKeyboardHighlightBorderBrush;
                button.Foreground = TypeFilterKeyboardHighlightForeground;
            }
        }

        private void BeginKeyboardScopeSelection()
        {
            if (ScopeComboBox == null)
                return;

            CaptureSearchBoxInputState();
            _scopeSelectionOriginalValue = GetSelectedScopePath();
            _isKeyboardScopeSelectionActive = true;
            ScopeComboBox.Focus();
            ScopeComboBox.IsDropDownOpen = true;
        }

        private void CommitKeyboardScopeSelection()
        {
            var scopeChanged = !string.Equals(GetSelectedScopePath(), _scopeSelectionOriginalValue, StringComparison.OrdinalIgnoreCase);
            _isKeyboardScopeSelectionActive = false;
            _skipScopeDropDownClosedRestore = true;
            ScopeComboBox.IsDropDownOpen = false;
            RestoreSearchBoxInputState(preserveSelection: true);

            if (scopeChanged)
                RestartSearchDebounce();
        }

        private void ApplyHighlightedScopeSelection()
        {
            if (ScopeComboBox == null)
                return;

            for (var i = 0; i < ScopeComboBox.Items.Count; i++)
            {
                var container = ScopeComboBox.ItemContainerGenerator.ContainerFromIndex(i) as ComboBoxItem;
                if (container == null || !container.IsHighlighted)
                    continue;

                if (container.DataContext is ComboOption option)
                {
                    SetScopeSelectionWithoutSearch(option.Key);
                    return;
                }

                if (container.Content is ComboOption contentOption)
                {
                    SetScopeSelectionWithoutSearch(contentOption.Key);
                    return;
                }
            }
        }

        private void CancelKeyboardScopeSelection()
        {
            _isKeyboardScopeSelectionActive = false;
            SetScopeSelectionWithoutSearch(_scopeSelectionOriginalValue);
            _skipScopeDropDownClosedRestore = true;
            ScopeComboBox.IsDropDownOpen = false;
            RestoreSearchBoxInputState(preserveSelection: true);
        }

        private void SetScopeSelectionWithoutSearch(string path)
        {
            var previous = _suppressScopeSelectionSearch;
            _suppressScopeSelectionSearch = true;
            try
            {
                SelectScopeOption(path);
            }
            finally
            {
                _suppressScopeSelectionSearch = previous;
            }
        }

        private void CaptureSearchBoxInputState()
        {
            if (SearchBox == null)
                return;

            _scopeSearchSelectionStart = SearchBox.SelectionStart;
            _scopeSearchSelectionLength = SearchBox.SelectionLength;
            _scopeSearchCaretIndex = SearchBox.CaretIndex;
        }

        private void RestoreSearchBoxInputState(bool preserveSelection)
        {
            if (SearchBox == null)
                return;

            Dispatcher.BeginInvoke(new Action(delegate
            {
                SearchBox.Focus();

                if (!preserveSelection)
                {
                    SearchBox.CaretIndex = SearchBox.Text == null ? 0 : SearchBox.Text.Length;
                    return;
                }

                var textLength = SearchBox.Text == null ? 0 : SearchBox.Text.Length;
                var selectionStart = Math.Max(0, Math.Min(_scopeSearchSelectionStart, textLength));
                var selectionLength = Math.Max(0, Math.Min(_scopeSearchSelectionLength, textLength - selectionStart));
                SearchBox.Select(selectionStart, selectionLength);
                if (selectionLength == 0)
                    SearchBox.CaretIndex = Math.Max(0, Math.Min(_scopeSearchCaretIndex, textLength));
            }), DispatcherPriority.Input);
        }

        private void ScopeComboBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isKeyboardScopeSelectionActive)
                return;

            var key = GetEffectiveKey(e);
            if (key == Key.Enter || key == Key.Tab)
            {
                e.Handled = true;
                ApplyHighlightedScopeSelection();
                CommitKeyboardScopeSelection();
                return;
            }

            if (key == Key.Escape || (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Up))
            {
                e.Handled = true;
                CancelKeyboardScopeSelection();
            }
        }

        private void ScopeComboBox_DropDownClosed(object sender, EventArgs e)
        {
            if (_skipScopeDropDownClosedRestore)
            {
                _skipScopeDropDownClosedRestore = false;
                return;
            }

            if (_isKeyboardScopeSelectionActive)
            {
                CommitKeyboardScopeSelection();
                return;
            }

            RestoreSearchBoxInputState(preserveSelection: false);
        }

        public void PrepareForProcessExit()
        {
            _allowProcessExit = true;
            _hideInsteadOfClose = false;
        }

        private void ConfigureStaticControls()
        {
            _suppressControlEvents = true;
            AllFilterButton.IsChecked = true;
            LaunchableFilterButton.ToolTip = "仅基于索引中的扩展名和目录标记过滤，不读取文件元数据。";
            FolderFilterButton.ToolTip = "仅显示目录结果。";
            ScriptFilterButton.ToolTip = "仅显示 bat/cmd/ps1 脚本。";
            LogFilterButton.ToolTip = "仅显示 log/txt 结果。";
            ConfigFilterButton.ToolTip = "仅显示 json/xml/ini/config/yaml/yml 结果。";
            UpdateTypeFilterKeyboardVisuals();

            ScopeComboBox.ItemsSource = _scopeOptions;
            ScopeComboBox.DisplayMemberPath = "DisplayName";
            ScopeComboBox.SelectedValuePath = "Key";
            ScopeComboBox.ToolTip = "下拉选择搜索范围，不修改索引服务层；输入框可用 Alt+Down 打开。";
            RefreshScopeOptions();
            ScopeComboBox.SelectedValue = string.Empty;

            SortComboBox.ItemsSource = new[]
            {
                new ComboOption("default", "默认"),
                new ComboOption("name", "名称"),
                new ComboOption("path", "路径"),
                new ComboOption("type", "类型")
            };
            SortComboBox.DisplayMemberPath = "DisplayName";
            SortComboBox.SelectedValuePath = "Key";
            SortComboBox.SelectedValue = "default";
            SortComboBox.ToolTip = "仅对当前已加载结果做内存重排，不触发索引服务重算。";
            QuerySummaryText.Text = "性能保护模式：排序仅做内存重排；路径限定复用路径前缀查询";
            _suppressControlEvents = false;
        }

        private void LoadWindowState()
        {
            var state = _stateStore.Load();
            foreach (var entry in state.RecentSearches.OrderByDescending(item => item.Timestamp))
                _recentSearches.Add(entry);

            _currentSortKey = state.SortKey == "name" || state.SortKey == "path" || state.SortKey == "type"
                ? state.SortKey
                : "default";
            SortComboBox.SelectedValue = _currentSortKey;
            RefreshScopeOptions();
            SelectScopeOption(state.ScopePath);
        }

        private void SaveWindowState()
        {
            _stateStore.Save(new SearchWindowState
            {
                SortKey = _currentSortKey,
                ScopePath = GetSelectedScopePath(),
                ViewModeKey = "compact",
                RecentSearches = _recentSearches.ToList()
            });
        }

        private void EverythingSearchWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SearchBox.Focus();
            AttachResultsScrollViewer();
            _ = StartIndexingAsync(false);
        }

        private void EverythingSearchWindow_Closing(object sender, CancelEventArgs e)
        {
            SaveWindowState();
            if (_allowProcessExit || !_hideInsteadOfClose)
                return;

            e.Cancel = true;
            Hide();
        }

        private void EverythingSearchWindow_Closed(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            _liveRefreshTimer.Stop();
            _indexChangeFlushTimer.Stop();
            if (_resultsScrollViewer != null)
                _resultsScrollViewer.ScrollChanged -= ResultsScrollViewer_ScrollChanged;
            CancelToken(ref _indexCts);
            CancelToken(ref _searchCts);
            _indexService.Shutdown();
        }

        private void EnqueueIndexChange(IndexChangedEventArgs e)
        {
            if (e == null)
                return;

            _pendingIndexChanges.Enqueue(e);
            if (!_indexChangeFlushTimer.IsEnabled)
                _indexChangeFlushTimer.Start();
        }

        private void IndexChangeFlushTimer_Tick(object sender, EventArgs e)
        {
            _indexChangeFlushTimer.Stop();
            if (_isApplyingPendingIndexChanges || _pendingIndexChanges.IsEmpty)
                return;

            _isApplyingPendingIndexChanges = true;
            try
            {
                var pendingChanges = new List<IndexChangedEventArgs>();
                IndexChangedEventArgs change;
                while (_pendingIndexChanges.TryDequeue(out change))
                {
                    if (change != null)
                        pendingChanges.Add(change);
                }

                if (pendingChanges.Count > 0)
                    ApplyIndexChangesBatch(pendingChanges);
            }
            finally
            {
                _isApplyingPendingIndexChanges = false;
                if (!_pendingIndexChanges.IsEmpty)
                    _indexChangeFlushTimer.Start();
            }
        }

        private void UpdateActionButtons()
        {
            var item = ResultsGrid.SelectedItem as EverythingSearchResultItem;
            var hasSelection = item != null;
            OpenButton.IsEnabled = hasSelection;
            OpenFolderButton.IsEnabled = hasSelection;
            CopyPathButton.IsEnabled = hasSelection;
            CopyNameButton.IsEnabled = hasSelection;
            RunAsAdminButton.IsEnabled = hasSelection && !item.IsDirectory;
            PropertiesButton.IsEnabled = hasSelection;
            OpenTerminalButton.IsEnabled = hasSelection;
            RenameButton.IsEnabled = hasSelection;
            DeleteButton.IsEnabled = hasSelection;
        }

        private void UpdateSummaryStatus()
        {
            var typeFilterText = GetTypeFilterText(_currentTypeFilter);
            var sortText = _currentSortKey == "name"
                ? "名称"
                : (_currentSortKey == "path"
                    ? "路径"
                    : (_currentSortKey == "type" ? "类型" : "默认"));
            var scopeText = GetSelectedScopeDisplayText();
            QuerySummaryText.Text = "当前类型：" + typeFilterText + "；当前排序：" + sortText + "；路径限定：" + scopeText;
            CurrentFilterSummaryText.Text = "当前过滤：" + typeFilterText + " / " + scopeText + " / " + sortText;
            StatusFilterText.Text = "过滤：" + typeFilterText + " / " + scopeText + " / " + sortText;
            ResultCountBadgeText.Text = _loadedResultCount + " 项";
            StatusCountText.Text = _currentTypeFilter == FileSearchTypeFilter.All
                ? (_loadedResultCount + "/" + _totalMatchedCount + " 项结果")
                : (_loadedResultCount + " 项类型命中 / " + _totalMatchedCount + " 项原始匹配");
            CurrentLoadSummaryText.Text = string.IsNullOrWhiteSpace(_activeKeyword)
                ? "等待搜索"
                : (_hasMoreSearchResults ? "滚动到底继续加载" : "已加载完成");

            if (!_indexReady)
            {
                StatusText.Text = "正在建立索引，请稍候...";
            }
            else if (_isSearchInProgress && _displayedResults.Count == 0)
            {
                StatusText.Text = "正在搜索 \"" + GetVisibleQueryText() + "\"...";
            }
            else if (string.IsNullOrWhiteSpace(_activeKeyword))
            {
                var status = string.IsNullOrWhiteSpace(_latestIndexStatusMessage) ? (_indexedCount + " 个对象") : _latestIndexStatusMessage;
                StatusText.Text = status + "；" + FormatBucketStatusText(_latestContainsBucketStatus);
            }
            else if (_totalMatchedCount <= 0)
            {
                StatusText.Text = "未找到匹配项（当前类型：" + typeFilterText + "）";
            }
            else if (_currentTypeFilter != FileSearchTypeFilter.All)
            {
                StatusText.Text = _loadedResultCount > 0
                    ? string.Format("已显示 {0} 个类型命中结果（原始匹配共 {1} 个）", _loadedResultCount, _totalMatchedCount)
                    : string.Format("检索到 {0} 个对象，但当前类型下没有命中", _totalMatchedCount);
            }
            else
            {
                StatusText.Text = string.Format("已显示 {0} 个对象（共 {1} 个）", _loadedResultCount, _totalMatchedCount);
            }
        }

        private void UpdateIndexStateBadge(bool isCatchingUp)
        {
            IndexStateBadgeText.Text = isCatchingUp ? "后台追平中" : "索引已就绪";
            UpdateBucketBadgeTexts();
        }

        private void UpdateBucketBadgeTexts()
        {
            var value = _latestContainsBucketStatus ?? ContainsBucketStatus.Empty;
            CharBucketBadgeText.Text = "单字符: " + FormatReadyText(value.CharReady);
            BigramBucketBadgeText.Text = "双字符: " + FormatReadyText(value.BigramReady);
            TrigramBucketBadgeText.Text = "多字符: " + FormatReadyText(value.TrigramReady);

            var tip = FormatBucketStatusText(value);
            CharBucketBadgeText.ToolTip = tip;
            BigramBucketBadgeText.ToolTip = tip;
            TrigramBucketBadgeText.ToolTip = tip;
            IndexStateBadgeText.ToolTip = tip;
        }

        private static string FormatBucketStatusText(ContainsBucketStatus status)
        {
            var value = status ?? ContainsBucketStatus.Empty;
            if (value.IsOverlayOverflowed)
            {
                return "搜索桶增量溢出，包含搜索临时走全量";
            }

            return "桶状态：单字符" + FormatReadyText(value.CharReady)
                   + "，双字符" + FormatReadyText(value.BigramReady)
                   + "，多字符" + FormatReadyText(value.TrigramReady);
        }

        private static string FormatReadyText(bool ready)
        {
            return ready ? "就绪" : "未就绪";
        }

        private void RefreshScopeOptions()
        {
            var selectedPath = GetSelectedScopePath();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _scopeOptions.Clear();
            AddScopeOption(string.Empty, "全盘", seen);

            AddScopeOptionIfSupported(Environment.CurrentDirectory, "当前目录", seen);
            AddScopeOptionIfSupported(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "桌面", seen);
            AddScopeOptionIfSupported(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "文档", seen);
            AddScopeOptionIfSupported(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "用户目录", seen);

            var downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            AddScopeOptionIfSupported(downloadsPath, "下载", seen);

            foreach (var drive in DriveInfo.GetDrives()
                         .Where(d => d.DriveType == DriveType.Fixed)
                         .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                AddScopeOptionIfSupported(drive.RootDirectory.FullName, drive.RootDirectory.FullName.TrimEnd('\\') + " 盘", seen);
            }

            foreach (var historyPath in _recentSearches
                         .Select(item => ParseHistoryScopePath(item.Query))
                         .Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                AddScopeOptionIfSupported(historyPath, historyPath, seen);
            }

            SelectScopeOption(selectedPath);
        }

        private static string ParseHistoryScopePath(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return null;

            var trimmedQuery = query.Trim();
            var spaceIndex = trimmedQuery.IndexOf(' ');
            if (spaceIndex <= 0)
                return null;

            var candidate = trimmedQuery.Substring(0, spaceIndex);
            return IsSupportedScopePath(candidate) ? candidate : null;
        }

        private void AddScopeOptionIfSupported(string path, string displayName, HashSet<string> seen)
        {
            if (!IsSupportedScopePath(path) || !Directory.Exists(path))
                return;

            AddScopeOption(path, displayName, seen);
        }

        private void AddScopeOption(string path, string displayName, HashSet<string> seen)
        {
            var normalizedPath = path ?? string.Empty;
            if (!seen.Add(normalizedPath))
                return;

            _scopeOptions.Add(new ComboOption(normalizedPath, displayName));
        }

        private static bool IsSupportedScopePath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && path.IndexOf(' ') < 0;
        }

        private string GetSelectedScopePath()
        {
            var option = ScopeComboBox.SelectedItem as ComboOption;
            return option == null ? string.Empty : option.Key ?? string.Empty;
        }

        private string GetSelectedScopeDisplayText()
        {
            var option = ScopeComboBox.SelectedItem as ComboOption;
            return option == null || string.IsNullOrWhiteSpace(option.DisplayName) ? "全盘" : option.DisplayName;
        }

        private void SelectScopeOption(string path)
        {
            var normalizedPath = path ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(normalizedPath)
                && IsSupportedScopePath(normalizedPath)
                && Directory.Exists(normalizedPath)
                && _scopeOptions.All(item => !string.Equals(item.Key, normalizedPath, StringComparison.OrdinalIgnoreCase)))
            {
                _scopeOptions.Add(new ComboOption(normalizedPath, normalizedPath));
            }

            ScopeComboBox.SelectedValue = normalizedPath;
            if (ScopeComboBox.SelectedIndex < 0)
                ScopeComboBox.SelectedIndex = 0;
        }

        private void UpdateEmptyState()
        {
            if (!_indexReady)
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                return;
            }

            if (_isSearchInProgress)
            {
                if (_displayedResults.Count > 0)
                {
                    EmptyStatePanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    EmptyStatePanel.Visibility = Visibility.Visible;
                    EmptyStateTitleText.Text = "正在搜索";
                    EmptyStateDescriptionText.Text = "搜索核心链路保持原有性能模型，不做额外高成本筛选。";
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(_activeKeyword))
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                EmptyStateTitleText.Text = "输入关键词开始搜索";
                EmptyStateDescriptionText.Text = "类型过滤已启用；排序仅对当前已加载结果做内存重排，路径限定会复用路径前缀查询。";
                return;
            }

            EmptyStatePanel.Visibility = _displayedResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_displayedResults.Count == 0)
            {
                if (_currentTypeFilter != FileSearchTypeFilter.All && _totalMatchedCount > 0)
                {
                    EmptyStateTitleText.Text = "当前类型下没有结果";
                    EmptyStateDescriptionText.Text = "关键词有原始匹配，但当前类型过滤没有命中，可以切回“全部”或更换类型。";
                }
                else
                {
                    EmptyStateTitleText.Text = "没有找到匹配结果";
                    EmptyStateDescriptionText.Text = "可以缩短关键词或尝试使用通配符、前缀、后缀和正则。";
                }
            }
        }

        private void UpdateSelectedItemDetails(EverythingSearchResultItem item)
        {
            if (item == null)
            {
                SelectedItemNameText.Text = "未选择结果";
                SelectedItemPathText.Text = "-";
                SelectedItemMetaText.Text = "搜索结果选中后，大小和修改时间会按需加载。";
                return;
            }

            SelectedItemNameText.Text = string.IsNullOrWhiteSpace(item.FileName) ? item.FullPath : item.FileName;
            SelectedItemPathText.Text = item.FullPath;
            if (item.MetadataLoaded)
            {
                SelectedItemMetaText.Text = string.Format("类型：{0}  大小：{1}  修改时间：{2}",
                    item.TypeText,
                    string.IsNullOrWhiteSpace(item.SizeText) ? "-" : item.SizeText,
                    string.IsNullOrWhiteSpace(item.ModifiedText) ? "-" : item.ModifiedText);
            }
            else
            {
                SelectedItemMetaText.Text = "类型：" + item.TypeText + "  大小和修改时间按需加载中...";
                _ = EnsureMetadataLoadedAsync(item);
            }
        }

        private void AttachResultsScrollViewer()
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_resultsScrollViewer != null)
                    _resultsScrollViewer.ScrollChanged -= ResultsScrollViewer_ScrollChanged;
                _resultsScrollViewer = FindDescendant<ScrollViewer>(ResultsGrid);
                if (_resultsScrollViewer != null)
                    _resultsScrollViewer.ScrollChanged += ResultsScrollViewer_ScrollChanged;
            }), DispatcherPriority.Loaded);
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
                return null;

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T)
                    return (T)child;
                var nested = FindDescendant<T>(child);
                if (nested != null)
                    return nested;
            }

            return null;
        }
    }
}
