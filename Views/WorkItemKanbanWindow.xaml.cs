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
                foreach (var m in list.OrderBy(x => x.Name ?? x.Id))
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
                }
            }
            catch
            {
                ItemsHost.Visibility = Visibility.Visible;
                WebView.Visibility = Visibility.Collapsed;
            }
        }
        
        private void UpdateWebView()
        {
            try
            {
                if (WebView.CoreWebView2 == null)
                {
                    ItemsHost.Visibility = Visibility.Visible;
                    WebView.Visibility = Visibility.Collapsed;
                    return;
                }
                var html = BuildHtmlBoard();
                WebView.NavigateToString(html);
                WebView.Visibility = Visibility.Visible;
                ItemsHost.Visibility = Visibility.Collapsed;
            }
            catch
            {
                ItemsHost.Visibility = Visibility.Visible;
                WebView.Visibility = Visibility.Collapsed;
            }
        }
        
        private string BuildHtmlBoard()
        {
            var order = new[] { "未开始", "进行中", "可测试", "测试中", "已完成", "已关闭" };
            var grouped = (ApplyCurrentFilter(_allItems ?? new List<PingCodeApiService.WorkItemInfo>()))
                .GroupBy(i => i.StateCategory)
                .ToDictionary(g => g.Key ?? "未开始", g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            
            var sb = new System.Text.StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            sb.Append("<style>");
            sb.Append("html,body{height:100%;margin:0;padding:0;background:#ffffff;font-family:'Segoe UI','Microsoft YaHei',Arial,sans-serif;}");
            sb.Append(".board{display:grid;grid-template-columns:repeat(6,1fr);gap:16px;padding:4px;}");
            sb.Append(".column{border:1px solid #E5E7EB;border-radius:10px;background:#F9FAFB;display:flex;flex-direction:column;min-width:220px;max-height:calc(100vh - 180px);}");
            sb.Append(".col-header{padding:10px;border-bottom:1px solid #E5E7EB;background:#F9FAFB;}");
            sb.Append(".col-title{font-weight:600;font-size:14px;color:#111827;}");
            sb.Append(".col-meta{color:#6B7280;font-size:12px;margin-top:4px;}");
            sb.Append(".cards{flex:1;overflow-y:auto;padding:8px;}");
            sb.Append(".cards::-webkit-scrollbar{width:0;height:0} .cards{scrollbar-width:none;-ms-overflow-style:none}");
            sb.Append(".card{background:#fff;border:1px solid #E5E7EB;border-radius:10px;padding:10px;margin-bottom:10px;box-shadow:0 1px 4px rgba(0,0,0,.08);transition:all .15s ease;}");
            sb.Append(".card:hover{transform:translateY(-2px);box-shadow:0 4px 12px rgba(0,0,0,.14)}");
            sb.Append(".card.in-progress{background:#FFFBEB} .card.completed{background:#ECFDF5} .card.closed{background:#F3F4F6;opacity:.8}");
            sb.Append(".card .title{font-weight:600;font-size:13px;color:#111827;margin-bottom:8px;}");
            sb.Append(".row{display:flex;align-items:center;gap:6px;color:#6B7280;font-size:12px;margin-top:4px;flex-wrap:wrap}");
            sb.Append(".badge{display:inline-flex;align-items:center;border-radius:999px;padding:2px 8px;font-size:12px;font-weight:600}");
            sb.Append(".badge.primary{background:#2563EB;color:#fff} .badge.warn{background:#F59E0B;color:#fff} .badge.muted{background:#E5E7EB;color:#374151}");
            sb.Append(".dot{width:10px;height:10px;border-radius:50%;display:inline-block;margin-right:6px}");
            sb.Append(".dot.in-progress{background:#F59E0B} .dot.completed{background:#10B981} .dot.closed{background:#9CA3AF} .dot.todo{background:#EF4444} .dot.testing{background:#A855F7} .dot.testable{background:#3B82F6}");
            sb.Append("</style></head><body>");
            sb.Append("<div class='board'>");
            foreach (var cat in order)
            {
                var items = grouped.TryGetValue(cat, out var list) ? list : new List<PingCodeApiService.WorkItemInfo>();
                var totalPoints = items.Sum(i => i.StoryPoints);
                sb.Append("<div class='column'>");
                sb.Append("<div class='col-header'>");
                sb.Append($"<div class='col-title'>{cat}</div>");
                sb.Append($"<div class='col-meta'>数量 · {items.Count}  ·  故事点 · {totalPoints}</div>");
                sb.Append("</div>");
                sb.Append("<div class='cards'>");
                foreach (var it in items)
                {
                    var cls = "card ";
                    var dot = "<span class='dot todo'></span>";
                    if (string.Equals(cat, "进行中", StringComparison.OrdinalIgnoreCase)) { cls += "in-progress"; dot = "<span class='dot in-progress'></span>"; }
                    else if (string.Equals(cat, "已完成", StringComparison.OrdinalIgnoreCase)) { cls += "completed"; dot = "<span class='dot completed'></span>"; }
                    else if (string.Equals(cat, "已关闭", StringComparison.OrdinalIgnoreCase)) { cls += "closed"; dot = "<span class='dot closed'></span>"; }
                    else if (string.Equals(cat, "测试中", StringComparison.OrdinalIgnoreCase)) { dot = "<span class='dot testing'></span>"; }
                    else if (string.Equals(cat, "可测试", StringComparison.OrdinalIgnoreCase)) { dot = "<span class='dot testable'></span>"; }
                    var repaired = ((it.Status ?? "").IndexOf("已修复", StringComparison.OrdinalIgnoreCase) >= 0);
                    sb.Append($"<div class='{cls}'>");
                    sb.Append($"<div class='title'>{dot}{System.Net.WebUtility.HtmlEncode(it.Title ?? it.Id)}</div>");
                    sb.Append("<div class='row'>");
                    sb.Append($"<span class='badge primary'>{System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(it.Type) ? "需求" : it.Type)}</span>");
                    sb.Append($"<span class='badge warn'>{System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(it.Priority) ? "普通" : it.Priority)}</span>");
                    if (repaired) sb.Append("<span class='badge muted'>已修复</span>");
                    sb.Append("</div>");
                    sb.Append("<div class='row'>");
                    sb.Append($"<span>负责人 · {System.Net.WebUtility.HtmlEncode(it.AssigneeName ?? "未指派")}</span>");
                    sb.Append($"<span>故事点 · {it.StoryPoints}</span>");
                    sb.Append("</div>");
                    sb.Append("<div class='row'>");
                    sb.Append($"<span class='badge muted'>{System.Net.WebUtility.HtmlEncode(it.Status ?? cat)}</span>");
                    sb.Append("</div>");
                    sb.Append("</div>");
                }
                sb.Append("</div></div>");
            }
            sb.Append("</div></body></html>");
            return sb.ToString();
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
