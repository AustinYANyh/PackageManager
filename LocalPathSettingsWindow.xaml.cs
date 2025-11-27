using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using PackageManager.Models;
using PackageManager.Services;
using System.Windows.Controls;
using System.Windows.Media;

namespace PackageManager
{
    public partial class LocalPathSettingsWindow : Window
    {
        private readonly DataPersistenceService dataPersistenceService;

        private readonly ObservableCollection<PackageInfo> packages;

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

        public ObservableCollection<LocalPathInfo> LocalPathItems { get; set; }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in LocalPathItems)
            {
                var pkg = packages.FirstOrDefault(p => p.ProductName == item.ProductName);
                if (pkg != null)
                {
                    if (!string.IsNullOrWhiteSpace(item.Version))
                    {
                        pkg.VersionLocalPaths[item.Version] = item.LocalPath;
                    }
                }
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
