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
using Newtonsoft.Json.Linq;
using System.Windows.Input;

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

        public void UpdateCountAndTotalPoints()
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
        public ObservableCollection<string> Participants { get; } = new ObservableCollection<string>();
        
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
                }
            }
        }
        
        private string _selectedParticipant;
        public string SelectedParticipant
        {
            get => _selectedParticipant;
            set
            {
                if (!string.Equals(_selectedParticipant, value, StringComparison.Ordinal))
                {
                    _selectedParticipant = value;
                    OnPropertyChanged(nameof(SelectedParticipant));
                    ApplyFilterAndBuildColumns();
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
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _refreshTimer.Tick += async (s, e) => await RefreshWorkItemsAsync();
            _refreshTimer.Start();
            Closed += (s, e) => _refreshTimer.Stop();
        }
        
        private Point _dragStart;
        private bool _dragInit;
        
        private async Task LoadWorkItemsAsync()
        {
            try
            {
                Overlay.IsBusy = true;
                _allItems = await _api.GetIterationWorkItemsAsync(_iterationId);
                RebuildMembersFromItems();
                SelectedMember = Members.FirstOrDefault();
                RebuildParticipantsFromItems();
                SelectedParticipant = Participants.FirstOrDefault();
                ApplyFilterAndBuildColumns();
                
            }
            catch (Exception ex)
            {
                MessageBox.Show("加载看板失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Overlay.IsBusy = false;
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
        
        private void RebuildParticipantsFromItems()
        {
            try
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var it in _allItems ?? Enumerable.Empty<PingCodeApiService.WorkItemInfo>())
                {
                    foreach (var nm in it.ParticipantNames ?? new List<string>())
                    {
                        var name = (nm ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(name)) set.Add(name);
                    }
                }
                var list = new List<string> { "无" };
                list.AddRange(set.OrderBy(x => x));
                Participants.Clear();
                foreach (var p in list)
                {
                    Participants.Add(p);
                }
            }
            catch
            {
            }
        }
        
        
        
        private IEnumerable<PingCodeApiService.WorkItemInfo> ApplyCurrentFilter(IEnumerable<PingCodeApiService.WorkItemInfo> items)
        {
            IEnumerable<PingCodeApiService.WorkItemInfo> filtered = items;
            var participantActive = !string.IsNullOrWhiteSpace(SelectedParticipant) && !string.Equals(SelectedParticipant.Trim(), "无", StringComparison.OrdinalIgnoreCase);
            if (participantActive)
            {
                var target = SelectedParticipant.Trim();
                filtered = filtered.Where(i => (i.ParticipantNames ?? new List<string>()).Any(n => string.Equals((n ?? "").Trim(), target, StringComparison.OrdinalIgnoreCase)));
                return filtered;
            }
            if (!(SelectedMember == null || SelectedMember.Id == "*" || (SelectedMember.Name ?? "").Trim() == "全部"))
            {
                var id = (SelectedMember.Id ?? "").Trim().ToLowerInvariant();
                var nm = (SelectedMember.Name ?? "").Trim().ToLowerInvariant();
                filtered = filtered.Where(i =>
                {
                    var iid = (i.AssigneeId ?? "").Trim().ToLowerInvariant();
                    var inm = (i.AssigneeName ?? "").Trim().ToLowerInvariant();
                    return (!string.IsNullOrEmpty(iid) && iid == id) || (!string.IsNullOrEmpty(inm) && inm == nm);
                });
            }
            return filtered;
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
                var prevP = SelectedParticipant;
                RebuildParticipantsFromItems();
                if (!string.IsNullOrWhiteSpace(prevP) && Participants.Any(p => string.Equals(p, prevP, StringComparison.OrdinalIgnoreCase)))
                {
                    SelectedParticipant = prevP;
                }
                ApplyFilterAndBuildColumns();
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
            var desiredTitles = new List<string>(order);
            foreach (var g in items.GroupBy(i => i.StateCategory).Where(g => !order.Contains(g.Key ?? "", StringComparer.OrdinalIgnoreCase)))
            {
                var t = string.IsNullOrWhiteSpace(g.Key) ? "其他" : g.Key;
                if (!desiredTitles.Contains(t, StringComparer.OrdinalIgnoreCase))
                {
                    desiredTitles.Add(t);
                }
            }
            
            if (Columns == null) Columns = new ObservableCollection<KanbanColumn>();
            
            foreach (var t in desiredTitles)
            {
                if (!Columns.Any(c => string.Equals(c.Title, t, StringComparison.OrdinalIgnoreCase)))
                {
                    Columns.Add(new KanbanColumn { Title = t });
                }
            }
            var toRemove = Columns.Where(c => !desiredTitles.Contains(c.Title ?? "", StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var r in toRemove)
            {
                Columns.Remove(r);
            }
            for (int i = 0; i < desiredTitles.Count; i++)
            {
                var t = desiredTitles[i];
                var idx = Columns.ToList().FindIndex(c => string.Equals(c.Title, t, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0 && idx != i)
                {
                    Columns.Move(idx, i);
                }
            }
            foreach (var col in Columns)
            {
                var title = col.Title ?? "";
                List<PingCodeApiService.WorkItemInfo> target;
                if (order.Contains(title, StringComparer.OrdinalIgnoreCase))
                {
                    target = items.Where(i => string.Equals(i.StateCategory ?? "", title, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                else
                {
                    if (string.Equals(title, "其他", StringComparison.OrdinalIgnoreCase))
                    {
                        target = items.Where(i => string.IsNullOrWhiteSpace(i.StateCategory)).ToList();
                    }
                    else
                    {
                        target = items.Where(i => string.Equals(i.StateCategory ?? "", title, StringComparison.OrdinalIgnoreCase)).ToList();
                    }
                }
                
                var existing = new HashSet<string>(col.Items.Select(x => x?.Id ?? x?.Identifier ?? ""), StringComparer.OrdinalIgnoreCase);
                var desired = new HashSet<string>(target.Select(x => x?.Id ?? x?.Identifier ?? ""), StringComparer.OrdinalIgnoreCase);
                
                foreach (var it in col.Items.ToList())
                {
                    var key = it?.Id ?? it?.Identifier ?? "";
                    if (!desired.Contains(key))
                    {
                        col.Items.Remove(it);
                    }
                }
                foreach (var it in target)
                {
                    var key = it?.Id ?? it?.Identifier ?? "";
                    if (!existing.Contains(key))
                    {
                        col.Items.Add(it);
                    }
                }
                col.UpdateCountAndTotalPoints();
            }
        }
        
        private void MemberCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { }
        private void ParticipantCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { }
        
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
        
        private void Item_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(null);
            _dragInit = true;
        }
        
        private void Item_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || !_dragInit) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }
            var fe = sender as FrameworkElement;
            var item = fe?.DataContext as PingCodeApiService.WorkItemInfo;
            if (item == null) return;
            _dragInit = false;
            DragDrop.DoDragDrop(fe, new DataObject(typeof(PingCodeApiService.WorkItemInfo), item), DragDropEffects.Move);
        }
        
        private async void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragInit) return;
            _dragInit = false;
            try
            {
                var fe = sender as FrameworkElement;
                var item = fe?.DataContext as PingCodeApiService.WorkItemInfo;
                if (item == null) return;
                Overlay.IsBusy = true;
                var details = await _api.GetWorkItemDetailsAsync(item.Id);
                if (details != null)
                {
                    var win = new WorkItemDetailsWindow(details, _api);
                    win.Owner = this;
                    win.ShowDialog();
                }
            }
            catch
            {
            }
            finally
            {
                Overlay.IsBusy = false;
            }
        }
        
        private void Column_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(PingCodeApiService.WorkItemInfo)))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            }
        }
        
        private static string MapCategoryToStateTypeForPatch(string category)
        {
            var c = (category ?? "").Trim();
            if (string.Equals(c, "进行中", StringComparison.OrdinalIgnoreCase)) return "in_progress";
            if (string.Equals(c, "可测试", StringComparison.OrdinalIgnoreCase)) return "in_progress";
            if (string.Equals(c, "测试中", StringComparison.OrdinalIgnoreCase)) return "in_progress";
            if (string.Equals(c, "已完成", StringComparison.OrdinalIgnoreCase)) return "done";
            if (string.Equals(c, "已关闭", StringComparison.OrdinalIgnoreCase)) return "closed";
            return "pending";
        }
        
        private async void Column_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(PingCodeApiService.WorkItemInfo))) return;
            var item = e.Data.GetData(typeof(PingCodeApiService.WorkItemInfo)) as PingCodeApiService.WorkItemInfo;
            var dest = (sender as FrameworkElement)?.DataContext as KanbanColumn;
            if (item == null || dest == null) return;
            var current = item.StateCategory ?? "";
            var target = dest.Title ?? "";
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase)) return;
            var src = Columns.FirstOrDefault(c => c.Items.Contains(item));
            if (src == null) return;
            try
            {
                Overlay.IsBusy = true;
                var targetStateId = await ResolveTargetStateIdAsync(item, target);
                var ok = false;
                if (targetStateId != null && !string.IsNullOrWhiteSpace(targetStateId.Value.Item1))
                {
                    ok = await _api.UpdateWorkItemStateByIdAsync(item.Id, targetStateId.Value.Item1);
                }
                
                if (ok)
                {
                    src.Items.Remove(item);
                    dest.Items.Add(item);
                    item.StateCategory = target;
                    item.Status = targetStateId.Value.Item2;
                    if (!string.IsNullOrWhiteSpace(targetStateId.Value.Item1))
                    {
                        item.StateId = targetStateId.Value.Item1;
                    }

                    src.UpdateCountAndTotalPoints();
                    dest.UpdateCountAndTotalPoints();
                }
                else
                {
                    MessageBox.Show("更新状态失败：未找到符合状态方案与流转规则的目标状态", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("更新状态异常：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Overlay.IsBusy = false;
            }
        }
        
        private async Task<(string, string)?> ResolveTargetStateIdAsync(PingCodeApiService.WorkItemInfo item, string targetCategory)
        {
            if (item == null || string.IsNullOrWhiteSpace(targetCategory)) return null;
            var type = (item.Type ?? "").Trim();
            if (string.IsNullOrWhiteSpace(type)) return null;
            var projectId = (item.ProjectId ?? "").Trim();
           
            var targetType = MapCategoryToStateTypeForPatch(targetCategory);
            var plans = await _api.GetWorkItemStatePlansAsync(projectId);
            var plan = plans.FirstOrDefault(p => string.Equals((p?.WorkItemType ?? "").Trim(), type, StringComparison.OrdinalIgnoreCase));
            if (plan == null || string.IsNullOrWhiteSpace(plan.Id)) return null;
            var flows = await _api.GetWorkItemStateFlowsAsync(plan.Id, item.StateId);
            PingCodeApiService.StateDto firstOrDefault = flows.FirstOrDefault(s => string.Equals(s?.Type ?? "", targetType, StringComparison.OrdinalIgnoreCase));
            if (firstOrDefault == null || string.IsNullOrWhiteSpace(firstOrDefault.Id)) return null;
            
            var candidate = firstOrDefault?.Id;
            return (candidate, firstOrDefault.Name);
        }
    }
}
