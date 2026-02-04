using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Controls.DataGrid;
using CustomControlLibrary.CustomControl.Controls.DataGrid.Filter;
using PackageManager.Function.CsvTool;
using PackageManager.Function.DnsTool;
using PackageManager.Models;
using PackageManager.Services;
using PackageManager.Views;
using PackageManager.Views.KanBan;

namespace PackageManager;

public class CommonLinkItem
{
    public CommonLinkItem(string name, string url)
    {
        Name = name;
        Url = url;
    }

    public string Name { get; }

    public string Url { get; }
}

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly PackageUpdateService _updateService;

    private readonly FtpService _ftpService;

    private readonly ApplicationFinderService _applicationFinderService;

    private readonly DataPersistenceService _dataPersistenceService;

    private ObservableCollection<PackageInfo> _packages;

    private PackageInfo _latestActivePackage;

    private CommonLinkItem _selectedCommonLink;

    // 当前选中的分类名称（供左侧导航使用）
    private string _selectedCategory;

    // 中央区域页面承载：主页与导航方法
    private PackagesHomePage _homePage;

    private int _navigationVersion;

    private bool _isHomeActive;

    public MainWindow()
    {
        InitializeComponent();

        _updateService = new PackageUpdateService();
        _ftpService = new FtpService();
        _applicationFinderService = new ApplicationFinderService();
        _dataPersistenceService = new DataPersistenceService();

        // 设置PackageInfo的静态DataPersistenceService引用
        PackageInfo.DataPersistenceService = _dataPersistenceService;
        FtpService.DataService = _dataPersistenceService;

        DataContext = this;

        // 首次进入主界面，加载包列表主页到中央区域
        NavigateHome();
        InitializePackages();
        BuildCategoryTree();
        InitializeCommonLinks();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        // 初始化命令，将现有点击处理函数以命令方式暴露给 NavigationPanel
        RefreshCommand = new RelayCommand(() => { _ = LoadVersionsFromFtpAsync(); });
        NavigateHomeCommand = new RelayCommand(() => { NavigateHome(); });
        SettingsCommand = new RelayCommand(() => { SettingsButton_Click(this, new RoutedEventArgs()); });
        LocalPathSettingsCommand = new RelayCommand(() => { LocalPathSettingsButton_Click(this, new RoutedEventArgs()); });
        OpenLogViewerCommand = new RelayCommand(() => { OpenLogViewerButton_Click(this, new RoutedEventArgs()); });
        OpenProductLogsCommand = new RelayCommand(() => { OpenProductLogButton_Click(this, new RoutedEventArgs()); });
        OpenPackageConfigCommand = new RelayCommand(() => { OpenPackageConfigButton_Click(this, new RoutedEventArgs()); });
        OpenCommonLinksPageCommand = new RelayCommand(OpenCommonLinksPage);
        OpenChangelogPageCommand = new RelayCommand(() => { OpenChangelogPageButton_Click(this, new RoutedEventArgs()); });
        OpenKanbanStatsPageCommand = new RelayCommand(() => { OpenKanbanStatsPageButton_Click(this, new RoutedEventArgs()); });
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public ObservableCollection<CommonLinkItem> CommonLinks { get; } = new();

    public CommonLinkItem SelectedCommonLink
    {
        get => _selectedCommonLink;

        set => SetProperty(ref _selectedCommonLink, value);
    }

    // 左侧导航分类数据
    public ObservableCollection<CategoryNode> CategoryTree { get; } = new();

    public string SelectedCategory
    {
        get => _selectedCategory;

        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                ApplyCategorySelection();
            }
        }
    }

    // 左侧导航命令（与 NavigationPanel.xaml 绑定）
    public ICommand NavigateHomeCommand { get; set; }

    public ICommand RefreshCommand { get; }

    public ICommand SettingsCommand { get; }

    public ICommand LocalPathSettingsCommand { get; }

    public ICommand OpenLogViewerCommand { get; }

    public ICommand OpenProductLogsCommand { get; }

    public ICommand OpenPackageConfigCommand { get; }

    public ICommand OpenCommonLinksPageCommand { get; }

    public ICommand OpenChangelogPageCommand { get; }
    
    public ICommand OpenKanbanStatsPageCommand { get; }

    public ObservableCollection<PackageInfo> Packages
    {
        get => _packages;

        set => SetProperty(ref _packages, value);
    }

    public PackageInfo LatestActivePackage
    {
        get => _latestActivePackage;

        set => SetProperty(ref _latestActivePackage, value);
    }

    // 每次成功导航后递增，用于左侧导航在命令执行失败时回退选中项
    public int NavigationVersion
    {
        get => _navigationVersion;

        private set => SetProperty(ref _navigationVersion, value);
    }

    // 指示中央区域是否处于主页（用于左侧导航同步选中状态）
    public bool IsHomeActive
    {
        get => _isHomeActive;

        private set => SetProperty(ref _isHomeActive, value);
    }

    // 定版：将当前选中版本的包下载到临时目录并上传到定版目录
    public async Task FinalizeSelectedPackageAsync()
    {
        var pkg = LatestActivePackage;
        if (pkg == null)
        {
            MessageBox.Show("请先在左侧选择一个产品包", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(pkg.Version))
        {
            MessageBox.Show("请先选择版本", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(pkg.UploadPackageName))
        {
            MessageBox.Show("请先选择包名", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(pkg.FinalizeFtpServerPath))
        {
            MessageBox.Show("未配置定版包地址，请在包管理中补充 ‘定版FTP路径’。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        pkg.IsFinalizing = true;
        pkg.StatusText = $"正在为 {pkg.ProductName} 执行定版...";

        try
        {
            string version = pkg.Version.Trim();
            string selectedName = pkg.UploadPackageName.Trim();

            // 开发版本源（强制按FTP访问）
            string devVersionFtpBase = ToFtpUrl((pkg.FtpServerPath ?? string.Empty).TrimEnd('/') + "/" + version + "/");
            string srcUrl = devVersionFtpBase + selectedName;
            var srcUri = new Uri(srcUrl);

            // 下载到临时目录（仅当前选中包）
            string tempRoot = Path.Combine(Path.GetTempPath(),
                                           "PackageManagerFinalize",
                                           SanitizeForPath(pkg.ProductName),
                                           version,
                                           DateTime.Now.Ticks.ToString());
            Directory.CreateDirectory(tempRoot);
            string localPath = Path.Combine(tempRoot, selectedName);

            using (var downClient = new WebClient())
            {
                if (srcUri.Scheme.Equals("ftp", StringComparison.OrdinalIgnoreCase))
                {
                    downClient.Credentials = new NetworkCredential("hongwauser", "hw_ftpa206");
                }

                await downClient.DownloadFileTaskAsync(srcUri, localPath);
            }

            // 定版目标（按HTTP配置显示，上传走FTP）
            string dirName = DateTime.Now.ToString("yyyy.MM.dd") + "_" + version; // 2025.12.02_v2.1.0.0
            string finalizeHttpBase = pkg.FinalizeFtpServerPath.TrimEnd('/') + "/";
            string remoteHttpDir = finalizeHttpBase + dirName + "/";
            string remoteFtpDir = ToFtpUrl(remoteHttpDir);

            await CreateRemoteDirectoryAsync(remoteFtpDir);

            using (var ftpClient = new WebClient())
            {
                ftpClient.Credentials = new NetworkCredential("hwuser", "hongwa666.");
                string destUrl = remoteFtpDir + selectedName;
                await ftpClient.UploadFileTaskAsync(new Uri(destUrl), WebRequestMethods.Ftp.UploadFile, localPath);
            }

            LoggingService.LogInfo($"定版完成，已上传到 {remoteHttpDir}");
            pkg.StatusText = $"{pkg.ProductName} 定版完成";
            ToastService.ShowToast("定版完成",$"已上传到 {remoteHttpDir}");
        }
        catch (Exception ex)
        {
            pkg.StatusText = $"{pkg.ProductName} 定版失败: {ex.Message}";
            LoggingService.LogError(ex,"定版失败");
        }
        finally
        {
            pkg.IsFinalizing = false;
        }
    }

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

    private static string SanitizeForPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Unknown";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return sb.ToString();
    }

    private static string ToFtpUrl(string anyUrl)
    {
        if (string.IsNullOrWhiteSpace(anyUrl))
        {
            return anyUrl;
        }

        // 已是FTP地址则直接规范化尾部斜杠
        if (anyUrl.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
        {
            var u = new Uri(anyUrl);
            var s = u.ToString();
            if (!s.EndsWith("/"))
            {
                s += "/";
            }

            return s;
        }

        // 将 http(s)://host[:port]/path 转为 ftp://host/path，使用默认端口（21）
        var httpUri = new Uri(anyUrl);
        var builder = new UriBuilder
        {
            Scheme = "ftp",
            Host = httpUri.Host,
            Path = httpUri.AbsolutePath,
        };
        var ftpUrl = builder.Uri.ToString();
        if (!ftpUrl.EndsWith("/"))
        {
            ftpUrl += "/";
        }

        return ftpUrl;
    }

    private static async Task CreateRemoteDirectoryAsync(string ftpDirUrl)
    {
        // 逐级创建目录，避免部分FTP服务不支持一次性创建深层目录
        var target = new Uri(ftpDirUrl);
        var baseUrl = $"{target.Scheme}://{target.Host}{(target.IsDefaultPort ? string.Empty : ":" + target.Port)}/";
        var segments = ftpDirUrl.Replace(baseUrl, string.Empty).Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

        string current = baseUrl;
        foreach (var seg in segments)
        {
            // 不进行URL编码，直接使用原始中文/全角字符，让服务器按文件系统编码处理
            current = current + seg + "/";
            var req = (FtpWebRequest)WebRequest.Create(current);
            req.Method = WebRequestMethods.Ftp.MakeDirectory;
            req.Credentials = new NetworkCredential("hwuser", "hongwa666.");
            req.UseBinary = true;
            req.UsePassive = true;
            req.KeepAlive = false;
            req.Proxy = null;
            try
            {
                using var resp = (FtpWebResponse)await req.GetResponseAsync();
            }
            catch (WebException wex)
            {
                // 目录存在或权限提示的错误，通常返回 550，可通过描述识别，忽略
                if (wex.Response is FtpWebResponse ftpResp)
                {
                    var desc = ftpResp.StatusDescription ?? string.Empty;
                    if ((ftpResp.StatusCode == FtpStatusCode.ActionNotTakenFileUnavailable) || desc.Contains("550") || desc.Contains("exists"))
                    {
                        continue; // 已存在则跳过
                    }
                }

                throw;
            }
        }
    }

    private void NavigateHome()
    {
        if (_homePage == null)
        {
            _homePage = new PackagesHomePage();

            // 使用主窗口的 DataContext 进行数据绑定
            _homePage.DataContext = this;
        }

        CentralFrame.Navigate(_homePage);
        NavigationVersion++;
        IsHomeActive = true;
    }

    private void NavigateTo(Page page)
    {
        if (page == null)
        {
            return;
        }

        // 不强制覆盖页面自己的DataContext；仅当未设置时才继承主窗口上下文
        if (page.DataContext == null)
        {
            page.DataContext = this;
        }

        CentralFrame.Navigate(page);
        NavigationVersion++;
        IsHomeActive = false;

        // 同步左侧导航选中项
        try
        {
            var name = (page is ProductLogsPage) ? "产品日志"
                       : (page is LogViewerPage) ? "软件日志"
                       : (page is PackageConfigPage) ? "产品管理"
                       : (page is SettingsPage) ? "软件设置"
                       : (page is KanbanStatsPage) ? "看板统计"
                       : null;
            if (!string.IsNullOrEmpty(name))
            {
                LeftNavPanel?.SelectActionByName(name);
            }
        }
        catch { }
    }

    public void UpdateLeftNavSelection(string name)
    {
        try { LeftNavPanel?.SelectActionByName(name); } catch { }
    }

    private CustomControlLibrary.CustomControl.Controls.DataGrid.CDataGrid GetPackageDataGrid()
    {
        return _homePage?.PackageGrid;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var appUpdater = new AppUpdateService();
            await appUpdater.CheckAndPromptUpdateAsync(this);
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "启动更新检查失败");
        }

        // await LoadVersionsFromFtpAsync();
        await LoadVersionsWhichVisiableFromFtpAsync();
        
        // 加载可执行文件版本
        await LoadExecutableVersionsAsync();
    }

    private void InitializeCommonLinks()
    {
        try
        {
            CommonLinks.Clear();
            CommonLinks.Add(new CommonLinkItem("JenKins", "http://192.168.0.245:8080/view/%E6%9C%BA%E7%94%B5%E9%A1%B9%E7%9B%AE%E7%BB%84/"));
            CommonLinks.Add(new CommonLinkItem("210", "http://192.168.0.210:8001/"));
            CommonLinks.Add(new CommonLinkItem("215", "http://192.168.0.215:8001/"));
            CommonLinks.Add(new CommonLinkItem("导航", "http://192.168.0.11:9999/"));
            CommonLinks.Add(new CommonLinkItem("传360",
                                               "https://i.360.cn/login/?src=pcw_renzheng&tpl=client&destUrl=https%3A%2F%2Fopen.soft.360.cn%2Fsoftsubmit.php"));
            SelectedCommonLink = CommonLinks.FirstOrDefault();
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "初始化常用链接失败");
        }
    }

    /// <summary>
    /// 根据当前包列表构建左侧分类树（单层：全部 + 各产品名）
    /// </summary>
    private void BuildCategoryTree()
    {
        try
        {
            CategoryTree.Clear();
            var names = new List<string> { "全部" };
            if (Packages != null)
            {
                names.AddRange(Packages.Select(p => p.ProductName).Distinct().Where(IsProductVisible));
            }

            foreach (var name in names)
            {
                CategoryTree.Add(new CategoryNode { Name = name });
            }

            SelectedCategory = names.FirstOrDefault();
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "构建分类导航失败");
        }
    }

    private void OpenSelectedLinkButton_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedCommonLink;
        if (item == null)
        {
            MessageBox.Show("请先选择一个常用网址", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Url))
        {
            MessageBox.Show("该常用网址尚未配置具体地址，请在设置中填写公司官网URL。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenUrl(item.Url);
    }

    private void OpenUrl(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show("目标链接为空", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            try
            {
                Process.Start("explorer.exe", url);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "打开网址失败");
                MessageBox.Show($"打开网址失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>
    /// 加载可执行文件版本
    /// </summary>
    private async Task LoadExecutableVersionsAsync()
    {
        await Task.Run(() =>
        {
            string programName = "Revit";
            try
            {
                // 检查是否有缓存数据
                if (_dataPersistenceService.HasCachedData(programName))
                {
                    var cachedVersions = _dataPersistenceService.GetCachedData(programName);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        foreach (PackageInfo packageInfo in Packages)
                        {
                            if ((packageInfo.AvailableExecutableVersions.Count == 0) || string.IsNullOrEmpty(packageInfo.SelectedExecutableVersion))
                            {
                                UpdatePackageExecutableVersions(packageInfo, cachedVersions);
                            }
                        }
                    });
                }
                else
                {
                    // 查找可执行文件版本
                    var versions = _applicationFinderService.FindAllApplicationVersions(programName);
                    if (versions.Any())
                    {
                        // 保存到缓存
                        _dataPersistenceService.UpdateCachedData(programName, versions);

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            foreach (PackageInfo packageInfo in Packages)
                            {
                                UpdatePackageExecutableVersions(packageInfo, versions);
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载 {programName} 可执行文件版本时出错: {ex.Message}");
            }
        });
    }

    private void OpenDnsSettingsWindowButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new DnsSettingsWindow { Owner = this };
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "打开DNS设置窗口失败");
        }
    }

    /// <summary>
    /// 更新包的可执行文件版本信息
    /// </summary>
    /// <param name="package">包信息</param>
    /// <param name="versions">版本列表</param>
    private void UpdatePackageExecutableVersions(PackageInfo package, List<ApplicationVersion> versions)
    {
        package.AvailableExecutableVersions.Clear();

        foreach (var version in versions)
        {
            package.AvailableExecutableVersions.Add(version);
        }

        // 设置默认选择第一个版本
        if (package.AvailableExecutableVersions.Any())
        {
            package.SelectedExecutableVersion = package.AvailableExecutableVersions.First().DisPlayName;
            var firstVersion = versions.First();
            package.ExecutablePath = firstVersion.ExecutablePath;
        }
    }

    /// <summary>
    /// 保存当前状态
    /// </summary>
    private void SaveCurrentState()
    {
        try
        {
            _dataPersistenceService.SaveMainWindowState(Packages);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"保存当前状态失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 窗口关闭时保存数据
    /// </summary>
    private void MainWindow_Closing(object sender, CancelEventArgs e)
    {
        try
        {
            // 保存主界面状态
            SaveCurrentState();
            Debug.WriteLine("应用程序正在关闭，数据已保存");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"关闭时保存数据出错: {ex.Message}");
        }
    }

    /// <summary>
    /// 从FTP服务器加载版本信息
    /// </summary>
    private async Task LoadVersionsFromFtpAsync()
    {
        foreach (PackageInfo packageInfo in Packages)
        {
            packageInfo.Version = string.Empty;
            packageInfo.UploadPackageName = string.Empty;
        }

        foreach (var package in Packages)
        {
            await LoadPackageDataAsync(package);
        }

        // 重新构建分类导航，确保新增包或产品名变更反映到左侧
        BuildCategoryTree();
    }
    
    /// <summary>
    /// 从FTP服务器加载版本信息
    /// </summary>
    private async Task LoadVersionsWhichVisiableFromFtpAsync()
    {
        var dataGrid = GetPackageDataGrid();
        if (dataGrid == null)
        {
            return;
        }

        try
        {
            var filteredPackages = new System.Collections.Generic.List<PackageInfo>();
            var view = CollectionViewSource.GetDefaultView(dataGrid.ItemsSource);
            if (view != null)
            {
                foreach (var obj in view)
                {
                    if (obj is PackageInfo pkg) filteredPackages.Add(pkg);
                }
            }
            else
            {
                foreach (var obj in dataGrid.Items)
                {
                    if (obj is PackageInfo pkg) filteredPackages.Add(pkg);
                }
            }

            if (filteredPackages.Count == 0)
            {
                foreach (var obj in Packages)
                {
                    if (obj is PackageInfo pkg && IsProductVisible(pkg.ProductName)) filteredPackages.Add(pkg);
                }
            }

            if (filteredPackages.Count == 0) return;

            foreach (var pkg in filteredPackages)
            {
                pkg.Version = string.Empty;
                pkg.UploadPackageName = string.Empty;
            }

            foreach (var pkg in filteredPackages)
            {
                await LoadPackageDataAsync(pkg);
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "仅刷新可见行版本信息失败");
        }
    }
    

    /// <summary>
    /// 加载单个包的数据（版本和文件列表）
    /// </summary>
    private async Task LoadPackageDataAsync(PackageInfo package)
    {
        try
        {
            // 从FTP路径读取所有文件夹作为版本
            var versions = await _ftpService.GetDirectoriesAsync(package.FtpServerPath);

            // 更新可用版本列表
            package.UpdateAvailableVersions(versions);

            // 如果有版本，加载第一个版本的文件列表作为上传时间
            if (versions.Count > 0)
            {
                var selectedVersion = package.Version ?? versions.Last();
                await LoadPackageFilesAsync(package, selectedVersion);
                package.StatusText = $"已加载 {versions.Count} 个版本";
            }
            else
            {
                package.StatusText = "未找到版本";
            }
        }
        catch (Exception ex)
        {
            // 如果读取失败，显示错误信息
            package.StatusText = $"读取版本失败: {ex.Message}";

            // 为了演示，添加一些默认版本
            package.UpdateAvailableVersions(new[] { package.Version });
        }
    }

    /// <summary>
    /// 加载指定版本的文件列表作为上传时间
    /// </summary>
    private async Task LoadPackageFilesAsync(PackageInfo package, string version)
    {
        try
        {
            if (string.IsNullOrEmpty(version))
            {
                return;
            }

            // 构建版本路径
            var versionPath = package.FtpServerPath.TrimEnd('/') + "/" + version + "/";

            // 从版本路径读取所有文件
            var files = await _ftpService.GetFilesAsync(versionPath);

            // 更新可用包列表（上传时间）
            package.UpdateAvailablePackages(files);
        }
        catch (Exception ex)
        {
            // 如果读取文件失败，不影响版本加载
            package.StatusText += $" (文件列表加载失败: {ex.Message})";
        }
    }

    private void InitializePackages()
    {
        var builtIns = _dataPersistenceService.GetBuiltInPackageConfigs();
        var customs = _dataPersistenceService.LoadPackageConfigs();
        var all = builtIns.Concat(customs ?? Enumerable.Empty<DataPersistenceService.PackageConfigItem>()).ToList();

        Packages = new ObservableCollection<PackageInfo>(all.Select(ci => new PackageInfo
        {
            ProductName = ci.ProductName,
            Version = string.Empty,
            FtpServerPath = ci.FtpServerPath,
            FinalizeFtpServerPath = ci.FinalizeFtpServerPath,
            LocalPath = ci.LocalPath,
            Status = PackageStatus.Ready,
            StatusText = "就绪",
            UploadPackageName = string.Empty,
            SupportsConfigOpsOverride = ci.SupportsConfigOps,
        }));

        // 为每个PackageInfo订阅事件
        foreach (var package in Packages)
        {
            package.UpdateRequested += OnPackageUpdateRequested;
            package.VersionChanged += OnPackageVersionChanged;
            package.DownloadRequested += OnPackageDownloadRequested;
            package.UnlockAndDownloadRequested += OnUnlockAndDownloadRequested;
            package.DownloadZipOnlyRequested += OnPackageDownloadZipOnlyRequested;
            package.DebugModeChanged += OnPackageDebugModeChanged;
            package.PropertyChanged += OnPackagePropertyChanged;
        }

        if (_dataPersistenceService.HasMainWindowState())
        {
            var stateData = _dataPersistenceService.LoadMainWindowState();
            if (stateData?.Packages != null)
            {
                var byName = new Dictionary<string, PackageStateData>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in stateData.Packages)
                {
                    var key = (s?.ProductName ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(key) && !byName.ContainsKey(key))
                    {
                        byName[key] = s;
                    }
                }

                for (var index = 0; index < Packages.Count; index++)
                {
                    var package = Packages[index];
                    PackageStateData savedState = null;
                    if (!string.IsNullOrWhiteSpace(package?.ProductName) && byName.TryGetValue(package.ProductName, out var match))
                    {
                        savedState = match;
                    }

                    if (savedState != null)
                    {
                        _dataPersistenceService.ApplyStateToPackage(package, savedState);
                    }
                }

                var view = CollectionViewSource.GetDefaultView(Packages);
                if (view != null)
                {
                    view.Filter = obj =>
                    {
                        var p = obj as PackageInfo;
                        return (p != null) && IsProductVisible(p.ProductName);
                    };
                }
            }
        }
    }

    /// <summary>
    /// 重新加载包配置并立即应用到主界面（无需重启）。
    /// </summary>
    public void ReloadPackagesFromConfig()
    {
        try
        {
            // 先解除旧集合的事件订阅，避免重复订阅导致资源泄露
            if (Packages != null)
            {
                foreach (var p in Packages)
                {
                    p.UpdateRequested -= OnPackageUpdateRequested;
                    p.VersionChanged -= OnPackageVersionChanged;
                    p.DownloadRequested -= OnPackageDownloadRequested;
                    p.UnlockAndDownloadRequested -= OnUnlockAndDownloadRequested;
                    p.DownloadZipOnlyRequested -= OnPackageDownloadZipOnlyRequested;
                    p.DebugModeChanged -= OnPackageDebugModeChanged;
                    p.PropertyChanged -= OnPackagePropertyChanged;
                }
            }

            // 重新初始化包集合
            InitializePackages();

            // 重新初始化可执行文件版本
            _ = LoadExecutableVersionsAsync();
            
            // 更新左侧分类导航
            BuildCategoryTree();

            // 返回主页以显示最新列表
            NavigateHome();

            // 自动加载各包的版本与文件信息（异步，不阻塞UI）
            // _ = LoadVersionsFromFtpAsync();
            
            _ = LoadVersionsWhichVisiableFromFtpAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"重新加载包配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnPackageDownloadRequested(PackageInfo packageInfo)
    {
        packageInfo.StatusText = $"正在更新 {packageInfo.ProductName}...";

        // 设置为只读状态，防止更新时编辑
        packageInfo.IsReadOnly = true;
        packageInfo.IsUpdatingRunning = true;
        packageInfo.UpdateCancellationSource = new System.Threading.CancellationTokenSource();

        // 开始更新
        var success = await _updateService.UpdatePackageAsync(packageInfo,
                                                              (progress, message) =>
                                                              {
                                                                  // 在UI线程更新状态
                                                                  Dispatcher.Invoke(() =>
                                                                  {
                                                                      packageInfo.StatusText =
                                                                          $"{packageInfo.ProductName}: {message}";
                                                                  });
                                                              },
                                                              false,
                                                              packageInfo.UpdateCancellationSource.Token);

        // 更新完成后的状态
        Dispatcher.Invoke(() =>
        {
            // 恢复可编辑状态
            packageInfo.IsReadOnly = false;
            packageInfo.IsUpdatingRunning = false;

            if (success)
            {
                packageInfo.StatusText = $"{packageInfo.ProductName} 更新完成";
            }
            else
            {
                // 如果服务端已将状态设置为取消，则沿用取消文案
                if ((packageInfo.Status == PackageStatus.Ready) && string.Equals(packageInfo.StatusText, "已取消", StringComparison.Ordinal))
                {
                    packageInfo.StatusText = $"{packageInfo.ProductName} 更新已取消";
                }
                else
                {
                    packageInfo.StatusText = $"{packageInfo.ProductName} 更新失败";
                }
            }
        });
    }

    private async void OnUnlockAndDownloadRequested(PackageInfo packageInfo)
    {
        packageInfo.StatusText = $"正在解锁并更新 {packageInfo.ProductName}...";
        packageInfo.IsReadOnly = true;
        packageInfo.IsUpdatingRunning = true;
        packageInfo.UpdateCancellationSource = new System.Threading.CancellationTokenSource();
        var success = await _updateService.UpdatePackageAsync(packageInfo,
                                                              (progress, message) =>
                                                              {
                                                                  Dispatcher.Invoke(() =>
                                                                  {
                                                                      packageInfo.StatusText =
                                                                          $"{packageInfo.ProductName}: {message}";
                                                                  });
                                                              },
                                                              true,
                                                              packageInfo.UpdateCancellationSource.Token);
        Dispatcher.Invoke(() =>
        {
            packageInfo.IsReadOnly = false;
            packageInfo.IsUpdatingRunning = false;
            if (success)
            {
                packageInfo.StatusText = $"{packageInfo.ProductName} 更新完成";
            }
            else
            {
                if ((packageInfo.Status == PackageStatus.Ready) && string.Equals(packageInfo.StatusText, "已取消", StringComparison.Ordinal))
                {
                    packageInfo.StatusText = $"{packageInfo.ProductName} 更新已取消";
                }
                else
                {
                    packageInfo.StatusText = $"{packageInfo.ProductName} 更新失败";
                }
            }
        });
    }

    /// <summary>
    /// 仅下载ZIP包到用户选择的位置（不解压）。
    /// </summary>
    private async void OnPackageDownloadZipOnlyRequested(PackageInfo packageInfo)
    {
        try
        {
            // 选择保存位置（默认桌面）
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var defaultFileName = System.IO.Path.GetFileName(string.IsNullOrWhiteSpace(packageInfo.UploadPackageName)
                ? $"{packageInfo.ProductName}.zip"
                : packageInfo.UploadPackageName);

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存ZIP包",
                Filter = "Zip 文件 (*.zip)|*.zip|所有文件 (*.*)|*.*",
                FileName = defaultFileName,
                InitialDirectory = string.IsNullOrWhiteSpace(desktop) ? Environment.CurrentDirectory : desktop,
            };

            var ok = dialog.ShowDialog(Application.Current?.MainWindow) ?? false;
            if (!ok)
            {
                // 用户取消保存对话框：不继续下载
                packageInfo.StatusText = "下载已取消";
                packageInfo.Status = PackageStatus.Ready;
                return;
            }
            var localZipPath = dialog.FileName;

            // 启动仅下载流程并禁用按钮
            packageInfo.IsDownloadOnlyRunning = true;
            packageInfo.Status = PackageStatus.Downloading;
            packageInfo.StatusText = $"正在仅下载 {packageInfo.ProductName}...";

            var cts = new System.Threading.CancellationTokenSource();
            var success = await _updateService.DownloadZipOnlyAsync(
                packageInfo,
                localZipPath,
                (progress, message) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        packageInfo.Progress = progress;
                        packageInfo.StatusText = $"{packageInfo.ProductName}: {message}";
                    });
                },
                cts.Token);

            // 恢复按钮状态
            packageInfo.IsDownloadOnlyRunning = false;

            Dispatcher.Invoke(() =>
            {
                if (success)
                {
                    packageInfo.StatusText = $"{packageInfo.ProductName} 仅下载完成";
                    packageInfo.Status = PackageStatus.Completed;
                }
                else
                {
                    if ((packageInfo.Status == PackageStatus.Ready) && string.Equals(packageInfo.StatusText, "已取消", StringComparison.Ordinal))
                    {
                        packageInfo.StatusText = $"{packageInfo.ProductName} 下载已取消";
                    }
                    else
                    {
                        packageInfo.StatusText = $"{packageInfo.ProductName} 下载失败";
                        packageInfo.Status = PackageStatus.Error;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            packageInfo.IsDownloadOnlyRunning = false;
            packageInfo.Status = PackageStatus.Error;
            packageInfo.StatusText = $"仅下载失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 处理调试模式切换事件，左下角状态栏写入当前模式
    /// </summary>
    private void OnPackageDebugModeChanged(PackageInfo package, bool isDebug)
    {
        try
        {
            package.StatusText = $"{package.ProductName} 当前模式：{(isDebug ? "调试模式" : "正常模式")}";

            // 持久化到主界面状态（包含IsDebugMode）
            SaveCurrentState();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"更新状态栏失败: {ex.Message}");
        }
    }

    private void ApplyCategorySelection()
    {
        try
        {
            // 无论当前是否在其它页面，切换分类后回到包列表主页
            NavigateHome();

            var view = CollectionViewSource.GetDefaultView(Packages);
            if (view == null)
            {
                return;
            }

        if (string.IsNullOrWhiteSpace(SelectedCategory) || string.Equals(SelectedCategory, "全部", StringComparison.Ordinal))
        {
            view.Filter = obj =>
            {
                var p = obj as PackageInfo;
                return (p != null) && IsProductVisible(p.ProductName);
            };
        }
        else
        {
            view.Filter = obj =>
            {
                var p = obj as PackageInfo;
                return (p != null)
                       && string.Equals(p.ProductName, SelectedCategory, StringComparison.OrdinalIgnoreCase)
                       && IsProductVisible(p.ProductName);
            };
        }
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "应用分类筛选失败");
        }
    }

    /// <summary>
    /// 刷新按钮点击事件
    /// </summary>
    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        // 重新加载可见包的版本和文件信息
        await LoadVersionsWhichVisiableFromFtpAsync();
        
        // 重新加载所有包的版本和文件信息
        //await LoadVersionsFromFtpAsync();
    }

    /// <summary>
    /// 处理PackageInfo的更新请求
    /// </summary>
    private async void OnPackageUpdateRequested(PackageInfo package)
    {
        try
        {
            // 重新加载该包的版本和文件信息
            await LoadPackageDataAsync(package);

            // 如果选择了版本，重新加载该版本的文件列表
            if (!string.IsNullOrEmpty(package.Version))
            {
                await LoadPackageFilesAsync(package, package.Version);
            }
        }
        catch (Exception ex)
        {
            package.StatusText = $"更新失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 处理版本切换事件，自动更新可用包列表
    /// </summary>
    private async void OnPackageVersionChanged(PackageInfo package, string newVersion)
    {
        try
        {
            if (!string.IsNullOrEmpty(newVersion))
            {
                package.StatusText = $"正在加载版本 {newVersion} 的包列表...";

                // 加载新版本的文件列表
                package.UploadPackageName = string.Empty;
                await LoadPackageFilesAsync(package, newVersion);

                package.StatusText = $"版本 {newVersion} 包列表加载完成";
            }
        }
        catch (Exception ex)
        {
            package.StatusText = $"加载版本包列表失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 设置按钮点击事件
    /// </summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var page = new SettingsPage(_dataPersistenceService);
            if (page is ICentralPage icp)
            {
                icp.RequestExit += () => NavigateHome();
            }

            NavigateTo(page);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开设置窗口失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 打开本地包路径设置窗口
    /// </summary>
    private void LocalPathSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var page = new LocalPathSettingsPage(_dataPersistenceService, Packages);
            if (page is ICentralPage icp)
            {
                icp.RequestExit += () => NavigateHome();
            }

            // 保存后更新状态提示
            page.Saved += () =>
            {
                _dataPersistenceService.SaveMainWindowState(Packages);
                var pkg = LatestActivePackage ?? Packages?.FirstOrDefault();
                if (pkg != null)
                {
                    pkg.StatusText = "本地路径设置已保存";
                }
            };
            NavigateTo(page);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开路径设置窗口失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 清除数据按钮点击事件
    /// </summary>
    private void ClearDataButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = MessageBox.Show("确定要清除所有保存的数据吗？\n这将删除：\n- 主界面状态数据\n- 应用程序缓存数据\n- 用户设置数据\n\n此操作不可撤销！",
                                         "确认清除数据",
                                         MessageBoxButton.YesNo,
                                         MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _dataPersistenceService.ClearMainWindowState();
                _dataPersistenceService.ClearAllCachedData();
                _dataPersistenceService.ClearSettings();

                var pkg = LatestActivePackage ?? Packages?.FirstOrDefault();
                if (pkg != null)
                {
                    pkg.StatusText = "所有数据已清除";
                }

                MessageBox.Show("数据清除完成！\n建议重启应用程序以确保所有更改生效。",
                                "清除完成",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"清除数据失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnPackagePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName == nameof(PackageInfo.StatusText)) && sender is PackageInfo pkg)
        {
            Dispatcher.Invoke(() => { LatestActivePackage = pkg; });
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackageManager", "logs");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true,
                });
            }
            catch
            {
                Process.Start("explorer.exe", dir);
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "打开日志目录失败");
            MessageBox.Show($"打开日志目录失败：{ex.Message}", "日志", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenLogViewerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var page = new LogViewerPage();
            if (page is ICentralPage icp)
            {
                icp.RequestExit += () => NavigateHome();
            }

            NavigateTo(page);
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "打开日志查看器失败");
            MessageBox.Show($"打开日志查看器失败：{ex.Message}", "日志", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenChangelogPageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var page = new ChangelogPage();
            if (page is ICentralPage icp)
            {
                icp.RequestExit += () => NavigateHome();
            }

            NavigateTo(page);
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "打开更新日志页面失败");
            MessageBox.Show($"打开更新日志页面失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenPackageConfigButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var page = new PackageConfigPage();
            if (page is ICentralPage icp)
            {
                icp.RequestExit += () => NavigateHome();
            }

            NavigateTo(page);
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "打开包管理配置页面失败");
            MessageBox.Show($"打开包管理配置页面失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 打开常用网址导航页面（窗口承载）
    /// </summary>
    private void OpenCommonLinksPage()
    {
        try
        {
            var page = new CommonLinksPage(CommonLinks);
            if (page is ICentralPage icp)
            {
                icp.RequestExit += () => NavigateHome();
            }

            NavigateTo(page);
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "打开常用网址导航页失败");
            MessageBox.Show($"打开常用网址导航页失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenProductLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var pkg = LatestActivePackage;
            if (pkg == null)
            {
                MessageBox.Show("请先选择一个产品包", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var baseDir = Path.Combine(Path.GetTempPath(), "HongWaSoftLog");
            var page = new ProductLogsPage(baseDir);
            if (page is ICentralPage icp)
            {
                icp.RequestExit += () => NavigateHome();
            }

            NavigateTo(page);
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "打开产品日志目录失败");
            MessageBox.Show($"打开产品日志目录失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    
    private void OpenKanbanStatsPageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var page = new KanbanStatsPage();
            if (page is ICentralPage icp)
            {
                icp.RequestExit += () => NavigateHome();
            }
            NavigateTo(page);
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "打开看板统计页面失败");
            MessageBox.Show($"打开看板统计页面失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenCsvCryptoWindowButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new CsvCryptoWindow();
            win.Owner = this;
            win.Show();
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "打开CSV加解密窗口失败");
            MessageBox.Show($"打开CSV加解密窗口失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        return;
    }

    private bool IsProductVisible(string name)
    {
        var vis = _dataPersistenceService.GetProductVisibility();
        if (vis == null || vis.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (vis.TryGetValue(name, out var flag)) return flag;
        return true;
    }
}
