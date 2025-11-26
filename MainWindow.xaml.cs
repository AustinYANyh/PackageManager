using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using System;
using System.Linq;
using PackageManager.Models;
using PackageManager.Services;
using System.IO;
using System.Diagnostics;

namespace PackageManager
{
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

        public MainWindow()
        {
            InitializeComponent();
            
            _updateService = new PackageUpdateService();
            _ftpService = new FtpService();
            _applicationFinderService = new ApplicationFinderService();
            _dataPersistenceService = new DataPersistenceService();
            
            // 设置PackageInfo的静态DataPersistenceService引用
            PackageInfo.DataPersistenceService = _dataPersistenceService;
            
            DataContext = this;
            InitializePackages();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var appUpdater = new Services.AppUpdateService();
                await appUpdater.CheckAndPromptUpdateAsync(this);
            }
            catch (Exception ex)
            {
                Services.LoggingService.LogError(ex, "启动更新检查失败");
            }

            await LoadVersionsFromFtpAsync();
            
            // 加载可执行文件版本
            await LoadExecutableVersionsAsync();
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
                                if (packageInfo.AvailableExecutableVersions.Count == 0 || string.IsNullOrEmpty(packageInfo.SelectedExecutableVersion))
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
                    System.Diagnostics.Debug.WriteLine($"加载 {programName} 可执行文件版本时出错: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 更新包的可执行文件版本信息
        /// </summary>
        /// <param name="package">包信息</param>
        /// <param name="versions">版本列表</param>
        private void UpdatePackageExecutableVersions(PackageInfo package, System.Collections.Generic.List<ApplicationVersion> versions)
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
                System.Diagnostics.Debug.WriteLine($"保存当前状态失败: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine("应用程序正在关闭，数据已保存");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"关闭时保存数据出错: {ex.Message}");
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
                if (string.IsNullOrEmpty(version)) return;

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
            Packages = new ObservableCollection<PackageInfo>
            {
                new PackageInfo
                {
                    ProductName = "MaxiBIM（CAB）Develop",
                    Version = string.Empty,
                    FtpServerPath = "http://doc-dev.hongwa.cc:8001/HWMaxiBIMCAB/",
                    LocalPath = @"C:\红瓦科技\MaxiBIM（CAB）Develop",
                    Status = PackageStatus.Ready,
                    StatusText = "就绪",
                    UploadPackageName = string.Empty,
                },
                new PackageInfo
                {
                    ProductName = "MaxiBIM（MEP）Develop",
                    Version = string.Empty,
                    FtpServerPath = "http://doc-dev.hongwa.cc:8001/BuildMaster(MEP)/",
                    LocalPath = @"C:\红瓦科技\MaxiBIM（MEP）Develop",
                    Status = PackageStatus.Ready,
                    StatusText = "就绪",
                    UploadPackageName = string.Empty,
                },
                new PackageInfo
                {
                    ProductName = "MaxiBIM（PMEP）Develop",
                    Version = string.Empty,
                    FtpServerPath = "http://doc-dev.hongwa.cc:8001/MaxiBIM(PMEP)/",
                    LocalPath = @"C:\红瓦科技\MaxiBIM（PMEP）Develop",
                    Status = PackageStatus.Ready,
                    StatusText = "就绪",
                    UploadPackageName = string.Empty,
                },
                new PackageInfo
                {
                    ProductName = "MaxiBIM（Duct）Develop",
                    Version = string.Empty,
                    FtpServerPath = "http://doc-dev.hongwa.cc:8001/HWMaxiBIMDUCT/",
                    LocalPath = @"C:\红瓦科技\MaxiBIM（Duct）Develop",
                    Status = PackageStatus.Ready,
                    StatusText = "就绪",
                    UploadPackageName = string.Empty,
                },
                
                new PackageInfo
                {
                    ProductName = "建模大师（CABE）Develop",
                    Version = string.Empty,
                    FtpServerPath = "http://doc-dev.hongwa.cc:8001/BuildMaster(CABE)/",
                    LocalPath = @"C:\红瓦科技\建模大师（CABE）Develop",
                    Status = PackageStatus.Ready,
                    StatusText = "就绪",
                    UploadPackageName = string.Empty,
                },
                new PackageInfo
                {
                    ProductName = "建模大师（钢构）Develop",
                    Version = string.Empty,
                    FtpServerPath = "http://doc-dev.hongwa.cc:8001/BuildMaster(ST)/",
                    LocalPath = @"C:\红瓦科技\建模大师（钢构）Develop",
                    Status = PackageStatus.Ready,
                    StatusText = "就绪",
                    UploadPackageName = string.Empty,
                },
                new PackageInfo
                {
                    ProductName = "建模大师（施工）Develop",
                    Version = string.Empty,
                    FtpServerPath = "http://doc-dev.hongwa.cc:8001/BuildMaster(CST)/",
                    LocalPath = @"C:\红瓦科技\建模大师（施工）Develop",
                    Status = PackageStatus.Ready,
                    StatusText = string.Empty,
                },
            };

            // 为每个PackageInfo订阅事件
            foreach (var package in Packages)
            {
                package.UpdateRequested += OnPackageUpdateRequested;
                package.VersionChanged += OnPackageVersionChanged;
                package.DownloadRequested += OnPackageDownloadRequested;
                package.UnlockAndDownloadRequested += OnUnlockAndDownloadRequested;
                package.DebugModeChanged += OnPackageDebugModeChanged;
                package.PropertyChanged += OnPackagePropertyChanged;
            }
            
            if (_dataPersistenceService.HasMainWindowState())
            {
                var stateData = _dataPersistenceService.LoadMainWindowState();
                if (stateData?.Packages != null)
                {
                    for (var index = 0; index < Packages.Count; index++)
                    {
                        var package = Packages[index];
                        var savedState = stateData.Packages[index];
                        if (savedState != null)
                        {
                            _dataPersistenceService.ApplyStateToPackage(package, savedState);
                        }
                    }
                }
            }
        }
        
        private async void OnPackageDownloadRequested(PackageInfo packageInfo)
        {
            packageInfo.StatusText = $"正在更新 {packageInfo.ProductName}...";
            
            // 设置为只读状态，防止更新时编辑
            packageInfo.IsReadOnly = true;
                
            // 开始更新
            var success = await _updateService.UpdatePackageAsync(packageInfo, 
                                                                  (progress, message) =>
                                                                  {
                                                                      // 在UI线程更新状态
                                                                      Dispatcher.Invoke(() =>
                                                                      {
                                                                          packageInfo.StatusText = $"{packageInfo.ProductName}: {message}";
                                                                      });
                                                                  });

            // 更新完成后的状态
            Dispatcher.Invoke(() =>
            {
                // 恢复可编辑状态
                packageInfo.IsReadOnly = false;
                
                if (success)
                {
                    packageInfo.StatusText = $"{packageInfo.ProductName} 更新完成";
                }
                else
                {
                    packageInfo.StatusText = $"{packageInfo.ProductName} 更新失败";
                }
            });
        }

        private async void OnUnlockAndDownloadRequested(PackageInfo packageInfo)
        {
            packageInfo.StatusText = $"正在解锁并更新 {packageInfo.ProductName}...";
            packageInfo.IsReadOnly = true;
            var success = await _updateService.UpdatePackageAsync(packageInfo,
                                                                  (progress, message) =>
                                                                  {
                                                                      Dispatcher.Invoke(() =>
                                                                      {
                                                                          packageInfo.StatusText = $"{packageInfo.ProductName}: {message}";
                                                                      });
                                                                  },
                                                                  true);
            Dispatcher.Invoke(() =>
            {
                packageInfo.IsReadOnly = false;
                if (success)
                {
                    packageInfo.StatusText = $"{packageInfo.ProductName} 更新完成";
                }
                else
                {
                    packageInfo.StatusText = $"{packageInfo.ProductName} 更新失败";
                }
            });
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
                System.Diagnostics.Debug.WriteLine($"更新状态栏失败: {ex.Message}");
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

        /// <summary>
        /// 刷新按钮点击事件
        /// </summary>
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            // 重新加载所有包的版本和文件信息
            await LoadVersionsFromFtpAsync();
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
                var settingsWindow = new SettingsWindow(_dataPersistenceService);
                settingsWindow.Owner = this;
                settingsWindow.ShowDialog();
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
                var win = new LocalPathSettingsWindow(_dataPersistenceService, Packages)
                {
                    Owner = this
                };
                var result = win.ShowDialog();
                if (result == true)
                {
                    // 保存主界面状态（包含LocalPath）
                    _dataPersistenceService.SaveMainWindowState(Packages);
                    var pkg = LatestActivePackage ?? Packages?.FirstOrDefault();
                    if (pkg != null)
                    {
                        pkg.StatusText = "本地路径设置已保存";
                    }
                }
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
                var result = MessageBox.Show(
                    "确定要清除所有保存的数据吗？\n这将删除：\n- 主界面状态数据\n- 应用程序缓存数据\n- 用户设置数据\n\n此操作不可撤销！", 
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
            if (e.PropertyName == nameof(PackageInfo.StatusText) && sender is PackageInfo pkg)
            {
                Dispatcher.Invoke(() =>
                {
                    LatestActivePackage = pkg;
                });
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
                        UseShellExecute = true
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
                var win = new LogViewerWindow();
                win.Owner = this;
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "打开日志查看器失败");
                MessageBox.Show($"打开日志查看器失败：{ex.Message}", "日志", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        
    }
}
