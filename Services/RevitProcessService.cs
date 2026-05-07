using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Management;

namespace PackageManager.Services
{
    internal sealed class RevitProcessService
    {
        private const int SwRestore = 9;
        private const int ProcessQueryLimitedInformation = 0x1000;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// 查找所有使用指定可执行文件的 Revit 进程。
        /// </summary>
        /// <param name="executablePath">Revit 可执行文件的完整路径。</param>
        /// <returns>匹配的进程信息列表。</returns>
        public IReadOnlyList<RevitProcessInfo> FindProcessesForExecutable(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                return Array.Empty<RevitProcessInfo>();

            string normalizedTargetPath;
            try
            {
                normalizedTargetPath = Path.GetFullPath(executablePath);
            }
            catch
            {
                normalizedTargetPath = executablePath;
            }

            var processName = Path.GetFileNameWithoutExtension(normalizedTargetPath);
            if (string.IsNullOrWhiteSpace(processName))
                return Array.Empty<RevitProcessInfo>();

            var matches = new List<RevitProcessInfo>();
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var processPath = TryGetExecutablePath(process);
                        if (string.IsNullOrWhiteSpace(processPath)
                            || !string.Equals(processPath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        matches.Add(new RevitProcessInfo
                        {
                            ProcessId = process.Id,
                            ExecutablePath = processPath,
                            IsResponding = TryGetResponding(process),
                            MainWindowHandle = TryGetMainWindowHandle(process),
                            StartTime = TryGetStartTime(process)
                        });
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogWarning($"读取 Revit 进程信息失败：Pid={process.Id} | {ex.Message}");
                    }
                }
            }

            return matches
                .OrderByDescending(info => info.StartTime ?? DateTime.MinValue)
                .ThenByDescending(info => info.ProcessId)
                .ToList();
        }

        /// <summary>
        /// 尝试将指定 Revit 进程的窗口激活到前台。
        /// </summary>
        /// <param name="processInfo">目标进程信息。</param>
        /// <returns>激活成功返回 true，否则 false。</returns>
        public bool TryActivateProcess(RevitProcessInfo processInfo)
        {
            if (processInfo == null || processInfo.MainWindowHandle == IntPtr.Zero)
                return false;

            try
            {
                if (IsIconic(processInfo.MainWindowHandle))
                    ShowWindowAsync(processInfo.MainWindowHandle, SwRestore);

                return SetForegroundWindow(processInfo.MainWindowHandle);
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"激活 Revit 窗口失败：Pid={processInfo.ProcessId} | {ex.Message}");
                return false;
            }
        }

        private static string TryGetExecutablePath(Process process)
        {
            try
            {
                var processId = process?.Id ?? 0;
                if (processId <= 0)
                    return null;

                var path = TryGetExecutablePathByHandle(processId);
                if (!string.IsNullOrWhiteSpace(path))
                    return path;

                path = TryGetExecutablePathByWmi(processId);
                if (!string.IsNullOrWhiteSpace(path))
                    return path;

                return null;
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"读取进程路径失败：Pid={process?.Id} | {ex.Message}");
                return null;
            }
        }

        private static string TryGetExecutablePathByHandle(int processId)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
                if (handle == IntPtr.Zero)
                    return null;

                var capacity = 1024;
                var builder = new StringBuilder(capacity);
                if (!QueryFullProcessImageName(handle, 0, builder, ref capacity) || capacity <= 0)
                    return null;

                return builder.ToString(0, capacity);
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"通过 QueryFullProcessImageName 读取进程路径失败：Pid={processId} | {ex.Message}");
                return null;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                    CloseHandle(handle);
            }
        }

        private static string TryGetExecutablePathByWmi(int processId)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                           $"SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = {processId}"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject process in results)
                    {
                        return process["ExecutablePath"]?.ToString();
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"通过 WMI 读取进程路径失败：Pid={processId} | {ex.Message}");
                return null;
            }
        }

        private static bool TryGetResponding(Process process)
        {
            try
            {
                return process != null && process.Responding;
            }
            catch
            {
                return true;
            }
        }

        private static IntPtr TryGetMainWindowHandle(Process process)
        {
            try
            {
                return process?.MainWindowHandle ?? IntPtr.Zero;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static DateTime? TryGetStartTime(Process process)
        {
            try
            {
                return process?.StartTime;
            }
            catch
            {
                return null;
            }
        }
    }

    internal sealed class RevitProcessInfo
    {
        /// <summary>
        /// 获取或设置进程 ID。
        /// </summary>
        public int ProcessId { get; set; }

        /// <summary>
        /// 获取或设置可执行文件路径。
        /// </summary>
        public string ExecutablePath { get; set; }

        /// <summary>
        /// 获取或设置进程是否正在响应。
        /// </summary>
        public bool IsResponding { get; set; }

        /// <summary>
        /// 获取或设置主窗口句柄。
        /// </summary>
        public IntPtr MainWindowHandle { get; set; }

        /// <summary>
        /// 获取或设置进程启动时间。
        /// </summary>
        public DateTime? StartTime { get; set; }

        /// <summary>
        /// 获取是否存在主窗口。
        /// </summary>
        public bool HasMainWindow => MainWindowHandle != IntPtr.Zero;
    }
}
