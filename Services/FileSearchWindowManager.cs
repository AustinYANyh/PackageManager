using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using MftScanner;

namespace PackageManager.Services
{
    internal sealed class FileSearchWindowManager
    {
        private const int ShowRequestRetryCount = 15;
        private const int ShowRequestRetryDelayMilliseconds = 100;
        private readonly string _sessionId = SharedIndexConstants.SearchUiSessionId;
        private readonly string _showRequestEventName;

        public FileSearchWindowManager()
        {
            _showRequestEventName = BuildShowRequestEventName(_sessionId);
        }

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

            if (IndexHostTaskService.TryRunRegisteredTaskSilently())
            {
                for (var i = 0; i < 20; i++)
                {
                    Thread.Sleep(150);
                    if (SharedIndexServiceClient.TryShowSearchUi())
                    {
                        return;
                    }
                }
            }

            if (TrySignalShowRequest())
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

        private bool TrySignalShowRequest(int retryCount = 1, int retryDelayMilliseconds = 0)
        {
            for (var attempt = 0; attempt < retryCount; attempt++)
            {
                try
                {
                    using (var showEvent = EventWaitHandle.OpenExisting(_showRequestEventName))
                    {
                        var signaled = showEvent.Set();
                        if (signaled)
                        {
                            return true;
                        }
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    if (attempt + 1 >= retryCount)
                    {
                        return false;
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    LoggingService.LogWarning($"文件搜索 UI 唤醒事件访问被拒绝：SessionId={_sessionId}，{ex.Message}");
                    return false;
                }
                catch (Exception ex)
                {
                    LoggingService.LogWarning($"文件搜索 UI 唤醒事件发送失败：SessionId={_sessionId}，{ex.Message}");
                    return false;
                }

                if (retryDelayMilliseconds > 0)
                {
                    Thread.Sleep(retryDelayMilliseconds);
                }
            }

            return false;
        }

        private static string BuildShowRequestEventName(string sessionId)
        {
            return "PackageManager.MftScanner.Show." + NormalizeSessionId(sessionId);
        }

        private static string NormalizeSessionId(string sessionId)
        {
            return string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId.Trim();
        }
    }
}
