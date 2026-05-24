using System;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;

namespace PackageManager.Models
{
    /// <summary>
    /// 日志条目数据模型。
    /// </summary>
    public class LogEntry
    {
        /// <summary>
        /// 获取或设置日志时间戳。
        /// </summary>
        [DataGridColumn(1, DisplayName = "时间", Width = "160", IsReadOnly = true)]
        public string Timestamp { get; set; }

        /// <summary>
        /// 获取或设置日志级别。
        /// </summary>
        [DataGridColumn(2, DisplayName = "级别", Width = "100", IsReadOnly = true)]
        public string Level { get; set; }

        /// <summary>
        /// 获取或设置日志消息内容。
        /// </summary>
        [DataGridColumn(3, DisplayName = "消息", Width = "750", IsReadOnly = true)]
        public string Message { get; set; }

        /// <summary>
        /// 获取或设置日志详细信息。
        /// </summary>
        public string Details { get; set; }
    }
}