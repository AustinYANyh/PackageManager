using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using CustomControlLibrary.CustomControl.Controls.TreeView;
using PackageManager.Models;
using PackageManager.Services;
using Forms = System.Windows.Forms;
using System.IO;
using System.Diagnostics;

namespace PackageManager.Function.Path
{
    public partial class LocalPathSettingsWindow : Window, INotifyPropertyChanged
    {
        private readonly DataPersistenceService dataPersistenceService;

        private readonly ObservableCollection<PackageInfo> packages;

        private LocalPathInfoVersion selectedLocalPathInfo;

        private LocalPathProduct selectedProduct;

        private readonly string initialProductName;
        private readonly string initialVersion;

        public LocalPathSettingsWindow(DataPersistenceService dataPersistenceService,
                                       ObservableCollection<PackageInfo> packages,
                                       string initialProductName = null,
                                       string initialVersion = null)
        {
            InitializeComponent();
            this.dataPersistenceService = dataPersistenceService;
            this.packages = packages;
            this.initialProductName = initialProductName;
            this.initialVersion = initialVersion;

            // 构建扁平版本列表（左侧树只显示版本）
            var all = new List<IMixedTreeNode>();
            foreach (var p in packages)
            {
                LocalPathProduct localPathProduct = new LocalPathProduct { Name = p.ProductName };
                var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (p.AvailableVersions != null)
                {
                    foreach (var v in p.AvailableVersions)
                    {
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            versions.Add(v);
                        }
                    }
                }

                if (p.VersionLocalPaths != null)
                {
                    foreach (var v in p.VersionLocalPaths.Keys)
                    {
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            versions.Add(v);
                        }
                    }
                }

                foreach (var v in versions)
                {
                    localPathProduct.Children.Add(new LocalPathInfoVersion
                    {
                        Version = v,
                        Path = p.GetLocalPathForVersion(v),
                    });
                }

                all.Add(localPathProduct);
            }

            AllVersionItems = new ObservableCollection<IMixedTreeNode>(all.OrderBy(i => i.Name));

            DataContext = this;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<IMixedTreeNode> AllVersionItems { get; set; }

        public LocalPathInfoVersion SelectedLocalPathInfo
        {
            get => selectedLocalPathInfo;

            set
            {
                if (selectedLocalPathInfo != value)
                {
                    selectedLocalPathInfo = value;
                    OnPropertyChanged(nameof(SelectedLocalPathInfo));
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(SelectedProductName));
                    OnPropertyChanged(nameof(SelectedVersionText));
                    OnPropertyChanged(nameof(SelectionHintText));
                    OnPropertyChanged(nameof(SelectedPath));
                    OnPropertyChanged(nameof(PathExistsText));
                    OnPropertyChanged(nameof(CanOpenDirectory));
                    OnPropertyChanged(nameof(CanApplyToAllVersions));
                }
            }
        }

        public LocalPathProduct SelectedProduct
        {
            get => selectedProduct;

            set
            {
                if (selectedProduct != value)
                {
                    selectedProduct = value;
                    OnPropertyChanged(nameof(SelectedProduct));
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(SelectedProductName));
                    OnPropertyChanged(nameof(SelectedVersionText));
                    OnPropertyChanged(nameof(SelectionHintText));
                    OnPropertyChanged(nameof(SelectedPath));
                    OnPropertyChanged(nameof(PathExistsText));
                    OnPropertyChanged(nameof(CanOpenDirectory));
                    OnPropertyChanged(nameof(CanApplyToAllVersions));
                }
            }
        }

        public bool HasSelection => (SelectedProduct != null) || (SelectedLocalPathInfo != null);

        public string SelectedProductName
        {
            get
            {
                if (SelectedProduct != null)
                {
                    return SelectedProduct.Name;
                }

                var parent = GetParentProduct(SelectedLocalPathInfo);
                return parent?.Name ?? string.Empty;
            }
        }

        public string SelectedVersionText => SelectedLocalPathInfo != null ? SelectedLocalPathInfo.Version : "全部版本";

        public string SelectionHintText
        {
            get
            {
                if (SelectedLocalPathInfo != null)
                {
                    return "选中项为版本，设置的路径只对当前版本生效";
                }

                if (SelectedProduct != null)
                {
                    return "选中项为产品，设置的路径将对该产品下所有版本生效";
                }

                return "请选择左侧的产品或版本以编辑路径";
            }
        }

        public string SelectedPath
        {
            get
            {
                if (SelectedLocalPathInfo != null)
                {
                    return SelectedLocalPathInfo.Path;
                }

                if (SelectedProduct != null)
                {
                    // 如果所有子版本的路径一致，则显示该路径，否则显示空
                    var paths = SelectedProduct.Children
                                               .OfType<LocalPathInfoVersion>()
                                               .Select(v => v.Path)
                                               .Distinct(StringComparer.OrdinalIgnoreCase)
                                               .ToList();
                    return paths.Count == 1 ? paths[0] : string.Empty;
                }

                return string.Empty;
            }

            set
            {
                if (SelectedLocalPathInfo != null)
                {
                    if (SelectedLocalPathInfo.Path != value)
                    {
                        SelectedLocalPathInfo.Path = value;
                        OnPropertyChanged(nameof(SelectedPath));
                        OnPropertyChanged(nameof(PathExistsText));
                        OnPropertyChanged(nameof(CanOpenDirectory));
                    }
                }
                else if (SelectedProduct != null)
                {
                    foreach (var child in SelectedProduct.Children.OfType<LocalPathInfoVersion>())
                    {
                        child.Path = value;
                    }

                    OnPropertyChanged(nameof(SelectedPath));
                    OnPropertyChanged(nameof(PathExistsText));
                    OnPropertyChanged(nameof(CanOpenDirectory));
                }
            }
        }

        public string PathExistsText
        {
            get
            {
                var path = SelectedPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return "未设置";
                }
                return Directory.Exists(path) ? "路径存在" : "路径不存在";
            }
        }

        public bool CanOpenDirectory => !string.IsNullOrWhiteSpace(SelectedPath) && Directory.Exists(SelectedPath);

        public bool CanApplyToAllVersions => SelectedProduct != null || (SelectedLocalPathInfo != null && GetParentProduct(SelectedLocalPathInfo) != null);

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
            where T : DependencyObject
        {
            if (parent == null)
            {
                yield break;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t)
                {
                    yield return t;
                }

                foreach (var c in FindVisualChildren<T>(child))
                {
                    yield return c;
                }
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in AllVersionItems)
            {
                if (item is LocalPathProduct product)
                {
                    var pkg = packages.FirstOrDefault(p => p.ProductName == product.Name);
                    if (pkg != null)
                    {
                        foreach (IMixedTreeNode mixedTreeNode in product.Children)
                        {
                            if (mixedTreeNode is LocalPathInfoVersion version)
                            {
                                if (!string.IsNullOrWhiteSpace(version.Version))
                                {
                                    pkg.VersionLocalPaths[version.Version] = version.Path;
                                }
                            }
                        }
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
                // 根据主界面初始选择，自动选中并滚动到目标产品/版本
                if (!string.IsNullOrWhiteSpace(initialProductName))
                {
                    var targetProduct = AllVersionItems.OfType<LocalPathProduct>()
                                                       .FirstOrDefault(p => string.Equals(p.Name, initialProductName, StringComparison.OrdinalIgnoreCase));
                    if (targetProduct != null)
                    {
                        SelectedProduct = targetProduct;

                        LocalPathInfoVersion targetVersion = null;
                        if (!string.IsNullOrWhiteSpace(initialVersion))
                        {
                            targetVersion = targetProduct.Children
                                                          .OfType<LocalPathInfoVersion>()
                                                          .FirstOrDefault(v => string.Equals(v.Version, initialVersion, StringComparison.OrdinalIgnoreCase));
                            if (targetVersion != null)
                            {
                                SelectedLocalPathInfo = targetVersion;
                            }
                        }

                        // 展开并滚动到可视区域
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            BringNodeIntoView(targetProduct);
                            if (targetVersion != null)
                            {
                                BringNodeIntoView(targetVersion);
                            }
                        }));
                    }
                }
            }
            catch
            {
                // ignored
            }
        }

        private void BringNodeIntoView(object node)
        {
            if (node == null || LocalPathTree == null)
            {
                return;
            }

            LocalPathTree.UpdateLayout();
            var containers = FindVisualChildren<System.Windows.Controls.TreeViewItem>(LocalPathTree).ToList();
            var container = containers.FirstOrDefault(tvi => Equals(tvi.DataContext, node));
            if (container != null)
            {
                // 如果是产品节点，先展开，以便子节点生成
                if (node is LocalPathProduct)
                {
                    container.IsExpanded = true;
                    LocalPathTree.UpdateLayout();
                }

                container.IsSelected = true;
                container.BringIntoView();
            }
        }

        private void LocalPathTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is LocalPathProduct product)
            {
                SelectedProduct = product;
                SelectedLocalPathInfo = null;
            }
            else if (e.NewValue is LocalPathInfoVersion version)
            {
                SelectedLocalPathInfo = version;
                SelectedProduct = GetParentProduct(version);
            }
            else
            {
                SelectedProduct = null;
                SelectedLocalPathInfo = null;
            }
        }

        

        private LocalPathProduct GetParentProduct(LocalPathInfoVersion version)
        {
            if (version == null)
            {
                return null;
            }

            foreach (var node in AllVersionItems)
            {
                if (node is LocalPathProduct product)
                {
                    if (product.Children.Contains(version))
                    {
                        return product;
                    }
                }
            }

            return null;
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            // 使用WPF的Microsoft.Win32.OpenFileDialog选择文件夹
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择本地包所在的文件夹",
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "选择文件夹",
                Filter = "文件夹|*.none",
            };

            var result = dialog.ShowDialog();
            if (result == true)
            {
                // 获取选择的文件夹路径（去掉FileName部分）
                SelectedPath = System.IO.Path.GetDirectoryName(dialog.FileName);
            }
        }

        private void OpenDirectory_Click(object sender, RoutedEventArgs e)
        {
            if (CanOpenDirectory)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = SelectedPath,
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // ignored
                }
            }
        }

        private void CopyPath_Click(object sender, RoutedEventArgs e)
        {
            var path = SelectedPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    Clipboard.SetText(path);
                }
                catch
                {
                    // ignored
                }
            }
        }

        private void ClearPath_Click(object sender, RoutedEventArgs e)
        {
            SelectedPath = string.Empty;
        }

        private void ApplyToAllVersions_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedProduct != null)
            {
                foreach (var child in SelectedProduct.Children.OfType<LocalPathInfoVersion>())
                {
                    child.Path = SelectedPath;
                }
                OnPropertyChanged(nameof(SelectedPath));
                OnPropertyChanged(nameof(PathExistsText));
                OnPropertyChanged(nameof(CanOpenDirectory));
            }
            else if (SelectedLocalPathInfo != null)
            {
                var parent = GetParentProduct(SelectedLocalPathInfo);
                if (parent != null)
                {
                    foreach (var child in parent.Children.OfType<LocalPathInfoVersion>())
                    {
                        child.Path = SelectedLocalPathInfo.Path;
                    }
                    OnPropertyChanged(nameof(SelectedPath));
                    OnPropertyChanged(nameof(PathExistsText));
                    OnPropertyChanged(nameof(CanOpenDirectory));
                }
            }
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ProductGroup
    {
        public string Name { get; set; }

        public ObservableCollection<LocalPathInfo> Children { get; set; } = new ObservableCollection<LocalPathInfo>();
    }
}
