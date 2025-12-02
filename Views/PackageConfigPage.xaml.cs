using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using PackageManager.Function.PackageManage;
using PackageManager.Models;
using PackageManager.Services;

namespace PackageManager.Views
{
    /// <summary>
    /// 包管理配置页面（支持导航），复用窗口逻辑。
    /// </summary>
    public partial class PackageConfigPage : Page, INotifyPropertyChanged, ICentralPage, IPackageEditorHost
    {
        private readonly DataPersistenceService _dataService;

        private PackageItem _selectedItem;

        public PackageConfigPage()
        {
            InitializeComponent();
            _dataService = new DataPersistenceService();
            DataContext = this;
            LoadData();
        }

        public event Action RequestExit;

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<PackageItem> AllItems { get; } = new ObservableCollection<PackageItem>();

        public PackageItem SelectedItem
        {
            get => _selectedItem;
            set => SetProperty(ref _selectedItem, value);
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

        public void EditItem(PackageItem item, bool isNew)
        {
            if (item.IsBuiltIn)
            {
                MessageBox.Show("内置项不可编辑", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var ownerWindow = Window.GetWindow(this);
            var win = new PackageManager.Function.PackageManage.PackageEditWindow(item, isNew)
            {
                Owner = ownerWindow
            };
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

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var item = new PackageItem(this)
            {
                ProductName = string.Empty,
                FtpServerPath = string.Empty,
                LocalPath = string.Empty,
                FinalizeFtpServerPath = string.Empty,
                SupportsConfigOps = true,
                IsBuiltIn = false
            };
            AllItems.Add(item);
            SelectedItem = item;
            EditItem(item, true);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var customList = AllItems.Where(i => !i.IsBuiltIn).Select(PackageItem.ToConfig).ToList();
                var ok = _dataService.SavePackageConfigs(customList);
                if (ok)
                {
                    // 保存成功后立即让主窗体重载包配置，无需重启
                    var main = System.Windows.Application.Current?.MainWindow as PackageManager.MainWindow;
                    main?.ReloadPackagesFromConfig();

                    LoggingService.LogInfo("保存成功，配置已立即生效，正在自动加载版本");
                    RequestExit?.Invoke();
                }
                else
                {
                    MessageBox.Show("保存失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex,"保存失败");
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            RequestExit?.Invoke();
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
    }
}
