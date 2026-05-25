using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using PackageManager.Features.Notifications.Services;
using PackageManager.Models;
using PackageManager.Services;

namespace PackageManager.Features.Dashboard.ViewModels
{
    public sealed class DashboardViewModel : INotifyPropertyChanged
    {
        private int _newVersionCount;
        private int _packagesPendingUpdate;
        private int _todayNotificationCount;
        private int _unreadNotificationCount;
        private int _lanPeersOnline;
        private int _lanActiveTransfers;

        public DashboardViewModel()
        {
            RefreshCommand = new RelayCommand(Refresh);

            var monitor = ServiceLocator.Resolve<PackageVersionMonitorService>();
            if (monitor != null)
            {
                monitor.VersionsChanged += () =>
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(RefreshPackageStats));
            }

            var notifService = ServiceLocator.Resolve<NotificationService>();
            if (notifService != null)
            {
                notifService.Notifications.CollectionChanged += (s, e) =>
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        RefreshNotificationStats();
                        RefreshPackageStats();
                    }));
            }
        }

        public int NewVersionCount
        {
            get => _newVersionCount;
            set
            {
                if (_newVersionCount != value)
                {
                    _newVersionCount = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasNewVersions));
                }
            }
        }

        public bool HasNewVersions => _newVersionCount > 0;

        public int PackagesPendingUpdate
        {
            get => _packagesPendingUpdate;
            set { if (_packagesPendingUpdate != value) { _packagesPendingUpdate = value; OnPropertyChanged(); } }
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

        public ICommand RefreshCommand { get; }

        public void Refresh()
        {
            RefreshPackageStats();
            RefreshNotificationStats();
            RefreshLanStats();
        }

        private void RefreshPackageStats()
        {
            try
            {
                var monitor = ServiceLocator.Resolve<PackageVersionMonitorService>();
                var mainWindow = Application.Current?.MainWindow as MainWindow;
                var packages = mainWindow?.Packages;

                NewVersionCount = monitor?.NewVersionCount ?? 0;

                if (packages != null)
                {
                    PackagesPendingUpdate = packages.Count(p =>
                        p.Status == PackageStatus.Downloading ||
                        p.Status == PackageStatus.Extracting ||
                        p.Status == PackageStatus.VerifyingSignature ||
                        p.Status == PackageStatus.VerifyingEncryption);
                }
            }
            catch { }
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
            catch { }
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
            catch { }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
