using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using System;
using PackageManager.Models;
using PackageManager.Services;

namespace PackageManager
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly PackageUpdateService _updateService;
        private readonly FtpService _ftpService;
        private ObservableCollection<PackageInfo> _packages;

        public ObservableCollection<PackageInfo> Packages
        {
            get => _packages;
            set => SetProperty(ref _packages, value);
        }

        public MainWindow()
        {
            InitializeComponent();
            _updateService = new PackageUpdateService();
            _ftpService = new FtpService();
            DataContext = this;
            InitializePackages();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadVersionsFromFtpAsync();
        }

        /// <summary>
        /// 从FTP服务器加载版本信息
        /// </summary>
        private async Task LoadVersionsFromFtpAsync()
        {
            foreach (var package in Packages)
            {
                try
                {
                    // 从FTP路径读取所有文件夹作为版本
                    var versions = await _ftpService.GetDirectoriesAsync(package.FtpServerPath);
                    
                    // 更新可用版本列表
                    package.UpdateAvailableVersions(versions);
                    
                    // 如果成功读取到版本，更新状态
                    if (versions.Count > 0)
                    {
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
                    UploadTime = string.Empty,
                },
                new PackageInfo
                {
                    ProductName = "MaxiBIM（MEP）Develop",
                    Version = string.Empty,
                    FtpServerPath = "http://doc-dev.hongwa.cc:8001/BuildMaster(MEP)/",
                    LocalPath = @"C:\红瓦科技\MaxiBIM（MEP）Develop",
                    Status = PackageStatus.Ready,
                    StatusText = "就绪",
                    UploadTime = string.Empty,
                },
                new PackageInfo
                {
                    ProductName = "MaxiBIM（PMEP）Develop",
                    Version = "v1.5.2.0",
                    FtpServerPath = "http://doc-dev.hongwa.cc:8001//MaxiBIM(PMEP)/",
                    LocalPath = @"C:\红瓦科技\MaxiBIM（PMEP）Develop",
                    Status = PackageStatus.Ready,
                    StatusText = "就绪",
                    UploadTime = string.Empty,
                },
                new PackageInfo
                {
                    ProductName = "建模大师（CABE）Develop",
                    Version = string.Empty,
                    FtpServerPath = "http://doc-dev.hongwa.cc:8001/BuildMaster(CABE)/",
                    LocalPath = @"C:\红瓦科技\建模大师（CABE）Develop",
                    Status = PackageStatus.Ready,
                    StatusText = "就绪",
                    UploadTime = string.Empty,
                },
                new PackageInfo
                {
                    ProductName = "建模大师（钢构）Develop",
                    Version = string.Empty,
                    FtpServerPath = "http://doc-dev.hongwa.cc:8001/BuildMaster(ST)/",
                    LocalPath = @"C:\红瓦科技\建模大师（钢构）Develop",
                    Status = PackageStatus.Ready,
                    StatusText = "就绪",
                    UploadTime = string.Empty,
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
        }

        /// <summary>
        /// 更新按钮点击事件
        /// </summary>
        private async void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PackageInfo packageInfo)
            {
                StatusText.Text = $"正在更新 {packageInfo.ProductName}...";
                
                // 开始更新
                var success = await _updateService.UpdatePackageAsync(packageInfo, 
                    (progress, message) =>
                    {
                        // 在UI线程更新状态
                        Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = $"{packageInfo.ProductName}: {message}";
                        });
                    });

                // 更新完成后的状态
                Dispatcher.Invoke(() =>
                {
                    if (success)
                    {
                        StatusText.Text = $"{packageInfo.ProductName} 更新完成";
                    }
                    else
                    {
                        StatusText.Text = $"{packageInfo.ProductName} 更新失败";
                    }
                });
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

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            throw new NotImplementedException();
        }
    }
}