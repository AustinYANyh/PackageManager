using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PackageManager.Services.PingCode;
using PackageManager.Services.PingCode.Dto;

namespace PackageManager.Features.DailyLog.Services
{
    /// <summary>
    /// 从 PingCode 查询当前用户未开始的工作项，用于日报「明日计划」模块。
    /// </summary>
    public class PingCodeTodoService
    {
        private readonly PingCodeApiService api;

        /// <summary>
        /// 初始化 <see cref="PingCodeTodoService"/>。
        /// </summary>
        public PingCodeTodoService()
        {
            api = new PingCodeApiService();
        }

        /// <summary>
        /// 获取当前用户在所有项目中未开始的工作项。
        /// </summary>
        /// <returns>未开始的工作项列表。</returns>
        public async Task<List<WorkItemInfo>> GetTodoItemsAsync()
        {
            var result = new List<WorkItemInfo>();
            try
            {
                var projects = await api.GetProjectsAsync();
                foreach (var project in projects)
                {
                    try
                    {
                        var iterations = await api.GetOngoingIterationsByProjectAsync(project.Id);
                        foreach (var iteration in iterations)
                        {
                            try
                            {
                                var items = await api.GetIterationWorkItemsAsync(iteration.Id);
                                foreach (var item in items)
                                {
                                    if (IsTodoState(item.StateCategory))
                                    {
                                        result.Add(item);
                                    }
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
    }
}
