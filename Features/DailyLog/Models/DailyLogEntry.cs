using System;

namespace PackageManager.Features.DailyLog.Models
{
    /// <summary>
    /// 表示一条版本控制提交记录，用于日报采集。
    /// </summary>
    public class DailyLogEntry
    {
        /// <summary>
        /// 获取或设置仓库名称。
        /// </summary>
        public string RepoName { get; set; }

        /// <summary>
        /// 获取或设置提交哈希或 SVN 版本号。
        /// </summary>
        public string CommitHash { get; set; }

        /// <summary>
        /// 获取或设置提交信息。
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// 获取或设置提交作者。
        /// </summary>
        public string Author { get; set; }

        /// <summary>
        /// 获取或设置提交时间。
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// 获取或设置来源类型（"git" 或 "svn"）。
        /// </summary>
        public string Source { get; set; }
    }
}
