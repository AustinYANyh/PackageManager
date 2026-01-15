using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PackageManager.Function.PackageManage;
using PackageManager.Services;

namespace PackageManager.Views;

public partial class SettingsPage : Page, INotifyPropertyChanged, ICentralPage
{
    private readonly DataPersistenceService dataPersistenceService;

    private string addinPath;

    private string updateServerUrl;

    private bool filterLogDirectories;

    private bool programEntryWithG;

    private string dataLocation;

    private string appVersionText;

    public SettingsPage(DataPersistenceService dataPersistenceService)
    {
        InitializeComponent();
        this.dataPersistenceService = dataPersistenceService ?? throw new ArgumentNullException(nameof(dataPersistenceService));
        DataContext = this;
        LoadSettings();

        var current = GetCurrentVersion();
        AppVersionText = $"版本：{current}";
    }

    public event Action RequestExit;

    public event PropertyChangedEventHandler PropertyChanged;

    public string AddinPath
    {
        get => addinPath;

        set => SetProperty(ref addinPath, value);
    }

    public string UpdateServerUrl
    {
        get => updateServerUrl;

        set => SetProperty(ref updateServerUrl, value);
    }

    public bool ProgramEntryWithG
    {
        get => programEntryWithG;

        set => SetProperty(ref programEntryWithG, value);
    }

    public bool FilterLogDirectories
    {
        get => filterLogDirectories;

        set => SetProperty(ref filterLogDirectories, value);
    }

    public string DataLocation
    {
        get => dataLocation;

        set => SetProperty(ref dataLocation, value);
    }

    public string AppVersionText
    {
        get => appVersionText;

        set => SetProperty(ref appVersionText, value);
    }

    public ObservableCollection<string> LogTxtReaders { get; } = new();

    public string LogTxtReader { get; set; }

    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

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
            return new Version(0, 0, 0, 0);
        }
    }

    private void LoadSettings()
    {
        try
        {
            var settings = dataPersistenceService.LoadSettings();
            ProgramEntryWithG = settings?.ProgramEntryWithG ?? true;
            AddinPath = settings?.AddinPath ?? @"C:\\ProgramData\\Autodesk\\Revit\\Addins";
            UpdateServerUrl = settings?.UpdateServerUrl ?? string.Empty;
            FilterLogDirectories = settings?.FilterLogDirectories ?? true;
            DataLocation = dataPersistenceService.GetDataFolderPath();

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
                ValidateNames = false,
            };

            if (dialog.ShowDialog() == true)
            {
                AddinPath = System.IO.Path.GetDirectoryName(dialog.FileName);
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
                dataPersistenceService.ClearAllCachedData();
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
                dataPersistenceService.ClearMainWindowState();
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
            var result = MessageBox.Show("确定要清除所有数据吗？\n这将删除：\n- 主界面状态数据\n- 应用程序缓存数据\n- 用户设置数据\n\n此操作不可撤销！",
                                         "确认清除所有数据",
                                         MessageBoxButton.YesNo,
                                         MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                dataPersistenceService.ClearMainWindowState();
                dataPersistenceService.ClearAllCachedData();
                dataPersistenceService.ClearSettings();

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

            var settings = new AppSettings
            {
                AddinPath = AddinPath.Trim(),
                ProgramEntryWithG = ProgramEntryWithG,
                UpdateServerUrl = string.IsNullOrWhiteSpace(UpdateServerUrl) ? null : UpdateServerUrl.Trim(),
                FilterLogDirectories = FilterLogDirectories,
                LogTxtReader = LogTxtReader,
            };

            dataPersistenceService.SaveSettings(settings);

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
}