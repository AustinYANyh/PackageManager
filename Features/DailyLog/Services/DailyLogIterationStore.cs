using System;
using System.IO;
using Newtonsoft.Json;

namespace PackageManager.Features.DailyLog.Services
{
    /// <summary>
    /// 工作日报迭代选择存储，保存用户手选的项目与迭代，用于下次打开日报时恢复默认迭代。
    /// </summary>
    public sealed class DailyLogIterationStore
    {
        private readonly string selectionFilePath;

        /// <summary>
        /// 初始化 <see cref="DailyLogIterationStore"/>。
        /// </summary>
        public DailyLogIterationStore()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appFolder = Path.Combine(appDataPath, "PackageManager", "daily-log");
            Directory.CreateDirectory(appFolder);
            selectionFilePath = Path.Combine(appFolder, "iteration.json");
        }

        /// <summary>
        /// 加载上次保存的迭代选择；从未保存或读取失败时返回 null。
        /// </summary>
        /// <returns>迭代选择数据。</returns>
        public DailyLogIterationSelection Load()
        {
            try
            {
                if (!File.Exists(selectionFilePath))
                {
                    return null;
                }

                return JsonConvert.DeserializeObject<DailyLogIterationSelection>(File.ReadAllText(selectionFilePath));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 保存迭代选择。
        /// </summary>
        /// <param name="selection">要保存的迭代选择数据。</param>
        /// <returns>保存成功返回 true。</returns>
        public bool Save(DailyLogIterationSelection selection)
        {
            try
            {
                File.WriteAllText(selectionFilePath, JsonConvert.SerializeObject(selection ?? new DailyLogIterationSelection()));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 工作日报迭代选择持久化模型。
    /// </summary>
    public sealed class DailyLogIterationSelection
    {
        /// <summary>
        /// 获取或设置项目唯一标识。
        /// </summary>
        public string ProjectId { get; set; }

        /// <summary>
        /// 获取或设置迭代唯一标识。
        /// </summary>
        public string IterationId { get; set; }

        /// <summary>
        /// 获取或设置迭代名称，仅用于展示与排查。
        /// </summary>
        public string IterationName { get; set; }
    }
}
