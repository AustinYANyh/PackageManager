using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        private const string GitAuthorFilter = "AustinYanyh";
        private const string SvnAuthorFilter = "yanyunhao";

        private readonly GitLogCollectorService gitCollector = new GitLogCollectorService();
        private readonly SvnLogCollectorService svnCollector = new SvnLogCollectorService();
        private readonly PingCodeTodoService pingCodeTodo = new PingCodeTodoService();
        private readonly DailyLogGeneratorService generator = new DailyLogGeneratorService();
        private readonly DailyLogFormatterService formatter = new DailyLogFormatterService();
        private readonly DailyLogDraftStore draftStore = new DailyLogDraftStore();

        private bool suppressDraftSave;

        /// <summary>
        /// 初始化 <see cref="DailyLogPage"/>。
        /// </summary>
        public DailyLogPage()
        {
            InitializeComponent();
            DatePick.SelectedDate = DateTime.Today;
            Loaded += Page_Loaded;
            Unloaded += Page_Unloaded;
            LogTextBox.TextChanged += LogTextBox_TextChanged;
            LogTextBox.PreviewKeyDown += LogTextBox_PreviewKeyDown;
        }

        private async void Generate_Click(object sender, RoutedEventArgs e)
        {
            var date = DatePick.SelectedDate ?? DateTime.Today;
            StatusText.Text = "正在采集数据...";

            try
            {
                var settings = ServiceLocator.Resolve<DataPersistenceService>()?.LoadSettings();
                var repos = settings?.CodeRepositories ?? new List<PackageManager.Features.CodeWorkspace.Models.CodeRepository>();

                var allGit = new List<DailyLogEntry>();
                var allSvn = new List<DailyLogEntry>();

                await Task.Run(() =>
                {
                    foreach (var repoPath in EnumerateRepositoryRoots(repos))
                    {
                        if (HasGitMetadata(repoPath))
                        {
                            allGit.AddRange(gitCollector.Collect(repoPath, date, GitAuthorFilter));
                        }

                        if (Directory.Exists(Path.Combine(repoPath, ".svn")))
                        {
                            allSvn.AddRange(svnCollector.Collect(repoPath, date, SvnAuthorFilter));
                        }
                    }
                });

                StatusText.Text = $"Git: {allGit.Count} 条, SVN: {allSvn.Count} 条, 正在查询 PingCode...";

                List<WorkItemInfo> todoItems;
                List<WorkItemInfo> completedItems;
                try
                {
                    todoItems = await pingCodeTodo.GetTodoItemsAsync();
                }
                catch
                {
                    todoItems = new List<WorkItemInfo>();
                }

                try
                {
                    completedItems = await pingCodeTodo.GetCompletedItemsAsync(date);
                }
                catch
                {
                    completedItems = new List<WorkItemInfo>();
                }

                var logText = generator.Generate(date, allGit, allSvn, completedItems, todoItems);
                SetLogText(formatter.Format(logText));
                StatusText.Text = $"已生成日报 (Git: {allGit.Count}, SVN: {allSvn.Count}, PingCode完成: {completedItems?.Count ?? 0}, 明日计划: {todoItems?.Count ?? 0})";
            }
            catch (Exception ex)
            {
                StatusText.Text = "生成失败: " + ex.Message;
                SetLogText($"生成日报时出错:\n{ex.Message}\n\n{ex.StackTrace}");
            }
        }

        private static IEnumerable<string> EnumerateRepositoryRoots(IEnumerable<PackageManager.Features.CodeWorkspace.Models.CodeRepository> repos)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var repo in repos ?? Enumerable.Empty<PackageManager.Features.CodeWorkspace.Models.CodeRepository>())
            {
                if (repo == null || string.IsNullOrWhiteSpace(repo.Path) || !Directory.Exists(repo.Path))
                {
                    continue;
                }

                foreach (var path in EnumerateRepositoryRoots(repo.Path))
                {
                    if (seen.Add(path))
                    {
                        yield return path;
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateRepositoryRoots(string rootPath)
        {
            if (HasGitMetadata(rootPath) || Directory.Exists(Path.Combine(rootPath, ".svn")))
            {
                yield return rootPath;
            }

            foreach (var child in EnumerateCandidateDirectories(rootPath))
            {
                if (HasGitMetadata(child) || Directory.Exists(Path.Combine(child, ".svn")))
                {
                    yield return child;
                    continue;
                }

                foreach (var grandChild in EnumerateCandidateDirectories(child))
                {
                    if (HasGitMetadata(grandChild) || Directory.Exists(Path.Combine(grandChild, ".svn")))
                    {
                        yield return grandChild;
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateCandidateDirectories(string path)
        {
            try
            {
                return Directory.GetDirectories(path)
                    .Where(dir => !ShouldSkipDirectory(Path.GetFileName(dir)))
                    .ToList();
            }
            catch (UnauthorizedAccessException)
            {
                return Enumerable.Empty<string>();
            }
            catch (IOException)
            {
                return Enumerable.Empty<string>();
            }
        }

        private static bool HasGitMetadata(string path)
        {
            return Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));
        }

        private static bool ShouldSkipDirectory(string dirName)
        {
            return string.IsNullOrWhiteSpace(dirName) ||
                   dirName.StartsWith(".", StringComparison.Ordinal) ||
                   string.Equals(dirName, "node_modules", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "bin", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "obj", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "packages", StringComparison.OrdinalIgnoreCase);
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

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            DatePick.SelectedDate = DateTime.Today;

            if (!string.IsNullOrWhiteSpace(LogTextBox.Text))
            {
                return;
            }

            var draft = draftStore.LoadDraft();
            if (string.IsNullOrWhiteSpace(draft))
            {
                return;
            }

            SetLogText(draft, updateDraft: false);
            StatusText.Text = "已恢复上次日报草稿。";
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            SaveDraft(LogTextBox.Text);
        }

        private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (suppressDraftSave)
            {
                return;
            }

            SaveDraft(LogTextBox.Text);
        }

        private void LogTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.D || Keyboard.Modifiers != ModifierKeys.Control)
            {
                return;
            }

            SetLogText(formatter.Format(LogTextBox.Text));
            LogTextBox.CaretIndex = LogTextBox.Text.Length;
            StatusText.Text = "已格式化日报";
            e.Handled = true;
        }

        private void SetLogText(string text, bool updateDraft = true)
        {
            suppressDraftSave = true;
            LogTextBox.Text = text ?? string.Empty;
            suppressDraftSave = false;

            if (updateDraft)
            {
                SaveDraft(LogTextBox.Text);
            }
        }

        private void SaveDraft(string text)
        {
            if (suppressDraftSave)
            {
                return;
            }

            draftStore.SaveDraft(text);
        }
    }
}
