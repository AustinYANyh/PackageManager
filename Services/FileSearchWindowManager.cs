using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace PackageManager.Services
{
    internal sealed class FileSearchWindowManager
    {
        private const int ShowRequestRetryCount = 15;
        private const int ShowRequestRetryDelayMilliseconds = 100;
        private const int SwShow = 5;
        private const int SwRestore = 9;
        private const int WmClose = 0x0010;

        private readonly string _sessionId = Guid.NewGuid().ToString("N");
        private readonly string _showRequestEventName;
        private int? _currentToolProcessId;

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

            if (TryGetCurrentToolProcess(out var existingProcess))
            {
                try
                {
                    LoggingService.LogDebug($"[CtrlE] 复用当前会话的文件搜索进程。会话={_sessionId}，PID={existingProcess.Id}");
                    AuthorizeForegroundForProcess(existingProcess.Id, "复用实例");
                    if (TrySignalShowRequest(ShowRequestRetryCount, ShowRequestRetryDelayMilliseconds))
                    {
                        EnsureForeground(existingProcess, "发送显示事件后");
                        return;
                    }

                    LoggingService.LogWarning($"当前会话文件搜索唤醒事件未就绪，尝试窗口置前。SessionId={_sessionId}，PID={existingProcess.Id}");
                    BringToFront(existingProcess, "显示事件未就绪");
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
                var exePath = AdminElevationService.ExtractEmbeddedTool("MftScanner.exe", "MftScanner.exe");
                if (string.IsNullOrEmpty(exePath))
                {
                    MessageBox.Show("未找到 MftScanner.exe 工具，请检查安装。", "文件搜索", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--window search --session-id {_sessionId} --owner-pid {Process.GetCurrentProcess().Id}",
                    UseShellExecute = true
                }))
                {
                    _currentToolProcessId = process?.Id;
                    if (process != null)
                    {
                        AuthorizeForegroundForProcess(process.Id, "启动新实例");
                    }
                }

                LoggingService.LogInfo($"已启动当前会话文件搜索工具：SessionId={_sessionId}，PID={_currentToolProcessId}");
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
                    LoggingService.LogWarning($"当前会话文件搜索唤醒事件访问被拒绝：SessionId={_sessionId}，{ex.Message}");
                    return false;
                }
                catch (Exception ex)
                {
                    LoggingService.LogWarning($"当前会话文件搜索唤醒事件发送失败：SessionId={_sessionId}，{ex.Message}");
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

        private void AuthorizeForegroundForProcess(int processId, string source)
        {
            if (processId <= 0)
            {
                return;
            }

            try
            {
                var allowed = AllowSetForegroundWindow(processId);
                LoggingService.LogDebug($"[CtrlE] 已调用前台授权。来源={source}，PID={processId}，结果={allowed}");
            }
            catch (Exception ex)
            {
                LoggingService.LogDebug($"[CtrlE] 前台授权调用失败。来源={source}，PID={processId}，原因={ex.Message}");
            }
        }

        private static void EnsureForeground(Process process, string source)
        {
            var handle = TryGetMainWindowHandle(process, 6, 50);
            if (handle == IntPtr.Zero)
            {
                LoggingService.LogDebug($"[CtrlE] 跳过前台校验，未获取到窗口句柄。来源={source}");
                return;
            }

            Thread.Sleep(80);
            if (IsForegroundWindow(handle))
            {
                LoggingService.LogDebug($"[CtrlE] 显示事件后窗口已在前台。来源={source}，句柄={handle}");
                return;
            }

            LoggingService.LogDebug($"[CtrlE] 显示事件后窗口仍不在前台，执行原生置前兜底。来源={source}，句柄={handle}");
            BringToFront(handle);
            Thread.Sleep(40);
            LoggingService.LogDebug($"[CtrlE] 原生置前兜底完成。来源={source}，句柄={handle}，当前是否前台={IsForegroundWindow(handle)}");
        }

        private static void BringToFront(Process process, string source)
        {
            if (process == null)
            {
                return;
            }

            var handle = TryGetMainWindowHandle(process, 10, 100);

            if (handle == IntPtr.Zero)
            {
                LoggingService.LogDebug($"[CtrlE] 跳过窗口置前，未获取到窗口句柄。来源={source}");
                return;
            }

            BringToFront(handle);
            LoggingService.LogDebug($"[CtrlE] 已执行窗口置前。来源={source}，句柄={handle}，当前是否前台={IsForegroundWindow(handle)}");
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

        private static IntPtr TryGetMainWindowHandle(Process process, int retryCount, int retryDelayMilliseconds)
        {
            if (process == null)
            {
                return IntPtr.Zero;
            }

            try
            {
                for (var i = 0; i < retryCount; i++)
                {
                    process.Refresh();
                    if (process.HasExited)
                    {
                        return IntPtr.Zero;
                    }

                    var handle = process.MainWindowHandle;
                    if (handle != IntPtr.Zero)
                    {
                        return handle;
                    }

                    if (retryDelayMilliseconds > 0)
                    {
                        Thread.Sleep(retryDelayMilliseconds);
                    }
                }
            }
            catch
            {
            }

            return IntPtr.Zero;
        }

        private static bool IsForegroundWindow(IntPtr handle)
        {
            return handle != IntPtr.Zero && GetForegroundWindow() == handle;
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
        private static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
