using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace PackageManager.Views
{
    /// <summary>
    /// 常用链接页面，展示并可打开预配置的网址列表。
    /// </summary>
    public partial class CommonLinksPage : Page, ICentralPage
    {
        /// <summary>
        /// 获取常用链接项的集合。
        /// </summary>
        public ObservableCollection<PackageManager.CommonLinkItem> Links { get; }

        /// <summary>
        /// 请求退出当前页面的导航事件。
        /// </summary>
        public event Action RequestExit;

        /// <summary>
        /// 初始化 <see cref="CommonLinksPage"/> 的新实例。
        /// </summary>
        /// <param name="links">要展示的常用链接集合，为 null 时使用空集合。</param>
        public CommonLinksPage(ObservableCollection<PackageManager.CommonLinkItem> links)
        {
            InitializeComponent();
            Links = links ?? new ObservableCollection<PackageManager.CommonLinkItem>();
            DataContext = this;
        }

        private void OpenLink_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is PackageManager.CommonLinkItem item)
            {
                OpenUrl(item.Url);
            }
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
                    UseShellExecute = true
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
                    PackageManager.Services.LoggingService.LogError(ex, "打开网址失败");
                    MessageBox.Show($"打开网址失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            RequestExit?.Invoke();
        }
    }
}
