using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PackageManager.Models;
using PackageManager.Services;
using PackageManager.Services.PingCode;
using PackageManager.Services.PingCode.Model;

namespace PackageManager.Views.KanBan;

/// <summary>
/// 看板统计页面，按人员汇总迭代故事点数据。
/// </summary>
public partial class KanbanStatsPage : Page, ICentralPage, INotifyPropertyChanged
{
    private readonly PingCodeApiService api;

    private bool loading;

    private ObservableCollection<MemberStatsItem> statsRows = new();

    private Entity selectedProject;

    private Entity selectedUser;

    private Entity selectedIteration;

    private string resultTextContent;

    /// <summary>
    /// 初始化 <see cref="KanbanStatsPage"/> 的新实例。
    /// </summary>
    public KanbanStatsPage()
    {
        InitializeComponent();
        api = new PingCodeApiService();
        DataContext = this;
    }

    /// <summary>
    /// 请求退出当前页面的导航事件。
    /// </summary>
    public event Action RequestExit;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// 获取项目成员（用户）列表。
    /// </summary>
    public ObservableCollection<Entity> Users { get; } = new();

    /// <summary>
    /// 获取可选项目列表。
    /// </summary>
    public ObservableCollection<Entity> Projects { get; } = new();

    /// <summary>
    /// 获取可选迭代列表。
    /// </summary>
    public ObservableCollection<Entity> Iterations { get; } = new();

    /// <summary>
    /// 获取或设置查询结果统计行数据。
    /// </summary>
    public ObservableCollection<MemberStatsItem> StatsRows
    {
        get => statsRows;

        set
        {
            if (!ReferenceEquals(statsRows, value))
            {
                statsRows = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 获取或设置当前选中的项目。
    /// </summary>
    public Entity SelectedProject
    {
        get => selectedProject;

        set
        {
            if (!Equals(selectedProject, value))
            {
                selectedProject = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 获取或设置当前选中的用户。
    /// </summary>
    public Entity SelectedUser
    {
        get => selectedUser;

        set
        {
            if (!Equals(selectedUser, value))
            {
                selectedUser = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 获取或设置当前选中的迭代。
    /// </summary>
    public Entity SelectedIteration
    {
        get => selectedIteration;

        set
        {
            if (!Equals(selectedIteration, value))
            {
                selectedIteration = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 获取或设置查询结果摘要文本。
    /// </summary>
    public string ResultTextContent
    {
        get => resultTextContent;

        set
        {
            if (!Equals(resultTextContent, value))
            {
                resultTextContent = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 触发 <see cref="PropertyChanged"/> 事件。
    /// </summary>
    /// <param name="name">发生更改的属性名称，默认为调用方成员名。</param>
    protected void OnPropertyChanged([CallerMemberName] string name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (loading)
        {
            return;
        }

        loading = true;
        try
        {
            Projects.Clear();
            Iterations.Clear();

            var projects = await api.GetProjectsAsync();
            foreach (var p in projects.OrderBy(x => x.Name ?? x.Id))
            {
                Projects.Add(p);
            }

            if (Projects.Count > 0)
            {
                var preferred = Projects.FirstOrDefault(x => (x.Name ?? "").Contains("建模组"));
                SelectedProject = preferred ?? Projects.First();
                var t1 = LoadMembersForProject();
                var t2 = LoadIterationsForProject();
                await Task.WhenAll(t1, t2);
                if (Iterations.Count == 0)
                {
                    ResultTextContent = "该项目没有进行中的迭代";
                }
            }
        }
        catch (Exception ex)
        {
            var msg = FormatFriendlyError(ex);
            LoggingService.LogError(ex, "加载PingCode数据失败");
            MessageBox.Show(msg, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            loading = false;
        }
    }

    private async void QueryButton_Click(object sender, RoutedEventArgs e)
    {
        await QueryAsync();
    }

    private async Task QueryAsync()
    {
        try
        {
            Overlay.IsBusy = true;
            if (Iterations.Count == 0)
            {
                return;
            }

            var iter = SelectedIteration;
            var proj = SelectedProject;
            if ((iter == null) || (proj == null))
            {
                MessageBox.Show("请先选择项目与迭代", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            double total = 0;
            ResultTextContent = "查询中...";
            var rows = new ObservableCollection<MemberStatsItem>();
            var aggregates = await api.GetIterationStoryPointsBreakdownByAssigneeAsync(iter.Id);
            var users = Users.GroupBy(x => x.Id).Select(g => g.First()).ToList();
            foreach (var u in users)
            {
                var keyId = (u.Id ?? "").Trim().ToLowerInvariant();
                var keyName = (u.Name ?? "").Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(keyId) && string.IsNullOrEmpty(keyName))
                {
                    continue;
                }

                if (aggregates.TryGetValue(keyId, out var b) || aggregates.TryGetValue(keyName, out b))
                {
                    if (b.Total > 0)
                    {
                        var closedPoints = b.Closed;
                        var validTotal = b.Total - closedPoints;
                        rows.Add(new MemberStatsItem
                        {
                            MemberName = u.Name ?? u.Id,
                            NotStarted = b.NotStarted,
                            InProgress = b.InProgress,
                            Done = b.Done,
                            Closed = closedPoints,
                            HighestPriorityCount = b.HighestPriorityCount,
                            HighestPriorityPoints = b.HighestPriorityPoints,
                            HigherPriorityCount = b.HigherPriorityCount,
                            HigherPriorityPoints = b.HigherPriorityPoints,
                            OtherPriorityCount = b.OtherPriorityCount,
                            OtherPriorityPoints = b.OtherPriorityPoints,
                            Total = validTotal,
                        });
                        total += validTotal;
                    }
                }
            }

            StatsRows = rows;
            ResultTextContent = $"统计完成：{rows.Count} 人员，故事点总数：{total}";
        }
        catch (Exception ex)
        {
            var msg = FormatFriendlyError(ex);
            LoggingService.LogError(ex, "查询故事点失败");
            MessageBox.Show(msg, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            ResultTextContent = string.Empty;
        }
        finally
        {
            Overlay.IsBusy = false;
        }
    }

    private async void UserCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private async void IterationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private async void ProjectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await LoadMembersForProject();
        await LoadIterationsForProject();
        if (Iterations.Count > 0)
        {
            await QueryAsync();
        }
        else
        {
            ResultTextContent = "该项目没有进行中的迭代";
        }
    }

    private async Task LoadMembersForProject()
    {
        try
        {
            var proj = SelectedProject;
            if (proj == null)
            {
                return;
            }

            var members = await api.GetProjectMembersAsync(proj.Id);
            Users.Clear();
            foreach (var m in members.GroupBy(x => x.Id).Select(g => g.First()).OrderBy(x => x.Name ?? x.Id))
            {
                Users.Add(m);
            }

            SelectedUser = Users.FirstOrDefault();
            StatsRows = new ObservableCollection<MemberStatsItem>();
        }
        catch (Exception ex)
        {
            var msg = FormatFriendlyError(ex);
            LoggingService.LogError(ex, "加载项目成员失败");
            MessageBox.Show(msg, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadIterationsForProject()
    {
        try
        {
            var proj = SelectedProject;
            if (proj == null)
            {
                return;
            }

            // var iters = await api.GetOngoingIterationsByProjectAsync(proj.Id);
            var iters = await api.GetNotCompletedIterationsByProjectAsync(proj.Id);
            Iterations.Clear();
            foreach (var it in iters.GroupBy(x => x.Id).Select(g => g.First()).OrderBy(x => x.Name ?? x.Id))
            {
                Iterations.Add(it);
            }

            SelectedIteration = Iterations.FirstOrDefault();
            StatsRows = new ObservableCollection<MemberStatsItem>();
        }
        catch (Exception ex)
        {
            var msg = FormatFriendlyError(ex);
            LoggingService.LogError(ex, "加载迭代失败");
            MessageBox.Show(msg, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string FormatFriendlyError(Exception ex)
    {
        var t = ex.GetType().Name;
        var m = ex.Message ?? "";
        if (t.Contains("ApiAuthException") || m.Contains("401"))
        {
            return "身份认证失败，请检查 ClientID 或 Secret 是否正确，或令牌是否过期。";
        }

        if (t.Contains("ApiForbiddenException") || m.Contains("403"))
        {
            return "没有权限访问当前资源，请确认当前账户的接口权限。";
        }

        if (t.Contains("ApiNotFoundException") || m.Contains("404"))
        {
            return "请求的资源不存在，可能项目或迭代标识不正确。";
        }

        return "操作失败，请稍后重试：" + m;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        RequestExit?.Invoke();
    }

    private void OpenKanbanWindow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var iter = SelectedIteration;
            var proj = SelectedProject;
            if ((iter == null) || (proj == null))
            {
                MessageBox.Show("请先选择项目与迭代", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var win = new WorkItemKanbanWindow(iter.Id, Users.ToList(), SelectedUser);
            // win.Owner = Application.Current?.MainWindow;
            win.Show();
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "打开迭代看板失败");
            MessageBox.Show("打开迭代看板失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}