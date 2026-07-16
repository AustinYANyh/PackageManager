using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PackageManager.Features.CodeWorkspace.Models;
using PackageManager.Services;

namespace PackageManager.Features.CodeWorkspace.Services
{
    public class AiCliLaunchService
    {
        public static string ClaudeCliCommand => BuildClaudeCliCommand(ResolveClaudePermissionMode());

        public static string CodexCliCommand => BuildCodexCliCommand(ResolveCodexPermissionMode());

        private const int PromptRetentionDays = 7;

        public Task LaunchClaudeAsync(CodeRepository repo, string prompt, string title)
        {
            var fullPrompt = "/plan\n" + (prompt ?? string.Empty);
            return LaunchAsync(repo, fullPrompt, title, "Claude", "claude", ClaudeCliCommand);
        }

        public Task LaunchCodexAsync(CodeRepository repo, string prompt, string title)
        {
            var fullPrompt = "/plan\n" + (prompt ?? string.Empty);
            return LaunchAsync(repo, fullPrompt, title, "Codex", "codex", CodexCliCommand);
        }

        private static Task LaunchAsync(CodeRepository repo, string prompt, string title, string engineName, string commandName, string commandPrefix)
        {
            if (repo == null || string.IsNullOrWhiteSpace(repo.Path) || !Directory.Exists(repo.Path))
            {
                throw new DirectoryNotFoundException("请选择有效的代码仓库。");
            }

            EnsureCommandExists(commandName);
            TryEnsureGlobalInstructions(engineName);
            var finalPrompt = CreatePromptFileArgument(repo.Path, prompt ?? string.Empty, "pingcode-ai", engineName);
            var command = $@"
Set-Location -LiteralPath {PsQuote(repo.Path)}
Write-Host 'PackageManager PingCode AI 执行入口' -ForegroundColor Cyan
Write-Host '执行引擎：{TerminalHelper.EscapePowerShellSingleQuoted(engineName)}' -ForegroundColor DarkCyan
Write-Host '仓库：{TerminalHelper.EscapePowerShellSingleQuoted(repo.Name ?? repo.Path)}' -ForegroundColor DarkCyan
{commandPrefix} {PsQuote(finalPrompt)}
";
            TerminalHelper.LaunchTerminalWithCommand(repo.Path, command, title);
            return Task.CompletedTask;
        }

        public static string GetClaudePermissionLabel()
        {
            switch (ResolveClaudePermissionMode())
            {
                case ClaudePermissionMode.FullAccess:
                    return "Full Access";
                case ClaudePermissionMode.Auto:
                default:
                    return "Auto";
            }
        }

        public static string GetCodexPermissionLabel()
        {
            switch (ResolveCodexPermissionMode())
            {
                case CodexPermissionMode.AskForApproval:
                    return "Ask for approval";
                case CodexPermissionMode.ApproveForMe:
                    return "Approve for me";
                case CodexPermissionMode.FullAccess:
                default:
                    return "Full Access";
            }
        }

        public static string CreatePromptFileArgument(string repositoryPath, string prompt, string scenario, string engineName)
        {
            var promptPath = CreatePromptFile(repositoryPath, prompt, scenario, engineName);
            return BuildPromptFileInstruction(promptPath);
        }

        public static string CreatePromptFile(string repositoryPath, string prompt, string scenario, string engineName)
        {
            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            {
                throw new DirectoryNotFoundException("请选择有效的代码仓库。");
            }

            var promptDirectory = Path.Combine(repositoryPath, ".pm-ai", "prompts");
            Directory.CreateDirectory(promptDirectory);
            CleanupOldPrompts(promptDirectory);

            var safeScenario = ToSafeFileNamePart(scenario, "ai");
            var safeEngineName = ToSafeFileNamePart(engineName, "cli");
            var fileName = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{safeScenario}-{safeEngineName}-{Guid.NewGuid():N}.md";
            var promptPath = Path.Combine(promptDirectory, fileName);
            File.WriteAllText(promptPath, prompt ?? string.Empty, Encoding.UTF8);
            return promptPath;
        }

        public static string BuildPromptFileInstruction(string promptPath)
        {
            return $"请读取并执行这个本地 prompt 文件：\"{promptPath}\"";
        }

        private static string ToSafeFileNamePart(string value, string fallback)
        {
            var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
            var invalidChars = Path.GetInvalidFileNameChars();
            var chars = source
                .Select(ch => invalidChars.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch)
                .ToArray();
            var result = new string(chars).Trim('-');
            return string.IsNullOrWhiteSpace(result) ? fallback : result;
        }

        private static void CleanupOldPrompts(string promptDirectory)
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-PromptRetentionDays);
                foreach (var file in Directory.EnumerateFiles(promptDirectory, "*.md", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < cutoff)
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static void EnsureCommandExists(string commandName)
        {
            var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
            var extensions = new[] { ".exe", ".cmd", ".bat" };
            foreach (var directory in pathValue.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    foreach (var ext in extensions)
                    {
                        var commandPath = Path.Combine(directory.Trim(), commandName + ext);
                        if (File.Exists(commandPath))
                        {
                            return;
                        }
                    }
                }
                catch
                {
                }
            }

            throw new FileNotFoundException($"未在 PATH 中找到 {commandName}，请先安装或配置环境变量。");
        }

        private static void TryEnsureGlobalInstructions(string engineName)
        {
            try
            {
                AiGlobalInstructionService.EnsureCodeGraphInstructions();
                AiGlobalInstructionService.EnsureBehaviorRules();
                AiGlobalBehaviorRuleService.EnsureGlobalBehaviorRules();
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"同步 {engineName} 全局 CodeGraph 规则失败");
            }
        }

        private static string PsQuote(string value)
        {
            return $"'{TerminalHelper.EscapePowerShellSingleQuoted(value)}'";
        }

        private static string BuildClaudeCliCommand(ClaudePermissionMode mode)
        {
            switch (mode)
            {
                case ClaudePermissionMode.FullAccess:
                    return "claude --dangerously-skip-permissions";
                case ClaudePermissionMode.Auto:
                default:
                    return "claude --permission-mode auto";
            }
        }

        private static string BuildCodexCliCommand(CodexPermissionMode mode)
        {
            switch (mode)
            {
                case CodexPermissionMode.AskForApproval:
                    return "codex -s workspace-write -a on-request -c 'approvals_reviewer=\"user\"'";
                case CodexPermissionMode.ApproveForMe:
                    return "codex -s workspace-write -a on-request -c 'approvals_reviewer=\"auto_review\"'";
                case CodexPermissionMode.FullAccess:
                default:
                    return "codex --sandbox danger-full-access --ask-for-approval never";
            }
        }

        private static ClaudePermissionMode ResolveClaudePermissionMode()
        {
            try
            {
                return new DataPersistenceService().LoadSettings()?.ClaudePermissionMode ?? ClaudePermissionMode.Auto;
            }
            catch
            {
                return ClaudePermissionMode.Auto;
            }
        }

        private static CodexPermissionMode ResolveCodexPermissionMode()
        {
            try
            {
                return new DataPersistenceService().LoadSettings()?.CodexPermissionMode ?? CodexPermissionMode.FullAccess;
            }
            catch
            {
                return CodexPermissionMode.FullAccess;
            }
        }
    }
}
