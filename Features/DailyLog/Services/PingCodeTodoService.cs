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
        /// 获取日报默认项目（优先名称包含「建模组」的项目）。
        /// </summary>
        /// <returns>默认项目实体，无可用项目时返回 null。</returns>
        public async Task<Entity> GetDefaultProjectAsync()
        {
            return SelectDefaultProject(await api.GetProjectsAsync());
        }

        /// <summary>
        /// 获取指定项目未完成的迭代，按开始时间倒序（最新在前）排列。
        /// </summary>
        /// <param name="projectId">项目的唯一标识。</param>
        /// <returns>未完成迭代实体列表。</returns>
        public async Task<List<Entity>> GetProjectIterationsAsync(string projectId)
        {
            var iterations = await api.GetNotCompletedIterationsByProjectAsync(projectId);
            return (iterations ?? new List<Entity>())
                .Where(iteration => iteration != null && !string.IsNullOrWhiteSpace(iteration.Id))
                .GroupBy(iteration => iteration.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(iteration => iteration.StartAt ?? long.MinValue)
                .ThenBy(iteration => iteration.Name ?? iteration.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 智能推荐默认迭代：上次手选且仍有效 → 今天落在起止区间内 → 结束日期最近的未来迭代 → 最近开始的迭代 → 名称排序。
        /// </summary>
        /// <param name="iterations">可选迭代列表。</param>
        /// <param name="storedIterationId">上次手选保存的迭代标识。</param>
        /// <param name="today">用于区间判断的日期。</param>
        /// <param name="storedMissing">返回上次保存的迭代是否已失效（不在列表中）。</param>
        /// <returns>推荐的迭代实体，列表为空时返回 null。</returns>
        public static Entity SelectRecommendedIteration(IReadOnlyList<Entity> iterations, string storedIterationId, DateTime today, out bool storedMissing)
        {
            storedMissing = false;
            var list = iterations ?? new List<Entity>();
            if (list.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(storedIterationId))
            {
                var stored = list.FirstOrDefault(iteration => string.Equals(iteration.Id, storedIterationId, StringComparison.OrdinalIgnoreCase));
                if (stored != null)
                {
                    return stored;
                }

                storedMissing = true;
            }

            var nowUnix = new DateTimeOffset(today.Date).ToUnixTimeSeconds();

            var active = list
                .Where(iteration => ContainsDay(iteration, nowUnix))
                .OrderByDescending(iteration => iteration.StartAt ?? long.MinValue)
                .FirstOrDefault();
            if (active != null)
            {
                return active;
            }

            var upcoming = list
                .Where(iteration => !iteration.EndAt.HasValue || iteration.EndAt.Value >= nowUnix)
                .OrderBy(iteration => iteration.EndAt ?? long.MaxValue)
                .ThenByDescending(iteration => iteration.StartAt ?? long.MinValue)
                .FirstOrDefault();
            if (upcoming != null)
            {
                return upcoming;
            }

            return list
                .OrderByDescending(iteration => iteration.StartAt ?? long.MinValue)
                .ThenBy(iteration => iteration.Name ?? iteration.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        /// <summary>
        /// 获取指定迭代中当前用户未开始的工作项；未指定迭代时自动解析默认项目与推荐迭代。
        /// </summary>
        /// <param name="iterationId">迭代唯一标识，可为 null。</param>
        /// <returns>未开始的工作项列表。</returns>
        public async Task<List<WorkItemInfo>> GetTodoItemsAsync(string iterationId)
        {
            var result = new List<WorkItemInfo>();
            try
            {
                var items = await GetScopedItemsAsync(iterationId);
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
        /// 获取指定迭代中当前用户当天完成的开发任务项；未指定迭代时自动解析默认项目与推荐迭代。
        /// </summary>
        /// <param name="date">日报日期。</param>
        /// <param name="iterationId">迭代唯一标识，可为 null。</param>
        /// <returns>当天完成或进入可测试状态的工作项列表。</returns>
        public async Task<List<WorkItemInfo>> GetCompletedItemsAsync(DateTime date, string iterationId)
        {
            var result = new List<WorkItemInfo>();
            try
            {
                var items = await GetScopedItemsAsync(iterationId);
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

        private static bool ContainsDay(Entity iteration, long nowUnix)
        {
            if (iteration == null)
            {
                return false;
            }

            return (!iteration.StartAt.HasValue || iteration.StartAt.Value <= nowUnix) &&
                   (!iteration.EndAt.HasValue || iteration.EndAt.Value >= nowUnix);
        }

        private async Task<List<WorkItemInfo>> GetScopedItemsAsync(string iterationId)
        {
            if (!string.IsNullOrWhiteSpace(iterationId))
            {
                return await api.GetIterationWorkItemsAsync(iterationId);
            }

            var project = SelectDefaultProject(await api.GetProjectsAsync());
            if (project == null || string.IsNullOrWhiteSpace(project.Id))
            {
                return new List<WorkItemInfo>();
            }

            var iterations = await api.GetNotCompletedIterationsByProjectAsync(project.Id);
            var iteration = SelectRecommendedIteration(iterations, null, DateTime.Today, out _);
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
    }
}
