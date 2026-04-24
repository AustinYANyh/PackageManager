using System;
using System.Windows;
using MftScanner;

namespace PackageManager.Services
{
    internal sealed class FileSearchWindowManager
    {
        private readonly string _sessionId = SharedIndexConstants.SearchUiSessionId;

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
                && SharedIndexServiceClient.TryWaitForHostAvailability(15000)
                && SharedIndexServiceClient.TryShowSearchUi())
            {
                return;
            }

            LoggingService.LogWarning($"文件搜索 UI 唤起失败：共享索引宿主未在规定时间内就绪，已禁止本地直启兜底。SessionId={_sessionId}");
        }

        public void Shutdown()
        {
        }
    }
}
