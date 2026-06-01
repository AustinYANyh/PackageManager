using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using PackageManager.Function.PackageManage;
using PackageManager.Services;

namespace PackageManager.Function.Setting
{
    /// <summary>
    /// SettingsWindow.xaml 的交互逻辑
    /// </summary>
    public partial class SettingsWindow : Window, INotifyPropertyChanged
    {
        private readonly DataPersistenceService _dataPersistenceService;
        private string _addinPath;
        private string _updateServerUrl;
        private bool _filterLogDirectories;

        private bool _programEntryWithG;
        private string _dataLocation;
        private string _appVersionText;

        /// <summary>
        /// 获取或设置插件路径。
        /// </summary>
        public string AddinPath
        {
            get => _addinPath;
            set => SetProperty(ref _addinPath, value);
        }
        
        /// <summary>
        /// 获取或设置更新服务器地址。
        /// </summary>
        public string UpdateServerUrl
        {
            get => _updateServerUrl;
            set => SetProperty(ref _updateServerUrl, value);
        }
        
        /// <summary>
        /// 获取或设置是否以 G 标识程序入口。
        /// </summary>
        public bool ProgramEntryWithG
        {
            get => _programEntryWithG;
            set => SetProperty(ref _programEntryWithG, value);
        }

        /// <summary>
        /// 获取或设置是否过滤日志目录。
        /// </summary>
        public bool FilterLogDirectories
        {
            get => _filterLogDirectories;
            set => SetProperty(ref _filterLogDirectories, value);
        }

        /// <summary>
        /// 获取或设置数据存储路径。
        /// </summary>
        public string DataLocation
        {
            get => _dataLocation;
            set => SetProperty(ref _dataLocation, value);
        }

        /// <summary>
        /// 获取应用程序版本显示文本。
        /// </summary>
        public string AppVersionText
        {
            get => _appVersionText;
            set => SetProperty(ref _appVersionText, value);
        }

        /// <summary>
        /// 初始化 <see cref="SettingsWindow"/> 的新实例。
        /// </summary>
        /// <param name="dataPersistenceService">数据持久化服务实例。</param>
        /// <exception cref="ArgumentNullException"><paramref name="dataPersistenceService"/> 为 <c>null</c>。</exception>
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
                UpdateServerUrl = settings?.UpdateServerUrl ?? string.Empty;
                FilterLogDirectories = settings?.FilterLogDirectories ?? true;
                DataLocation = _dataPersistenceService.GetDataFolderPath();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载设置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                // 使用默认值
                AddinPath = @"C:\ProgramData\Autodesk\Revit\Addins";
                UpdateServerUrl = string.Empty;
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
                    UpdateServerUrl = string.Empty;
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
                var settings = _dataPersistenceService.LoadSettings() ?? new PackageManager.Services.AppSettings();
                settings.AddinPath = AddinPath.Trim();
                settings.ProgramEntryWithG = ProgramEntryWithG;
                settings.UpdateServerUrl = string.IsNullOrWhiteSpace(UpdateServerUrl) ? null : UpdateServerUrl.Trim();
                settings.FilterLogDirectories = FilterLogDirectories;

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

        private void OpenPackagesConfigButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new PackageConfigWindow { Owner = this };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开包配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

        /// <summary>
        /// 当属性值更改时触发。
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 触发 <see cref="PropertyChanged"/> 事件。
        /// </summary>
        /// <param name="propertyName">发生更改的属性名称。</param>
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// 设置属性值，如果值发生更改则触发 <see cref="PropertyChanged"/> 事件。
        /// </summary>
        /// <typeparam name="T">属性值的类型。</typeparam>
        /// <param name="field">属性后备字段的引用。</param>
        /// <param name="value">要设置的新值。</param>
        /// <param name="propertyName">属性名称。</param>
        /// <returns>如果值已更改返回 <c>true</c>，否则返回 <c>false</c>。</returns>
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
