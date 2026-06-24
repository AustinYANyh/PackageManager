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
                var project = SelectDefaultProject(await api.GetProjectsAsync());
                if (project == null || string.IsNullOrWhiteSpace(project.Id))
                {
                    return result;
                }

                var iteration = SelectDefaultIteration(await api.GetNotCompletedIterationsByProjectAsync(project.Id));
                if (iteration == null || string.IsNullOrWhiteSpace(iteration.Id))
                {
                    return result;
                }

                var items = await api.GetIterationWorkItemsAsync(iteration.Id);
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
