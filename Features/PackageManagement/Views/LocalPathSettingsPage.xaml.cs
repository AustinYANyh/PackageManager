using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PackageManager.Models;
using PackageManager.Services;

namespace PackageManager.Views
{
    /// <summary>
    /// 本地路径设置页面，允许按产品和版本配置本地包路径。
    /// </summary>
    public partial class LocalPathSettingsPage : Page, ICentralPage
    {
        private readonly DataPersistenceService dataPersistenceService;
        private readonly ObservableCollection<PackageInfo> packages;

        /// <summary>
        /// 请求退出当前页面的导航事件。
        /// </summary>
        public event Action RequestExit;

        /// <summary>
        /// 保存操作完成时触发的事件。
        /// </summary>
        public event Action Saved;

        /// <summary>
        /// 初始化 <see cref="LocalPathSettingsPage"/> 的新实例。
        /// </summary>
        /// <param name="dataPersistenceService">数据持久化服务实例。</param>
        /// <param name="packages">产品包信息集合。</param>
        public LocalPathSettingsPage(DataPersistenceService dataPersistenceService,
                                      ObservableCollection<PackageInfo> packages)
        {
            InitializeComponent();
            this.dataPersistenceService = dataPersistenceService;
            this.packages = packages;

            var items = new ObservableCollection<LocalPathInfo>();
            foreach (var p in packages.OrderBy(pkg => pkg.ProductName, StringComparer.OrdinalIgnoreCase))
            {
                var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (p.AvailableVersions != null)
                {
                    foreach (var v in p.AvailableVersions)
                    {
                        if (!string.IsNullOrWhiteSpace(v)) versions.Add(v);
                    }
                }
                if (p.VersionLocalPaths != null)
                {
                    foreach (var v in p.VersionLocalPaths.Keys)
                    {
                        if (!string.IsNullOrWhiteSpace(v)) versions.Add(v);
                    }
                }
                foreach (var v in FtpService.SortNamesByVersion(versions))
                {
                    items.Add(new LocalPathInfo
                    {
                        ProductName = p.ProductName,
                        Version = v,
                        LocalPath = p.GetLocalPathForVersion(v),
                    });
                }
            }
            LocalPathItems = items;

            DataContext = this;
        }

        /// <summary>
        /// 获取或设置本地路径配置项的集合。
        /// </summary>
        public ObservableCollection<LocalPathInfo> LocalPathItems { get; set; }

        private void ApplyGroupPathButton_Click(object sender, RoutedEventArgs e)
        {
            var productName = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrWhiteSpace(productName))
            {
                return;
            }

            var groupItems = GetItemsByProductName(productName).ToList();
            if (groupItems.Count == 0)
            {
                return;
            }

            var initialPath = groupItems.Select(item => item.LocalPath)
                                        .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            var selectedPath = FolderPickerService.PickFolder($"为 {productName} 选择统一的本地包路径", initialPath);
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            foreach (var item in groupItems)
            {
                item.LocalPath = selectedPath;
            }
        }

        private void ClearGroupPathButton_Click(object sender, RoutedEventArgs e)
        {
            var productName = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrWhiteSpace(productName))
            {
                return;
            }

            var result = MessageBox.Show($"确定要清空 {productName} 下所有版本的本地路径吗？",
                                         "确认清空",
                                         MessageBoxButton.YesNo,
                                         MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            foreach (var item in GetItemsByProductName(productName))
            {
                item.LocalPath = string.Empty;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // 先把表格中的编辑结果按「全组众数」原则落回每个包（与 NormalizeVersionPaths 的选默认规则一致），
            // 再调用 NormalizeVersionPaths 收敛为「产品默认 + 版本例外」，使 FTP 上新出现的版本自动沿用产品默认路径。
            foreach (var group in LocalPathItems.GroupBy(item => item.ProductName, StringComparer.OrdinalIgnoreCase))
            {
                var pkg = packages.FirstOrDefault(p => string.Equals(p.ProductName, group.Key, StringComparison.OrdinalIgnoreCase));
                if (pkg == null)
                {
                    continue;
                }

                var groupDefault = group.Select(item => item.LocalPath)
                                        .Where(path => !string.IsNullOrWhiteSpace(path))
                                        .GroupBy(path => path.Trim(), StringComparer.OrdinalIgnoreCase)
                                        .OrderByDescending(pathGroup => pathGroup.Count())
                                        .Select(pathGroup => pathGroup.Key)
                                        .FirstOrDefault() ?? string.Empty;

                pkg.LocalPath = groupDefault;

                foreach (var item in group)
                {
                    if (string.IsNullOrWhiteSpace(item.Version))
                    {
                        continue;
                    }

                    var effective = string.IsNullOrWhiteSpace(item.LocalPath) ? string.Empty : item.LocalPath.Trim();
                    if (string.IsNullOrEmpty(effective) ||
                        string.Equals(effective, groupDefault, StringComparison.OrdinalIgnoreCase))
                    {
                        pkg.VersionLocalPaths.Remove(item.Version);
                    }
                    else
                    {
                        pkg.VersionLocalPaths[item.Version] = effective;
                    }
                }
            }

            foreach (var pkg in packages)
            {
                try
                {
                    pkg.NormalizeVersionPaths();
                }
                catch { }
            }

            dataPersistenceService.SaveMainWindowState(packages);

            try
            {
                Saved?.Invoke();
            }
            catch { }

            RequestExit?.Invoke();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            RequestExit?.Invoke();
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // 组头 Border 宽度已改为 XAML 绑定（GridWidthOffsetConverter，ActualWidth-72），
            // 虚拟化按需生成的组头也能自动获得正确宽度，无需代码后置遍历视觉树。
        }

        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;
                foreach (var c in FindVisualChildren<T>(child)) yield return c;
            }
        }

        private IEnumerable<LocalPathInfo> GetItemsByProductName(string productName)
        {
            return LocalPathItems.Where(item => string.Equals(item.ProductName,
                                                               productName,
                                                               StringComparison.OrdinalIgnoreCase));
        }
    }
}
