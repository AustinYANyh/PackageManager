using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PackageManager.Function.PackageManage;
using PackageManager.Services;

namespace PackageManager.Views;

/// <summary>
/// 应用设置页面，管理 Addin 路径、更新服务器、Jenkins 配置等。
/// </summary>
public partial class SettingsPage : Page, INotifyPropertyChanged, ICentralPage
{
    private readonly DataPersistenceService dataPersistenceService;

    private string addinPath;

    private string updateServerUrl;

    private bool filterLogDirectories;

    private bool programEntryWithG;

    private string dataLocation;

    private string appVersionText;

    private string productLogLevel;

    private string jenkinsBaseUrl;

    private string jenkinsViewName;

    private string jenkinsUsername;
    private bool enableIndexServicePerformanceAnalysis;

    /// <summary>
    /// 初始化 <see cref="SettingsPage"/> 的新实例。
    /// </summary>
    /// <param name="dataPersistenceService">数据持久化服务实例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataPersistenceService"/> 为 null。</exception>
    public SettingsPage(DataPersistenceService dataPersistenceService)
    {
        InitializeComponent();
        this.dataPersistenceService = dataPersistenceService ?? throw new ArgumentNullException(nameof(dataPersistenceService));
        DataContext = this;
        LoadSettings();

        var current = GetCurrentVersion();
        AppVersionText = $"版本：{current}";
    }

    /// <summary>
    /// 请求退出当前页面的导航事件。
    /// </summary>
    public event Action RequestExit;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// 获取或设置 Revit Addin 根目录路径。
    /// </summary>
    public string AddinPath
    {
        get => addinPath;

        set => SetProperty(ref addinPath, value);
    }

    /// <summary>
    /// 获取或设置更新服务器 URL。
    /// </summary>
    public string UpdateServerUrl
    {
        get => updateServerUrl;

        set => SetProperty(ref updateServerUrl, value);
    }

    /// <summary>
    /// 获取或设置程序入口是否带 G 参数的标志。
    /// </summary>
    public bool ProgramEntryWithG
    {
        get => programEntryWithG;

        set => SetProperty(ref programEntryWithG, value);
    }

    /// <summary>
    /// 获取或设置产品日志等级。
    /// </summary>
    public string ProductLogLevel
    {
        get => productLogLevel;

        set => SetProperty(ref productLogLevel, value);
    }

    /// <summary>
    /// 获取或设置 Jenkins 服务器基础 URL。
    /// </summary>
    public string JenkinsBaseUrl
    {
        get => jenkinsBaseUrl;

        set => SetProperty(ref jenkinsBaseUrl, value);
    }

    /// <summary>
    /// 获取或设置 Jenkins 视图名称。
    /// </summary>
    public string JenkinsViewName
    {
        get => jenkinsViewName;

        set => SetProperty(ref jenkinsViewName, value);
    }

    /// <summary>
    /// 获取或设置 Jenkins 用户名。
    /// </summary>
    public string JenkinsUsername
    {
        get => jenkinsUsername;

        set => SetProperty(ref jenkinsUsername, value);
    }

    /// <summary>
    /// 获取或设置是否启用索引服务性能分析日志。
    /// </summary>
    public bool EnableIndexServicePerformanceAnalysis
    {
        get => enableIndexServicePerformanceAnalysis;

        set => SetProperty(ref enableIndexServicePerformanceAnalysis, value);
    }

    /// <summary>
    /// 获取或设置是否过滤日志目录的标志。
    /// </summary>
    public bool FilterLogDirectories
    {
        get => filterLogDirectories;

        set => SetProperty(ref filterLogDirectories, value);
    }

    /// <summary>
    /// 获取或设置应用数据存储位置的显示文本。
    /// </summary>
    public string DataLocation
    {
        get => dataLocation;

        set => SetProperty(ref dataLocation, value);
    }

    /// <summary>
    /// 获取或设置应用版本号的显示文本。
    /// </summary>
    public string AppVersionText
    {
        get => appVersionText;

        set => SetProperty(ref appVersionText, value);
    }

    /// <summary>
    /// 获取日志文本阅读器选项列表。
    /// </summary>
    public ObservableCollection<string> LogTxtReaders { get; } = new();

    /// <summary>
    /// 获取或设置当前选中的日志文本阅读器名称。
    /// </summary>
    public string LogTxtReader { get; set; }

    /// <summary>
    /// 获取索引服务性能分析日志目录的显示文本。
    /// </summary>
    public string IndexServicePerformanceLogDirectory =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PackageManager",
            "logs",
            "index-service-diagnostics");

    /// <summary>
    /// 触发 <see cref="PropertyChanged"/> 事件。
    /// </summary>
    /// <param name="propertyName">发生更改的属性名称，默认为调用方成员名。</param>
    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 设置属性值并在值变化时触发 <see cref="PropertyChanged"/> 通知。
    /// </summary>
    /// <typeparam name="T">属性值的类型。</typeparam>
    /// <param name="field">属性后备字段的引用。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">属性名称，默认为调用方成员名。</param>
    /// <returns>如果值已更改返回 <c>true</c>，否则返回 <c>false</c>。</returns>
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
            ProductLogLevel = settings?.ProductLogLevel ?? "ERROR";
            JenkinsBaseUrl = settings?.JenkinsBaseUrl ?? "http://192.168.0.245:8080";
            JenkinsViewName = settings?.JenkinsViewName ?? "机电项目组";
            JenkinsUsername = settings?.JenkinsUsername ?? string.Empty;
            EnableIndexServicePerformanceAnalysis = settings?.EnableIndexServicePerformanceAnalysis ?? false;
            JenkinsPasswordBox.Password = CredentialProtectionService.Unprotect(settings?.JenkinsPasswordProtected);

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
    
    private void OpenAddinButtonOnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(AddinPath))
            {
                Process.Start(addinPath);
            }
            else
            {
                MessageBox.Show($"请先设置Addin文件夹", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开Addin文件夹: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
                JenkinsBaseUrl = "http://192.168.0.245:8080";
                JenkinsViewName = "机电项目组";
                JenkinsUsername = string.Empty;
                EnableIndexServicePerformanceAnalysis = false;
                JenkinsPasswordBox.Password = string.Empty;
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

            var settings = dataPersistenceService.LoadSettings() ?? new AppSettings();
            settings.AddinPath = AddinPath.Trim();
            settings.ProgramEntryWithG = ProgramEntryWithG;
            settings.UpdateServerUrl = string.IsNullOrWhiteSpace(UpdateServerUrl) ? null : UpdateServerUrl.Trim();
            settings.FilterLogDirectories = FilterLogDirectories;
            settings.LogTxtReader = LogTxtReader;
            settings.ProductLogLevel = ProductLogLevel;
            settings.JenkinsBaseUrl = string.IsNullOrWhiteSpace(JenkinsBaseUrl) ? null : JenkinsBaseUrl.Trim();
            settings.JenkinsViewName = string.IsNullOrWhiteSpace(JenkinsViewName) ? null : JenkinsViewName.Trim();
            settings.JenkinsUsername = string.IsNullOrWhiteSpace(JenkinsUsername) ? null : JenkinsUsername.Trim();
            settings.JenkinsPasswordProtected = CredentialProtectionService.Protect(JenkinsPasswordBox.Password);
            settings.EnableIndexServicePerformanceAnalysis = EnableIndexServicePerformanceAnalysis;

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
