using System;
using System.ComponentModel;
using System.Diagnostics;
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

            StartNewToolProcess();
        }

        public void Shutdown()
        {
        }

        private void StartNewToolProcess()
        {
            try
            {
                var exePath = AdminElevationService.ExtractEmbeddedTool("MftScanner.exe", "MftScanner.exe");
                if (string.IsNullOrEmpty(exePath))
                {
                    MessageBox.Show("未找到 MftScanner.exe 工具，请检查安装。", "文件搜索", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--window search-ui --session-id {_sessionId}",
                    UseShellExecute = true
                }))
                {
                    LoggingService.LogWarning($"文件搜索 UI 进入最后兜底启动路径：SessionId={_sessionId}");
                    LoggingService.LogInfo($"已启动文件搜索 UI：SessionId={_sessionId}，PID={process?.Id}");
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "打开文件搜索工具失败");
                MessageBox.Show($"打开文件搜索工具失败：{ex.Message}", "文件搜索", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
