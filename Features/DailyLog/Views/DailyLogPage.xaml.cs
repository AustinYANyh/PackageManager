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
using PackageManager.Services.PingCode.Model;

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
        private readonly DailyLogIterationStore iterationStore = new DailyLogIterationStore();

        private bool suppressDraftSave;
        private bool suppressIterationPersist;
        private bool iterationsLoaded;
        private Entity scopeProject;
        private List<Entity> scopeIterations = new List<Entity>();

        private sealed class IterationOption
        {
            public Entity Iteration { get; set; }

            public string Display { get; set; }
        }

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
            var iterationOption = IterationPick.SelectedItem as IterationOption;
            var iterationId = iterationOption?.Iteration?.Id;
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
                    todoItems = await pingCodeTodo.GetTodoItemsAsync(iterationId);
                }
                catch
                {
                    todoItems = new List<WorkItemInfo>();
                }

                try
                {
                    completedItems = await pingCodeTodo.GetCompletedItemsAsync(date, iterationId);
                }
                catch
                {
                    completedItems = new List<WorkItemInfo>();
                }

                var logText = generator.Generate(date, allGit, allSvn, completedItems, todoItems);
                SetLogText(formatter.Format(logText));
                var scopeLabel = iterationOption?.Iteration == null
                    ? "数据源: 自动迭代"
                    : $"数据源: {scopeProject?.Name ?? "?"}/{iterationOption.Iteration.Name}";
                StatusText.Text = $"已生成日报 (Git: {allGit.Count}, SVN: {allSvn.Count}, PingCode完成: {completedItems?.Count ?? 0}, 明日计划: {todoItems?.Count ?? 0}, {scopeLabel})";
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
            LoadIterationsAsync();

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

        private async void LoadIterationsAsync()
        {
            if (iterationsLoaded)
            {
                return;
            }

            iterationsLoaded = true;
            try
            {
                scopeProject = await pingCodeTodo.GetDefaultProjectAsync();
                if (scopeProject == null || string.IsNullOrWhiteSpace(scopeProject.Id))
                {
                    StatusText.Text = "未找到可用 PingCode 项目，明日计划可能为空";
                    return;
                }

                scopeIterations = await pingCodeTodo.GetProjectIterationsAsync(scopeProject.Id);
                if (scopeIterations.Count == 0)
                {
                    StatusText.Text = $"项目「{scopeProject.Name}」没有未完成的迭代";
                    return;
                }

                var stored = iterationStore.Load();
                var recommended = PingCodeTodoService.SelectRecommendedIteration(scopeIterations, stored?.IterationId, DateTime.Today, out var storedMissing);

                suppressIterationPersist = true;
                try
                {
                    IterationPick.Items.Clear();
                    foreach (var iteration in scopeIterations)
                    {
                        IterationPick.Items.Add(new IterationOption
                        {
                            Iteration = iteration,
                            Display = FormatIterationDisplay(iteration),
                        });
                    }

                    IterationPick.SelectedIndex = recommended == null
                        ? -1
                        : scopeIterations.FindIndex(iteration => string.Equals(iteration.Id, recommended.Id, StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    suppressIterationPersist = false;
                }

                if (IterationPick.SelectedItem is IterationOption selected)
                {
                    PersistIteration(selected);
                }

                if (storedMissing && IterationPick.SelectedItem is IterationOption current)
                {
                    StatusText.Text = $"上次选择的迭代已结束，已切换为「{current.Iteration.Name}」";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "迭代加载失败，生成时将自动选择迭代: " + ex.Message;
            }
        }

        private static string FormatIterationDisplay(Entity iteration)
        {
            var name = iteration?.Name ?? string.Empty;
            var start = FromUnixSeconds(iteration?.StartAt);
            var end = FromUnixSeconds(iteration?.EndAt);
            if (start == null && end == null)
            {
                return name;
            }

            var startText = start?.ToString("MM-dd") ?? "?";
            var endText = end?.ToString("MM-dd") ?? "?";
            return $"{name}（{startText} ~ {endText}）";
        }

        private static DateTime? FromUnixSeconds(long? seconds)
        {
            return seconds.HasValue ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value).LocalDateTime : (DateTime?)null;
        }

        private void IterationPick_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressIterationPersist)
            {
                return;
            }

            if (IterationPick.SelectedItem is IterationOption option)
            {
                PersistIteration(option);
            }
        }

        private void PersistIteration(IterationOption option)
        {
            if (option?.Iteration == null || string.IsNullOrWhiteSpace(option.Iteration.Id) || string.IsNullOrWhiteSpace(scopeProject?.Id))
            {
                return;
            }

            iterationStore.Save(new DailyLogIterationSelection
            {
                ProjectId = scopeProject.Id,
                IterationId = option.Iteration.Id,
                IterationName = option.Iteration.Name,
            });
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
            if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            {
                ClearCurrentLine();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.D || Keyboard.Modifiers != ModifierKeys.Control)
            {
                return;
            }

            SetLogText(formatter.Format(LogTextBox.Text));
            LogTextBox.CaretIndex = LogTextBox.Text.Length;
            StatusText.Text = "已格式化日报";
            e.Handled = true;
        }

        private void ClearCurrentLine()
        {
            var text = LogTextBox.Text ?? string.Empty;
            var caretIndex = LogTextBox.CaretIndex;
            if (caretIndex < 0)
            {
                caretIndex = 0;
            }

            if (caretIndex > text.Length)
            {
                caretIndex = text.Length;
            }

            var lineStart = text.LastIndexOf('\n', Math.Max(0, caretIndex - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;

            var lineEnd = text.IndexOf('\n', caretIndex);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            if (lineEnd <= lineStart)
            {
                return;
            }

            var cleared = text.Remove(lineStart, lineEnd - lineStart);
            SetLogText(cleared);
            LogTextBox.CaretIndex = Math.Min(lineStart, LogTextBox.Text.Length);
            StatusText.Text = "已清空当前行";
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
