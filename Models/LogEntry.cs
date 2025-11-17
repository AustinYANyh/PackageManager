using System;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;

namespace PackageManager.Models
{
    public class LogEntry
    {
        [DataGridColumn(1, DisplayName = "时间", Width = "160", IsReadOnly = true)]
        public string Timestamp { get; set; }

        [DataGridColumn(2, DisplayName = "级别", Width = "100", IsReadOnly = true)]
        public string Level { get; set; }

        [DataGridColumn(3, DisplayName = "消息", Width = "1190", IsReadOnly = true)]
        public string Message { get; set; }

        // [DataGridColumn(4, DisplayName = "详情", Width = "*", IsReadOnly = true)]
        public string Details { get; set; }
    }
}