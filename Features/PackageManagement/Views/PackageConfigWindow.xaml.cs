using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using PackageManager.Models;
using PackageManager.Services;

namespace PackageManager.Function.PackageManage
{
    /// <summary>
    /// 包配置窗口，用于管理产品包的配置项。
    /// </summary>
    public partial class PackageConfigWindow : Window, INotifyPropertyChanged, IPackageEditorHost
    {
        private readonly DataPersistenceService _dataService;

        private PackageItem _selectedItem;

        /// <summary>
        /// 初始化 <see cref="PackageConfigWindow"/> 的新实例。
        /// </summary>
        public PackageConfigWindow()
        {
            InitializeComponent();
            _dataService = new DataPersistenceService();
            DataContext = this;
            LoadData();
        }

        /// <summary>
        /// 属性值变更时触发。
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 获取所有配置项集合。
        /// </summary>
        public ObservableCollection<PackageItem> AllItems { get; } = new ObservableCollection<PackageItem>();

        /// <summary>
        /// 获取或设置当前选中的配置项。
        /// </summary>
        public PackageItem SelectedItem
        {
            get => _selectedItem;
            set => SetProperty(ref _selectedItem, value);
        }

        /// <summary>
        /// 编辑指定的包配置项。
        /// </summary>
        /// <param name="item">要编辑的包配置项。</param>
        /// <param name="isNew">是否为新建模式。</param>
        public void EditItem(PackageItem item, bool isNew)
        {
            if (item.IsBuiltIn)
            {
                MessageBox.Show("内置项不可编辑", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var win = new PackageEditWindow(item,isNew) { Owner = this };
            var result = win.ShowDialog();
            if (result != true && isNew)
            {
                AllItems.Remove(item);
                SelectedItem = null;
            }
        }

        /// <summary>
        /// 移除指定的包配置项。
        /// </summary>
        /// <param name="item">要移除的包配置项。</param>
        public void RemoveItem(PackageItem item)
        {
            if (item.IsBuiltIn)
            {
                MessageBox.Show("内置项不可删除", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            AllItems.Remove(item);
            if (ReferenceEquals(SelectedItem, item)) SelectedItem = null;
        }

        /// <summary>
        /// 触发 <see cref="PropertyChanged"/> 事件。
        /// </summary>
        /// <param name="propertyName">发生变更的属性名称。</param>
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// 设置属性值并在值变更时触发 <see cref="PropertyChanged"/> 事件。
        /// </summary>
        /// <typeparam name="T">属性类型。</typeparam>
        /// <param name="field">属性 backing 字段的引用。</param>
        /// <param name="value">新值。</param>
        /// <param name="propertyName">属性名称。</param>
        /// <returns>值是否发生变更。</returns>
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void LoadData()
        {
            AllItems.Clear();
            foreach (var bi in _dataService.GetBuiltInPackageConfigs())
            {
                AllItems.Add(PackageItem.From(bi, true, this));
            }
            foreach (var ci in _dataService.LoadPackageConfigs())
            {
                AllItems.Add(PackageItem.From(ci, false, this));
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var item = new PackageItem(this)
            {
                ProductName = string.Empty,
                FtpServerPath = string.Empty,
                LocalPath = string.Empty,
                SupportsConfigOps = true,
                IsBuiltIn = false
            };
            AllItems.Add(item);
            SelectedItem = item;
            EditItem(item, true);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem == null)
            {
                MessageBox.Show("请先选择一个项", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (SelectedItem.IsBuiltIn)
            {
                MessageBox.Show("内置项不可删除", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            AllItems.Remove(SelectedItem);
            SelectedItem = null;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var customList = AllItems.Where(i => !i.IsBuiltIn).Select(PackageItem.ToConfig).ToList();
                var ok = _dataService.SavePackageConfigs(customList);
                if (ok)
                {
                    MessageBox.Show("保存成功，重启应用后生效", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    Close();
                }
                else
                {
                    MessageBox.Show("保存失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem == null)
            {
                MessageBox.Show("请先选择一个项", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            EditItem(SelectedItem, false);
        }
    }
}
