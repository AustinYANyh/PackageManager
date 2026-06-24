using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using PackageManager.Features.DailyLog.Models;

namespace PackageManager.Features.DailyLog.Services
{
    /// <summary>
    /// 从本地 SVN 工作副本采集指定日期的提交记录。
    /// </summary>
    public class SvnLogCollectorService
    {
        /// <summary>
        /// 采集指定 SVN 工作副本在给定日期的提交。
        /// </summary>
        /// <param name="wcRoot">SVN 工作副本根目录。</param>
        /// <param name="date">目标日期。</param>
        /// <param name="authorFilter">作者过滤（可选，为空则采集所有人的提交）。</param>
        /// <returns>提交记录列表。</returns>
        public List<DailyLogEntry> Collect(string wcRoot, DateTime date, string authorFilter = null)
        {
            var result = new List<DailyLogEntry>();
            if (string.IsNullOrWhiteSpace(wcRoot) || !Directory.Exists(Path.Combine(wcRoot, ".svn")))
            {
                return result;
            }

            var repoName = new DirectoryInfo(wcRoot).Name;
            var startDate = date.Date;
            var endDate = startDate.AddDays(1);
            var args = $"log -r {{{startDate:yyyy-MM-ddTHH:mm:ss}}}:{{{endDate:yyyy-MM-ddTHH:mm:ss}}} --xml -v \"{wcRoot}\"";

            var output = RunSvn(args);
            if (string.IsNullOrWhiteSpace(output))
            {
                return result;
            }

            try
            {
                var doc = XDocument.Parse(output);
                foreach (var logEntry in doc.Descendants("logentry"))
                {
                    var revision = logEntry.Attribute("revision")?.Value ?? "";
                    var author = logEntry.Element("author")?.Value ?? "";
                    var msg = logEntry.Element("msg")?.Value ?? "";
                    var dateStr2 = logEntry.Element("date")?.Value ?? "";
                    var entryDate = DateTime.TryParse(dateStr2, out var dt) ? dt.ToLocalTime() : date;
                    if (entryDate.Date != startDate)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(authorFilter) &&
                        !string.Equals(author.Trim(), authorFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Add(new DailyLogEntry
                    {
                        RepoName = repoName,
                        CommitHash = $"r{revision}",
                        Message = msg.Trim(),
                        Author = author.Trim(),
                        Date = entryDate,
                        Source = "svn"
                    });
                }
            }
            catch
            {
            }

            return result;
        }

        private static string RunSvn(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo("svn", arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using var proc = Process.Start(psi);
                var stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(15000);
                return proc.ExitCode == 0 ? stdout : "";
            }
            catch
            {
                return "";
            }
        }
    }
}
