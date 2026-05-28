using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using PackageManager.Features.CodeWorkspace.Models;
using PackageManager.Features.CodeWorkspace.Services;
using PackageManager.Models;
using PackageManager.Services;
using PackageManager.Views;

namespace PackageManager.Features.CodeWorkspace.Views
{
    public partial class CodeWorkspacePage : Page, INotifyPropertyChanged, ICentralPage
    {
        private readonly DataPersistenceService _dataPersistenceService;
        private readonly AiCommitSkillService _aiCommitSkillService = new AiCommitSkillService();
        private readonly VcsStatusService _vcsStatusService;
        private readonly CodeWorkspaceVcsCacheService _vcsCacheService;
        private CodeRepository _selectedRepository;
        private string _statusText;
        private string _refreshButtonText = "刷新状态";
        private string _subRepositoryFilter;
        private CancellationTokenSource _autoRefreshCts;
        private bool _hasLoaded;
        private bool _isRefreshingStatus;
        private bool _isCacheEventSubscribed;

        public CodeWorkspacePage()
        {
            InitializeComponent();
            _dataPersistenceService = ServiceLocator.Resolve<DataPersistenceService>() ?? new DataPersistenceService();
            _vcsStatusService = ServiceLocator.Resolve<VcsStatusService>() ?? new VcsStatusService();
            _vcsCacheService = ServiceLocator.Resolve<CodeWorkspaceVcsCacheService>() ?? new CodeWorkspaceVcsCacheService(_dataPersistenceService, _vcsStatusService);
            SubscribeCacheUpdates();
            DataContext = this;
            LoadRepositories();
        }

        public event Action RequestExit;

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<CodeRepository> Repositories { get; } = new ObservableCollection<CodeRepository>();

        public CodeRepository SelectedRepository
        {
            get => _selectedRepository;
            set
            {
                if (SetProperty(ref _selectedRepository, value))
                {
                    SubRepositoryFilter = string.Empty;
                    RefreshSubRepositoryView();
                    RaisePropertyChanged(nameof(HasSelectedRepository));
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public string RefreshButtonText
        {
            get => _refreshButtonText;
            set => SetProperty(ref _refreshButtonText, value);
        }

        public bool CanRefreshStatus => !_isRefreshingStatus;

        public bool HasSelectedRepository => SelectedRepository != null;

        public string RepositoryCountText => $"{Repositories.Count} 个仓库";

        public ICollectionView SubRepositoryView { get; private set; }

        public string SubRepositoryFilter
        {
            get => _subRepositoryFilter;
            set
            {
                if (SetProperty(ref _subRepositoryFilter, value))
                {
                    RefreshSubRepositoryView();
                }
            }
        }

        private void LoadRepositories()
        {
            Repositories.Clear();
            var settings = _dataPersistenceService.LoadSettings();
            foreach (var repo in (settings.CodeRepositories ?? new List<CodeRepository>())
                         .Where(repo => repo != null && !string.IsNullOrWhiteSpace(repo.Path))
                         .OrderByDescending(repo => repo.LastUsed)
                         .ThenBy(repo => repo.Name))
            {
                var cloned = repo.Clone();
                _vcsCacheService.ApplyCachedStatus(cloned);
                SetupRepositoryCommands(cloned);
                Repositories.Add(cloned);
            }

            StatusText = Repositories.Count == 0
                ? "未配置代码仓库，请点击管理仓库添加。"
                : $"已加载 {Repositories.Count} 个仓库。";
            SelectedRepository = Repositories.FirstOrDefault();
            RaisePropertyChanged(nameof(RepositoryCountText));
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            SubscribeCacheUpdates();
            _vcsCacheService.ApplyCachedStatuses(Repositories);
            RefreshSubRepositoryView();
            if (!_hasLoaded)
            {
                _hasLoaded = true;
                if (Repositories.Any(repo => repo.LastStatusRefresh != DateTime.MinValue))
                {
                    StatusText = $"已加载 {Repositories.Count} 个仓库，显示内存缓存状态。";
                }
                else if (_vcsCacheService.IsRefreshRunning)
                {
                    StatusText = $"已加载 {Repositories.Count} 个仓库，后台正在扫描 VCS 状态。";
                }
                else
                {
                    await RefreshAllVcsStatusAsync(forceRefresh: false);
                }
            }

            StartAutoRefresh();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            StopAutoRefresh();
            UnsubscribeCacheUpdates();
        }

        private void SetupRepositoryCommands(CodeRepository repo)
        {
            repo.ClaudeCommitCommand = new RelayCommand(() => RunRepositoryAction(repo, DoClaudeCommit));
            repo.CodexCommitCommand = new RelayCommand(() => RunRepositoryAction(repo, DoCodexCommit));
            repo.PullCommand = new RelayCommand(() => RunRepositoryAction(repo, DoPullRepository));
            repo.OpenVSCommand = new RelayCommand(() => RunRepositoryAction(repo, DoOpenVisualStudio));
            repo.OpenRiderCommand = new RelayCommand(() => RunRepositoryAction(repo, r => DoOpenIde(r, new[] { "Rider", "JetBrains Rider" }, "Rider")));
            repo.OpenCursorCommand = new RelayCommand(() => RunRepositoryAction(repo, DoOpenCursor));
            repo.OpenClaudeCommand = new RelayCommand(() => RunRepositoryAction(repo, DoOpenClaudeCode));
            repo.OpenCodexCommand = new RelayCommand(() => RunRepositoryAction(repo, DoOpenCodex));
            repo.OpenFolderCommand = new RelayCommand(() => RunRepositoryAction(repo, DoOpenFolder));
        }

        private void VcsCacheService_CacheUpdated(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _vcsCacheService.ApplyCachedStatuses(Repositories);
                RefreshSubRepositoryView();
            });
        }

        private void SubscribeCacheUpdates()
        {
            if (_isCacheEventSubscribed)
            {
                return;
            }

            _vcsCacheService.CacheUpdated += VcsCacheService_CacheUpdated;
            _isCacheEventSubscribed = true;
        }

        private void UnsubscribeCacheUpdates()
        {
            if (!_isCacheEventSubscribed)
            {
                return;
            }

            _vcsCacheService.CacheUpdated -= VcsCacheService_CacheUpdated;
            _isCacheEventSubscribed = false;
        }

        private void RepositoryRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null)
            {
                return;
            }

            if ((sender as FrameworkElement)?.DataContext is CodeRepository repo)
            {
                SelectedRepository = repo;
                RunRepositoryAction(repo, DoOpenFolder);
                e.Handled = true;
            }
        }

        private void ActionMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.ContextMenu != null)
            {
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.IsOpen = true;
                e.Handled = true;
            }
        }

        private void RunRepositoryAction(CodeRepository repo, Action<CodeRepository> action)
        {
            if (!EnsureRepositoryExists(repo))
            {
                return;
            }

            try
            {
                action(repo);
                MarkRepositoryUsed(repo);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"代码工作区操作失败：{repo.Path}");
                MessageBox.Show($"操作失败：{ex.Message}", "代码工作区", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ManageRepositoriesButton_Click(object sender, RoutedEventArgs e)
        {
            var page = new CodeRepositoryManagementPage();
            var window = new Window
            {
                Title = "代码仓库管理",
                Content = page,
                Width = 900,
                Height = 560,
                Owner = Window.GetWindow(this),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            page.RequestExit += window.Close;
            window.ShowDialog();
            SyncRepositories();
        }

        private async void RefreshStatusButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAllVcsStatusAsync(forceRefresh: true);
        }

        private async void RefreshSelectedRepositoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRepository == null)
            {
                return;
            }

            StatusText = $"正在刷新仓库状态: {SelectedRepository.Name}";
            try
            {
                SelectedRepository.IsRefreshing = true;
                await _vcsStatusService.RefreshRepositoryStatusAsync(SelectedRepository, forceRefresh: true);
                _vcsCacheService.UpdateCache(SelectedRepository);
                RefreshSubRepositoryView();
                StatusText = $"状态刷新完成 - {SelectedRepository.Name} - {DateTime.Now:HH:mm:ss}";
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"刷新代码仓库状态失败: {SelectedRepository.Path}");
                StatusText = $"状态刷新失败: {ex.Message}";
            }
            finally
            {
                SelectedRepository.IsRefreshing = false;
            }
        }

        private void RefreshProjectFilesButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var repo in Repositories)
            {
                RefreshProjectFiles(repo);
            }

            SaveRepositories();
            StatusText = "项目文件已刷新。";
        }

        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            LoadRepositories();
            _ = RefreshAllVcsStatusAsync(forceRefresh: false);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            StopAutoRefresh();
            RequestExit?.Invoke();
        }

        private async Task RefreshAllVcsStatusAsync(bool forceRefresh = false)
        {
            if (Repositories.Count == 0)
            {
                return;
            }

            StatusText = "正在刷新仓库状态...";
            SetRefreshingStatus(true);
            try
            {
                await _vcsCacheService.RefreshRepositoriesAsync(Repositories, forceRefresh);
                RefreshSubRepositoryView();
                StatusText = $"状态刷新完成 - {DateTime.Now:HH:mm:ss}";
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "刷新代码仓库状态失败");
                StatusText = $"状态刷新失败: {ex.Message}";
            }
            finally
            {
                SetRefreshingStatus(false);
            }
        }

        private async void StartAutoRefresh()
        {
            _autoRefreshCts?.Cancel();
            _autoRefreshCts = new CancellationTokenSource();
            var token = _autoRefreshCts.Token;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), token);
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    await _vcsCacheService.RefreshRepositoriesAsync(Repositories, false, token);
                    Dispatcher.Invoke(RefreshSubRepositoryView);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "自动刷新代码仓库状态失败");
            }
        }

        private void StopAutoRefresh()
        {
            _autoRefreshCts?.Cancel();
        }

        private void SyncRepositories()
        {
            var previousSelectionPath = NormalizePath(SelectedRepository?.Path);
            var existingByPath = Repositories
                .Where(repo => !string.IsNullOrWhiteSpace(repo.Path))
                .GroupBy(repo => NormalizePath(repo.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            Repositories.Clear();
            var settings = _dataPersistenceService.LoadSettings();
            foreach (var repo in (settings.CodeRepositories ?? new List<CodeRepository>())
                         .Where(repo => repo != null && !string.IsNullOrWhiteSpace(repo.Path))
                         .OrderByDescending(repo => repo.LastUsed)
                         .ThenBy(repo => repo.Name))
            {
                var cloned = repo.Clone();
                var key = NormalizePath(cloned.Path);
                if (!string.IsNullOrWhiteSpace(key) && existingByPath.TryGetValue(key, out var existing))
                {
                    cloned.ApplyVcsStatusFrom(existing);
                }
                else
                {
                    _vcsCacheService.ApplyCachedStatus(cloned);
                }

                SetupRepositoryCommands(cloned);
                Repositories.Add(cloned);
            }

            SelectedRepository = Repositories.FirstOrDefault(repo =>
                string.Equals(NormalizePath(repo.Path), previousSelectionPath, StringComparison.OrdinalIgnoreCase))
                ?? Repositories.FirstOrDefault();
            StatusText = Repositories.Count == 0
                ? "未配置代码仓库，请点击管理仓库添加。"
                : $"已同步 {Repositories.Count} 个仓库，保留内存 VCS 状态。";
            RaisePropertyChanged(nameof(RepositoryCountText));
            RefreshSubRepositoryView();
        }

        private void SetRefreshingStatus(bool isRefreshing)
        {
            if (_isRefreshingStatus == isRefreshing)
            {
                return;
            }

            _isRefreshingStatus = isRefreshing;
            RefreshButtonText = isRefreshing ? "刷新中..." : "刷新状态";
            RaisePropertyChanged(nameof(CanRefreshStatus));
        }

        private void RefreshSubRepositoryView()
        {
            var source = SelectedRepository?.SubRepositories;
            SubRepositoryView = source == null ? null : CollectionViewSource.GetDefaultView(source);
            if (SubRepositoryView != null)
            {
                SubRepositoryView.Filter = FilterSubRepository;
                SubRepositoryView.Refresh();
            }

            RaisePropertyChanged(nameof(SubRepositoryView));
        }

        private bool FilterSubRepository(object value)
        {
            if (string.IsNullOrWhiteSpace(SubRepositoryFilter))
            {
                return true;
            }

            if (value is SubRepository subRepository)
            {
                return (subRepository.RelativePath ?? string.Empty)
                    .IndexOf(SubRepositoryFilter, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return false;
        }

        private void DoClaudeCommit(CodeRepository repo)
        {
            DoAiCommit(repo, "Claude", "claude", "claude --dangerously-skip-permissions");
        }

        private void DoCodexCommit(CodeRepository repo)
        {
            DoAiCommit(repo, "Codex", "codex", "codex --sandbox danger-full-access --ask-for-approval never");
        }

        private async void DoPullRepository(CodeRepository repo)
        {
            StatusText = $"正在拉取代码: {repo.Name}";
            try
            {
                repo.IsRefreshing = true;
                var result = await PullRepositoryAsync(repo);
                if (result.HasConflicts)
                {
                    var conflictMessage = BuildConflictMessage(result);
                    var dialogResult = MessageBox.Show(
                        conflictMessage + "\n\n是否打开仓库文件夹？",
                        "拉取代码 - 检测到冲突",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (dialogResult == MessageBoxResult.Yes)
                    {
                        DoOpenFolder(repo);
                    }

                    StatusText = $"拉取完成但有冲突: {repo.Name}";
                }
                else if (result.Success)
                {
                    await _vcsStatusService.RefreshRepositoryStatusAsync(repo, forceRefresh: true);
                    _vcsCacheService.UpdateCache(repo);
                    RefreshSubRepositoryView();
                    StatusText = $"拉取成功: {repo.Name}";
                }
                else
                {
                    StatusText = $"拉取失败: {result.ErrorMessage}";
                    MessageBox.Show($"拉取失败: {result.ErrorMessage}", "拉取代码", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"拉取代码失败: {repo.Path}");
                StatusText = $"拉取失败: {ex.Message}";
                MessageBox.Show($"拉取失败: {ex.Message}", "拉取代码", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                repo.IsRefreshing = false;
            }
        }

        private void DoAiCommit(CodeRepository repo, string engineName, string commandName, string commandPrefix)
        {
            EnsureCommandExists(commandName);
            var skillInfo = _aiCommitSkillService.EnsureSkillAvailable(repo.Path);
            var syncedUserSkills = skillInfo.SyncedUserSkillPaths.Count == 0
                ? "Write-Host '  - 未找到用户 skill 目录。'"
                : string.Join(Environment.NewLine, skillInfo.SyncedUserSkillPaths.Select(path => $"Write-Host '  - {TerminalHelper.EscapePowerShellSingleQuoted(path)}'"));
            var repositorySkill = string.IsNullOrWhiteSpace(skillInfo.RepositorySkillPath)
                ? "Write-Host '  - 当前仓库没有自己的 .claude skill。'"
                : $"Write-Host '  - {TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.RepositorySkillPath)}（只检测，不覆盖）'";
            var prompt = BuildCommitPrompt(skillInfo.WorkingChangesScriptPath, skillInfo.LastChangesJsonPath, skillInfo.LastChangesModelJsonPath);
            var command = $@"
Set-Location -LiteralPath {PsQuote(repo.Path)}
Write-Host 'PackageManager AI 提交入口' -ForegroundColor Cyan
Write-Host '提交引擎：{TerminalHelper.EscapePowerShellSingleQuoted(engineName)}' -ForegroundColor DarkCyan
Write-Host '内嵌解压：{TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.SourcePath)}' -ForegroundColor DarkCyan
Write-Host '本次执行：{TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.PrimarySkillPath)}' -ForegroundColor DarkCyan
Write-Host '规则文件：{TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.SkillMarkdownPath)}' -ForegroundColor DarkCyan
Write-Host '采集脚本：{TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.WorkingChangesScriptPath)}' -ForegroundColor DarkCyan
Write-Host '完整状态文件：{TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.LastChangesJsonPath)}' -ForegroundColor DarkCyan
Write-Host '模型状态文件：{TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.LastChangesModelJsonPath)}' -ForegroundColor DarkCyan
Write-Host '已覆盖用户级 skill：' -ForegroundColor DarkCyan
{syncedUserSkills}
Write-Host '仓库内 skill：' -ForegroundColor DarkCyan
{repositorySkill}
{commandPrefix} {PsQuote(prompt)}
";
            TerminalHelper.LaunchTerminalWithCommand(repo.Path, command, $"{engineName} 代码提交 - {repo.Name}");
            StatusText = $"已启动 {engineName} 代码提交：{repo.Name}；使用脚本 {skillInfo.WorkingChangesScriptPath}";
        }

        private void DoOpenIde(CodeRepository repo, string[] possibleNames, string displayName)
        {
            var toolPath = GetToolPathFromCommonStartup(possibleNames);
            if (string.IsNullOrWhiteSpace(toolPath))
            {
                MessageBox.Show($"未在常用启动项中找到 {displayName}，请先配置工具路径。", "代码工作区", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var target = SelectProjectFile(repo);
            if (target == null)
            {
                StatusText = "已取消选择项目文件。";
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = toolPath,
                Arguments = QuoteArgument(target),
                WorkingDirectory = repo.Path,
                UseShellExecute = true,
            });
            StatusText = $"已在 {displayName} 中打开 {Path.GetFileName(target)}。";
        }

        private void DoOpenVisualStudio(CodeRepository repo)
        {
            var toolPath = ResolveVisualStudioPath() ?? GetToolPathFromCommonStartup("Visual Studio", "devenv", "VS");
            if (string.IsNullOrWhiteSpace(toolPath))
            {
                MessageBox.Show("未找到 Visual Studio，请确认已安装或在常用启动项中配置 VS。", "代码工作区", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var target = SelectProjectFile(repo);
            if (target == null)
            {
                StatusText = "已取消选择项目文件。";
                return;
            }

            StartToolWithTarget(toolPath, target, repo.Path);
            StatusText = $"已在 Visual Studio 中打开 {Path.GetFileName(target)}。";
        }

        private void DoOpenCursor(CodeRepository repo)
        {
            var toolPath = GetToolPathFromCommonStartup("Cursor");
            if (!string.IsNullOrWhiteSpace(toolPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = toolPath,
                    Arguments = QuoteArgument(repo.Path),
                    WorkingDirectory = repo.Path,
                    UseShellExecute = true,
                });
            }
            else
            {
                TerminalHelper.LaunchTerminalWithCommand(repo.Path, $"Set-Location -LiteralPath {PsQuote(repo.Path)}\ncursor .", $"Cursor - {repo.Name}");
            }

            StatusText = $"已启动 Cursor：{repo.Name}";
        }

        private void DoOpenClaudeCode(CodeRepository repo)
        {
            var command = $@"
Set-Location -LiteralPath {PsQuote(repo.Path)}
claude --dangerously-skip-permissions
";

            TerminalHelper.LaunchTerminalWithCommand(repo.Path, command, $"Claude Code - {repo.Name}");
            StatusText = $"已启动 Claude Code：{repo.Name}";
        }

        private void DoOpenCodex(CodeRepository repo)
        {
            var command = $@"
Set-Location -LiteralPath {PsQuote(repo.Path)}
codex --sandbox danger-full-access --ask-for-approval never
";
            TerminalHelper.LaunchTerminalWithCommand(repo.Path, command, $"Codex - {repo.Name}");
            StatusText = $"已启动 Codex（danger-full-access / never approval）：{repo.Name}";
        }

        private void DoOpenFolder(CodeRepository repo)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = repo.Path,
                UseShellExecute = true,
            });
            StatusText = $"已打开文件夹：{repo.Name}";
        }

        private string SelectProjectFile(CodeRepository repo)
        {
            if (repo.ProjectFiles == null || repo.ProjectFiles.Count == 0 || repo.ProjectFiles.All(file => !File.Exists(file)))
            {
                RefreshProjectFiles(repo);
            }

            var projectFiles = (repo.ProjectFiles ?? new List<string>()).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (projectFiles.Count == 0)
            {
                return repo.Path;
            }

            if (projectFiles.Count == 1)
            {
                return projectFiles[0];
            }

            var dialog = new ProjectFileSelectionDialog(projectFiles, repo.Path)
            {
                Owner = Window.GetWindow(this),
            };
            return dialog.ShowDialog() == true ? dialog.SelectedProjectFile : null;
        }

        private void RefreshProjectFiles(CodeRepository repo)
        {
            if (repo == null || string.IsNullOrWhiteSpace(repo.Path) || !Directory.Exists(repo.Path))
            {
                return;
            }

            try
            {
                var slnFiles = EnumerateProjectFiles(repo.Path, "*.sln")
                    .Where(path => path.IndexOf("\\.vs\\", StringComparison.OrdinalIgnoreCase) < 0)
                    .Take(100)
                    .ToList();

                repo.ProjectFiles = slnFiles.Count > 0
                    ? slnFiles
                    : EnumerateProjectFiles(repo.Path, "*.csproj").Take(100).ToList();
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"刷新仓库项目文件失败：{repo.Path}");
            }
        }

        private string GetToolPathFromCommonStartup(params string[] possibleNames)
        {
            var settings = _dataPersistenceService.LoadSettings();
            var items = settings.CommonStartupItems ?? new List<CommonStartupItem>();

            foreach (var name in possibleNames.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                foreach (var item in items.Where(i =>
                    !string.IsNullOrWhiteSpace(i?.FullPath) &&
                    File.Exists(i.FullPath) &&
                    (MatchesToolName(i.Name, name) || MatchesToolPath(i.FullPath, name))))
                {
                    var launchPath = ResolveLaunchablePath(item.FullPath);
                    if (!string.IsNullOrWhiteSpace(launchPath))
                    {
                        return launchPath;
                    }
                }
            }

            return null;
        }

        private static string ResolveLaunchablePath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                return null;
            }

            if (!string.Equals(Path.GetExtension(fullPath), ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }

            var targetPath = TryResolveShortcutTarget(fullPath);
            return !string.IsNullOrWhiteSpace(targetPath) && File.Exists(targetPath) ? targetPath : null;
        }

        private static string TryResolveShortcutTarget(string shortcutPath)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    return null;
                }

                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                return shortcut?.GetType()
                    .InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null)
                    ?.ToString();
            }
            catch
            {
                return null;
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut))
                {
                    Marshal.FinalReleaseComObject(shortcut);
                }

                if (shell != null && Marshal.IsComObject(shell))
                {
                    Marshal.FinalReleaseComObject(shell);
                }
            }
        }

        private static string ResolveVisualStudioPath()
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft Visual Studio"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio"),
            };

            foreach (var root in roots.Where(Directory.Exists))
            {
                try
                {
                    var candidates = Directory.EnumerateFiles(root, "devenv.exe", SearchOption.AllDirectories)
                        .OrderByDescending(path => path.IndexOf("\\Community\\", StringComparison.OrdinalIgnoreCase) >= 0)
                        .ThenBy(path => path.IndexOf("\\Insiders\\", StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                    var candidate = candidates.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static void StartToolWithTarget(string toolPath, string target, string workingDirectory)
        {
            var launchPath = ResolveLaunchablePath(toolPath);
            if (string.IsNullOrWhiteSpace(launchPath))
            {
                throw new FileNotFoundException($"工具路径无效或快捷方式目标不存在：{toolPath}");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = launchPath,
                Arguments = QuoteArgument(target),
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : null,
                UseShellExecute = true,
            });
        }

        private static void EnsureCommandExists(string commandName)
        {
            var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var directory in pathValue.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    var cleanDirectory = directory.Trim().Trim('"');
                    if (File.Exists(Path.Combine(cleanDirectory, commandName + ".cmd")) ||
                        File.Exists(Path.Combine(cleanDirectory, commandName + ".exe")) ||
                        File.Exists(Path.Combine(cleanDirectory, commandName + ".bat")))
                    {
                        return;
                    }
                }
                catch
                {
                }
            }

            throw new FileNotFoundException($"未找到 {commandName} 命令，请确认已安装并加入 PATH。");
        }

        private async Task<PullResult> PullRepositoryAsync(CodeRepository repo)
        {
            var result = new PullResult { Success = true };
            var hasGit = Directory.Exists(Path.Combine(repo.Path, ".git")) || File.Exists(Path.Combine(repo.Path, ".git"));
            var hasRootSvn = Directory.Exists(Path.Combine(repo.Path, ".svn"));

            if (hasGit)
            {
                var gitResult = await RunCommandAsync("git", "pull", repo.Path, TimeSpan.FromSeconds(60));
                if (gitResult.ExitCode != 0)
                {
                    result.Success = false;
                    result.ErrorMessage = AppendError(result.ErrorMessage, gitResult.Error, gitResult.Output);
                }

                var gitText = (gitResult.Output ?? string.Empty) + Environment.NewLine + (gitResult.Error ?? string.Empty);
                if (gitText.IndexOf("CONFLICT", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.HasConflicts = true;
                    result.GitConflicts.AddRange(ParseGitConflicts(gitText));
                }
            }

            var svnPaths = new List<string>();
            if (hasRootSvn && !hasGit)
            {
                svnPaths.Add(repo.Path);
            }

            if (repo.SubRepositories != null)
            {
                svnPaths.AddRange(repo.SubRepositories
                    .Where(sub => !string.IsNullOrWhiteSpace(sub.RelativePath))
                    .Select(sub => Path.Combine(repo.Path, sub.RelativePath))
                    .Where(Directory.Exists));
            }

            if (svnPaths.Count > 0)
            {
                var svnTasks = svnPaths.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(async path => new
                    {
                        Path = path,
                        Result = await RunCommandAsync("svn", "update", path, TimeSpan.FromSeconds(60)),
                    });
                var svnResults = await Task.WhenAll(svnTasks);
                foreach (var svnResult in svnResults)
                {
                    if (svnResult.Result.ExitCode != 0)
                    {
                        result.Success = false;
                        result.ErrorMessage = AppendError(
                            result.ErrorMessage,
                            $"SVN更新失败 ({Path.GetFileName(svnResult.Path)}): {svnResult.Result.Error}",
                            svnResult.Result.Output);
                    }

                    var svnText = (svnResult.Result.Output ?? string.Empty) + Environment.NewLine + (svnResult.Result.Error ?? string.Empty);
                    if (HasSvnConflict(svnText))
                    {
                        result.HasConflicts = true;
                        result.SvnConflicts.Add(Path.GetFileName(svnResult.Path));
                    }
                }
            }

            if (!hasGit && !hasRootSvn && svnPaths.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = "未检测到 Git 或 SVN 仓库。";
            }

            return result;
        }

        private static async Task<CommandResult> RunCommandAsync(string fileName, string arguments, string workingDirectory, TimeSpan timeout)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    process.Start();
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    var exited = await Task.Run(() => process.WaitForExit((int)timeout.TotalMilliseconds));
                    if (!exited)
                    {
                        try { process.Kill(); } catch { }
                        return new CommandResult { ExitCode = -1, Output = string.Empty, Error = "Timeout" };
                    }

                    return new CommandResult
                    {
                        ExitCode = process.ExitCode,
                        Output = await outputTask,
                        Error = await errorTask,
                    };
                }
            }
            catch (Exception ex)
            {
                return new CommandResult { ExitCode = -1, Output = string.Empty, Error = ex.Message };
            }
        }

        private static string AppendError(string current, string error, string output)
        {
            var text = string.IsNullOrWhiteSpace(error) ? output : error;
            if (string.IsNullOrWhiteSpace(text))
            {
                return current;
            }

            return string.IsNullOrWhiteSpace(current) ? text.Trim() : current + Environment.NewLine + text.Trim();
        }

        private static List<string> ParseGitConflicts(string output)
        {
            var conflicts = new List<string>();
            foreach (var line in (output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var marker = " in ";
                var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (line.IndexOf("CONFLICT", StringComparison.OrdinalIgnoreCase) >= 0 && index >= 0)
                {
                    conflicts.Add(line.Substring(index + marker.Length).Trim());
                }
            }

            return conflicts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool HasSvnConflict(string output)
        {
            return (output ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.IndexOf("conflict", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             line.StartsWith("C ", StringComparison.Ordinal) ||
                             line.StartsWith("C\t", StringComparison.Ordinal));
        }

        private static string BuildConflictMessage(PullResult result)
        {
            var parts = new List<string> { "检测到冲突，请在 IDE 中手动解决。" };
            if (result.GitConflicts.Count > 0)
            {
                parts.Add("Git冲突文件: " + string.Join(", ", result.GitConflicts.Take(8)));
            }

            if (result.SvnConflicts.Count > 0)
            {
                parts.Add("SVN冲突仓库: " + string.Join(", ", result.SvnConflicts.Take(8)));
            }

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                parts.Add(result.ErrorMessage);
            }

            return string.Join(Environment.NewLine, parts);
        }

        private static string BuildCommitPrompt(string workingChangesScriptPath, string lastChangesJsonPath, string lastChangesModelJsonPath)
        {
            var skillMarkdownPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(workingChangesScriptPath)), "SKILL.md");
            return "按这个内嵌同步后的 git-svn-commitlog-generator skill 完成本次 Git/SVN 提交流程："
                   + $"SKILL.md=\"{skillMarkdownPath}\"。"
                   + "不要依赖当前目录或用户目录里原本安装的旧 skill；如果自动加载了同名 skill，也以这里给出的 SKILL.md 和脚本绝对路径为准。"
                   + "必须直接运行下面这个绝对路径脚本完成 Step 1："
                   + $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{workingChangesScriptPath}\" -PromptTimeoutSeconds 30。"
                   + $"脚本会打开/等待交互并生成 JSON；Step 1 结束后生成日志时优先读取轻量模型状态文件：\"{lastChangesModelJsonPath}\"。"
                   + $"完整状态文件只供 Step 3 提交脚本使用：\"{lastChangesJsonPath}\"，不要为了生成日志读取完整文件，除非轻量文件缺失或字段不完整。"
                   + "不要读取仓库 .claude/skills 里的 .state/last_changes.json。"
                   + "之后按脚本包 SKILL.md 的规则生成提交日志，并调用同一个内嵌解压目录下的 invoke-commit-push-interactive.ps1 做最终提交确认。"
                   + "不要手动执行 git add、git commit、git push、svn add 或 svn commit；这些操作必须由脚本完成。";
        }

        private void MarkRepositoryUsed(CodeRepository repo)
        {
            repo.LastUsed = DateTime.Now;
            repo.UsageCount++;

            var settings = _dataPersistenceService.LoadSettings();
            settings.LastUsedRepositoryPath = repo.Path;
            var repositories = settings.CodeRepositories ?? new List<CodeRepository>();
            var stored = repositories.FirstOrDefault(r => string.Equals(NormalizePath(r.Path), NormalizePath(repo.Path), StringComparison.OrdinalIgnoreCase));
            if (stored == null)
            {
                repositories.Add(repo.Clone());
            }
            else
            {
                stored.Name = repo.Name;
                stored.Path = repo.Path;
                stored.Note = repo.Note;
                stored.ProjectFiles = repo.ProjectFiles == null ? new List<string>() : new List<string>(repo.ProjectFiles);
                stored.LastUsed = repo.LastUsed;
                stored.UsageCount = repo.UsageCount;
            }

            settings.CodeRepositories = repositories
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Path))
                .OrderByDescending(r => r.LastUsed)
                .ThenBy(r => r.Name)
                .ToList();
            _dataPersistenceService.SaveSettings(settings);
        }

        private void SaveRepositories()
        {
            var settings = _dataPersistenceService.LoadSettings();
            settings.CodeRepositories = Repositories.Select(repo => repo.Clone()).ToList();
            _dataPersistenceService.SaveSettings(settings);
        }

        private bool EnsureRepositoryExists(CodeRepository repo)
        {
            if (repo == null || string.IsNullOrWhiteSpace(repo.Path) || !Directory.Exists(repo.Path))
            {
                MessageBox.Show("仓库路径不存在，请在管理页修正。", "代码工作区", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private static IEnumerable<string> EnumerateProjectFiles(string rootPath, string pattern)
        {
            return Directory.EnumerateFiles(rootPath, pattern, SearchOption.AllDirectories)
                .Where(path => path.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) < 0)
                .Where(path => path.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) < 0);
        }

        private static bool MatchesToolName(string itemName, string searchName)
        {
            if (string.IsNullOrWhiteSpace(itemName) || string.IsNullOrWhiteSpace(searchName))
            {
                return false;
            }

            if (string.Equals(itemName, searchName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(searchName, "VS", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(itemName, "VS", StringComparison.OrdinalIgnoreCase) ||
                       (itemName.IndexOf("Visual Studio", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        itemName.IndexOf("Code", StringComparison.OrdinalIgnoreCase) < 0);
            }

            return itemName.IndexOf(searchName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool MatchesToolPath(string fullPath, string searchName)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(searchName))
            {
                return false;
            }

            var fileName = Path.GetFileNameWithoutExtension(fullPath);
            if (string.Equals(searchName, "Visual Studio", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(searchName, "devenv", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(fileName, "devenv", StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(fileName, searchName, StringComparison.OrdinalIgnoreCase);
        }

        private static string PsQuote(string value)
        {
            return $"'{TerminalHelper.EscapePowerShellSingleQuoted(value)}'";
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim();
            }
        }

        private static T FindAncestor<T>(DependencyObject current)
            where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T target)
                {
                    return target;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        private void RaisePropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private class PullResult
        {
            public bool Success { get; set; }

            public bool HasConflicts { get; set; }

            public string ErrorMessage { get; set; }

            public List<string> GitConflicts { get; } = new List<string>();

            public List<string> SvnConflicts { get; } = new List<string>();
        }

        private class CommandResult
        {
            public int ExitCode { get; set; }

            public string Output { get; set; }

            public string Error { get; set; }
        }
    }
}
