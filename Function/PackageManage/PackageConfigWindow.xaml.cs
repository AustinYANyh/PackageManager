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
    public partial class PackageConfigWindow : Window, INotifyPropertyChanged
    {
        private readonly DataPersistenceService _dataService;

        private PackageItem _selectedItem;

        public PackageConfigWindow()
        {
            InitializeComponent();
            _dataService = new DataPersistenceService();
            DataContext = this;
            LoadData();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<PackageItem> AllItems { get; } = new ObservableCollection<PackageItem>();

        public PackageItem SelectedItem
        {
            get => _selectedItem;
            set => SetProperty(ref _selectedItem, value);
        }

        public void EditItem(PackageItem item, bool isNew)
        {
            if (item.IsBuiltIn)
            {
                MessageBox.Show("内置项不可编辑", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var win = new PackageEditWindow(item) { Owner = this };
            var result = win.ShowDialog();
            if (result != true && isNew)
            {
                AllItems.Remove(item);
                SelectedItem = null;
            }
        }

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

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

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
