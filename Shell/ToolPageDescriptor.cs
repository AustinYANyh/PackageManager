using System;
using System.Windows.Controls;

namespace PackageManager.Shell
{
    public class ToolPageDescriptor
    {
        public string Key { get; set; }

        public string DisplayName { get; set; }

        public string Glyph { get; set; }

        public string Group { get; set; }

        /// <summary>
        /// 获取或设置工具是否可用；false 时入口保留但不允许导航（受限功能）。
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        public Func<Page> Factory { get; set; }
    }
}
