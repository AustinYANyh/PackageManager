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
        private const string ShowRequestEventName = "PackageManager.CommonStartupTool.Show";
        private const int ShowRequestRetryCount = 15;
        private const int ShowRequestRetryDelayMilliseconds = 100;

        public void ShowOrActivate()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(ShowOrActivate));
                return;
            }

            if (TrySignalShowRequest())
            {
                return;
            }

            var existingProcess = FindRunningToolProcess();
            if (existingProcess != null)
            {
                try
                {
                    LoggingService.LogInfo("常用启动项工具进程已存在，等待唤醒事件就绪。");
                    if (TrySignalShowRequest(ShowRequestRetryCount, ShowRequestRetryDelayMilliseconds))
                    {
                        return;
                    }

                    LoggingService.LogWarning("常用启动项工具唤醒事件未就绪，回退到窗口置前。");
                    BringToFront(existingProcess);
                    if (TrySignalShowRequest(5, ShowRequestRetryDelayMilliseconds))
                    {
                        return;
                    }
                }
                finally
                {
                    existingProcess.Dispose();
                }

                return;
            }

            var existingWindowHandle = FindToolWindowHandle();
            if (existingWindowHandle != IntPtr.Zero)
            {
                LoggingService.LogInfo("检测到常用启动项窗口句柄，尝试直接置前。");
                BringToFront(existingWindowHandle);
                return;
            }

            try
            {
                var exePath = AdminElevationService.ExtractEmbeddedTool("CommonStartupTool.exe", "CommonStartupTool.exe");
                if (string.IsNullOrEmpty(exePath))
                {
                    MessageBox.Show("未找到 CommonStartupTool.exe 工具，请检查安装。", "常用启动项", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--owner-pid {Process.GetCurrentProcess().Id}",
                    UseShellExecute = true
                });
                LoggingService.LogInfo($"已启动常用启动项工具：{exePath}");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "打开常用启动项工具失败");
                MessageBox.Show($"打开常用启动项工具失败：{ex.Message}", "常用启动项", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void Shutdown()
        {
            var process = FindRunningToolProcess();
            if (process == null)
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

        private static Process FindRunningToolProcess()
        {
            try
            {
                foreach (var process in Process.GetProcessesByName("CommonStartupTool"))
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            return process;
                        }
                    }
                    catch
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool TrySignalShowRequest(int retryCount = 1, int retryDelayMilliseconds = 0)
        {
            for (var attempt = 0; attempt < retryCount; attempt++)
            {
                try
                {
                    using (var showEvent = EventWaitHandle.OpenExisting(ShowRequestEventName))
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
                    LoggingService.LogWarning($"常用启动项唤醒事件访问被拒绝：{ex.Message}");
                    return false;
                }
                catch (Exception ex)
                {
                    LoggingService.LogWarning($"常用启动项唤醒事件发送失败：{ex.Message}");
                    return false;
                }

                if (retryDelayMilliseconds > 0)
                {
                    Thread.Sleep(retryDelayMilliseconds);
                }
            }

            return false;
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

                    System.Threading.Thread.Sleep(100);
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

        private static IntPtr FindToolWindowHandle()
        {
            try
            {
                return FindWindow(null, "常用启动项");
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private const int SwShow = 5;
        private const int SwRestore = 9;
        private const int WmClose = 0x0010;

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

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
