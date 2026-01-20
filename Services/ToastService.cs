using System;
using System.Windows;
using System.Windows.Media;
using CustomControlLibrary.CustomControl.Controls.Notification;
using CustomControlLibrary.CustomControl.Helper;

namespace PackageManager.Services
{
    public static class ToastService
    {
        /// <summary>
        /// 使用 CustomControlLibrary 中的 ToastNotifier 弹出提示。
        /// </summary>
        public static void ShowToast(string title, string message, string level = "Info", int durationMs = 3000)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    return;
                }

                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        CToastNotifier.Show(message, ToastPosition.TopRight, 5000,new SolidColorBrush(Color.FromRgb(9,150,136)),true);
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogWarning($"ToastNotifier 显示失败：{ex.Message}");
                    }
                }));
            }
            catch (Exception exOuter)
            {
                LoggingService.LogWarning($"ToastNotifier 调用失败（外部）：{exOuter.Message}");
            }
        }

    }
}