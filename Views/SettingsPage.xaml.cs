using Microsoft.Win32;
using PackageManager.Function.PackageManage;
using PackageManager.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace PackageManager.Views
{
    public partial class SettingsPage : Page, INotifyPropertyChanged, ICentralPage
    {
        private readonly DataPersistenceService _dataPersistenceService;
        private string _addinPath;
        private string _updateServerUrl;
        private bool _filterLogDirectories;
        private bool _programEntryWithG;
        private string _dataLocation;
        private string _appVersionText;

        public event Action RequestExit;

        public string AddinPath
        {
            get => _addinPath;
            set => SetProperty(ref _addinPath, value);
        }
        
        public string UpdateServerUrl
        {
            get => _updateServerUrl;
            set => SetProperty(ref _updateServerUrl, value);
        }
        
        public bool ProgramEntryWithG
        {
            get => _programEntryWithG;
            set => SetProperty(ref _programEntryWithG, value);
        }

        public bool FilterLogDirectories
        {
            get => _filterLogDirectories;
            set => SetProperty(ref _filterLogDirectories, value);
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

        public ObservableCollection<string> LogTxtReaders { get; } = new ObservableCollection<string>();
        public string LogTxtReader { get; set; }

        public SettingsPage(DataPersistenceService dataPersistenceService)
        {
            InitializeComponent();
            _dataPersistenceService = dataPersistenceService ?? throw new ArgumentNullException(nameof(dataPersistenceService));
            DataContext = this;
            LoadSettings();

            var current = GetCurrentVersion();
            AppVersionText = $"版本：{current}";
        }

        private void LoadSettings()
        {
            try
            {
                var settings = _dataPersistenceService.LoadSettings();
                ProgramEntryWithG = settings?.ProgramEntryWithG ?? true;
                AddinPath = settings?.AddinPath ?? @"C:\\ProgramData\\Autodesk\\Revit\\Addins";
                UpdateServerUrl = settings?.UpdateServerUrl ?? string.Empty;
                FilterLogDirectories = settings?.FilterLogDirectories ?? true;
                DataLocation = _dataPersistenceService.GetDataFolderPath();

                LogTxtReader = settings?.LogTxtReader ?? "LogViewPro";
                LogTxtReaders.Add("LogViewPro");
                LogTxtReaders.Add("VSCode");
                LogTxtReaders.Add("Notepad");
                LogTxtReaders.Add("NotepadPlusPlus");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载设置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                AddinPath = @"C:\\ProgramData\\Autodesk\\Revit\\Addins";
                UpdateServerUrl = string.Empty;
                DataLocation = "未知";
            }
        }

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

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show("确定要重置为默认设置吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    AddinPath = @"C:\\ProgramData\\Autodesk\\Revit\\Addins";
                    UpdateServerUrl = string.Empty;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"重置设置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(AddinPath))
                {
                    MessageBox.Show("Addin路径不能为空", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var settings = new PackageManager.Services.AppSettings
                {
                    AddinPath = AddinPath.Trim(),
                    ProgramEntryWithG = ProgramEntryWithG,
                    UpdateServerUrl = string.IsNullOrWhiteSpace(UpdateServerUrl) ? null : UpdateServerUrl.Trim(),
                    FilterLogDirectories = FilterLogDirectories,
                    LogTxtReader = LogTxtReader,
                };

                _dataPersistenceService.SaveSettings(settings);
                
                MessageBox.Show("设置已保存", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
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
                var win = new PackageConfigWindow { Owner = Application.Current.MainWindow };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开包配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            RequestExit?.Invoke();
        }

        private async void UpgradeToLatestButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var svc = new AppUpdateService();
                await svc.UpgradeToLatestAsync(Application.Current?.MainWindow);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"升级到最新失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

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
    }
}

