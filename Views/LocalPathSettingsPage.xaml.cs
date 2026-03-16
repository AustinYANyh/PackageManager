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
    public partial class LocalPathSettingsPage : Page, ICentralPage
    {
        private readonly DataPersistenceService dataPersistenceService;
        private readonly ObservableCollection<PackageInfo> packages;

        public event Action RequestExit;
        public event Action Saved;

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
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // var groups = FindVisualChildren<GroupItem>(LocalPathGrid).ToList();
                    // for (int i = 0; i < groups.Count; i++)
                    // {
                    //     var expander = FindVisualChildren<Expander>(groups[i]).FirstOrDefault();
                    //     if (expander != null)
                    //     {
                    //         expander.IsExpanded = (i == 0);
                    //     }
                    // }

                    UpdateGroupExpandersLayout();
                }), DispatcherPriority.Loaded);
            }
            catch
            {
            }
        }

        private void LocalPathGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateGroupExpandersLayout();
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

        private void UpdateGroupExpandersLayout()
        {
            if (LocalPathGrid == null || LocalPathGrid.ActualWidth <= 0)
            {
                return;
            }

            var targetWidth = Math.Max(0, LocalPathGrid.ActualWidth - 72);

            foreach (var expander in FindVisualChildren<Expander>(LocalPathGrid))
            {
                expander.HorizontalAlignment = HorizontalAlignment.Stretch;
            }

            foreach (var border in FindVisualChildren<Border>(LocalPathGrid))
            {
                var tag = border.Tag as string;
                if (!string.Equals(tag, "GroupHeaderBorder", StringComparison.Ordinal))
                {
                    continue;
                }

                border.Width = targetWidth;
                border.HorizontalAlignment = HorizontalAlignment.Left;
            }
        }
    }
}
