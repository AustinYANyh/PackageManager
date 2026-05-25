using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using PackageManager.Features.Notifications.Models;
using PackageManager.Features.Notifications.Services;
using PackageManager.Models;
using PackageManager.Services;

namespace PackageManager.Features.Dashboard.ViewModels
{
    /// <summary>
    /// 仪表盘视图模型，聚合概览统计和最近活动数据。
    /// </summary>
    public sealed class DashboardViewModel : INotifyPropertyChanged
    {
        private int _packagesUpdatedToday;
        private int _packagesPendingUpdate;
        private string _latestPackageInfo;
        private int _todayNotificationCount;
        private int _unreadNotificationCount;
        private int _lanPeersOnline;
        private int _lanActiveTransfers;

        public DashboardViewModel()
        {
            RecentActivities = new ObservableCollection<NotificationItem>();
            RefreshCommand = new RelayCommand(Refresh);
        }

        public int PackagesUpdatedToday
        {
            get => _packagesUpdatedToday;
            set { if (_packagesUpdatedToday != value) { _packagesUpdatedToday = value; OnPropertyChanged(); } }
        }

        public int PackagesPendingUpdate
        {
            get => _packagesPendingUpdate;
            set { if (_packagesPendingUpdate != value) { _packagesPendingUpdate = value; OnPropertyChanged(); } }
        }

        public string LatestPackageInfo
        {
            get => _latestPackageInfo;
            set { if (_latestPackageInfo != value) { _latestPackageInfo = value; OnPropertyChanged(); } }
        }

        public int TodayNotificationCount
        {
            get => _todayNotificationCount;
            set { if (_todayNotificationCount != value) { _todayNotificationCount = value; OnPropertyChanged(); } }
        }

        public int UnreadNotificationCount
        {
            get => _unreadNotificationCount;
            set { if (_unreadNotificationCount != value) { _unreadNotificationCount = value; OnPropertyChanged(); } }
        }

        public int LanPeersOnline
        {
            get => _lanPeersOnline;
            set { if (_lanPeersOnline != value) { _lanPeersOnline = value; OnPropertyChanged(); } }
        }

        public int LanActiveTransfers
        {
            get => _lanActiveTransfers;
            set { if (_lanActiveTransfers != value) { _lanActiveTransfers = value; OnPropertyChanged(); } }
        }

        public ObservableCollection<NotificationItem> RecentActivities { get; }

        public ICommand RefreshCommand { get; }

        /// <summary>
        /// 刷新所有概览数据。在 DashboardPage.Loaded 时调用。
        /// </summary>
        public void Refresh()
        {
            RefreshPackageStats();
            RefreshNotificationStats();
            RefreshLanStats();
            RefreshRecentActivities();
        }

        private void RefreshPackageStats()
        {
            try
            {
                var mainWindow = Application.Current?.MainWindow as MainWindow;
                var packages = mainWindow?.Packages;
                if (packages == null) return;

                var completed = packages.Count(p => p.Status == PackageStatus.Completed);
                var busy = packages.Count(p =>
                    p.Status == PackageStatus.Downloading ||
                    p.Status == PackageStatus.Extracting ||
                    p.Status == PackageStatus.VerifyingSignature ||
                    p.Status == PackageStatus.VerifyingEncryption);

                PackagesUpdatedToday = completed;
                PackagesPendingUpdate = busy;

                var latest = packages
                    .Where(p => p.Status == PackageStatus.Completed)
                    .FirstOrDefault();
                LatestPackageInfo = latest != null
                    ? $"{latest.ProductName} {latest.Version}"
                    : null;
            }
            catch
            {
                // 包数据不可用时静默
            }
        }

        private void RefreshNotificationStats()
        {
            try
            {
                var service = ServiceLocator.Resolve<NotificationService>();
                if (service == null) return;

                TodayNotificationCount = service.GetTodayCount();
                UnreadNotificationCount = service.UnreadCount;
            }
            catch
            {
                // ignore
            }
        }

        private void RefreshLanStats()
        {
            try
            {
                var lanService = ServiceLocator.Resolve<LanTransferService>();
                if (lanService == null) return;

                LanPeersOnline = lanService.Peers.Count(p => p.IsOnline);
                LanActiveTransfers = lanService.ActiveTransfers.Count;
            }
            catch
            {
                // ignore
            }
        }

        private void RefreshRecentActivities()
        {
            try
            {
                var service = ServiceLocator.Resolve<NotificationService>();
                if (service == null) return;

                RecentActivities.Clear();
                foreach (var item in service.Notifications.Take(5))
                {
                    RecentActivities.Add(item);
                }
            }
            catch
            {
                // ignore
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
