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
        /// <param name="title">提示标题。</param>
        /// <param name="message">提示消息内容。</param>
        /// <param name="level">提示级别（如 "Info"、"Success"、"Warning"、"Error"），默认为 "Info"。</param>
        /// <param name="durationMs">提示显示时长（毫秒），默认为 3000。</param>
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