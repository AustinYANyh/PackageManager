using System.Collections.ObjectModel;
using PackageManager.Models;

namespace PackageManager.Function.Path
{
    /// <summary>
    /// 产品分组，按产品名称聚合本地路径设置项。
    /// </summary>
    public class ProductGroup
    {
        /// <summary>
        /// 获取或设置产品名称。
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 获取或设置该产品下的本地路径集合。
        /// </summary>
        public ObservableCollection<LocalPathInfo> Children { get; set; } = new ObservableCollection<LocalPathInfo>();
    }
}

