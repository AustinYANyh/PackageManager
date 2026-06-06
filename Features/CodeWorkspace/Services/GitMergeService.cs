using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PackageManager.Features.CodeWorkspace.Models;

namespace PackageManager.Features.CodeWorkspace.Services
{
    public class GitMergeService
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

        public async Task<MergePrecheckResult> PrecheckAsync(MergeRepositoryItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.RepositoryPath) || !Directory.Exists(item.RepositoryPath))
            {
                return FailPrecheck("仓库路径不存在。");
            }

            var inside = await RunGitAsync("rev-parse --is-inside-work-tree", item.RepositoryPath);
            if (inside.ExitCode != 0 || !inside.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return FailPrecheck("不是有效的 Git 工作副本。");
            }

            var branch = await GetCurrentBranchAsync(item.RepositoryPath);
            if (string.IsNullOrWhiteSpace(branch) || branch == "HEAD")
            {
                return FailPrecheck("当前处于 detached HEAD，不能自动合并。");
            }

            item.SourceBranch = branch;
            var targetBranch = string.IsNullOrWhiteSpace(item.TargetBranch) ? "master" : item.TargetBranch;
            var fetch = await RunGitAsync("fetch origin", item.RepositoryPath, DefaultTimeout);
            if (fetch.ExitCode != 0)
            {
                return FailPrecheck(BuildFailure("远端检查失败", fetch));
            }

            var targetExists = await BranchExistsAsync(item.RepositoryPath, targetBranch);
            if (!targetExists)
            {
                return FailPrecheck($"目标分支 {targetBranch} 不存在。");
            }

            var status = await RunGitAsync("-c core.quotepath=false status --porcelain --untracked-files=no", item.RepositoryPath);
            if (status.ExitCode != 0)
            {
                return FailPrecheck(BuildFailure("读取工作区状态失败", status));
            }

            if (!string.IsNullOrWhiteSpace(status.Output))
            {
                return FailPrecheck("存在未提交的已跟踪文件改动，请先提交或还原。");
            }

            var untrackedConflict = await FindUntrackedCheckoutConflictAsync(item.RepositoryPath, targetBranch);
            if (!string.IsNullOrWhiteSpace(untrackedConflict))
            {
                return FailPrecheck($"未跟踪文件会被目标分支覆盖：{untrackedConflict}");
            }

            var remoteStatus = await GetRemoteStatusAsync(item.RepositoryPath);
            return new MergePrecheckResult
            {
                Success = true,
                Message = "检查通过",
                CurrentBranch = branch,
                RemoteStatus = remoteStatus,
            };
        }

        public async Task<MergeExecutionResult> MergeAsync(MergeRepositoryItem item)
        {
            var targetBranch = string.IsNullOrWhiteSpace(item.TargetBranch) ? "master" : item.TargetBranch;
            var sourceBranch = item.SourceBranch;
            if (string.IsNullOrWhiteSpace(sourceBranch))
            {
                sourceBranch = await GetCurrentBranchAsync(item.RepositoryPath);
            }

            var fetch = await RunGitAsync("fetch origin", item.RepositoryPath, DefaultTimeout);
            if (fetch.ExitCode != 0)
            {
                return FailExecution(BuildFailure("fetch 失败", fetch));
            }

            var localTargetExists = await LocalBranchExistsAsync(item.RepositoryPath, targetBranch);
            var checkoutArguments = localTargetExists
                ? $"checkout {QuoteArgument(targetBranch)}"
                : $"checkout -B {QuoteArgument(targetBranch)} {QuoteArgument("origin/" + targetBranch)}";
            var checkout = await RunGitAsync(checkoutArguments, item.RepositoryPath, DefaultTimeout);
            if (checkout.ExitCode != 0)
            {
                return FailExecution(BuildFailure($"切换 {targetBranch} 失败", checkout));
            }

            var pull = await RunGitAsync($"pull --ff-only origin {QuoteArgument(targetBranch)}", item.RepositoryPath, DefaultTimeout);
            if (pull.ExitCode != 0)
            {
                return FailExecution(BuildFailure($"拉取 {targetBranch} 失败", pull));
            }

            var merge = await RunGitAsync($"merge --no-ff {QuoteArgument(sourceBranch)}", item.RepositoryPath, DefaultTimeout);
            if (merge.ExitCode == 0)
            {
                return new MergeExecutionResult { Success = true, Message = "合并完成" };
            }

            var conflicts = await GetConflictFilesAsync(item.RepositoryPath);
            if (conflicts.Count > 0)
            {
                var result = new MergeExecutionResult
                {
                    Success = false,
                    HasConflict = true,
                    Message = "合并产生冲突，请解决后继续。",
                };
                result.ConflictFiles.AddRange(conflicts);
                return result;
            }

            return FailExecution(BuildFailure("合并失败", merge));
        }

        public async Task<IReadOnlyList<MergeConflictFile>> GetConflictFilesAsync(string repositoryPath)
        {
            var result = await RunGitAsync("-c core.quotepath=false diff --name-only --diff-filter=U", repositoryPath);
            if (result.ExitCode != 0)
            {
                return new List<MergeConflictFile>();
            }

            return result.Output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Trim().Replace('\\', '/'))
                .Select(path => new MergeConflictFile
                {
                    RelativePath = path,
                    FullPath = Path.Combine(repositoryPath, path.Replace('/', Path.DirectorySeparatorChar)),
                    StatusText = "冲突",
                })
                .ToList();
        }

        public async Task<MergeExecutionResult> ContinueMergeAsync(MergeRepositoryItem item)
        {
            var conflicts = await GetConflictFilesAsync(item.RepositoryPath);
            if (conflicts.Count > 0)
            {
                var result = new MergeExecutionResult
                {
                    Success = false,
                    HasConflict = true,
                    Message = "仍有冲突文件未解决。",
                };
                result.ConflictFiles.AddRange(conflicts);
                return result;
            }

            var add = await RunGitAsync("add -A", item.RepositoryPath, DefaultTimeout);
            if (add.ExitCode != 0)
            {
                return FailExecution(BuildFailure("暂存解决结果失败", add));
            }

            var commit = await RunGitAsync("commit --no-edit", item.RepositoryPath, DefaultTimeout);
            if (commit.ExitCode != 0)
            {
                return FailExecution(BuildFailure("完成合并提交失败", commit));
            }

            return new MergeExecutionResult { Success = true, Message = "冲突已解决，合并提交完成" };
        }

        public async Task<MergeExecutionResult> AbortMergeAsync(MergeRepositoryItem item)
        {
            var abort = await RunGitAsync("merge --abort", item.RepositoryPath, DefaultTimeout);
            if (abort.ExitCode != 0)
            {
                return FailExecution(BuildFailure("终止合并失败", abort));
            }

            return new MergeExecutionResult { Success = true, Message = "已终止合并" };
        }

        public async Task<MergeExecutionResult> PushTargetAsync(MergeRepositoryItem item)
        {
            var targetBranch = string.IsNullOrWhiteSpace(item.TargetBranch) ? "master" : item.TargetBranch;
            var push = await RunGitAsync($"push origin {QuoteArgument(targetBranch)}", item.RepositoryPath, DefaultTimeout);
            if (push.ExitCode != 0)
            {
                return FailExecution(BuildFailure($"推送 {targetBranch} 失败", push));
            }

            return new MergeExecutionResult { Success = true, Message = "推送完成" };
        }

        public async Task<string> CreateAiConflictPromptAsync(MergeRepositoryItem item, MergeConflictFile conflictFile)
        {
            var relativePath = conflictFile?.RelativePath;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new InvalidOperationException("请选择一个冲突文件。");
            }

            var baseText = await ReadGitStageAsync(item.RepositoryPath, relativePath, 1);
            var oursText = await ReadGitStageAsync(item.RepositoryPath, relativePath, 2);
            var theirsText = await ReadGitStageAsync(item.RepositoryPath, relativePath, 3);
            var workingText = File.Exists(conflictFile.FullPath) ? File.ReadAllText(conflictFile.FullPath, Encoding.UTF8) : string.Empty;
            var prompt = $@"请辅助解决 Git 合并冲突，但不要自动提交或推送。

仓库：{item.RepositoryPath}
文件：{relativePath}
目标分支：{item.TargetBranch}
源分支：{item.SourceBranch}

请输出三部分：
1. 建议合并后的完整文件内容。
2. 保留了 ours/theirs 中哪些关键逻辑。
3. 需要人工复核的风险点。

## BASE
```text
{baseText}
```

## OURS
```text
{oursText}
```

## THEIRS
```text
{theirsText}
```

## 当前冲突文件
```text
{workingText}
```
";
            return AiCliLaunchService.CreatePromptFile(item.RepositoryPath, prompt, "merge-conflict", "codex");
        }

        public void ApplyConflictSuggestion(MergeConflictFile conflictFile, string suggestionText)
        {
            if (conflictFile == null || string.IsNullOrWhiteSpace(conflictFile.FullPath))
            {
                throw new InvalidOperationException("请选择一个冲突文件。");
            }

            File.WriteAllText(conflictFile.FullPath, suggestionText ?? string.Empty, Encoding.UTF8);
            conflictFile.StatusText = "已应用建议，待继续检查";
        }

        private static async Task<string> ReadGitStageAsync(string repositoryPath, string relativePath, int stage)
        {
            var result = await RunGitAsync($"show {QuoteArgument(":" + stage + ":" + relativePath)}", repositoryPath);
            return result.ExitCode == 0 ? result.Output : string.Empty;
        }

        private static async Task<bool> BranchExistsAsync(string repositoryPath, string branch)
        {
            return await LocalBranchExistsAsync(repositoryPath, branch) || await RemoteBranchExistsAsync(repositoryPath, branch);
        }

        private static async Task<bool> LocalBranchExistsAsync(string repositoryPath, string branch)
        {
            var local = await RunGitAsync($"show-ref --verify --quiet {QuoteArgument("refs/heads/" + branch)}", repositoryPath);
            return local.ExitCode == 0;
        }

        private static async Task<bool> RemoteBranchExistsAsync(string repositoryPath, string branch)
        {
            var remote = await RunGitAsync($"show-ref --verify --quiet {QuoteArgument("refs/remotes/origin/" + branch)}", repositoryPath);
            return remote.ExitCode == 0;
        }

        private static async Task<string> FindUntrackedCheckoutConflictAsync(string repositoryPath, string targetBranch)
        {
            var untracked = await RunGitAsync("-c core.quotepath=false ls-files --others --exclude-standard", repositoryPath);
            if (untracked.ExitCode != 0 || string.IsNullOrWhiteSpace(untracked.Output))
            {
                return null;
            }

            var targetRef = await LocalBranchExistsAsync(repositoryPath, targetBranch) ? targetBranch : "origin/" + targetBranch;
            var targetFiles = await RunGitAsync($"ls-tree -r --name-only {QuoteArgument(targetRef)}", repositoryPath);
            if (targetFiles.ExitCode != 0 || string.IsNullOrWhiteSpace(targetFiles.Output))
            {
                return null;
            }

            var targetSet = new HashSet<string>(
                targetFiles.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
            return untracked.Output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Trim())
                .FirstOrDefault(path => targetSet.Contains(path));
        }

        private static async Task<string> GetCurrentBranchAsync(string repositoryPath)
        {
            var branch = await RunGitAsync("branch --show-current", repositoryPath);
            if (!string.IsNullOrWhiteSpace(branch.Output))
            {
                return branch.Output.Trim();
            }

            var fallback = await RunGitAsync("rev-parse --abbrev-ref HEAD", repositoryPath);
            return fallback.Output.Trim();
        }

        private static async Task<string> GetRemoteStatusAsync(string repositoryPath)
        {
            var upstream = await RunGitAsync("rev-parse --abbrev-ref --symbolic-full-name @{upstream}", repositoryPath);
            if (upstream.ExitCode != 0)
            {
                return "未设置 upstream";
            }

            var count = await RunGitAsync("rev-list --left-right --count HEAD...@{upstream}", repositoryPath);
            if (count.ExitCode != 0)
            {
                return "远端状态未知";
            }

            var parts = count.Output.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $"↑{parts[0]} ↓{parts[1]}" : "远端状态未知";
        }

        private static MergePrecheckResult FailPrecheck(string message)
        {
            return new MergePrecheckResult { Success = false, Message = message };
        }

        private static MergeExecutionResult FailExecution(string message)
        {
            return new MergeExecutionResult { Success = false, Message = message };
        }

        private static string BuildFailure(string prefix, CommandResult result)
        {
            var detail = string.IsNullOrWhiteSpace(result?.Error) ? result?.Output : result.Error;
            return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix}: {detail.Trim()}";
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static async Task<CommandResult> RunGitAsync(string arguments, string workingDirectory)
        {
            return await RunGitAsync(arguments, workingDirectory, DefaultTimeout);
        }

        private static async Task<CommandResult> RunGitAsync(string arguments, string workingDirectory, TimeSpan timeout)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
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
                        return new CommandResult { ExitCode = -1, Error = "Timeout" };
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
                return new CommandResult { ExitCode = -1, Error = ex.Message };
            }
        }

        private class CommandResult
        {
            public int ExitCode { get; set; }

            public string Output { get; set; }

            public string Error { get; set; }
        }
    }
}
