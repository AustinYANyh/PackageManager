using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PackageManager.Features.Notifications.Models
{
    /// <summary>
    /// 通知级别。
    /// </summary>
    public enum NotificationLevel
    {
        Info,
        Success,
        Warning,
        Error
    }

    /// <summary>
    /// 通知中心的单条通知项。
    /// </summary>
    public sealed class NotificationItem : INotifyPropertyChanged
    {
        private bool _isRead;

        public NotificationItem()
        {
            Id = Guid.NewGuid().ToString("N");
            Timestamp = DateTime.Now;
        }

        public string Id { get; set; }

        public string Title { get; set; }

        public string Message { get; set; }

        public NotificationLevel Level { get; set; }

        public DateTime Timestamp { get; set; }

        /// <summary>
        /// 点击通知后导航到的目标 Key（可空）。
        /// </summary>
        public string NavigationTarget { get; set; }

        public bool IsRead
        {
            get => _isRead;
            set
            {
                if (_isRead == value) return;
                _isRead = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 格式化的时间戳文本，用于 UI 显示。
        /// </summary>
        public string TimestampText
        {
            get
            {
                var now = DateTime.Now;
                if (Timestamp.Date == now.Date)
                    return Timestamp.ToString("HH:mm");
                if (Timestamp.Date == now.Date.AddDays(-1))
                    return "昨天 " + Timestamp.ToString("HH:mm");
                return Timestamp.ToString("MM/dd HH:mm");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
