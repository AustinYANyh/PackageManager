using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PackageManager.Features.DailyLog.Models;

namespace PackageManager.Features.DailyLog.Services
{
    /// <summary>
    /// 从本地 Git 仓库采集指定日期的提交记录。
    /// </summary>
    public class GitLogCollectorService
    {
        /// <summary>
        /// 采集指定仓库在给定日期的 Git 提交。
        /// </summary>
        /// <param name="repoPath">仓库根目录路径。</param>
        /// <param name="date">目标日期。</param>
        /// <param name="author">作者过滤（可选，为空则采集所有人的提交）。</param>
        /// <returns>提交记录列表。</returns>
        public List<DailyLogEntry> Collect(string repoPath, DateTime date, string author = null)
        {
            var result = new List<DailyLogEntry>();
            if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(Path.Combine(repoPath, ".git")))
            {
                return result;
            }

            var since = date.ToString("yyyy-MM-ddT00:00:00");
            var until = date.AddDays(1).ToString("yyyy-MM-ddT00:00:00");
            var repoName = new DirectoryInfo(repoPath).Name;

            var args = $"log --since=\"{since}\" --until=\"{until}\" --format=\"%h||%s||%an||%ai\" --no-merges";
            if (!string.IsNullOrWhiteSpace(author))
            {
                args += $" --author=\"{author}\"";
            }

            var output = RunGit(repoPath, args);
            if (string.IsNullOrWhiteSpace(output))
            {
                return result;
            }

            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(new[] { "||" }, StringSplitOptions.None);
                if (parts.Length < 3)
                {
                    continue;
                }

                result.Add(new DailyLogEntry
                {
                    RepoName = repoName,
                    CommitHash = parts[0].Trim(),
                    Message = parts[1].Trim(),
                    Author = parts[2].Trim(),
                    Date = DateTime.TryParse(parts.Length > 3 ? parts[3].Trim() : "", out var dt) ? dt : date,
                    Source = "git"
                });
            }

            return result;
        }

        private static string RunGit(string workingDir, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo("git", arguments)
                {
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using var proc = Process.Start(psi);
                var stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(10000);
                return proc.ExitCode == 0 ? stdout : "";
            }
            catch
            {
                return "";
            }
        }
    }
}
