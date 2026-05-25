using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using PackageManager.Features.Notifications.Models;

namespace PackageManager.Features.Notifications.Services
{
    /// <summary>
    /// 通知服务，维护内存中最近 50 条通知的列表，为通知面板和仪表盘提供数据源。
    /// </summary>
    public sealed class NotificationService : INotifyPropertyChanged
    {
        private const int MaxNotifications = 50;
        private int _unreadCount;

        public NotificationService()
        {
            Notifications = new ObservableCollection<NotificationItem>();
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
        }

        /// <summary>
        /// 清除所有通知。
        /// </summary>
        public void Clear()
        {
            Notifications.Clear();
            RefreshUnreadCount();
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

            while (Notifications.Count > MaxNotifications)
            {
                var removed = Notifications[Notifications.Count - 1];
                Notifications.RemoveAt(Notifications.Count - 1);
            }

            RefreshUnreadCount();
        }

        private void RefreshUnreadCount()
        {
            UnreadCount = Notifications.Count(n => !n.IsRead);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
