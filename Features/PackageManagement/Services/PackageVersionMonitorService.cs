using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PackageManager.Models;

namespace PackageManager.Services
{
    /// <summary>
    /// 后台轮询服务，定期检测 FTP 服务器上各包是否有新版本。
    /// </summary>
    public sealed class PackageVersionMonitorService : INotifyPropertyChanged
    {
        private readonly FtpService _ftpService;
        private readonly DispatcherTimer _timer;
        private readonly Dictionary<string, string> _knownLatestVersions = new Dictionary<string, string>();

        private int _newVersionCount;
        private DateTime _lastCheckTime;
        private bool _isChecking;
        private bool _isFirstCheck = true;

        public PackageVersionMonitorService(FtpService ftpService)
        {
            _ftpService = ftpService;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _timer.Tick += async (s, e) => await CheckNowAsync();
        }

        public event Action VersionsChanged;
        public event PropertyChangedEventHandler PropertyChanged;

        public int NewVersionCount
        {
            get => _newVersionCount;
            private set { if (_newVersionCount != value) { _newVersionCount = value; OnPropertyChanged(); } }
        }

        public DateTime LastCheckTime
        {
            get => _lastCheckTime;
            private set { _lastCheckTime = value; OnPropertyChanged(); }
        }

        public bool IsChecking
        {
            get => _isChecking;
            private set { if (_isChecking != value) { _isChecking = value; OnPropertyChanged(); } }
        }

        public void Start()
        {
            _timer.Start();
            _ = CheckNowAsync();
        }

        public void Stop()
        {
            _timer.Stop();
        }

        public async Task CheckNowAsync()
        {
            if (IsChecking) return;

            var mainWindow = Application.Current?.MainWindow as MainWindow;
            var packages = mainWindow?.Packages;
            if (packages == null || packages.Count == 0) return;

            IsChecking = true;

            try
            {
                var tasks = packages.Select(async pkg =>
                {
                    try
                    {
                        var versions = await _ftpService.GetDirectoriesAsync(pkg.FtpServerPath);
                        var latestVersion = versions.Count > 0 ? versions.Last() : null;
                        string latestTime = null;
                        if (!string.IsNullOrEmpty(latestVersion))
                        {
                            try
                            {
                                var path = pkg.FtpServerPath.TrimEnd('/') + "/" + latestVersion + "/";
                                var files = await _ftpService.GetFilesAsync(path);
                                var lastFile = files.LastOrDefault();
                                if (!string.IsNullOrEmpty(lastFile))
                                {
                                    var t = FtpService.ParseTimeFromFileName(lastFile);
                                    latestTime = t != DateTime.MinValue ? t.ToString("yyyy-MM-dd HH:mm") : "";
                                }
                            }
                            catch { }
                        }
                        return (pkg.ProductName, LatestVersion: latestVersion, LatestTime: latestTime, pkg);
                    }
                    catch
                    {
                        return (pkg.ProductName, LatestVersion: (string)null, LatestTime: (string)null, pkg);
                    }
                }).ToArray();

                var results = await Task.WhenAll(tasks);

                int newCount = 0;

                foreach (var (productName, latestVersion, latestTime, pkg) in results)
                {
                    if (string.IsNullOrEmpty(latestVersion)) continue;

                    if (latestTime != null)
                        pkg.LatestServerTime = latestTime;
                    pkg.LatestServerVersion = latestVersion;

                    if (!_isFirstCheck && _knownLatestVersions.TryGetValue(productName, out var known) && known != latestVersion)
                    {
                        ToastService.ShowToast("新版本", $"{productName} 有新版本 {latestVersion}", "Info");
                    }

                    _knownLatestVersions[productName] = latestVersion;

                    if (!string.IsNullOrEmpty(pkg.Version) &&
                        !string.Equals(pkg.Version, latestVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        newCount++;
                    }
                }

                _isFirstCheck = false;

                NewVersionCount = newCount;
                LastCheckTime = DateTime.Now;

                VersionsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"版本检测失败: {ex.Message}");
            }
            finally
            {
                IsChecking = false;
            }
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
