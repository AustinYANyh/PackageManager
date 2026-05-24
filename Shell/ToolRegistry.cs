using System.Collections.Generic;
using System.Linq;

namespace PackageManager.Shell
{
    public class ToolRegistry
    {
        private readonly List<ToolPageDescriptor> _tools = new List<ToolPageDescriptor>();

        public IReadOnlyList<ToolPageDescriptor> Tools => _tools;

        public void Register(ToolPageDescriptor descriptor)
        {
            _tools.Add(descriptor);
        }

        public ToolPageDescriptor FindByKey(string key)
        {
            return _tools.FirstOrDefault(t => t.Key == key);
        }

        public ToolPageDescriptor FindByDisplayName(string displayName)
        {
            return _tools.FirstOrDefault(t => t.DisplayName == displayName);
        }
    }
}
