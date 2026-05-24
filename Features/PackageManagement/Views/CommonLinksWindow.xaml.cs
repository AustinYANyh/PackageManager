using System.Collections.ObjectModel;
using System.Windows;

namespace PackageManager.Views
{
    /// <summary>
    /// 以独立窗口形式承载 <see cref="CommonLinksPage"/> 的常用链接列表。
    /// </summary>
    public partial class CommonLinksWindow : Window
    {
        /// <summary>
        /// 初始化 <see cref="CommonLinksWindow"/> 的新实例。
        /// </summary>
        /// <param name="links">要展示的常用链接集合。</param>
        public CommonLinksWindow(ObservableCollection<PackageManager.CommonLinkItem> links)
        {
            InitializeComponent();
            // 以页面形式承载常用网址列表
            ContentHost.Content = new CommonLinksPage(links);
        }
    }
}

