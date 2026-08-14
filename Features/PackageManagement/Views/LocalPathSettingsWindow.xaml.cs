using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PackageManager.Models;
using PackageManager.Services;

namespace PackageManager.Function.Path
{
    /// <summary>
    /// 本地路径设置窗口，用于管理各产品版本对应的本地安装路径。
    /// </summary>
    public partial class LocalPathSettingsWindow : Window
    {
        private readonly DataPersistenceService dataPersistenceService;

        private readonly ObservableCollection<PackageInfo> packages;

        /// <summary>
        /// 初始化 <see cref="LocalPathSettingsWindow"/> 的新实例。
        /// </summary>
        /// <param name="dataPersistenceService">数据持久化服务实例。</param>
        /// <param name="packages">产品包信息集合。</param>
        public LocalPathSettingsWindow(DataPersistenceService dataPersistenceService,
                                       ObservableCollection<PackageInfo> packages)
        {
            InitializeComponent();
            this.dataPersistenceService = dataPersistenceService;
            this.packages = packages;

            var items = new ObservableCollection<LocalPathInfo>();
            foreach (var p in packages)
            {
                var versions = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
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
                foreach (var v in versions)
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
        /// 获取或设置本地路径信息项的集合。
        /// </summary>
        public ObservableCollection<LocalPathInfo> LocalPathItems { get; set; }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // 先把表格中的编辑结果按「全组众数」原则落回每个包，再调用 NormalizeVersionPaths 收敛为「产品默认 + 版本例外」
            // （与 LocalPathSettingsPage 保持一致）。
            foreach (var group in LocalPathItems.GroupBy(item => item.ProductName, System.StringComparer.OrdinalIgnoreCase))
            {
                var pkg = packages.FirstOrDefault(p => System.String.Equals(p.ProductName, group.Key, System.StringComparison.OrdinalIgnoreCase));
                if (pkg == null)
                {
                    continue;
                }

                var groupDefault = group.Select(item => item.LocalPath)
                                        .Where(path => !string.IsNullOrWhiteSpace(path))
                                        .GroupBy(path => path.Trim(), System.StringComparer.OrdinalIgnoreCase)
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
                        System.String.Equals(effective, groupDefault, System.StringComparison.OrdinalIgnoreCase))
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

            // 保存主界面状态（包含LocalPath）
            dataPersistenceService.SaveMainWindowState(packages);

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    var groups = FindVisualChildren<GroupItem>(LocalPathGrid).ToList();
                    for (int i = 0; i < groups.Count; i++)
                    {
                        var expander = FindVisualChildren<Expander>(groups[i]).FirstOrDefault();
                        if (expander != null)
                        {
                            expander.IsExpanded = (i == 0);
                        }
                    }
                }));
            }
            catch
            {
                // ignored
            }
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
    }
}
