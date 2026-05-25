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

        public Func<Page> Factory { get; set; }
    }
}
