using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PackageManager.Features.DailyLog.Models;
using PackageManager.Services.PingCode.Dto;

namespace PackageManager.Features.DailyLog.Services
{
    /// <summary>
    /// 汇总 Git/SVN 提交与 PingCode 工作项，生成 4 模块钉钉日报文本。
    /// </summary>
    public class DailyLogGeneratorService
    {
        /// <summary>
        /// 根据采集数据生成日报文本。
        /// </summary>
        /// <param name="date">日报日期。</param>
        /// <param name="gitCommits">Git 提交列表。</param>
        /// <param name="svnCommits">SVN 提交列表。</param>
        /// <param name="todoItems">PingCode 未开始工作项。</param>
        /// <returns>格式化的日报文本。</returns>
        public string Generate(
            DateTime date,
            List<DailyLogEntry> gitCommits,
            List<DailyLogEntry> svnCommits,
            List<WorkItemInfo> todoItems)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"【工作日报】{date:yyyy-MM-dd}");
            sb.AppendLine();

            // 模块 1：今日工作
            sb.AppendLine("一、今日工作");
            var allCommits = new List<DailyLogEntry>();
            if (gitCommits != null) allCommits.AddRange(gitCommits);
            if (svnCommits != null) allCommits.AddRange(svnCommits);

            if (allCommits.Count == 0)
            {
                sb.AppendLine("无");
            }
            else
            {
                var commits = allCommits
                    .OrderBy(c => c.Date)
                    .ThenBy(c => c.Message)
                    .ToList();

                for (int i = 0; i < commits.Count; i++)
                {
                    sb.AppendLine($"{i + 1}. {GetSummaryTitle(commits[i].Message)}");
                }
            }

            sb.AppendLine();

            // 模块 2：未完成工作
            sb.AppendLine("二、未完成工作");
            sb.AppendLine("无");
            sb.AppendLine();

            // 模块 3：明日计划
            sb.AppendLine("三、明日计划");
            if (todoItems == null || todoItems.Count == 0)
            {
                sb.AppendLine("无");
            }
            else
            {
                var sorted = todoItems
                    .OrderByDescending(i => GetPriorityWeight(i.Priority))
                    .ThenBy(i => i.Title)
                    .ToList();

                for (int i = 0; i < sorted.Count; i++)
                {
                    var item = sorted[i];
                    var identifier = string.IsNullOrWhiteSpace(item.Identifier) ? item.Id : item.Identifier;
                    sb.AppendLine($"{i + 1}. [{identifier}] {item.Title}");
                }
            }

            sb.AppendLine();

            // 模块 4：障碍与困难
            sb.AppendLine("四、障碍与困难");
            sb.AppendLine("无");

            return sb.ToString();
        }

        private static int GetPriorityWeight(string priority)
        {
            if (string.IsNullOrWhiteSpace(priority)) return 0;
            var p = priority.Trim().ToLowerInvariant();
            if (p.Contains("highest") || p.Contains("最高") || p.Contains("urgent")) return 5;
            if (p.Contains("higher") || p.Contains("高")) return 4;
            if (p.Contains("high") || p.Contains("较高")) return 3;
            if (p.Contains("medium") || p.Contains("中")) return 2;
            if (p.Contains("low") || p.Contains("低")) return 1;
            return 0;
        }

        private static string GetSummaryTitle(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "未填写提交说明";
            }

            var firstLine = message
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? message.Trim();

            var normalized = Regex.Replace(firstLine, @"^\w+(?:\([^)]+\))?\s*[:：]\s*", string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? firstLine : normalized;
        }
    }
}
