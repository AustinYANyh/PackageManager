using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using PackageManager.Services;

namespace PackageManager
{
    /// <summary>
    /// SettingsWindow.xaml 的交互逻辑
    /// </summary>
    public partial class SettingsWindow : Window, INotifyPropertyChanged
    {
        private readonly DataPersistenceService _dataPersistenceService;
        private string _addinPath;

        private bool _programEntryWithG;
        private string _dataLocation;
        private string _appVersionText;

        public string AddinPath
        {
            get => _addinPath;
            set => SetProperty(ref _addinPath, value);
        }
        
        public bool ProgramEntryWithG
        {
            get => _programEntryWithG;
            set => SetProperty(ref _programEntryWithG, value);
        }

        public string DataLocation
        {
            get => _dataLocation;
            set => SetProperty(ref _dataLocation, value);
        }

        public string AppVersionText
        {
            get => _appVersionText;
            set => SetProperty(ref _appVersionText, value);
        }

        public SettingsWindow(DataPersistenceService dataPersistenceService)
        {
            InitializeComponent();
            _dataPersistenceService = dataPersistenceService ?? throw new ArgumentNullException(nameof(dataPersistenceService));
            
            DataContext = this;
            LoadSettings();

            // 设置版本显示文本：从当前程序集版本读取
            var current = GetCurrentVersion();
            AppVersionText = $"版本：{current}";
        }

        /// <summary>
        /// 加载设置数据
        /// </summary>
        private void LoadSettings()
        {
            try
            {
                var settings = _dataPersistenceService.LoadSettings();
                
                ProgramEntryWithG = settings?.ProgramEntryWithG ?? true;
                AddinPath = settings?.AddinPath ?? @"C:\ProgramData\Autodesk\Revit\Addins";
                DataLocation = _dataPersistenceService.GetDataFolderPath();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载设置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                // 使用默认值
                AddinPath = @"C:\ProgramData\Autodesk\Revit\Addins";
                DataLocation = "未知";
            }
        }

        /// <summary>
        /// 浏览按钮点击事件
        /// </summary>
        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "选择Addin文件夹",
                    CheckFileExists = false,
                    CheckPathExists = true,
                    FileName = "选择文件夹",
                    Filter = "文件夹|*.folder",
                    ValidateNames = false
                };

                if (dialog.ShowDialog() == true)
                {
                    AddinPath = dialog.FileName;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"选择文件夹失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 清除缓存数据按钮点击事件
        /// </summary>
        private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show("确定要清除缓存数据吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _dataPersistenceService.ClearAllCachedData();
                    MessageBox.Show("缓存数据已清除", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"清除缓存数据失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 清除状态数据按钮点击事件
        /// </summary>
        private void ClearStateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show("确定要清除状态数据吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _dataPersistenceService.ClearMainWindowState();
                    MessageBox.Show("状态数据已清除", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"清除状态数据失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 清除所有数据按钮点击事件
        /// </summary>
        private void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show(
                    "确定要清除所有数据吗？\n这将删除：\n- 主界面状态数据\n- 应用程序缓存数据\n- 用户设置数据\n\n此操作不可撤销！", 
                    "确认清除所有数据", 
                    MessageBoxButton.YesNo, 
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    _dataPersistenceService.ClearMainWindowState();
                    _dataPersistenceService.ClearAllCachedData();
                    _dataPersistenceService.ClearSettings();
                    
                    MessageBox.Show("所有数据已清除！\n建议重启应用程序以确保所有更改生效。", 
                                  "清除完成", 
                                  MessageBoxButton.OK, 
                                  MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"清除所有数据失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 重置为默认按钮点击事件
        /// </summary>
        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show("确定要重置为默认设置吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    AddinPath = @"C:\ProgramData\Autodesk\Revit\Addins";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"重置设置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 保存按钮点击事件
        /// </summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 验证路径
                if (string.IsNullOrWhiteSpace(AddinPath))
                {
                    MessageBox.Show("Addin路径不能为空", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 保存设置
                var settings = new PackageManager.Services.AppSettings
                {
                    AddinPath = AddinPath.Trim(),
                    ProgramEntryWithG =ProgramEntryWithG,
                };

                _dataPersistenceService.SaveSettings(settings);
                
                MessageBox.Show("设置已保存", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存设置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 取消按钮点击事件
        /// </summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        #region INotifyPropertyChanged Implementation

        public event PropertyChangedEventHandler PropertyChanged;

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

        private static Version GetCurrentVersion()
        {
            try
            {
                return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            }
            catch
            {
                return new Version(0,0,0,0);
            }
        }

        #endregion
    }
}