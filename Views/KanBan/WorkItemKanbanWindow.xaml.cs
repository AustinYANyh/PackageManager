using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PackageManager.Services;
using PackageManager.Services.PingCode;
using PackageManager.Services.PingCode.Dto;
using PackageManager.Services.PingCode.Model;

namespace PackageManager.Views.KanBan;

public partial class WorkItemKanbanWindow : Window, INotifyPropertyChanged
{
    private readonly PingCodeApiService api;

    private readonly string iterationId;

    private readonly DispatcherTimer refreshTimer;

    private readonly TimeSpan baseRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly TimeSpan fastRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, StatePlanInfo> planCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<StateDto>> flowsCache = new(StringComparer.OrdinalIgnoreCase);

    private List<WorkItemInfo> allItems = new();

    private bool refreshing;

    private DateTime fastRefreshUntil = DateTime.MinValue;

    private string lastItemsSignature;

    private Entity selectedMember;

    private string selectedParticipant;

    private ObservableCollection<KanbanColumn> columns = new();

    private Point dragStart;

    private bool dragInit;

    private bool handlingDrop;

    public WorkItemKanbanWindow(string iterationId, IEnumerable<Entity> members, Entity selectedMember)
    {
        InitializeComponent();
        api = new PingCodeApiService();
        this.iterationId = iterationId;
        WindowState = WindowState.Maximized;
        DataContext = this;
        Loaded += async (s, e) => await LoadWorkItemsAsync();
        refreshTimer = new DispatcherTimer { Interval = baseRefreshInterval };
        refreshTimer.Tick += async (s, e) => await RefreshWorkItemsAsync();
        refreshTimer.Start();
        Closed += (s, e) => refreshTimer.Stop();
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public ObservableCollection<Entity> Members { get; } = new();

    public ObservableCollection<string> Participants { get; } = new();

    public Entity SelectedMember
    {
        get => selectedMember;

        set
        {
            if (!Equals(selectedMember, value))
            {
                selectedMember = value;
                OnPropertyChanged();
                ApplyFilterAndBuildColumns();
            }
        }
    }

    public string SelectedParticipant
    {
        get => selectedParticipant;

        set
        {
            if (!string.Equals(selectedParticipant, value, StringComparison.Ordinal))
            {
                selectedParticipant = value;
                OnPropertyChanged();
                ApplyFilterAndBuildColumns();
            }
        }
    }

    public ObservableCollection<KanbanColumn> Columns
    {
        get => columns;

        set
        {
            if (!ReferenceEquals(columns, value))
            {
                columns = value;
                OnPropertyChanged();
            }
        }
    }

    protected void OnPropertyChanged([CallerMemberName] string name = null) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }

    private static string ComputeItemsSignature(IEnumerable<WorkItemInfo> items)
    {
        var list = new List<string>();
        foreach (var it in items ?? Enumerable.Empty<WorkItemInfo>())
        {
            var id = it?.Id ?? it?.Identifier ?? "";
            var cat = it?.StateCategory ?? "";
            var st = it?.Status ?? "";
            var aid = it?.AssigneeId ?? "";
            var sp = it?.StoryPoints ?? 0;
            var pn = it?.ParticipantNames?.Count ?? 0;
            var wn = it?.WatcherNames?.Count ?? 0;
            list.Add($"{id}|{cat}|{st}|{aid}|{sp}|{pn}|{wn}");
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join(";", list);
    }

    private static string MapCategoryToStateTypeForPatch(string category)
    {
        var c = (category ?? "").Trim();
        if (string.Equals(c, "进行中", StringComparison.OrdinalIgnoreCase))
        {
            return "in_progress";
        }

        if (string.Equals(c, "已修复", StringComparison.OrdinalIgnoreCase))
        {
            return "in_progress";
        }

        if (string.Equals(c, "可测试", StringComparison.OrdinalIgnoreCase))
        {
            return "in_progress";
        }

        if (string.Equals(c, "测试中", StringComparison.OrdinalIgnoreCase))
        {
            return "in_progress";
        }

        if (string.Equals(c, "待完善", StringComparison.OrdinalIgnoreCase))
        {
            return "in_progress";
        }

        if (string.Equals(c, "已完成", StringComparison.OrdinalIgnoreCase))
        {
            return "done";
        }

        if (string.Equals(c, "已关闭", StringComparison.OrdinalIgnoreCase))
        {
            return "closed";
        }

        return "pending";
    }

    private static string MapStateNameToCategory(string name)
    {
        var s = (name ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s))
        {
            return "未开始";
        }

        if (s.Contains("关闭") || s.Contains("已拒绝"))
        {
            return "已关闭";
        }

        if (s.Contains("已完成") || s.Contains("已发布"))
        {
            return "已完成";
        }

        if (s.Contains("测试中"))
        {
            return "测试中";
        }

        if (s.Contains("可测试") || s.Contains("已修复"))
        {
            return "可测试";
        }

        if (s.Contains("重新打开") || s.Contains("进行中") || s.Contains("处理中") || s.Contains("待完善") || s.Contains("开发中") || s.Contains("挂起"))
        {
            return "进行中";
        }

        if (s.Contains("新提交") || s.Contains("打开") || s.Contains("未开始") || s.Contains("新建") || s.Contains("待处理") || s.Contains("todo"))
        {
            return "未开始";
        }

        return "未开始";
    }

    private static List<string> GetPriorityNamesForCategory(string category)
    {
        var c = (category ?? "").Trim();
        if (string.Equals(c, "未开始", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { "新提交", "打开", "未开始", "新建", "待处理" };
        }

        if (string.Equals(c, "进行中", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { "待完善", "处理中", "重新打开", "进行中" };
        }

        if (string.Equals(c, "可测试", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { "已修复", "可测试" };
        }

        if (string.Equals(c, "测试中", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { "测试中" };
        }

        if (string.Equals(c, "已完成", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { "已完成", "已发布" };
        }

        if (string.Equals(c, "已关闭", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { "关闭", "已拒绝" };
        }

        return new List<string>();
    }

    private void SetRefreshInterval(TimeSpan interval)
    {
        if (refreshTimer != null)
        {
            refreshTimer.Interval = interval;
        }
    }

    private async Task LoadWorkItemsAsync()
    {
        try
        {
            Overlay.IsBusy = true;
            allItems = await api.GetIterationWorkItemsAsync(iterationId);
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
            foreach (var it in allItems ?? Enumerable.Empty<WorkItemInfo>())
            {
                var id = (it.AssigneeId ?? "").Trim().ToLowerInvariant();
                var nm = (it.AssigneeName ?? "").Trim().ToLowerInvariant();
                var key = !string.IsNullOrEmpty(id) ? id : nm;
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                pointsByPerson[key] = (pointsByPerson.TryGetValue(key, out var v) ? v : 0) + it.StoryPoints;
            }

            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<Entity>();
            list.Add(new Entity { Id = "*", Name = "全部" });
            foreach (var kv in pointsByPerson.Where(kv => kv.Value > 0.0))
            {
                var one = allItems.FirstOrDefault(i =>
                                                       string.Equals((i.AssigneeId ?? "").Trim(), kv.Key, StringComparison.OrdinalIgnoreCase) ||
                                                       string.Equals((i.AssigneeName ?? "").Trim(), kv.Key, StringComparison.OrdinalIgnoreCase));
                var id = (one?.AssigneeId ?? kv.Key) ?? kv.Key;
                var nm = (one?.AssigneeName ?? id) ?? id;
                if (kept.Add((id ?? nm).ToLowerInvariant()))
                {
                    list.Add(new Entity { Id = id, Name = nm });
                }
            }

            var desired = list
                          .OrderBy(x => string.Equals(x.Name, "全部", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(x.Id, "*", StringComparison.OrdinalIgnoreCase)
                                            ? "\0"
                                            : x.Name ?? x.Id).ToList();
            var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Members.Count; i++)
            {
                var k = (Members[i]?.Id ?? Members[i]?.Name ?? "") ?? "";
                indexByKey[k] = i;
            }

            var desiredKeys = new HashSet<string>(desired.Select(x => x.Id ?? x.Name ?? ""), StringComparer.OrdinalIgnoreCase);
            var toRemove = Members.Where(m => !desiredKeys.Contains(m?.Id ?? m?.Name ?? "")).ToList();
            foreach (var r in toRemove)
            {
                Members.Remove(r);
            }

            for (int di = 0; di < desired.Count; di++)
            {
                var key = desired[di].Id ?? desired[di].Name ?? "";
                if (indexByKey.TryGetValue(key, out var idx))
                {
                    if (idx != di)
                    {
                        Members.Move(idx, di);
                    }
                }
                else
                {
                    Members.Insert(di, desired[di]);
                }

                indexByKey[Members[di]?.Id ?? Members[di]?.Name ?? ""] = di;
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
            foreach (var it in allItems ?? Enumerable.Empty<WorkItemInfo>())
            {
                foreach (var nm in it.ParticipantNames ?? new List<string>())
                {
                    var name = (nm ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        set.Add(name);
                    }
                }

                foreach (var nm in it.WatcherNames ?? new List<string>())
                {
                    var name = (nm ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        set.Add(name);
                    }
                }
            }

            var desired = new List<string> { "无" };
            desired.AddRange(set.OrderBy(x => x));
            var indexByVal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Participants.Count; i++)
            {
                var k = Participants[i] ?? "";
                indexByVal[k] = i;
            }

            var desiredKeys = new HashSet<string>(desired, StringComparer.OrdinalIgnoreCase);
            var toRemove = Participants.Where(p => !desiredKeys.Contains(p ?? "")).ToList();
            foreach (var r in toRemove)
            {
                Participants.Remove(r);
            }

            for (int di = 0; di < desired.Count; di++)
            {
                var key = desired[di] ?? "";
                if (indexByVal.TryGetValue(key, out var idx))
                {
                    if (idx != di)
                    {
                        Participants.Move(idx, di);
                    }
                }
                else
                {
                    Participants.Insert(di, key);
                }

                indexByVal[Participants[di] ?? ""] = di;
            }
        }
        catch
        {
        }
    }

    private IEnumerable<WorkItemInfo> ApplyCurrentFilter(IEnumerable<WorkItemInfo> items)
    {
        IEnumerable<WorkItemInfo> filtered = items;
        var participantActive = !string.IsNullOrWhiteSpace(SelectedParticipant) &&
                                !string.Equals(SelectedParticipant.Trim(), "无", StringComparison.OrdinalIgnoreCase);
        if (participantActive)
        {
            var target = SelectedParticipant.Trim();
            filtered = filtered.Where(i =>
                                          (i.ParticipantNames ?? new List<string>()).Any(n => string.Equals((n ?? "").Trim(),
                                                                                                            target,
                                                                                                            StringComparison.OrdinalIgnoreCase)) ||
                                          (i.WatcherNames ?? new List<string>()).Any(n => string.Equals((n ?? "").Trim(),
                                                                                                        target,
                                                                                                        StringComparison.OrdinalIgnoreCase)));
            return filtered;
        }

        if (!((SelectedMember == null) || (SelectedMember.Id == "*") || ((SelectedMember.Name ?? "").Trim() == "全部")))
        {
            var id = (SelectedMember.Id ?? "").Trim().ToLowerInvariant();
            var nm = (SelectedMember.Name ?? "").Trim().ToLowerInvariant();
            filtered = filtered.Where(i =>
            {
                var iid = (i.AssigneeId ?? "").Trim().ToLowerInvariant();
                var inm = (i.AssigneeName ?? "").Trim().ToLowerInvariant();
                return (!string.IsNullOrEmpty(iid) && (iid == id)) || (!string.IsNullOrEmpty(inm) && (inm == nm));
            });
        }

        return filtered;
    }

    private async Task RefreshWorkItemsAsync()
    {
        if (refreshing)
        {
            return;
        }

        refreshing = true;
        try
        {
            if (!IsVisible || (WindowState == WindowState.Minimized))
            {
                return;
            }

            var latest = await api.GetIterationWorkItemsAsync(iterationId);
            var latestList = latest ?? new List<WorkItemInfo>();
            var latestSig = ComputeItemsSignature(latestList);
            if (string.Equals(latestSig, lastItemsSignature, StringComparison.Ordinal))
            {
                if (DateTime.UtcNow > fastRefreshUntil)
                {
                    SetRefreshInterval(baseRefreshInterval);
                }

                return;
            }

            lastItemsSignature = latestSig;
            allItems = latestList;
            var prev = SelectedMember;
            RebuildMembersFromItems();
            if ((prev != null) && Members.Any(m => string.Equals(m.Id, prev.Id, StringComparison.OrdinalIgnoreCase)))
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

            if (DateTime.UtcNow > fastRefreshUntil)
            {
                SetRefreshInterval(baseRefreshInterval);
            }
        }
        catch
        {
        }
        finally
        {
            refreshing = false;
        }
    }

    private void ApplyFilterAndBuildColumns()
    {
        IEnumerable<WorkItemInfo> src = ApplyCurrentFilter(allItems ?? Enumerable.Empty<WorkItemInfo>());
        BuildColumns(src);
    }

    private void BuildColumns(IEnumerable<WorkItemInfo> items)
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

        if (Columns == null)
        {
            Columns = new ObservableCollection<KanbanColumn>();
        }

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
            if ((idx >= 0) && (idx != i))
            {
                Columns.Move(idx, i);
            }
        }

        foreach (var col in Columns)
        {
            var title = col.Title ?? "";
            List<WorkItemInfo> target;
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

            var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int ii = 0; ii < col.Items.Count; ii++)
            {
                var k = col.Items[ii]?.Id ?? col.Items[ii]?.Identifier ?? "";
                if (!string.IsNullOrWhiteSpace(k))
                {
                    indexByKey[k] = ii;
                }
            }

            foreach (var it in target)
            {
                var key = it?.Id ?? it?.Identifier ?? "";
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                int idx;
                if (indexByKey.TryGetValue(key, out idx))
                {
                    var existingItem = col.Items[idx];
                    if (!ReferenceEquals(existingItem, it) && (existingItem != null))
                    {
                        existingItem.UpdateFrom(it);
                    }

                    indexByKey[key] = idx;
                }
                else
                {
                    col.Items.Add(it);
                    indexByKey[key] = col.Items.Count - 1;
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
        dragStart = e.GetPosition(null);
        dragInit = true;
    }

    private void Item_MouseMove(object sender, MouseEventArgs e)
    {
        if ((e.LeftButton != MouseButtonState.Pressed) || !dragInit)
        {
            return;
        }

        var pos = e.GetPosition(null);
        if ((Math.Abs(pos.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance) &&
            (Math.Abs(pos.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance))
        {
            return;
        }

        var fe = sender as FrameworkElement;
        var item = fe?.DataContext as WorkItemInfo;
        if (item == null)
        {
            return;
        }

        dragInit = false;
        DragDrop.DoDragDrop(fe, new DataObject(typeof(WorkItemInfo), item), DragDropEffects.Move);
    }

    private async void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragInit)
        {
            return;
        }

        dragInit = false;
        try
        {
            var fe = sender as FrameworkElement;
            var item = fe?.DataContext as WorkItemInfo;
            if (item == null)
            {
                return;
            }

            Overlay.IsBusy = true;
            var details = await api.GetWorkItemDetailsAsync(item.Id);
            if (details != null)
            {
                var win = new WorkItemDetailsWindow(details, api);
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
        if (e.Data.GetDataPresent(typeof(WorkItemInfo)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private async void Column_Drop(object sender, DragEventArgs e)
    {
        if (handlingDrop)
        {
            e.Handled = true;
            return;
        }

        if (!e.Data.GetDataPresent(typeof(WorkItemInfo)))
        {
            return;
        }

        var item = e.Data.GetData(typeof(WorkItemInfo)) as WorkItemInfo;
        var dest = (sender as FrameworkElement)?.DataContext as KanbanColumn;
        if ((item == null) || (dest == null))
        {
            return;
        }

        var current = item.StateCategory ?? "";
        var target = dest.Title ?? "";
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var src = Columns.FirstOrDefault(c => c.Items.Contains(item));
        if (src == null)
        {
            return;
        }

        try
        {
            handlingDrop = true;
            Overlay.IsBusy = true;
            var targetStateId = await ResolveTargetStateIdAsync(item, target);
            var ok = false;
            if ((targetStateId != null) && !string.IsNullOrWhiteSpace(targetStateId.Value.Item1))
            {
                ok = await api.UpdateWorkItemStateByIdAsync(item.Id, targetStateId.Value.Item1);
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

                fastRefreshUntil = DateTime.UtcNow.AddSeconds(30);
                SetRefreshInterval(fastRefreshInterval);
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
            handlingDrop = false;
            e.Handled = true;
        }
    }

    private async Task<(string, string)?> ResolveTargetStateIdAsync(WorkItemInfo item, string targetCategory)
    {
        if ((item == null) || string.IsNullOrWhiteSpace(targetCategory))
        {
            return null;
        }

        var type = (item.Type ?? "").Trim();
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        var projectId = (item.ProjectId ?? "").Trim();

        var targetType = MapCategoryToStateTypeForPatch(targetCategory);
        var planKey = $"{projectId}|{type}";
        if (!planCache.TryGetValue(planKey, out var plan))
        {
            var plans = await api.GetWorkItemStatePlansAsync(projectId);
            plan = plans.FirstOrDefault(p => string.Equals((p?.WorkItemType ?? "").Trim(), type, StringComparison.OrdinalIgnoreCase));
            if ((plan == null) || string.IsNullOrWhiteSpace(plan.Id))
            {
                return null;
            }

            planCache[planKey] = plan;
        }

        var flowsKey = $"{plan.Id}|{item.StateId}";
        if (!flowsCache.TryGetValue(flowsKey, out var flows))
        {
            flows = await api.GetWorkItemStateFlowsAsync(plan.Id, item.StateId);
            flowsCache[flowsKey] = flows ?? new List<StateDto>();
        }

        if ((flows == null) || (flows.Count == 0))
        {
            return null;
        }

        flows.RemoveAll(x => (x?.Name ?? "").Contains("挂起") || (x?.Name ?? "").Contains("受阻"));
        var candidates = flows.Where(f => string.Equals(MapStateNameToCategory(f?.Name), targetCategory, StringComparison.OrdinalIgnoreCase))
                              .ToList();
        var priority = GetPriorityNamesForCategory(targetCategory);
        foreach (var pn in priority)
        {
            var m = candidates.FirstOrDefault(f => string.Equals(f?.Name ?? "", pn, StringComparison.OrdinalIgnoreCase));
            if ((m != null) && !string.IsNullOrWhiteSpace(m.Id))
            {
                return (m.Id, m.Name);
            }
        }

        // var byType = flows.FirstOrDefault(s => string.Equals(s?.Type ?? "", targetType, StringComparison.OrdinalIgnoreCase));
        // if (byType != null && !string.IsNullOrWhiteSpace(byType.Id)) return (byType.Id, byType.Name);
        return null;
    }
}