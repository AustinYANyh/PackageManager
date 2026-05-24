using PackageManager.Models;

namespace PackageManager.Function.PackageManage
{
    /// <summary>
    /// 为包配置列表提供编辑/删除能力的宿主接口，供窗口或页面实现。
    /// </summary>
    public interface IPackageEditorHost
    {
        /// <summary>
        /// 编辑指定的包配置项。
        /// </summary>
        /// <param name="item">要编辑的包配置项。</param>
        /// <param name="isNew">是否为新建模式。</param>
        void EditItem(PackageItem item, bool isNew);

        /// <summary>
        /// 移除指定的包配置项。
        /// </summary>
        /// <param name="item">要移除的包配置项。</param>
        void RemoveItem(PackageItem item);
    }
}

