using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PackageManager.Features.Notifications.Models;
using PackageManager.Features.Notifications.Services;
using PackageManager.Services;
using PackageManager.Shell;

namespace PackageManager.Features.Notifications.Views
{
    public partial class NotificationPanel : UserControl
    {
        private NotificationService _service;

        public NotificationPanel()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _service = ServiceLocator.Resolve<NotificationService>();
            if (_service == null) return;

            DataContext = _service;
            _service.Notifications.CollectionChanged += OnNotificationsChanged;
            UpdateEmptyState();
        }

        private void OnNotificationsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            if (_service == null) return;
            EmptyState.Visibility = _service.Notifications.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void MarkAllAsRead_Click(object sender, MouseButtonEventArgs e)
        {
            _service?.MarkAllAsRead();
        }

        private void ClearAll_Click(object sender, MouseButtonEventArgs e)
        {
            _service?.Clear();
        }

        private void NotificationItem_Click(object sender, MouseButtonEventArgs e)
        {
            var border = sender as FrameworkElement;
            var item = border?.Tag as NotificationItem;
            if (item == null) return;

            _service?.MarkAsRead(item.Id);

            if (!string.IsNullOrEmpty(item.NavigationTarget))
            {
                var navService = ServiceLocator.Resolve<NavigationService>();
                navService?.NavigateTo(item.NavigationTarget);

                CloseParentPopup();
            }
        }

        private void CloseParentPopup()
        {
            var popup = Parent as System.Windows.Controls.Primitives.Popup;
            if (popup != null)
            {
                popup.IsOpen = false;
            }
        }
    }
}
