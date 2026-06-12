using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PackageManager.Features.DailyLog.Models;
using PackageManager.Features.DailyLog.Services;
using PackageManager.Services;
using PackageManager.Services.PingCode.Dto;

namespace PackageManager.Features.DailyLog.Views
{
    /// <summary>
    /// 工作日报页面，支持自动采集 Git/SVN 提交与 PingCode 工作项，生成可编辑日报。
    /// </summary>
    public partial class DailyLogPage : Page
    {
        private readonly GitLogCollectorService gitCollector = new GitLogCollectorService();
        private readonly SvnLogCollectorService svnCollector = new SvnLogCollectorService();
        private readonly PingCodeTodoService pingCodeTodo = new PingCodeTodoService();
        private readonly DailyLogGeneratorService generator = new DailyLogGeneratorService();

        /// <summary>
        /// 初始化 <see cref="DailyLogPage"/>。
        /// </summary>
        public DailyLogPage()
        {
            InitializeComponent();
            DatePick.SelectedDate = DateTime.Today;
        }

        private async void Generate_Click(object sender, RoutedEventArgs e)
        {
            var date = DatePick.SelectedDate ?? DateTime.Today;
            StatusText.Text = "正在采集数据...";
            LogTextBox.Text = "";

            try
            {
                var settings = ServiceLocator.Resolve<DataPersistenceService>().LoadSettings();
                var repos = settings?.CodeRepositories ?? new List<PackageManager.Features.CodeWorkspace.Models.CodeRepository>();

                var allGit = new List<DailyLogEntry>();
                var allSvn = new List<DailyLogEntry>();

                await Task.Run(() =>
                {
                    foreach (var repo in repos)
                    {
                        if (string.IsNullOrWhiteSpace(repo.Path))
                        {
                            continue;
                        }

                        if (System.IO.Directory.Exists(System.IO.Path.Combine(repo.Path, ".git")))
                        {
                            allGit.AddRange(gitCollector.Collect(repo.Path, date));
                        }

                        if (System.IO.Directory.Exists(System.IO.Path.Combine(repo.Path, ".svn")))
                        {
                            allSvn.AddRange(svnCollector.Collect(repo.Path, date));
                        }
                    }
                });

                StatusText.Text = $"Git: {allGit.Count} 条, SVN: {allSvn.Count} 条, 正在查询 PingCode...";

                List<WorkItemInfo> todoItems = null;
                try
                {
                    todoItems = await pingCodeTodo.GetTodoItemsAsync();
                }
                catch
                {
                    todoItems = new List<WorkItemInfo>();
                }

                var logText = generator.Generate(date, allGit, allSvn, todoItems);
                LogTextBox.Text = logText;
                LogTextBox.Focus();
                LogTextBox.SelectAll();

                StatusText.Text = $"已生成日报 (Git: {allGit.Count}, SVN: {allSvn.Count}, 明日计划: {todoItems?.Count ?? 0})";
            }
            catch (Exception ex)
            {
                StatusText.Text = "生成失败: " + ex.Message;
                LogTextBox.Text = $"生成日报时出错:\n{ex.Message}\n\n{ex.StackTrace}";
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(LogTextBox.Text))
            {
                MessageBox.Show("请先生成日报内容。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                Clipboard.SetText(LogTextBox.Text);
                StatusText.Text = "已复制到剪贴板";
            }
            catch (Exception ex)
            {
                StatusText.Text = "复制失败: " + ex.Message;
            }
        }
    }
}
