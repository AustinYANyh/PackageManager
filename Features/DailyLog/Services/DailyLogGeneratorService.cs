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
        private const double TomorrowPlanTargetStoryPoints = 1.0;
        private const int TomorrowPlanMaxItems = 4;

        /// <summary>
        /// 根据采集数据生成日报文本。
        /// </summary>
        /// <param name="date">日报日期。</param>
        /// <param name="gitCommits">Git 提交列表。</param>
        /// <param name="svnCommits">SVN 提交列表。</param>
        /// <param name="completedItems">PingCode 当天完成的工作项。</param>
        /// <param name="todoItems">PingCode 未开始工作项。</param>
        /// <returns>格式化的日报文本。</returns>
        public string Generate(
            DateTime date,
            List<DailyLogEntry> gitCommits,
            List<DailyLogEntry> svnCommits,
            List<WorkItemInfo> completedItems,
            List<WorkItemInfo> todoItems)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"【工作日报】{date:yyyy-MM-dd}");
            sb.AppendLine();

            // 模块 1：今日工作
            sb.AppendLine("一、今日工作");
            var todayWorkItems = BuildTodayWorkItems(gitCommits, svnCommits, completedItems);
            if (todayWorkItems.Count == 0)
            {
                sb.AppendLine("无");
            }
            else
            {
                for (int i = 0; i < todayWorkItems.Count; i++)
                {
                    sb.AppendLine($"{i + 1}. {todayWorkItems[i]}");
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
                var sorted = BuildTomorrowPlanItems(todoItems);

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

        private static List<string> BuildTodayWorkItems(
            IEnumerable<DailyLogEntry> gitCommits,
            IEnumerable<DailyLogEntry> svnCommits,
            IEnumerable<WorkItemInfo> completedItems)
        {
            var result = new List<string>();
            var allCommits = new List<DailyLogEntry>();
            if (gitCommits != null) allCommits.AddRange(gitCommits);
            if (svnCommits != null) allCommits.AddRange(svnCommits);

            result.AddRange(allCommits
                .OrderBy(c => c.Date)
                .ThenBy(c => c.Message)
                .Select(c => GetSummaryTitle(c.Message)));

            if (completedItems != null)
            {
                result.AddRange(completedItems
                    .Where(item => item != null)
                    .OrderBy(item => item.CompletedAt ?? item.UpdatedAt ?? DateTime.MinValue)
                    .ThenBy(item => item.Title)
                    .Select(GetWorkItemSummary));
            }

            return DeduplicateWorkItems(result);
        }

        private static List<WorkItemInfo> BuildTomorrowPlanItems(IEnumerable<WorkItemInfo> todoItems)
        {
            var selected = new List<WorkItemInfo>();
            var totalStoryPoints = 0d;

            var priorityGroups = (todoItems ?? Enumerable.Empty<WorkItemInfo>())
                .Where(item => item != null)
                .GroupBy(item => GetPriorityWeight(item.Priority))
                .OrderByDescending(group => group.Key);

            foreach (var group in priorityGroups)
            {
                var items = group
                    .OrderBy(item => GetStoryPointSortValue(item))
                    .ThenBy(item => item.Title)
                    .ToList();

                foreach (var item in items)
                {
                    if (selected.Count >= TomorrowPlanMaxItems)
                    {
                        return selected;
                    }

                    selected.Add(item);
                    totalStoryPoints += GetCapacityStoryPoints(item);
                    if (totalStoryPoints >= TomorrowPlanTargetStoryPoints)
                    {
                        return selected;
                    }
                }
            }

            return selected;
        }

        private static List<string> DeduplicateWorkItems(IEnumerable<string> items)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items ?? Enumerable.Empty<string>())
            {
                var text = (item ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (seen.Add(NormalizeWorkItemKey(text)))
                {
                    result.Add(text);
                }
            }

            return result;
        }

        private static string NormalizeWorkItemKey(string text)
        {
            return Regex.Replace(text ?? string.Empty, @"\s+", string.Empty).Trim();
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

        private static double GetStoryPointSortValue(WorkItemInfo item)
        {
            var storyPoints = item?.StoryPoints ?? 0;
            return storyPoints > 0 ? storyPoints : double.MaxValue;
        }

        private static double GetCapacityStoryPoints(WorkItemInfo item)
        {
            var storyPoints = item?.StoryPoints ?? 0;
            return storyPoints > 0 ? storyPoints : TomorrowPlanTargetStoryPoints;
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

        private static string GetWorkItemSummary(WorkItemInfo item)
        {
            if (item == null)
            {
                return "未命名工作项";
            }

            var title = string.IsNullOrWhiteSpace(item.Title) ? "未命名工作项" : item.Title.Trim();
            var identifier = string.IsNullOrWhiteSpace(item.Identifier) ? item.Id : item.Identifier;
            return string.IsNullOrWhiteSpace(identifier) ? title : $"[{identifier}] {title}";
        }
    }
}
