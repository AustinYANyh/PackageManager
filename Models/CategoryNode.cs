using System.Collections.ObjectModel;
using PackageManager.Models;

namespace PackageManager.Models
{
    /// <summary>
    /// 用于左侧导航的层级分类节点。Package为null表示分类节点。
    /// </summary>
    public class CategoryNode
    {
        /// <summary>
        /// 获取或设置分类名称。
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 获取子节点集合。
        /// </summary>
        public ObservableCollection<CategoryNode> Children { get; } = new ObservableCollection<CategoryNode>();

        /// <summary>
        /// 获取或设置关联的产品包信息；为 null 时表示此节点为分类节点。
        /// </summary>
        public PackageInfo Package { get; set; }

        /// <summary>
        /// 获取或设置父级分类节点。
        /// </summary>
        public CategoryNode Parent { get; set; }
    }
}

