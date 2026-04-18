using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace MftScanner
{
    public partial class EverythingSearchWindow : Window
    {
        private const int SearchBatchSize = 500;
        private const int DisplayBatchSize = 200;
        private const double LoadMoreThreshold = 24d;
        private const int MaxRecentSearches = 12;

        private readonly IndexService _indexService = new IndexService();
        private readonly IncrementalFilter _filter;
        private readonly ObservableCollection<EverythingSearchResultItem> _displayedResults = new ObservableCollection<EverythingSearchResultItem>();
        private readonly ObservableCollection<SearchHistoryEntry> _recentSearches = new ObservableCollection<SearchHistoryEntry>();
        private readonly DispatcherTimer _debounceTimer;
        private readonly DispatcherTimer _liveRefreshTimer;
        private readonly SearchWindowStateStore _stateStore = new SearchWindowStateStore();
        private readonly Dictionary<string, FileMetadata> _metadataCache = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _displayedPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource _indexCts;
        private CancellationTokenSource _searchCts;
        private ScrollViewer _resultsScrollViewer;
        private List<EverythingSearchResultItem> _allLoadedResults = new List<EverythingSearchResultItem>();
        private string _activeKeyword = string.Empty;
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
        private string _latestIndexStatusMessage = string.Empty;
        private string _cachedKeyword;
        private Regex _cachedRegex;
        private bool _suppressControlEvents;
        private FileSearchTypeFilter _currentTypeFilter = FileSearchTypeFilter.All;
        private string _currentSortKey = "default";
        private string _currentViewModeKey = "compact";

        public EverythingSearchWindow()
        {
            InitializeComponent();
            ResultsGrid.ItemsSource = _displayedResults;
            RecentSearchList.ItemsSource = _recentSearches;
            _filter = new IncrementalFilter(_indexService);
            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _debounceTimer.Tick += DebounceTimer_Tick;
            _liveRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _liveRefreshTimer.Tick += LiveRefreshTimer_Tick;

            _indexService.IndexChanged += delegate(object sender, IndexChangedEventArgs e)
            {
                Dispatcher.BeginInvoke(new Action(delegate { ApplyIndexChange(e); }));
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

            ScopeComboBox.ItemsSource = new[] { new ComboOption("all", "全盘（性能保护模式）") };
            ScopeComboBox.DisplayMemberPath = "DisplayName";
            ScopeComboBox.SelectedValuePath = "Key";
            ScopeComboBox.SelectedIndex = 0;
            ScopeComboBox.IsEnabled = false;
            ScopeComboBox.ToolTip = "范围筛选会引入额外搜索成本，本轮升级暂不启用。";

            SortComboBox.ItemsSource = new[] { new ComboOption("default", "默认（性能保护模式）") };
            SortComboBox.DisplayMemberPath = "DisplayName";
            SortComboBox.SelectedValuePath = "Key";
            SortComboBox.SelectedValue = "default";
            SortComboBox.IsEnabled = false;
            SortComboBox.ToolTip = "排序会改变当前结果加载模型，本轮升级暂不启用。";

            ViewModeComboBox.ItemsSource = new[]
            {
                new ComboOption("compact", "紧凑")
            };
            ViewModeComboBox.DisplayMemberPath = "DisplayName";
            ViewModeComboBox.SelectedValuePath = "Key";
            ViewModeComboBox.SelectedValue = "compact";
            ViewModeComboBox.IsEnabled = false;
            ViewModeComboBox.ToolTip = "结果列表仅保留紧凑视图，大小和修改时间继续按需加载。";
            QuerySummaryText.Text = "性能保护模式：类型过滤已启用；范围筛选、大小/时间排序暂未启用";
            _suppressControlEvents = false;
        }

        private void LoadWindowState()
        {
            var state = _stateStore.Load();
            foreach (var entry in state.RecentSearches.OrderByDescending(item => item.Timestamp))
                _recentSearches.Add(entry);

            _currentSortKey = "default";
            SortComboBox.SelectedValue = "default";

            _currentViewModeKey = "compact";
            ViewModeComboBox.SelectedValue = "compact";
        }

        private void SaveWindowState()
        {
            _stateStore.Save(new SearchWindowState
            {
                SortKey = _currentSortKey,
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
            if (_resultsScrollViewer != null)
                _resultsScrollViewer.ScrollChanged -= ResultsScrollViewer_ScrollChanged;
            CancelToken(ref _indexCts);
            CancelToken(ref _searchCts);
            _indexService.Shutdown();
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
            var sortText = _currentSortKey == "name" ? "名称" : (_currentSortKey == "path" ? "路径" : "默认");
            CurrentFilterSummaryText.Text = "当前过滤：" + typeFilterText + " / 全盘 / " + sortText;
            StatusFilterText.Text = "过滤：" + typeFilterText + " / 全盘 / " + sortText;
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
                StatusText.Text = "正在搜索 \"" + _activeKeyword + "\"...";
            }
            else if (string.IsNullOrWhiteSpace(_activeKeyword))
            {
                StatusText.Text = string.IsNullOrWhiteSpace(_latestIndexStatusMessage) ? (_indexedCount + " 个对象") : _latestIndexStatusMessage;
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
                EmptyStateDescriptionText.Text = "本轮升级优先保证性能，类型过滤已启用；范围筛选、大小/时间排序暂不启用。";
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
