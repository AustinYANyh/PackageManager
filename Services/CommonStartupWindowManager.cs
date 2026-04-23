using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace PackageManager.Services
{
    internal sealed class CommonStartupWindowManager
    {
        private const int ShowRequestRetryCount = 15;
        private const int ShowRequestRetryDelayMilliseconds = 100;
        private const int SwShow = 5;
        private const int SwRestore = 9;
        private const int WmClose = 0x0010;

        private readonly string _sessionId = Guid.NewGuid().ToString("N");
        private readonly string _showRequestEventName;
        private int? _currentToolProcessId;

        public CommonStartupWindowManager()
        {
            _showRequestEventName = BuildShowRequestEventName(_sessionId);
        }

        public void ShowOrActivate()
        {
            if (TrySignalShowRequest())
            {
                return;
            }

            if (TryGetCurrentToolProcess(out var existingProcess))
            {
                try
                {
                    LoggingService.LogInfo($"当前会话启动项进程已存在，SessionId={_sessionId}，PID={existingProcess.Id}。");
                    if (TrySignalShowRequest(ShowRequestRetryCount, ShowRequestRetryDelayMilliseconds))
                    {
                        return;
                    }

                    LoggingService.LogWarning($"当前会话启动项唤醒事件未就绪，尝试窗口置前。SessionId={_sessionId}，PID={existingProcess.Id}");
                    BringToFront(existingProcess);
                }
                finally
                {
                    existingProcess.Dispose();
                }

                return;
            }

            StartNewToolProcess();
        }

        public void Shutdown()
        {
            if (!TryGetCurrentToolProcess(out var process))
            {
                return;
            }

            try
            {
                process.Refresh();
                var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        private void StartNewToolProcess()
        {
            try
            {
                var exePath = AdminElevationService.ExtractEmbeddedTool("CommonStartupTool.exe", "CommonStartupTool.exe");
                if (string.IsNullOrEmpty(exePath))
                {
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                        MessageBox.Show("未找到 CommonStartupTool.exe 工具，请检查安装。", "常用启动项", MessageBoxButton.OK, MessageBoxImage.Error)));
                    return;
                }

                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--session-id {_sessionId} --owner-pid {Process.GetCurrentProcess().Id}",
                    UseShellExecute = true
                }))
                {
                    _currentToolProcessId = process?.Id;
                }

                LoggingService.LogInfo($"已启动当前会话常用启动项工具：SessionId={_sessionId}，PID={_currentToolProcessId}");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "打开常用启动项工具失败");
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    MessageBox.Show($"打开常用启动项工具失败：{ex.Message}", "常用启动项", MessageBoxButton.OK, MessageBoxImage.Error)));
            }
        }

        private bool TryGetCurrentToolProcess(out Process process)
        {
            process = null;
            if (!_currentToolProcessId.HasValue)
            {
                return false;
            }

            try
            {
                process = Process.GetProcessById(_currentToolProcessId.Value);
                if (process.HasExited)
                {
                    process.Dispose();
                    process = null;
                    _currentToolProcessId = null;
                    return false;
                }

                return true;
            }
            catch
            {
                process?.Dispose();
                process = null;
                _currentToolProcessId = null;
                return false;
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
                    LoggingService.LogWarning($"当前会话启动项唤醒事件访问被拒绝：SessionId={_sessionId}，{ex.Message}");
                    return false;
                }
                catch (Exception ex)
                {
                    LoggingService.LogWarning($"当前会话启动项唤醒事件发送失败：SessionId={_sessionId}，{ex.Message}");
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
            return "PackageManager.CommonStartupTool.Show." + NormalizeSessionId(sessionId);
        }

        private static string NormalizeSessionId(string sessionId)
        {
            return string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId.Trim();
        }

        private static void BringToFront(Process process)
        {
            if (process == null)
            {
                return;
            }

            IntPtr handle = IntPtr.Zero;
            try
            {
                for (var i = 0; i < 10; i++)
                {
                    process.Refresh();
                    if (process.HasExited)
                    {
                        return;
                    }

                    handle = process.MainWindowHandle;
                    if (handle != IntPtr.Zero)
                    {
                        break;
                    }

                    Thread.Sleep(100);
                }
            }
            catch
            {
                return;
            }

            if (handle == IntPtr.Zero)
            {
                return;
            }

            BringToFront(handle);
        }

        private static void BringToFront(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            if (IsIconic(handle))
            {
                ShowWindow(handle, SwRestore);
            }
            else
            {
                ShowWindow(handle, SwShow);
            }

            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
