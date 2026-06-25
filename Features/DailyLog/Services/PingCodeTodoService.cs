using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PackageManager.Services.PingCode;
using PackageManager.Services.PingCode.Dto;
using PackageManager.Services.PingCode.Model;

namespace PackageManager.Features.DailyLog.Services
{
    /// <summary>
    /// 从 PingCode 查询当前用户未开始的工作项，用于日报「明日计划」模块。
    /// </summary>
    public class PingCodeTodoService
    {
        private static readonly string[] AssigneeFilters = { "yanyunhao", "AustinYanyh", "闫云皓" };

        private readonly PingCodeApiService api;

        /// <summary>
        /// 初始化 <see cref="PingCodeTodoService"/>。
        /// </summary>
        public PingCodeTodoService()
        {
            api = new PingCodeApiService();
        }

        /// <summary>
        /// 获取看板统计默认项目与默认迭代中的未开始工作项。
        /// </summary>
        /// <returns>未开始的工作项列表。</returns>
        public async Task<List<WorkItemInfo>> GetTodoItemsAsync()
        {
            var result = new List<WorkItemInfo>();
            try
            {
                var items = await GetScopedItemsAsync();
                foreach (var item in items.Where(item => item != null && IsTodoState(item.StateCategory) && IsSelectedAssignee(item)))
                {
                    result.Add(item);
                }
            }
            catch
            {
            }

            return result;
        }

        /// <summary>
        /// 获取看板统计默认项目与默认迭代中的当天完成开发任务项。
        /// </summary>
        /// <param name="date">日报日期。</param>
        /// <returns>当天完成或进入可测试状态的工作项列表。</returns>
        public async Task<List<WorkItemInfo>> GetCompletedItemsAsync(DateTime date)
        {
            var result = new List<WorkItemInfo>();
            try
            {
                var items = await GetScopedItemsAsync();
                foreach (var item in items.Where(item => item != null && IsSelectedAssignee(item) && IsCompletedDevelopmentItem(item, date))
                                          .GroupBy(GetStableKey, StringComparer.OrdinalIgnoreCase)
                                          .Select(group => group.First()))
                {
                    result.Add(item);
                }
            }
            catch
            {
            }

            return result;
        }

        private async Task<List<WorkItemInfo>> GetScopedItemsAsync()
        {
            var project = SelectDefaultProject(await api.GetProjectsAsync());
            if (project == null || string.IsNullOrWhiteSpace(project.Id))
            {
                return new List<WorkItemInfo>();
            }

            var iteration = SelectDefaultIteration(await api.GetNotCompletedIterationsByProjectAsync(project.Id));
            if (iteration == null || string.IsNullOrWhiteSpace(iteration.Id))
            {
                return new List<WorkItemInfo>();
            }

            return await api.GetIterationWorkItemsAsync(iteration.Id);
        }

        private static bool IsTodoState(string stateCategory)
        {
            if (string.IsNullOrWhiteSpace(stateCategory))
            {
                return false;
            }

            var s = stateCategory.Trim().ToLowerInvariant();
            return s == "未开始" || s == "新提交" || s == "打开" || s == "新建" || s == "待处理" || s == "todo";
        }

        private static bool IsSelectedAssignee(WorkItemInfo item)
        {
            if (item == null)
            {
                return false;
            }

            return IsAssigneeMatch(item.AssigneeId) || IsAssigneeMatch(item.AssigneeName);
        }

        private static bool IsAssigneeMatch(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var text = value.Trim();
            return AssigneeFilters.Any(filter => string.Equals(text, filter, StringComparison.OrdinalIgnoreCase) ||
                                                 text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsCompletedDevelopmentItem(WorkItemInfo item, DateTime date)
        {
            var category = (item?.StateCategory ?? string.Empty).Trim();
            if (string.Equals(category, "可测试", StringComparison.OrdinalIgnoreCase))
            {
                return IsSameLocalDate(item.UpdatedAt, date);
            }

            if (string.Equals(category, "已完成", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(category, "已关闭", StringComparison.OrdinalIgnoreCase))
            {
                return IsSameLocalDate(item.CompletedAt, date) || IsSameLocalDate(item.UpdatedAt, date);
            }

            return false;
        }

        private static bool IsSameLocalDate(DateTime? timestamp, DateTime targetDate)
        {
            if (!timestamp.HasValue)
            {
                return false;
            }

            var value = timestamp.Value;
            var localDate = value.Kind == DateTimeKind.Utc ? value.ToLocalTime().Date : value.Date;
            return localDate == targetDate.Date;
        }

        private static string GetStableKey(WorkItemInfo item)
        {
            return item?.Id ?? item?.Identifier ?? item?.Title ?? string.Empty;
        }

        private static Entity SelectDefaultProject(IEnumerable<Entity> projects)
        {
            var ordered = (projects ?? Enumerable.Empty<Entity>())
                .Where(project => project != null && !string.IsNullOrWhiteSpace(project.Id))
                .OrderBy(project => project.Name ?? project.Id)
                .ToList();
            return ordered.FirstOrDefault(project => (project.Name ?? string.Empty).Contains("建模组")) ??
                   ordered.FirstOrDefault();
        }

        private static Entity SelectDefaultIteration(IEnumerable<Entity> iterations)
        {
            return (iterations ?? Enumerable.Empty<Entity>())
                .Where(iteration => iteration != null && !string.IsNullOrWhiteSpace(iteration.Id))
                .GroupBy(iteration => iteration.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(iteration => iteration.Name ?? iteration.Id)
                .FirstOrDefault();
        }
    }
}
