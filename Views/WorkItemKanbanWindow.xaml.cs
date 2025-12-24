using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PackageManager.Services;

namespace PackageManager.Views
{
    public class KanbanColumn : INotifyPropertyChanged
    {
        private string _title;
        private ObservableCollection<PingCodeApiService.WorkItemInfo> _items = new ObservableCollection<PingCodeApiService.WorkItemInfo>();
        public string Title { get => _title; set { if (_title != value) { _title = value; OnPropertyChanged(); OnPropertyChanged(nameof(Count)); } } }
        public ObservableCollection<PingCodeApiService.WorkItemInfo> Items
        {
            get => _items;
            set
            {
                if (!ReferenceEquals(_items, value))
                {
                    if (_items != null) _items.CollectionChanged -= Items_CollectionChanged;
                    _items = value ?? new ObservableCollection<PingCodeApiService.WorkItemInfo>();
                    _items.CollectionChanged += Items_CollectionChanged;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Count));
                    OnPropertyChanged(nameof(TotalPoints));
                }
            }
        }
        private void Items_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(TotalPoints));
        }
        public int Count => _items?.Count ?? 0;
        public double TotalPoints => _items?.Sum(i => i?.StoryPoints ?? 0) ?? 0;
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
    }
    
    public partial class WorkItemKanbanWindow : Window, INotifyPropertyChanged
    {
        private readonly PingCodeApiService _api;
        private readonly string _iterationId;
        private List<PingCodeApiService.WorkItemInfo> _allItems = new List<PingCodeApiService.WorkItemInfo>();
        private readonly DispatcherTimer _refreshTimer;
        private bool _refreshing;
        
        public ObservableCollection<PingCodeApiService.Entity> Members { get; } = new ObservableCollection<PingCodeApiService.Entity>();
        
        private PingCodeApiService.Entity _selectedMember;
        public PingCodeApiService.Entity SelectedMember
        {
            get => _selectedMember;
            set
            {
                if (!Equals(_selectedMember, value))
                {
                    _selectedMember = value;
                    OnPropertyChanged(nameof(SelectedMember));
                    ApplyFilterAndBuildColumns();
                    UpdateWebView();
                }
            }
        }
        
        private ObservableCollection<KanbanColumn> _columns = new ObservableCollection<KanbanColumn>();
        public ObservableCollection<KanbanColumn> Columns
        {
            get => _columns;
            set
            {
                if (!ReferenceEquals(_columns, value))
                {
                    _columns = value;
                    OnPropertyChanged(nameof(Columns));
                }
            }
        }
        
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
        
        public WorkItemKanbanWindow(string iterationId, IEnumerable<PingCodeApiService.Entity> members, PingCodeApiService.Entity selectedMember)
        {
            InitializeComponent();
            _api = new PingCodeApiService();
            _iterationId = iterationId;
            WindowState = WindowState.Maximized;
            DataContext = this;
            Loaded += async (s, e) => await LoadWorkItemsAsync();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _refreshTimer.Tick += async (s, e) => await RefreshWorkItemsAsync();
            _refreshTimer.Start();
            Closed += (s, e) => _refreshTimer.Stop();
        }
        
        private async Task LoadWorkItemsAsync()
        {
            try
            {
                // Overlay.IsBusy = true;
                _allItems = await _api.GetIterationWorkItemsAsync(_iterationId);
                RebuildMembersFromItems();
                SelectedMember = Members.FirstOrDefault();
                ApplyFilterAndBuildColumns();
                await EnsureWebViewAsync();
                UpdateWebView();
            }
            catch (Exception ex)
            {
                MessageBox.Show("加载看板失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Overlay.IsBusy = false;
            }
        }
        
        private void RebuildMembersFromItems()
        {
            try
            {
                var pointsByPerson = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var it in _allItems ?? Enumerable.Empty<PingCodeApiService.WorkItemInfo>())
                {
                    var id = (it.AssigneeId ?? "").Trim().ToLowerInvariant();
                    var nm = (it.AssigneeName ?? "").Trim().ToLowerInvariant();
                    var key = !string.IsNullOrEmpty(id) ? id : nm;
                    if (string.IsNullOrEmpty(key)) continue;
                    pointsByPerson[key] = (pointsByPerson.TryGetValue(key, out var v) ? v : 0) + (it.StoryPoints);
                }
                var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var list = new List<PingCodeApiService.Entity>();
                list.Add(new PingCodeApiService.Entity { Id = "*", Name = "全部" });
                foreach (var kv in pointsByPerson.Where(kv => (kv.Value > 0.0)))
                {
                    var one = _allItems.FirstOrDefault(i =>
                        string.Equals((i.AssigneeId ?? "").Trim(), kv.Key, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals((i.AssigneeName ?? "").Trim(), kv.Key, StringComparison.OrdinalIgnoreCase));
                    var id = (one?.AssigneeId ?? kv.Key) ?? kv.Key;
                    var nm = (one?.AssigneeName ?? id) ?? id;
                    if (kept.Add((id ?? nm).ToLowerInvariant()))
                    {
                        list.Add(new PingCodeApiService.Entity { Id = id, Name = nm });
                    }
                }
                Members.Clear();
                foreach (var m in list.OrderBy(x => (string.Equals(x.Name, "全部", StringComparison.OrdinalIgnoreCase) || string.Equals(x.Id, "*", StringComparison.OrdinalIgnoreCase)) ? "\0" : (x.Name ?? x.Id)))
                {
                    Members.Add(m);
                }
            }
            catch
            {
            }
        }
        
        private async Task EnsureWebViewAsync()
        {
            try
            {
                if (WebView.CoreWebView2 == null)
                {
                    await WebView.EnsureCoreWebView2Async();
                    WebView.CoreWebView2.WebMessageReceived += (s, e) =>
                    {
                        try
                        {
                            var msg = e.WebMessageAsJson;
                            if (string.IsNullOrWhiteSpace(msg)) return;
                            if (msg.IndexOf("\"type\":\"ready\"", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                SendBoardDataToWebView();
                            }
                        }
                        catch
                        {
                        }
                    };
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    var htmlPath = System.IO.Path.Combine(baseDir, "Views", "WorkItemKanban.html");
                    if (System.IO.File.Exists(htmlPath))
                    {
                        var fileUri = new UriBuilder("file", "", -1, htmlPath.Replace('\\', '/')).Uri;
                        WebView.Source = fileUri;
                    }
                }
            }
            catch
            {
            }
        }
        
        private void UpdateWebView()
        {
            try
            {
                if (WebView.CoreWebView2 == null)
                {
                    return;
                }
                SendBoardDataToWebView();
            }
            catch
            {
            }
        }
        
        private void SendBoardDataToWebView()
        {
            try
            {
                var order = new[] { "未开始", "进行中", "可测试", "测试中", "已完成", "已关闭" };
                var itemsForWeb = ApplyCurrentFilter(_allItems ?? new List<PingCodeApiService.WorkItemInfo>())?.ToList() ?? new List<PingCodeApiService.WorkItemInfo>();
                var linkTpl = Environment.GetEnvironmentVariable("PINGCODE_WEB_LINK_TEMPLATE");
                var payloadObj = new
                {
                    type = "data",
                    order = order,
                    linkTemplate = string.IsNullOrWhiteSpace(linkTpl) ? "#" : linkTpl,
                    items = itemsForWeb.Select(i => new
                    {
                        id = i.Id,
                        title = i.Title,
                        status = i.Status,
                        category = i.StateCategory,
                        assigneeName = i.AssigneeName,
                        storyPoints = i.StoryPoints,
                        priority = i.Priority,
                        type = i.Type
                    }).ToArray()
                };
                var payloadJson = Newtonsoft.Json.JsonConvert.SerializeObject(payloadObj);
                if (WebView.CoreWebView2 != null)
                {
                    WebView.CoreWebView2.PostWebMessageAsJson(payloadJson);
                }
            }
            catch
            {
            }
        }
        
        private IEnumerable<PingCodeApiService.WorkItemInfo> ApplyCurrentFilter(IEnumerable<PingCodeApiService.WorkItemInfo> items)
        {
            if (SelectedMember == null || SelectedMember.Id == "*" || (SelectedMember.Name ?? "").Trim() == "全部")
            {
                return items;
            }
            var id = (SelectedMember.Id ?? "").Trim().ToLowerInvariant();
            var nm = (SelectedMember.Name ?? "").Trim().ToLowerInvariant();
            return items.Where(i =>
            {
                var iid = (i.AssigneeId ?? "").Trim().ToLowerInvariant();
                var inm = (i.AssigneeName ?? "").Trim().ToLowerInvariant();
                return (!string.IsNullOrEmpty(iid) && iid == id) || (!string.IsNullOrEmpty(inm) && inm == nm);
            });
        }
        private async Task RefreshWorkItemsAsync()
        {
            if (_refreshing) return;
            _refreshing = true;
            try
            {
                var latest = await _api.GetIterationWorkItemsAsync(_iterationId);
                _allItems = latest ?? new List<PingCodeApiService.WorkItemInfo>();
                var prev = SelectedMember;
                RebuildMembersFromItems();
                if (prev != null && Members.Any(m => string.Equals(m.Id, prev.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    SelectedMember = Members.First(m => string.Equals(m.Id, prev.Id, StringComparison.OrdinalIgnoreCase));
                }
                ApplyFilterAndBuildColumns();
                UpdateWebView();
            }
            catch
            {
            }
            finally
            {
                _refreshing = false;
            }
        }
        
        private void ApplyFilterAndBuildColumns()
        {
            IEnumerable<PingCodeApiService.WorkItemInfo> src = ApplyCurrentFilter(_allItems ?? Enumerable.Empty<PingCodeApiService.WorkItemInfo>());
            BuildColumns(src);
        }
        
        private void BuildColumns(IEnumerable<PingCodeApiService.WorkItemInfo> items)
        {
            var order = new[] { "未开始", "进行中", "可测试", "测试中", "已完成", "已关闭" };
            var cols = new List<KanbanColumn>();
            foreach (var cat in order)
            {
                var col = new KanbanColumn { Title = cat };
                foreach (var it in items.Where(i => string.Equals(i.StateCategory ?? "", cat, StringComparison.OrdinalIgnoreCase)))
                {
                    col.Items.Add(it);
                }
                cols.Add(col);
            }
            foreach (var g in items.GroupBy(i => i.StateCategory).Where(g => !order.Contains(g.Key ?? "", StringComparer.OrdinalIgnoreCase)))
            {
                var col = new KanbanColumn { Title = string.IsNullOrWhiteSpace(g.Key) ? "其他" : g.Key };
                foreach (var it in g)
                {
                    col.Items.Add(it);
                }
                cols.Add(col);
            }
            Columns = new ObservableCollection<KanbanColumn>(cols);
        }
        
        private void MemberCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { }
        
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
