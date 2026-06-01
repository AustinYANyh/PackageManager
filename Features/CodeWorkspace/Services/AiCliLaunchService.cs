using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PackageManager.Features.CodeWorkspace.Models;

namespace PackageManager.Features.CodeWorkspace.Services
{
    public class AiCliLaunchService
    {
        private const int DirectPromptLimit = 24000;

        public Task LaunchClaudeAsync(CodeRepository repo, string prompt, string title)
        {
            var fullPrompt = "/plan\n" + (prompt ?? string.Empty);
            return LaunchAsync(repo, fullPrompt, title, "Claude", "claude", "claude --dangerously-skip-permissions");
        }

        public Task LaunchCodexAsync(CodeRepository repo, string prompt, string title)
        {
            return LaunchAsync(repo, prompt, title, "Codex", "codex", "codex --sandbox danger-full-access --ask-for-approval never");
        }

        private static Task LaunchAsync(CodeRepository repo, string prompt, string title, string engineName, string commandName, string commandPrefix)
        {
            if (repo == null || string.IsNullOrWhiteSpace(repo.Path) || !Directory.Exists(repo.Path))
            {
                throw new DirectoryNotFoundException("请选择有效的代码仓库。");
            }

            EnsureCommandExists(commandName);
            var finalPrompt = BuildPromptArgument(repo.Path, prompt ?? string.Empty);
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

        private static string BuildPromptArgument(string repositoryPath, string prompt)
        {
            prompt = prompt ?? string.Empty;
            if (prompt.Length <= DirectPromptLimit)
            {
                return prompt;
            }

            var promptDirectory = Path.Combine(repositoryPath, ".pm-ai");
            Directory.CreateDirectory(promptDirectory);
            var promptPath = Path.Combine(promptDirectory, "pingcode-workitem-prompt.md");
            File.WriteAllText(promptPath, prompt);
            return $"请读取并执行这个 PingCode 工作项 prompt 文件：{promptPath}";
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

        private static string PsQuote(string value)
        {
            return $"'{TerminalHelper.EscapePowerShellSingleQuoted(value)}'";
        }
    }
}
