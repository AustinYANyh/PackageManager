using System.Collections.ObjectModel;
using PackageManager.Models;

namespace PackageManager.Models
{
    /// <summary>
    /// 用于左侧导航的层级分类节点。Package为null表示分类节点。
    /// </summary>
    public class CategoryNode
    {
        public string Name { get; set; }
        public ObservableCollection<CategoryNode> Children { get; } = new ObservableCollection<CategoryNode>();
        public PackageInfo Package { get; set; }
        public CategoryNode Parent { get; set; }
    }
}

