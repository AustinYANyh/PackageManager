using System;
using System.Windows;
using MftScanner;

namespace PackageManager.Services
{
    internal sealed class FileSearchWindowManager
    {
        private readonly string _sessionId = SharedIndexConstants.SearchUiSessionId;

        /// <summary>
        /// 显示或激活文件搜索窗口；若索引宿主未运行则尝试静默启动。
        /// </summary>
        public void ShowOrActivate()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(ShowOrActivate));
                return;
            }

            if (SharedIndexServiceClient.TryShowSearchUi())
            {
                return;
            }

            if (IndexHostTaskService.TryRunRegisteredTaskSilently()
                && SharedIndexServiceClient.TryWaitForHostAvailability(5000)
                && SharedIndexServiceClient.TryShowSearchUi())
            {
                return;
            }

            LoggingService.LogWarning($"文件搜索 UI 唤起失败：共享索引宿主未在规定时间内就绪，已禁止本地直启兜底。SessionId={_sessionId}");
        }

        /// <summary>
        /// 关闭文件搜索窗口（当前实现为空操作）。
        /// </summary>
        public void Shutdown()
        {
        }
    }
}
