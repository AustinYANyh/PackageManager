using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
        private readonly CodePackageLinkService _packageLinkService;
        private readonly CodeWorkspaceNavigationRequestService _navigationRequestService;
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
            _packageLinkService = ServiceLocator.Resolve<CodePackageLinkService>() ?? new CodePackageLinkService(_dataPersistenceService);
            _navigationRequestService = ServiceLocator.Resolve<CodeWorkspaceNavigationRequestService>() ?? new CodeWorkspaceNavigationRequestService();
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
                         .Where(repo => repo != null && !string.IsNullOrWhiteSpace(repo.Path)))
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
            HandlePendingNavigationRequest();
            if (!_hasLoaded)
            {
                _hasLoaded = true;
                if (Repositories.Any(repo => repo.LastStatusRefresh != DateTime.MinValue))
                {
                    StatusText = $"已加载 {Repositories.Count} 个仓库，显示内存缓存状态。";
                    if (!_vcsCacheService.IsRefreshRunning)
                    {
                        _ = RefreshAllVcsStatusAsync(forceRefresh: false, includeRemoteStatus: false, startRemoteRefreshAfterLocal: true);
                    }
                }
                else if (_vcsCacheService.IsRefreshRunning)
                {
                    StatusText = $"已加载 {Repositories.Count} 个仓库，后台正在扫描 VCS 状态。";
                }
                else
                {
                    await RefreshAllVcsStatusAsync(forceRefresh: false, includeRemoteStatus: false, startRemoteRefreshAfterLocal: true);
                }
            }

            StartAutoRefresh();
        }

        private void HandlePendingNavigationRequest()
        {
            var request = _navigationRequestService.Consume();
            if (request == null)
            {
                return;
            }

            if (request.Kind == CodeWorkspaceNavigationRequestKind.SelectLinkedRepository)
            {
                var repository = FindRepositoryByPackageKey(request.PackageKey);
                if (repository == null)
                {
                    StatusText = $"未找到关联 {request.PackageName} 的源码仓库。";
                    MessageBox.Show("当前产品包还没有关联源码仓库，请先关联。", "代码工作区", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                SelectRepository(repository);
                StatusText = $"已定位源码仓库: {repository.Name}";
                return;
            }

            if (request.Kind == CodeWorkspaceNavigationRequestKind.BindPackageToRepository)
            {
                BindPackageFromRequest(request);
            }
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
            repo.MergeToMainCommand = new RelayCommand(() => RunRepositoryAction(repo, DoMergeToMain));
            repo.BuildCommand = new RelayCommand(() => RunRepositoryAction(repo, r => DoBuildWithMsBuild(r, "Build")));
            repo.ReBuildCommand = new RelayCommand(() => RunRepositoryAction(repo, r => DoBuildWithMsBuild(r, "Rebuild")));
            repo.OpenVSCommand = new RelayCommand(() => RunRepositoryAction(repo, DoOpenVisualStudio));
            repo.OpenRiderCommand = new RelayCommand(() => RunRepositoryAction(repo, r => DoOpenIde(r, new[] { "Rider", "JetBrains Rider" }, "Rider")));
            repo.OpenCursorCommand = new RelayCommand(() => RunRepositoryAction(repo, DoOpenCursor));
            repo.OpenClaudeCommand = new RelayCommand(() => RunRepositoryAction(repo, DoOpenClaudeCode));
            repo.OpenCodexCommand = new RelayCommand(() => RunRepositoryAction(repo, DoOpenCodex));
            repo.OpenFolderCommand = new RelayCommand(() => RunRepositoryAction(repo, DoOpenFolder));
            repo.LinkPackageCommand = new RelayCommand(() => LinkPackage(repo));
            repo.OpenLinkedPackageCommand = new RelayCommand(() => OpenLinkedPackage(repo), () => repo.HasLinkedPackage);
            repo.UnlinkPackageCommand = new RelayCommand(() => UnlinkPackage(repo), () => repo.HasLinkedPackage);
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
            if ((sender as FrameworkElement)?.DataContext is CodeRepository repo)
            {
                SelectedRepository = repo;
                e.Handled = true;
            }
        }

        private void GitDetailCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2)
            {
                return;
            }

            OpenDiffWindowForRepository("Git 变更", BuildGitChangedFiles(SelectedRepository));
            e.Handled = true;
        }

        private void SvnDetailCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2)
            {
                return;
            }

            OpenDiffWindowForRepository("SVN 变更", BuildSvnChangedFiles(SelectedRepository));
            e.Handled = true;
        }

        private void SubRepositoryItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2)
            {
                return;
            }

            if ((sender as FrameworkElement)?.DataContext is SubRepository subRepository)
            {
                OpenDiffWindowForRepository($"{subRepository.VcsTypeText} 子仓库/{subRepository.RelativePath}", subRepository.ChangedFiles);
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

        private void LinkedPackageBadge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (SelectedRepository?.HasLinkedPackage == true)
            {
                OpenLinkedPackage(SelectedRepository);
            }
            else
            {
                LinkPackage(SelectedRepository);
            }

            e.Handled = true;
        }

        private void OpenDiffWindowForRepository(string scopeTitle, IEnumerable<VcsChangedFile> files)
        {
            if (SelectedRepository == null)
            {
                return;
            }

            var changedFiles = files?
                .Where(file => file != null)
                .Select(file => file.Clone())
                .ToList() ?? new List<VcsChangedFile>();
            if (changedFiles.Count == 0)
            {
                return;
            }

            var window = new CodeWorkspaceDiffWindow(SelectedRepository, changedFiles, scopeTitle)
            {
                Owner = Window.GetWindow(this),
            };
            window.Show();
        }

        private static IEnumerable<VcsChangedFile> BuildAllChangedFiles(CodeRepository repository)
        {
            return BuildGitChangedFiles(repository).Concat(BuildSvnChangedFiles(repository));
        }

        private static IEnumerable<VcsChangedFile> BuildGitChangedFiles(CodeRepository repository)
        {
            if (repository == null)
            {
                return Enumerable.Empty<VcsChangedFile>();
            }

            var rootFiles = repository.GitChangedFiles ?? Enumerable.Empty<VcsChangedFile>();
            var subFiles = repository.SubRepositories?
                .Where(sub => sub.VcsType == VcsType.Git)
                .SelectMany(sub => sub.ChangedFiles ?? Enumerable.Empty<VcsChangedFile>())
                ?? Enumerable.Empty<VcsChangedFile>();
            return rootFiles.Concat(subFiles);
        }

        private static IEnumerable<VcsChangedFile> BuildSvnChangedFiles(CodeRepository repository)
        {
            if (repository == null)
            {
                return Enumerable.Empty<VcsChangedFile>();
            }

            var rootFiles = repository.RootSvnChangedFiles ?? Enumerable.Empty<VcsChangedFile>();
            var subFiles = repository.SubRepositories?
                .Where(sub => sub.VcsType == VcsType.Svn)
                .SelectMany(sub => sub.ChangedFiles ?? Enumerable.Empty<VcsChangedFile>())
                ?? Enumerable.Empty<VcsChangedFile>();
            return rootFiles.Concat(subFiles);
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
            await RefreshAllVcsStatusAsync(forceRefresh: true, includeRemoteStatus: true);
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
                await _vcsStatusService.RefreshRepositoryStatusAsync(SelectedRepository, forceRefresh: true, includeRemoteStatus: true);
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

        private async void RefreshProjectFilesButton_Click(object sender, RoutedEventArgs e)
        {
            var hasFailure = false;
            foreach (var repo in Repositories)
            {
                if (!await RefreshProjectFilesAsync(repo))
                {
                    hasFailure = true;
                }
            }

            SaveRepositories();
            StatusText = hasFailure ? "部分项目文件刷新失败。" : "项目文件已刷新。";
        }

        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            LoadRepositories();
            _ = RefreshAllVcsStatusAsync(forceRefresh: false, includeRemoteStatus: false, startRemoteRefreshAfterLocal: true);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            StopAutoRefresh();
            RequestExit?.Invoke();
        }

        private async Task RefreshAllVcsStatusAsync(bool forceRefresh = false, bool includeRemoteStatus = false, bool startRemoteRefreshAfterLocal = false)
        {
            if (Repositories.Count == 0)
            {
                return;
            }

            StatusText = "正在刷新仓库状态...";
            SetRefreshingStatus(true);
            try
            {
                await _vcsCacheService.RefreshRepositoriesAsync(Repositories, forceRefresh, includeRemoteStatus: includeRemoteStatus);
                RefreshSubRepositoryView();
                StatusText = includeRemoteStatus
                    ? $"状态刷新完成 - {DateTime.Now:HH:mm:ss}"
                    : $"本地状态刷新完成 - {DateTime.Now:HH:mm:ss}";
                if (startRemoteRefreshAfterLocal)
                {
                    StartBackgroundRemoteStatusRefresh();
                }
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

        private void StartBackgroundRemoteStatusRefresh()
        {
            StatusText = "本地状态已显示，正在后台检查远端更新...";
            _ = RefreshRemoteStatusInBackgroundAsync();
        }

        private async Task RefreshRemoteStatusInBackgroundAsync()
        {
            try
            {
                await _vcsCacheService.RefreshRepositoriesAsync(Repositories, forceRefresh: true, includeRemoteStatus: true);
                Dispatcher.Invoke(() =>
                {
                    RefreshSubRepositoryView();
                    StatusText = $"远端更新检查完成 - {DateTime.Now:HH:mm:ss}";
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "后台检查远端更新失败");
                Dispatcher.Invoke(() => StatusText = $"后台检查远端更新失败: {ex.Message}");
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

                    await _vcsCacheService.RefreshRepositoriesAsync(Repositories, false, token, includeRemoteStatus: false);
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
                         .Where(repo => repo != null && !string.IsNullOrWhiteSpace(repo.Path)))
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

        private async void DoClaudeCommit(CodeRepository repo)
        {
            await DoAiCommitAsync(repo, "Claude", "claude", AiCliLaunchService.ClaudeCliCommand);
        }

        private async void DoCodexCommit(CodeRepository repo)
        {
            await DoAiCommitAsync(repo, "Codex", "codex", AiCliLaunchService.CodexCliCommand);
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
                        conflictMessage + "\n\n是：启动 Codex 分析冲突\n否：打开仓库文件夹\n取消：暂不处理",
                        "拉取代码 - 检测到冲突",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Warning);
                    if (dialogResult == MessageBoxResult.Yes)
                    {
                        LaunchPullConflictAi(repo, result);
                    }
                    else if (dialogResult == MessageBoxResult.No)
                    {
                        DoOpenFolder(repo);
                    }

                    StatusText = $"拉取完成但有冲突: {repo.Name}";
                }
                else if (result.Success)
                {
                    await _vcsStatusService.RefreshRepositoryStatusAsync(repo, forceRefresh: true, includeRemoteStatus: true);
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

        private void DoMergeToMain(CodeRepository repo)
        {
            var window = new MergeToMainWindow(repo)
            {
                Owner = Window.GetWindow(this),
            };
            window.ShowDialog();
            StatusText = $"已关闭合并回主干窗口: {repo.Name}";
        }

        private async Task DoAiCommitAsync(CodeRepository repo, string engineName, string commandName, string commandPrefix)
        {
            if (repo.IsRefreshing)
            {
                StatusText = $"{repo.Name} 仓库状态刷新或其他操作尚未结束，请稍后重试。";
                return;
            }

            AiCommitSkillInfo skillInfo;
            var globalInstructionWarning = string.Empty;
            try
            {
                repo.IsRefreshing = true;
                StatusText = $"正在准备 {engineName} 提交环境...";
                skillInfo = await Task.Run(() =>
                {
                    EnsureCommandExists(commandName);
                    globalInstructionWarning = TryEnsureGlobalAiInstructions(engineName);
                    return _aiCommitSkillService.EnsureSkillAvailable(repo.Path);
                });
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"准备 {engineName} 提交环境失败: {repo.Path}");
                StatusText = $"{engineName} 提交环境准备失败: {ex.Message}";
                MessageBox.Show($"{engineName} 提交环境准备失败: {ex.Message}", $"{engineName} 提交", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                repo.IsRefreshing = false;
            }

            var syncedUserSkills = skillInfo.SyncedUserSkillPaths.Count == 0
                ? "Write-Host '  - 未找到用户 skill 目录。'"
                : string.Join(Environment.NewLine, skillInfo.SyncedUserSkillPaths.Select(path => $"Write-Host '  - {TerminalHelper.EscapePowerShellSingleQuoted(path)}'"));
            var repositorySkill = string.IsNullOrWhiteSpace(skillInfo.RepositorySkillPath)
                ? "Write-Host '  - 当前仓库没有自己的 .claude skill。'"
                : $"Write-Host '  - {TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.RepositorySkillPath)}（只检测，不覆盖）'";
            try
            {
                var runState = _aiCommitSkillService.CreateRunState(repo.Path, engineName);
                var prompt = BuildCommitPrompt(
                    skillInfo.WorkingChangesScriptPath,
                    runState.StateDirectoryPath,
                    runState.LastChangesJsonPath,
                    runState.LastChangesModelJsonPath);
                var promptArgument = AiCliLaunchService.CreatePromptFileArgument(repo.Path, prompt, "ai-commit", engineName);
                var command = $@"
Set-Location -LiteralPath {PsQuote(repo.Path)}
Write-Host 'PackageManager AI 提交入口' -ForegroundColor Cyan
Write-Host '提交引擎：{TerminalHelper.EscapePowerShellSingleQuoted(engineName)}' -ForegroundColor DarkCyan
Write-Host '内嵌解压：{TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.SourcePath)}' -ForegroundColor DarkCyan
Write-Host '本次执行：{TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.PrimarySkillPath)}' -ForegroundColor DarkCyan
Write-Host '规则文件：{TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.SkillMarkdownPath)}' -ForegroundColor DarkCyan
Write-Host '采集脚本：{TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.WorkingChangesScriptPath)}' -ForegroundColor DarkCyan
Write-Host '本次状态目录：{TerminalHelper.EscapePowerShellSingleQuoted(runState.StateDirectoryPath)}' -ForegroundColor DarkCyan
Write-Host '完整状态文件：{TerminalHelper.EscapePowerShellSingleQuoted(runState.LastChangesJsonPath)}' -ForegroundColor DarkCyan
Write-Host '模型状态文件：{TerminalHelper.EscapePowerShellSingleQuoted(runState.LastChangesModelJsonPath)}' -ForegroundColor DarkCyan
Write-Host '已覆盖用户级 skill：' -ForegroundColor DarkCyan
{syncedUserSkills}
Write-Host '仓库内 skill：' -ForegroundColor DarkCyan
{repositorySkill}
{commandPrefix} {PsQuote(promptArgument)}
";
                TerminalHelper.LaunchTerminalWithCommand(repo.Path, command, $"{engineName} 代码提交 - {repo.Name}");
                StatusText = $"已启动 {engineName} 代码提交：{repo.Name}；状态目录 {runState.StateDirectoryPath}{globalInstructionWarning}";
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"启动 {engineName} 提交流程失败: {repo.Path}");
                StatusText = $"{engineName} 提交流程启动失败: {ex.Message}";
                MessageBox.Show($"{engineName} 提交流程启动失败: {ex.Message}", $"{engineName} 提交", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void DoOpenIde(CodeRepository repo, string[] possibleNames, string displayName)
        {
            var target = await SelectProjectFileAsync(repo);
            if (target == null)
            {
                StatusText = "已取消选择项目文件。";
                return;
            }

            StatusText = $"正在准备打开 {displayName}...";
            var toolPath = await Task.Run(() => GetToolPathFromCommonStartup(possibleNames));
            if (string.IsNullOrWhiteSpace(toolPath))
            {
                MessageBox.Show($"未在常用启动项中找到 {displayName}，请先配置工具路径。", "代码工作区", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                await Task.Run(() => StartToolWithTarget(toolPath, target, repo.Path));
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"打开 {displayName} 失败: {repo.Path}");
                StatusText = $"打开 {displayName} 失败: {ex.Message}";
                MessageBox.Show($"打开 {displayName} 失败: {ex.Message}", "代码工作区", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            StatusText = $"已在 {displayName} 中打开 {Path.GetFileName(target)}。";
        }

        private async void DoOpenVisualStudio(CodeRepository repo)
        {
            var target = await SelectProjectFileAsync(repo);
            if (target == null)
            {
                StatusText = "已取消选择项目文件。";
                return;
            }

            StatusText = "正在准备打开 Visual Studio...";
            var toolPath = await Task.Run(() => ResolveVisualStudioPath() ?? GetToolPathFromCommonStartup("Visual Studio", "devenv", "VS"));
            if (string.IsNullOrWhiteSpace(toolPath))
            {
                MessageBox.Show("未找到 Visual Studio，请确认已安装或在常用启动项中配置 VS。", "代码工作区", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                await Task.Run(() => StartToolWithTarget(toolPath, target, repo.Path));
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"打开 Visual Studio 失败: {repo.Path}");
                StatusText = $"打开 Visual Studio 失败: {ex.Message}";
                MessageBox.Show($"打开 Visual Studio 失败: {ex.Message}", "代码工作区", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

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
            var syncWarning = TryEnsureGlobalAiInstructions("Claude");
            var command = $@"
Set-Location -LiteralPath {PsQuote(repo.Path)}
{AiCliLaunchService.ClaudeCliCommand}
";

            TerminalHelper.LaunchTerminalWithCommand(repo.Path, command, $"Claude Code - {repo.Name}");
            StatusText = $"已启动 Claude Code（{AiCliLaunchService.GetClaudePermissionLabel()}）：{repo.Name}{syncWarning}";
        }

        private void DoOpenCodex(CodeRepository repo)
        {
            var syncWarning = TryEnsureGlobalAiInstructions("Codex");
            var command = $@"
Set-Location -LiteralPath {PsQuote(repo.Path)}
{AiCliLaunchService.CodexCliCommand}
";
            TerminalHelper.LaunchTerminalWithCommand(repo.Path, command, $"Codex - {repo.Name}");
            StatusText = $"已启动 Codex（{AiCliLaunchService.GetCodexPermissionLabel()}）：{repo.Name}{syncWarning}";
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

        private async void DoBuildWithMsBuild(CodeRepository repo, string targetName)
        {
            var selection = await SelectBuildTargetAsync(repo);
            if (selection == null)
            {
                StatusText = "已取消构建。";
                return;
            }

            var command = BuildMsBuildTerminalCommand(repo.Path, selection.ProjectFile, selection.Configurations, targetName, selection.RestorePolicy);
            repo.LastBuildProjectFile = selection.ProjectFile;
            repo.LastBuildConfigurations = new List<string>(selection.Configurations);
            repo.LastBuildRestorePolicy = selection.RestorePolicy;
            SaveRepositories();
            var displayTarget = string.Equals(targetName, "Rebuild", StringComparison.OrdinalIgnoreCase) ? "ReBuild" : "Build";
            TerminalHelper.LaunchTerminalWithCommand(repo.Path, command, $"{displayTarget} - {repo.Name}");
            StatusText = $"已启动 {displayTarget}: {repo.Name} / {Path.GetFileName(selection.ProjectFile)} / {string.Join(", ", selection.Configurations)} / Restore {selection.RestorePolicy}";
        }

        private async Task<BuildTargetSelection> SelectBuildTargetAsync(CodeRepository repo)
        {
            if (repo.ProjectFiles == null || repo.ProjectFiles.Count == 0 || repo.ProjectFiles.All(file => !File.Exists(file)))
            {
                if (repo.IsRefreshing)
                {
                    StatusText = $"{repo.Name} 正在执行操作，请稍后。";
                    return null;
                }

                StatusText = $"正在扫描项目文件: {repo.Name}";
                try
                {
                    repo.IsRefreshing = true;
                    if (!await RefreshProjectFilesAsync(repo))
                    {
                        return null;
                    }
                }
                finally
                {
                    repo.IsRefreshing = false;
                }
            }

            var projectFiles = (repo.ProjectFiles ?? new List<string>())
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (projectFiles.Count == 0)
            {
                MessageBox.Show("未找到可用的项目文件。", "选择项目文件", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            var dialog = new ProjectFileSelectionDialog(
                projectFiles,
                repo.Path,
                enableBuildConfigurations: true,
                preferredProjectFile: repo.LastBuildProjectFile,
                preferredConfigurations: repo.LastBuildConfigurations,
                preferredRestorePolicy: repo.LastBuildRestorePolicy)
            {
                Owner = Window.GetWindow(this),
            };

            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            return new BuildTargetSelection
            {
                ProjectFile = dialog.SelectedProjectFile,
                Configurations = dialog.SelectedConfigurations == null || dialog.SelectedConfigurations.Count == 0
                    ? new List<string> { "Debug2024" }
                    : new List<string>(dialog.SelectedConfigurations),
                RestorePolicy = dialog.SelectedRestorePolicy,
            };
        }

        private async Task<string> SelectProjectFileAsync(CodeRepository repo)
        {
            if (repo.ProjectFiles == null || repo.ProjectFiles.Count == 0 || repo.ProjectFiles.All(file => !File.Exists(file)))
            {
                if (repo.IsRefreshing)
                {
                    StatusText = $"{repo.Name} 正在执行操作，请稍后。";
                    return null;
                }

                StatusText = $"正在扫描项目文件: {repo.Name}";
                try
                {
                    repo.IsRefreshing = true;
                    if (!await RefreshProjectFilesAsync(repo))
                    {
                        return null;
                    }
                }
                finally
                {
                    repo.IsRefreshing = false;
                }
            }

            if (repo.ProjectFiles == null || repo.ProjectFiles.Count == 0 || repo.ProjectFiles.All(file => !File.Exists(file)))
            {
                MessageBox.Show("未找到可用的项目文件。", "选择项目文件", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            var projectFiles = (repo.ProjectFiles ?? new List<string>())
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (projectFiles.Count == 0)
            {
                MessageBox.Show("未找到可用的项目文件。", "选择项目文件", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
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

        private async Task<bool> RefreshProjectFilesAsync(CodeRepository repo)
        {
            if (repo == null || string.IsNullOrWhiteSpace(repo.Path) || !Directory.Exists(repo.Path))
            {
                return false;
            }

            try
            {
                repo.ProjectFiles = await Task.Run(() => ScanProjectFiles(repo.Path));
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"刷新仓库项目文件失败：{repo.Path}");
                MessageBox.Show($"扫描项目文件失败：{ex.Message}", "刷新项目文件", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText = $"扫描项目文件失败: {ex.Message}";
                return false;
            }
        }

        private static List<string> ScanProjectFiles(string rootPath)
        {
            var slnFiles = EnumerateProjectFiles(rootPath, "*.sln")
                .Where(path => path.IndexOf("\\.vs\\", StringComparison.OrdinalIgnoreCase) < 0)
                .Take(100)
                .ToList();
            var csprojFiles = EnumerateProjectFiles(rootPath, "*.csproj")
                    .Take(100)
                    .ToList();

            return slnFiles
                .Concat(csprojFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
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
                    result.ErrorMessage = AppendError(result.ErrorMessage, BuildCommandFailure("Git拉取失败 (根仓库)", gitResult), null);
                }

                var gitText = (gitResult.Output ?? string.Empty) + Environment.NewLine + (gitResult.Error ?? string.Empty);
                if (gitText.IndexOf("CONFLICT", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.HasConflicts = true;
                    result.GitConflicts.AddRange(ParseGitConflicts(gitText).Select(conflict => $"根仓库: {conflict}"));
                }

                await AppendGitWorkingConflictsAsync(repo.Path, "根仓库", result);
            }

            var gitSubRepositories = new List<Tuple<string, string>>();
            if (repo.SubRepositories != null)
            {
                gitSubRepositories.AddRange(repo.SubRepositories
                    .Where(sub => sub.VcsType == VcsType.Git && !string.IsNullOrWhiteSpace(sub.RelativePath))
                    .Select(sub => Tuple.Create(sub.RelativePath, Path.Combine(repo.Path, sub.RelativePath)))
                    .Where(sub => Directory.Exists(sub.Item2)));
            }

            foreach (var gitSubRepository in gitSubRepositories)
            {
                var gitResult = await RunCommandAsync("git", "pull", gitSubRepository.Item2, TimeSpan.FromSeconds(60));
                if (gitResult.ExitCode != 0)
                {
                    result.Success = false;
                    result.ErrorMessage = AppendError(
                        result.ErrorMessage,
                        BuildCommandFailure($"Git拉取失败 ({gitSubRepository.Item1})", gitResult),
                        null);
                }

                var gitText = (gitResult.Output ?? string.Empty) + Environment.NewLine + (gitResult.Error ?? string.Empty);
                if (gitText.IndexOf("CONFLICT", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.HasConflicts = true;
                    result.GitConflicts.AddRange(ParseGitConflicts(gitText).Select(conflict => $"{gitSubRepository.Item1}: {conflict}"));
                }

                await AppendGitWorkingConflictsAsync(gitSubRepository.Item2, gitSubRepository.Item1, result);
            }

            var svnPaths = new List<string>();
            if (hasRootSvn && !hasGit)
            {
                svnPaths.Add(repo.Path);
            }

            if (repo.SubRepositories != null)
            {
                svnPaths.AddRange(repo.SubRepositories
                    .Where(sub => sub.VcsType == VcsType.Svn)
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
                        var relativePath = GetRepositoryRelativePath(repo.Path, svnResult.Path);
                        result.ErrorMessage = AppendError(
                            result.ErrorMessage,
                            BuildCommandFailure($"SVN更新失败 ({relativePath})", svnResult.Result),
                            null);
                    }

                    var svnText = (svnResult.Result.Output ?? string.Empty) + Environment.NewLine + (svnResult.Result.Error ?? string.Empty);
                    if (HasSvnConflict(svnText))
                    {
                        result.HasConflicts = true;
                        result.SvnConflicts.Add(GetRepositoryRelativePath(repo.Path, svnResult.Path));
                    }

                    await AppendSvnWorkingConflictsAsync(repo.Path, svnResult.Path, result);
                }
            }

            if (!hasGit && gitSubRepositories.Count == 0 && !hasRootSvn && svnPaths.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = "未检测到 Git 或 SVN 仓库。";
            }

            DeduplicatePullConflicts(result);
            return result;
        }

        private static async Task AppendGitWorkingConflictsAsync(string workingDirectory, string label, PullResult result)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                return;
            }

            var unmergedResult = await RunCommandAsync(
                "git",
                "-c core.quotepath=false diff --name-only --diff-filter=U",
                workingDirectory,
                TimeSpan.FromSeconds(30));
            foreach (var path in SplitCommandLines(unmergedResult.Output))
            {
                result.HasConflicts = true;
                result.GitConflicts.Add($"{label}: {path}");
            }

            var markerResult = await RunCommandAsync(
                "git",
                "-c core.quotepath=false grep -n \"^<<<<<<< \" -- .",
                workingDirectory,
                TimeSpan.FromSeconds(30));
            if (markerResult.ExitCode == 0)
            {
                foreach (var marker in ParseGitGrepPaths(markerResult.Output))
                {
                    result.HasConflicts = true;
                    result.MarkerConflicts.Add($"{label}: {marker}");
                }
            }
        }

        private static async Task AppendSvnWorkingConflictsAsync(string repositoryPath, string svnPath, PullResult result)
        {
            if (string.IsNullOrWhiteSpace(svnPath) || !Directory.Exists(svnPath))
            {
                return;
            }

            var statusResult = await RunCommandAsync("svn", "status", svnPath, TimeSpan.FromSeconds(30));
            foreach (var conflict in ParseSvnStatusConflicts(statusResult.Output))
            {
                result.HasConflicts = true;
                var relativeRoot = GetRepositoryRelativePath(repositoryPath, svnPath);
                result.SvnConflicts.Add(string.Equals(relativeRoot, "根仓库", StringComparison.OrdinalIgnoreCase)
                    ? conflict
                    : $"{relativeRoot}: {conflict}");
            }
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

        private static string BuildCommandFailure(string prefix, CommandResult result)
        {
            var details = string.IsNullOrWhiteSpace(result?.Error) ? result?.Output : result.Error;
            return string.IsNullOrWhiteSpace(details) ? prefix : $"{prefix}: {details.Trim()}";
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

        private static List<string> SplitCommandLines(string output)
        {
            return (output ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> ParseGitGrepPaths(string output)
        {
            var paths = new List<string>();
            foreach (var line in SplitCommandLines(output))
            {
                var colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    paths.Add(line.Substring(0, colonIndex).Trim());
                }
            }

            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> ParseSvnStatusConflicts(string output)
        {
            var conflicts = new List<string>();
            foreach (var rawLine in (output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (rawLine.Length < 8)
                {
                    continue;
                }

                var statusColumns = rawLine.Substring(0, Math.Min(7, rawLine.Length));
                if (statusColumns.IndexOf('C') < 0)
                {
                    continue;
                }

                var path = rawLine.Substring(Math.Min(8, rawLine.Length)).Trim();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    conflicts.Add(path);
                }
            }

            return conflicts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void DeduplicatePullConflicts(PullResult result)
        {
            if (result == null)
            {
                return;
            }

            ReplaceWithDistinct(result.GitConflicts);
            ReplaceWithDistinct(result.SvnConflicts);
            ReplaceWithDistinct(result.MarkerConflicts);
        }

        private static void ReplaceWithDistinct(List<string> values)
        {
            if (values == null)
            {
                return;
            }

            var distinct = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            values.Clear();
            values.AddRange(distinct);
        }

        private static string BuildConflictMessage(PullResult result)
        {
            var parts = new List<string> { "检测到冲突或冲突标记，建议先处理后再编译。" };
            if (result.GitConflicts.Count > 0)
            {
                parts.Add("Git冲突文件: " + string.Join(", ", result.GitConflicts.Take(8)));
            }

            if (result.SvnConflicts.Count > 0)
            {
                parts.Add("SVN冲突仓库: " + string.Join(", ", result.SvnConflicts.Take(8)));
            }

            if (result.MarkerConflicts.Count > 0)
            {
                parts.Add("冲突标记文件: " + string.Join(", ", result.MarkerConflicts.Take(8)));
            }

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                parts.Add(result.ErrorMessage);
            }

            return string.Join(Environment.NewLine, parts);
        }

        private void LaunchPullConflictAi(CodeRepository repo, PullResult result)
        {
            var prompt = BuildPullConflictPrompt(repo, result);
            var promptArgument = AiCliLaunchService.CreatePromptFileArgument(repo.Path, prompt, "pull-conflict", "codex");
            var command = $@"
Set-Location -LiteralPath {PsQuote(repo.Path)}
Write-Host 'PackageManager 拉取冲突 AI 分析入口' -ForegroundColor Cyan
{AiCliLaunchService.CodexCliCommand} {PsQuote(promptArgument)}
";
            TerminalHelper.LaunchTerminalWithCommand(repo.Path, command, $"Codex 拉取冲突 - {repo.Name}");
            StatusText = $"已启动 Codex 分析拉取冲突：{repo.Name}";
        }

        private static string BuildPullConflictPrompt(CodeRepository repo, PullResult result)
        {
            var gitConflicts = result.GitConflicts.Count == 0
                ? "无"
                : string.Join(Environment.NewLine, result.GitConflicts.Select(item => "- " + item));
            var svnConflicts = result.SvnConflicts.Count == 0
                ? "无"
                : string.Join(Environment.NewLine, result.SvnConflicts.Select(item => "- " + item));
            var markerConflicts = result.MarkerConflicts.Count == 0
                ? "无"
                : string.Join(Environment.NewLine, result.MarkerConflicts.Select(item => "- " + item));

            return $@"拉取代码后检测到冲突或疑似混入版本。请在当前仓库中协助分析并修复，但不要自动提交、推送或回滚整仓。

仓库：{repo.Path}

处理要求：
1. 先运行 git status / svn status 复核冲突状态。
2. 优先检查以下冲突文件和带冲突标记的文件。
3. 不要用整文件覆盖的方式粗暴解决；保留两边真实需要的代码。
4. 修复后运行必要的编译或最小验证。
5. 如果无法判断业务取舍，明确列出需要人工确认的位置。

Git冲突文件：
{gitConflicts}

SVN冲突/树冲突：
{svnConflicts}

冲突标记文件：
{markerConflicts}

拉取错误信息：
{(string.IsNullOrWhiteSpace(result.ErrorMessage) ? "无" : result.ErrorMessage)}
";
        }

        private static string BuildCommitPrompt(string workingChangesScriptPath, string stateDirectoryPath, string lastChangesJsonPath, string lastChangesModelJsonPath)
        {
            var skillRootPath = Path.GetDirectoryName(Path.GetDirectoryName(workingChangesScriptPath));
            var skillMarkdownPath = Path.Combine(skillRootPath, "SKILL.md");
            var commitPushScriptPath = Path.Combine(skillRootPath, "scripts", "invoke-commit-push-interactive.ps1");
            return "按这个内嵌同步后的 git-svn-commitlog-generator skill 完成本次 Git/SVN 提交流程："
                   + $"SKILL.md=\"{skillMarkdownPath}\"。"
                   + "不要依赖当前目录或用户目录里原本安装的旧 skill；如果自动加载了同名 skill，也以这里给出的 SKILL.md 和脚本绝对路径为准。"
                   + $"本次流程的唯一状态目录是：\"{stateDirectoryPath}\"。"
                   + "这是并发隔离边界；Step 1、Step 2 重新采集、Step 3 必须始终使用这个目录，禁止回退到 skill 默认 .state。"
                   + "必须直接运行下面这个绝对路径脚本完成 Step 1："
                   + $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{workingChangesScriptPath}\" -PromptTimeoutSeconds 30 -StateDir \"{stateDirectoryPath}\"。"
                   + $"脚本会打开/等待交互并生成 JSON；Step 1 结束后生成日志时优先读取轻量模型状态文件：\"{lastChangesModelJsonPath}\"。"
                   + "如果依赖核对后需要重新采集并调整范围，必须用 -AddPaths/-ExcludePaths 传稳定仓库相对路径；Id 只属于单次采集快照，禁止跨采集复用 -AddIds/-ExcludeIds。"
                   + $"完整状态文件只供 Step 3 提交脚本使用：\"{lastChangesJsonPath}\"，不要为了生成日志读取完整文件，除非轻量文件缺失或字段不完整。"
                   + "不要读取仓库 .claude/skills 里的 .state/last_changes.json，也不要读取内嵌 skill 默认 .state/last_changes.json。"
                   + $"之后按脚本包 SKILL.md 的规则生成提交日志，并调用这个提交确认脚本：\"{commitPushScriptPath}\"。"
                   + $"Step 3 调用时必须同时传 -StateDir \"{stateDirectoryPath}\" 和 -ChangesJsonFile \"{lastChangesJsonPath}\"，确保提交脚本读取的是本次 Step 1 采集结果。"
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
                stored.LastBuildProjectFile = repo.LastBuildProjectFile;
                stored.LastBuildConfigurations = repo.LastBuildConfigurations == null ? new List<string>() : new List<string>(repo.LastBuildConfigurations);
                stored.LastBuildRestorePolicy = repo.LastBuildRestorePolicy;
                stored.LastUsed = repo.LastUsed;
                stored.UsageCount = repo.UsageCount;
                stored.LinkedPackageKey = repo.LinkedPackageKey;
                stored.LinkedPackageName = repo.LinkedPackageName;
            }

            settings.CodeRepositories = repositories
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Path))
                .ToList();
            _dataPersistenceService.SaveSettings(settings);
        }

        private void SaveRepositories()
        {
            var settings = _dataPersistenceService.LoadSettings();
            settings.CodeRepositories = Repositories.Select(repo => repo.Clone()).ToList();
            _dataPersistenceService.SaveSettings(settings);
        }

        private void LinkPackage(CodeRepository repo, PackageLinkOption preferredPackage = null)
        {
            if (repo == null)
            {
                MessageBox.Show("请先选择一个代码仓库。", "关联产品包", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var options = _packageLinkService.GetPackageOptions()
                .Where(option => IsPackageAvailableForRepository(option, repo))
                .ToList();
            if (options.Count == 0)
            {
                MessageBox.Show("当前没有可关联的产品包配置。已有关联的包需要先解除关联。", "关联产品包", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var currentPackage = preferredPackage ?? _packageLinkService.FindPackageByKey(repo.LinkedPackageKey);
            var suggestedPackage = preferredPackage ?? currentPackage ?? _packageLinkService.SuggestPackage(repo);
            var window = new PackageLinkSelectionWindow(repo, options, suggestedPackage, currentPackage)
            {
                Owner = Window.GetWindow(this),
            };

            if (window.ShowDialog() != true || window.SelectedPackage == null)
            {
                return;
            }

            ApplyPackageLink(repo, window.SelectedPackage);
            StatusText = $"已关联 {repo.Name} -> {window.SelectedPackage.ProductName}";
        }

        private void OpenLinkedPackage(CodeRepository repo)
        {
            if (repo == null || !repo.HasLinkedPackage)
            {
                MessageBox.Show("当前仓库还没有关联产品包。", "代码工作区", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var mainWindow = ServiceLocator.Resolve<global::PackageManager.MainWindow>() ?? Window.GetWindow(this) as global::PackageManager.MainWindow;
            if (mainWindow?.SelectPackageByLinkKey(repo.LinkedPackageKey) != true)
            {
                MessageBox.Show("未找到关联的产品包，可能包配置已被删除或 FTP 路径已变化。", "代码工作区", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ServiceLocator.Resolve<PackageManager.Shell.NavigationService>()?.NavigateTo("packages-home");
        }

        private void UnlinkPackage(CodeRepository repo)
        {
            if (repo == null || !repo.HasLinkedPackage)
            {
                return;
            }

            repo.LinkedPackageKey = null;
            repo.LinkedPackageName = null;
            SaveRepositories();
            CommandManager.InvalidateRequerySuggested();
            StatusText = $"已解除 {repo.Name} 的产品包关联。";
        }

        private void BindPackageFromRequest(CodeWorkspaceNavigationRequest request)
        {
            if (Repositories.Count == 0)
            {
                MessageBox.Show("还没有配置代码仓库，请先添加仓库。", "代码工作区", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var preferredPackage = _packageLinkService.FindPackageByKey(request.PackageKey);
            var repository = FindBestRepositoryForPackage(request);
            if (repository != null)
            {
                SelectRepository(repository);
            }

            LinkPackage(SelectedRepository ?? Repositories.FirstOrDefault(), preferredPackage);
        }

        private CodeRepository FindBestRepositoryForPackage(CodeWorkspaceNavigationRequest request)
        {
            var linked = FindRepositoryByPackageKey(request.PackageKey);
            if (linked != null)
            {
                return linked;
            }

            var packageName = NormalizeMatchText(request.PackageName);
            if (string.IsNullOrWhiteSpace(packageName))
            {
                return Repositories.OrderByDescending(repo => repo.LastUsed).FirstOrDefault();
            }

            return Repositories
                .OrderByDescending(repo => CalculateRepositoryPackageScore(repo, packageName))
                .ThenByDescending(repo => repo.LastUsed)
                .FirstOrDefault();
        }

        private CodeRepository FindRepositoryByPackageKey(string packageKey)
        {
            if (string.IsNullOrWhiteSpace(packageKey))
            {
                return null;
            }

            return Repositories
                .Where(repo => string.Equals(repo.LinkedPackageKey, packageKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(repo => repo.LastUsed)
                .FirstOrDefault();
        }

        private void ApplyPackageLink(CodeRepository repo, PackageLinkOption package)
        {
            repo.LinkedPackageKey = package.Key;
            repo.LinkedPackageName = package.ProductName;
            SaveRepositories();
            CommandManager.InvalidateRequerySuggested();
        }

        private bool IsPackageAvailableForRepository(PackageLinkOption option, CodeRepository repository)
        {
            if (option == null)
            {
                return false;
            }

            if (string.Equals(repository?.LinkedPackageKey, option.Key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !Repositories.Any(repo =>
                !ReferenceEquals(repo, repository) &&
                string.Equals(repo.LinkedPackageKey, option.Key, StringComparison.OrdinalIgnoreCase));
        }

        private void SelectRepository(CodeRepository repo)
        {
            if (repo == null)
            {
                return;
            }

            SelectedRepository = repo;
            RepositoryGrid.SelectedItem = repo;
            RepositoryGrid.ScrollIntoView(repo);
        }

        private static int CalculateRepositoryPackageScore(CodeRepository repo, string normalizedPackageName)
        {
            var text = NormalizeMatchText($"{repo?.Name} {repo?.Path} {repo?.Note}");
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            var score = text.Contains(normalizedPackageName) ? 5 : 0;
            foreach (var token in normalizedPackageName.Split(new[] { "develop" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length >= 3 && text.Contains(token))
                {
                    score++;
                }
            }

            return score;
        }

        private static string NormalizeMatchText(string value)
        {
            return (value ?? string.Empty)
                .Replace("（", string.Empty)
                .Replace("）", string.Empty)
                .Replace("(", string.Empty)
                .Replace(")", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty)
                .Replace(" ", string.Empty)
                .ToLowerInvariant();
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

        private static string TryEnsureGlobalAiInstructions(string engineName)
        {
            try
            {
                AiGlobalInstructionService.EnsureCodeGraphInstructions();
                return string.Empty;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"同步 {engineName} 全局 CodeGraph 规则失败");
                return "；全局 CodeGraph 规则同步失败，请查看日志";
            }
        }

        private const string MsBuildBuildScriptResourceName = "PackageManager.CodeWorkspace.Scripts.Invoke-MsBuildBuild.ps1";

        private static string BuildMsBuildTerminalCommand(string repositoryPath, string projectFile, IReadOnlyList<string> configurations, string targetName, string restorePolicy)
        {
            var safeConfigurations = configurations == null || configurations.Count == 0
                ? new List<string> { "Debug2024" }
                : configurations.ToList();
            var configLiteral = string.Join(", ", safeConfigurations.Select(PsQuote));
            var normalizedRestorePolicy = string.Equals(restorePolicy, "Always", StringComparison.OrdinalIgnoreCase)
                ? "Always"
                : string.Equals(restorePolicy, "Never", StringComparison.OrdinalIgnoreCase)
                    ? "Never"
                    : "Auto";
            var nugetPackagesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");
            var scriptPath = ExtractEmbeddedPowerShellScript(MsBuildBuildScriptResourceName, "Invoke-MsBuildBuild.ps1");

            return $@"
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath {PsQuote(repositoryPath)}
& {PsQuote(scriptPath)} `
    -RepositoryPath {PsQuote(repositoryPath)} `
    -ProjectFile {PsQuote(projectFile)} `
    -TargetName {PsQuote(targetName)} `
    -RestorePolicy {PsQuote(normalizedRestorePolicy)} `
    -NuGetPackagesPath {PsQuote(nugetPackagesPath)} `
    -Configurations @({configLiteral})
exit $LASTEXITCODE
";
        }

        private static string ExtractEmbeddedPowerShellScript(string resourceName, string fileName)
        {
            var scriptDirectory = Path.Combine(Path.GetTempPath(), "PackageManager", "CodeWorkspaceScripts");
            Directory.CreateDirectory(scriptDirectory);
            var scriptPath = Path.Combine(scriptDirectory, fileName);

            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    var availableResources = string.Join(", ", assembly.GetManifestResourceNames());
                    throw new InvalidOperationException($"Embedded script resource not found: {resourceName}. Available resources: {availableResources}");
                }

                using (var reader = new StreamReader(stream))
                {
                    var content = reader.ReadToEnd();
                    if (!File.Exists(scriptPath) || !string.Equals(File.ReadAllText(scriptPath), content, StringComparison.Ordinal))
                    {
                        File.WriteAllText(scriptPath, content, new System.Text.UTF8Encoding(false));
                    }
                }
            }

            return scriptPath;
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static string GetRepositoryRelativePath(string repositoryPath, string childPath)
        {
            if (string.IsNullOrWhiteSpace(repositoryPath) || string.IsNullOrWhiteSpace(childPath))
            {
                return childPath;
            }

            try
            {
                var basePath = repositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var baseUri = new Uri(basePath);
                var childUri = new Uri(childPath);
                var relativePath = Uri.UnescapeDataString(baseUri.MakeRelativeUri(childUri).ToString().Replace('/', Path.DirectorySeparatorChar));
                return string.IsNullOrWhiteSpace(relativePath) ? "根仓库" : relativePath;
            }
            catch
            {
                return Path.GetFileName(childPath);
            }
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

            public List<string> MarkerConflicts { get; } = new List<string>();
        }

        private class CommandResult
        {
            public int ExitCode { get; set; }

            public string Output { get; set; }

            public string Error { get; set; }
        }

        private sealed class BuildTargetSelection
        {
            public string ProjectFile { get; set; }

            public List<string> Configurations { get; set; }

            public string RestorePolicy { get; set; }
        }
    }
}
