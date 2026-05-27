using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
        private CodeRepository _selectedRepository;
        private string _statusText;

        public CodeWorkspacePage()
        {
            InitializeComponent();
            _dataPersistenceService = ServiceLocator.Resolve<DataPersistenceService>() ?? new DataPersistenceService();
            DataContext = this;
            LoadRepositories();
        }

        public event Action RequestExit;

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<CodeRepository> Repositories { get; } = new ObservableCollection<CodeRepository>();

        public CodeRepository SelectedRepository
        {
            get => _selectedRepository;
            set => SetProperty(ref _selectedRepository, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
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
                SetupRepositoryCommands(cloned);
                Repositories.Add(cloned);
            }

            StatusText = Repositories.Count == 0
                ? "未配置代码仓库，请点击管理仓库添加。"
                : $"已加载 {Repositories.Count} 个仓库。";
        }

        private void SetupRepositoryCommands(CodeRepository repo)
        {
            repo.ClaudeCommitCommand = new RelayCommand(() => RunRepositoryAction(repo, DoClaudeCommit));
            repo.CodexCommitCommand = new RelayCommand(() => RunRepositoryAction(repo, DoCodexCommit));
            repo.OpenVSCommand = new RelayCommand(() => RunRepositoryAction(repo, DoOpenVisualStudio));
            repo.OpenRiderCommand = new RelayCommand(() => RunRepositoryAction(repo, r => DoOpenIde(r, new[] { "Rider", "JetBrains Rider" }, "Rider")));
            repo.OpenCursorCommand = new RelayCommand(() => RunRepositoryAction(repo, DoOpenCursor));
            repo.OpenClaudeCommand = new RelayCommand(() => RunRepositoryAction(repo, DoOpenClaudeCode));
            repo.OpenCodexCommand = new RelayCommand(() => RunRepositoryAction(repo, DoOpenCodex));
            repo.OpenFolderCommand = new RelayCommand(() => RunRepositoryAction(repo, DoOpenFolder));
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
            LoadRepositories();
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
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            RequestExit?.Invoke();
        }

        private void DoClaudeCommit(CodeRepository repo)
        {
            DoAiCommit(repo, "Claude", "claude", "claude --dangerously-skip-permissions");
        }

        private void DoCodexCommit(CodeRepository repo)
        {
            DoAiCommit(repo, "Codex", "codex", "codex --sandbox danger-full-access --ask-for-approval never");
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
            var prompt = BuildCommitPrompt(skillInfo.WorkingChangesScriptPath, skillInfo.LastChangesJsonPath);
            var command = $@"
Set-Location -LiteralPath {PsQuote(repo.Path)}
Write-Host 'PackageManager AI 提交入口' -ForegroundColor Cyan
Write-Host '提交引擎：{TerminalHelper.EscapePowerShellSingleQuoted(engineName)}' -ForegroundColor DarkCyan
Write-Host '内嵌解压：{TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.SourcePath)}' -ForegroundColor DarkCyan
Write-Host '本次执行：{TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.PrimarySkillPath)}' -ForegroundColor DarkCyan
Write-Host '规则文件：{TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.SkillMarkdownPath)}' -ForegroundColor DarkCyan
Write-Host '采集脚本：{TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.WorkingChangesScriptPath)}' -ForegroundColor DarkCyan
Write-Host '状态文件：{TerminalHelper.EscapePowerShellSingleQuoted(skillInfo.LastChangesJsonPath)}' -ForegroundColor DarkCyan
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

        private static string BuildCommitPrompt(string workingChangesScriptPath, string lastChangesJsonPath)
        {
            var skillMarkdownPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(workingChangesScriptPath)), "SKILL.md");
            return "按这个内嵌同步后的 git-svn-commitlog-generator skill 完成本次 Git/SVN 提交流程："
                   + $"SKILL.md=\"{skillMarkdownPath}\"。"
                   + "不要依赖当前目录或用户目录里原本安装的旧 skill；如果自动加载了同名 skill，也以这里给出的 SKILL.md 和脚本绝对路径为准。"
                   + "必须直接运行下面这个绝对路径脚本完成 Step 1："
                   + $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{workingChangesScriptPath}\" -PromptTimeoutSeconds 30。"
                   + $"脚本会打开/等待交互并生成 JSON；Step 1 结束后只能从这个绝对路径读取采集结果：\"{lastChangesJsonPath}\"，不要读取仓库 .claude/skills 里的 .state/last_changes.json。"
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
    }
}
