using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PackageManager.Services;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using PackageManager.Models;

namespace PackageManager.Views
{
    public partial class KanbanStatsPage : Page, ICentralPage, INotifyPropertyChanged
    {
        private readonly PingCodeApiService _api;
        private bool _loading;
        
        public event Action RequestExit;
        public event PropertyChangedEventHandler PropertyChanged;
        
        public ObservableCollection<PingCodeApiService.Entity> Users { get; } = new ObservableCollection<PingCodeApiService.Entity>();
        public ObservableCollection<PingCodeApiService.Entity> Projects { get; } = new ObservableCollection<PingCodeApiService.Entity>();
        public ObservableCollection<PingCodeApiService.Entity> Iterations { get; } = new ObservableCollection<PingCodeApiService.Entity>();
        private ObservableCollection<MemberStatsItem> _statsRows = new ObservableCollection<MemberStatsItem>();
        public ObservableCollection<MemberStatsItem> StatsRows
        {
            get => _statsRows;
            set
            {
                if (!ReferenceEquals(_statsRows, value))
                {
                    _statsRows = value;
                    OnPropertyChanged(nameof(StatsRows));
                }
            }
        }
        private PingCodeApiService.Entity _selectedProject;
        public PingCodeApiService.Entity SelectedProject
        {
            get => _selectedProject;
            set
            {
                if (!Equals(_selectedProject, value))
                {
                    _selectedProject = value;
                    OnPropertyChanged(nameof(SelectedProject));
                }
            }
        }
        private PingCodeApiService.Entity _selectedUser;
        public PingCodeApiService.Entity SelectedUser
        {
            get => _selectedUser;
            set
            {
                if (!Equals(_selectedUser, value))
                {
                    _selectedUser = value;
                    OnPropertyChanged(nameof(SelectedUser));
                }
            }
        }
        private PingCodeApiService.Entity _selectedIteration;
        public PingCodeApiService.Entity SelectedIteration
        {
            get => _selectedIteration;
            set
            {
                if (!Equals(_selectedIteration, value))
                {
                    _selectedIteration = value;
                    OnPropertyChanged(nameof(SelectedIteration));
                }
            }
        }
        private string _resultTextContent;
        public string ResultTextContent
        {
            get => _resultTextContent;
            set
            {
                if (!Equals(_resultTextContent, value))
                {
                    _resultTextContent = value;
                    OnPropertyChanged(nameof(ResultTextContent));
                }
            }
        }
        
        public KanbanStatsPage()
        {
            InitializeComponent();
            _api = new PingCodeApiService();
            DataContext = this;
        }
        
        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _loading = true;
            try
            {
                Projects.Clear();
                Iterations.Clear();
                
                var projects = await _api.GetProjectsAsync();
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
                _loading = false;
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
                if (Iterations.Count == 0) return;
                var iter = SelectedIteration;
                var proj = SelectedProject;
                if (iter == null || proj == null)
                {
                    MessageBox.Show("请先选择项目与迭代", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                double total = 0;
                ResultTextContent = "查询中...";
                var rows = new ObservableCollection<MemberStatsItem>();
                var aggregates = await _api.GetIterationStoryPointsBreakdownByAssigneeAsync(iter.Id);
                var users = Users.GroupBy(x => x.Id).Select(g => g.First()).ToList();
                foreach (var u in users)
                {
                    var keyId = (u.Id ?? "").Trim().ToLowerInvariant();
                    var keyName = (u.Name ?? "").Trim().ToLowerInvariant();
                    if (string.IsNullOrEmpty(keyId) && string.IsNullOrEmpty(keyName)) continue;
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
                                Total = validTotal
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
                if (proj == null) return;
                var members = await _api.GetProjectMembersAsync(proj.Id);
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
                if (proj == null) return;
                var iters = await _api.GetOngoingIterationsByProjectAsync(proj.Id);
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
        
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
                if (iter == null || proj == null)
                {
                    MessageBox.Show("请先选择项目与迭代", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var win = new WorkItemKanbanWindow(iter.Id, Users.ToList(), SelectedUser);
                win.Owner = Application.Current?.MainWindow;
                win.Show();
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "打开迭代看板失败");
                MessageBox.Show("打开迭代看板失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
