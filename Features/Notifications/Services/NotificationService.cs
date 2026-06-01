using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using PackageManager.Features.Notifications.Models;
using PackageManager.Services;
using Newtonsoft.Json;

namespace PackageManager.Features.Notifications.Services
{
    /// <summary>
    /// 通知服务，维护最近通知列表，为通知面板和仪表盘提供数据源。
    /// </summary>
    public sealed class NotificationService : INotifyPropertyChanged
    {
        private const int MaxNotifications = 200;
        private readonly string _storagePath;
        private int _unreadCount;

        public NotificationService(DataPersistenceService dataPersistenceService = null)
        {
            Notifications = new ObservableCollection<NotificationItem>();
            var dataService = dataPersistenceService ?? ServiceLocator.Resolve<DataPersistenceService>() ?? new DataPersistenceService();
            _storagePath = Path.Combine(dataService.GetDataFolderPath(), "notifications.json");
            LoadNotifications();
        }

        /// <summary>
        /// 所有通知列表（按时间倒序），UI 直接绑定此集合。
        /// </summary>
        public ObservableCollection<NotificationItem> Notifications { get; }

        /// <summary>
        /// 未读通知计数，用于驱动铃铛徽章。
        /// </summary>
        public int UnreadCount
        {
            get => _unreadCount;
            private set
            {
                if (_unreadCount == value) return;
                _unreadCount = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 推送一条新通知。线程安全，可从任意线程调用。
        /// </summary>
        public void Push(string title, string message, NotificationLevel level,
            string navigationTarget = null)
        {
            var item = new NotificationItem
            {
                Title = title,
                Message = message,
                Level = level,
                NavigationTarget = navigationTarget
            };

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (dispatcher.CheckAccess())
            {
                InsertNotification(item);
            }
            else
            {
                dispatcher.BeginInvoke(new Action(() => InsertNotification(item)));
            }
        }

        /// <summary>
        /// 标记指定通知为已读。
        /// </summary>
        public void MarkAsRead(string id)
        {
            var item = Notifications.FirstOrDefault(n => n.Id == id);
            if (item != null && !item.IsRead)
            {
                item.IsRead = true;
                RefreshUnreadCount();
                SaveNotifications();
            }
        }

        /// <summary>
        /// 标记所有通知为已读。
        /// </summary>
        public void MarkAllAsRead()
        {
            foreach (var item in Notifications)
            {
                item.IsRead = true;
            }

            RefreshUnreadCount();
            SaveNotifications();
        }

        /// <summary>
        /// 清除所有通知。
        /// </summary>
        public void Clear()
        {
            Notifications.Clear();
            RefreshUnreadCount();
            SaveNotifications();
        }

        /// <summary>
        /// 获取今日通知计数。
        /// </summary>
        public int GetTodayCount()
        {
            var today = DateTime.Now.Date;
            return Notifications.Count(n => n.Timestamp.Date == today);
        }

        private void InsertNotification(NotificationItem item)
        {
            Notifications.Insert(0, item);
            TrimNotifications();
            RefreshUnreadCount();
            SaveNotifications();
        }

        private void RefreshUnreadCount()
        {
            UnreadCount = Notifications.Count(n => !n.IsRead);
        }

        private void LoadNotifications()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_storagePath) || !File.Exists(_storagePath))
                {
                    RefreshUnreadCount();
                    return;
                }

                var json = File.ReadAllText(_storagePath);
                var items = JsonConvert.DeserializeObject<List<NotificationItem>>(json) ?? new List<NotificationItem>();
                foreach (var item in items
                             .Where(IsValidNotification)
                             .OrderByDescending(item => item.Timestamp)
                             .Take(MaxNotifications))
                {
                    Notifications.Add(item);
                }

                RefreshUnreadCount();
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "加载通知中心历史失败");
                Notifications.Clear();
                RefreshUnreadCount();
            }
        }

        private void SaveNotifications()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_storagePath))
                {
                    return;
                }

                var directory = Path.GetDirectoryName(_storagePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var items = Notifications
                    .OrderByDescending(item => item.Timestamp)
                    .Take(MaxNotifications)
                    .ToList();
                var json = JsonConvert.SerializeObject(items, Formatting.Indented);
                File.WriteAllText(_storagePath, json);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "保存通知中心历史失败");
            }
        }

        private void TrimNotifications()
        {
            while (Notifications.Count > MaxNotifications)
            {
                Notifications.RemoveAt(Notifications.Count - 1);
            }
        }

        private static bool IsValidNotification(NotificationItem item)
        {
            return item != null &&
                   !string.IsNullOrWhiteSpace(item.Id) &&
                   !string.IsNullOrWhiteSpace(item.Title);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
