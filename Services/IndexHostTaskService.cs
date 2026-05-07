using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Management;
using MftScanner;

namespace PackageManager.Services
{
    internal static class IndexHostTaskService
    {
        private const int HostAvailabilityWaitMilliseconds = 15000;

        /// <summary>
        /// 确保索引宿主计划任务已注册并在运行；若未注册则创建任务并启动。
        /// </summary>
        /// <returns>成功注册且正在运行返回 true，否则 false。</returns>
        public static bool EnsureRegisteredAndRunningOnStartup()
        {
            try
            {
                var toolPath = EnsureHostToolCurrent();
                if (string.IsNullOrWhiteSpace(toolPath))
                {
                    LoggingService.LogWarning("同步 MftScanner.exe 失败，无法启动后台索引宿主。");
                    return false;
                }

                var taskExists = TaskExists();
                var taskMatches = taskExists && TaskDefinitionMatches(toolPath);
                LoggingService.LogDebug($"[索引宿主启动] taskExists={taskExists} taskMatches={taskMatches} toolPath={toolPath}");
                if (!taskExists)
                {
                    LoggingService.LogInfo("后台索引宿主计划任务不存在，准备以管理员权限注册。");
                    if (!RunElevatedRegister(toolPath))
                    {
                        return false;
                    }
                }
                else if (!taskMatches)
                {
                    LoggingService.LogDebug("[索引宿主启动] 计划任务已存在，但定义校验未通过；当前版本跳过重建，继续复用既有任务。");
                }

                return EnsureTaskRunning(toolPath);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "确保后台索引宿主计划任务失败");
                return false;
            }
        }

        private static string EnsureHostToolCurrent()
        {
            var toolPath = AdminElevationService.GetExtractedToolPath("MftScanner.exe");
            var toolExists = !string.IsNullOrWhiteSpace(toolPath) && File.Exists(toolPath);
            var toolUpToDate = toolExists && AdminElevationService.IsEmbeddedToolUpToDate("MftScanner.exe", "MftScanner.exe");
            if (toolUpToDate)
            {
                return toolPath;
            }

            LoggingService.LogInfo("检测到后台索引宿主文件缺失或内容已变化，准备同步最新宿主。");
            return EnsureHostToolCurrentCore();
        }

        private static string EnsureHostToolCurrentCore()
        {
            var toolPath = AdminElevationService.GetExtractedToolPath("MftScanner.exe");
            if (TrySyncHostToolOnce(toolPath))
            {
                return toolPath;
            }

            if (!AdminElevationService.IsRunningAsAdministrator())
            {
                LoggingService.LogInfo("后台索引宿主需要管理员权限完成同步，准备拉起提升流程。");
                if (!RunElevatedEnsureHost())
                {
                    return null;
                }

                toolPath = AdminElevationService.GetExtractedToolPath("MftScanner.exe");
                if (File.Exists(toolPath) && AdminElevationService.IsEmbeddedToolUpToDate("MftScanner.exe", "MftScanner.exe"))
                {
                    return toolPath;
                }

                LoggingService.LogWarning("管理员同步后台索引宿主已执行，但宿主文件仍不是最新版本。");
                return null;
            }

            LoggingService.LogInfo("后台索引宿主仍被占用，准备结束旧版 MftScanner 进程后重试同步。");
            TryStopProcessesUsingImagePath(toolPath);
            Thread.Sleep(500);

            if (TrySyncHostToolOnce(toolPath))
            {
                return toolPath;
            }

            LoggingService.LogWarning("同步后台索引宿主失败，提取后的 MftScanner.exe 仍不是最新版本。");
            return null;
        }

        private static bool TrySyncHostToolOnce(string toolPath)
        {
            toolPath = AdminElevationService.ExtractEmbeddedTool("MftScanner.exe", "MftScanner.exe");
            return !string.IsNullOrWhiteSpace(toolPath)
                && File.Exists(toolPath)
                && AdminElevationService.IsEmbeddedToolUpToDate("MftScanner.exe", "MftScanner.exe");
        }

        private static bool EnsureTaskRunning(string toolPath)
        {
            if (SharedIndexServiceClient.TryWaitForHostAvailability(1500))
            {
                return true;
            }

            if (TryRunRegisteredTaskSilently()
                && SharedIndexServiceClient.TryWaitForHostAvailability(HostAvailabilityWaitMilliseconds))
            {
                return true;
            }

            LoggingService.LogWarning("后台索引宿主启动后仍未就绪，准备停止旧宿主并重新同步。");
            return AdminElevationService.IsRunningAsAdministrator()
                ? RunAdminEnsureHost() == 0
                : RunElevatedEnsureHost();
        }

        private static bool TaskExists()
        {
            return RunSchtasks($"/Query /TN \"{SharedIndexConstants.IndexHostTaskName}\"", true).ExitCode == 0;
        }

        private static bool TaskDefinitionMatches(string toolPath)
        {
            if (string.IsNullOrWhiteSpace(toolPath))
            {
                return false;
            }

            try
            {
                var result = RunSchtasks($"/Query /TN \"{SharedIndexConstants.IndexHostTaskName}\" /V /FO LIST", true);
                if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Stdout))
                {
                    return false;
                }

                var expectedCommand = $"\"{toolPath}\" --index-agent";
                var line = result.Stdout
                    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                    .FirstOrDefault(item => item.IndexOf("Task To Run:", StringComparison.OrdinalIgnoreCase) >= 0);
                if (string.IsNullOrWhiteSpace(line))
                {
                    LoggingService.LogDebug("[索引宿主启动] 计划任务输出中未找到 Task To Run 行。");
                    return false;
                }

                var actualCommand = line.Substring(line.IndexOf(':') + 1).Trim();
                var normalizedActual = NormalizeTaskCommand(actualCommand);
                var normalizedExpected = NormalizeTaskCommand(expectedCommand);
                var matched = string.Equals(normalizedActual, normalizedExpected, StringComparison.OrdinalIgnoreCase)
                    || (normalizedActual.IndexOf(NormalizeTaskCommand(toolPath), StringComparison.OrdinalIgnoreCase) >= 0
                        && normalizedActual.IndexOf("--index-agent", StringComparison.OrdinalIgnoreCase) >= 0);

                LoggingService.LogDebug($"[索引宿主启动] expectedTaskCommand={expectedCommand} actualTaskCommand={actualCommand} matched={matched}");
                return matched;
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"校验后台索引宿主计划任务定义失败：{ex.Message}");
                return false;
            }
        }

        private static string NormalizeTaskCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return string.Empty;
            }

            return string.Join(" ",
                command.Trim()
                    .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        internal static bool TryRunRegisteredTaskSilently()
        {
            try
            {
                if (!TaskExists())
                {
                    return false;
                }

                return RunSchtasks($"/Run /TN \"{SharedIndexConstants.IndexHostTaskName}\"", true).ExitCode == 0;
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"静默启动后台索引宿主任务失败：{ex.Message}");
                return false;
            }
        }

        private static void TryStopRegisteredTaskInstance()
        {
            try
            {
                if (!TaskExists())
                {
                    return;
                }

                RunSchtasks($"/End /TN \"{SharedIndexConstants.IndexHostTaskName}\"", true);
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"结束后台索引宿主计划任务实例失败：{ex.Message}");
            }
        }

        private static bool StopRegisteredTaskInstanceWithElevation()
        {
            try
            {
                TryStopRegisteredTaskInstance();
                return true;
            }
            catch
            {
                if (AdminElevationService.IsRunningAsAdministrator())
                {
                    return false;
                }

                return RunElevatedEnsureHost();
            }
        }

        private static void TryStopProcessesUsingImagePath(string toolPath)
        {
            if (string.IsNullOrWhiteSpace(toolPath))
            {
                return;
            }

            try
            {
                foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(toolPath)))
                {
                    try
                    {
                        var processPath = TryGetProcessImagePath(process);
                        if (!string.Equals(processPath, toolPath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        LoggingService.LogInfo($"结束旧版 MftScanner 进程：PID={process.Id}");
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogWarning($"结束旧版 MftScanner 进程失败：PID={process.Id}，{ex.Message}");
                    }
                    finally
                    {
                        try
                        {
                            process.Dispose();
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"扫描旧版 MftScanner 进程失败：{ex.Message}");
            }
        }

        private static string TryGetProcessImagePath(Process process)
        {
            if (process == null)
            {
                return null;
            }

            try
            {
                return process.MainModule?.FileName;
            }
            catch
            {
            }

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    $"SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = {process.Id}"))
                {
                    foreach (var item in searcher.Get().OfType<ManagementObject>())
                    {
                        var executablePath = item["ExecutablePath"] as string;
                        if (!string.IsNullOrWhiteSpace(executablePath))
                        {
                            return executablePath;
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool RunElevatedRegister(string toolPath)
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return false;
                }

                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = $"--pm-admin-register-index-host-task {Quote(toolPath)}"
                }))
                {
                    process?.WaitForExit();
                    return process != null && process.ExitCode == 0;
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                LoggingService.LogWarning("用户取消了后台索引宿主计划任务的管理员授权。");
                return false;
            }
        }

        private static bool RunElevatedEnsureHost()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return false;
                }

                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = "--pm-admin-ensure-index-host"
                }))
                {
                    process?.WaitForExit();
                    return process != null && process.ExitCode == 0;
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                LoggingService.LogWarning("用户取消了后台索引宿主同步所需的管理员授权。");
                return false;
            }
        }

        internal static int RunAdminRegister(string toolPath)
        {
            if (string.IsNullOrWhiteSpace(toolPath) || !File.Exists(toolPath))
            {
                return 1;
            }

            var arguments = new StringBuilder();
            arguments.Append("/Create /F /RL HIGHEST /SC ONLOGON ");
            arguments.Append("/TN ").Append(Quote(SharedIndexConstants.IndexHostTaskName)).Append(' ');
            arguments.Append("/TR ").Append(Quote($"\"{toolPath}\" --index-agent"));

            return RunSchtasks(arguments.ToString(), true).ExitCode;
        }

        internal static int RunAdminEnsureHost()
        {
            try
            {
                var toolPath = AdminElevationService.GetExtractedToolPath("MftScanner.exe");
                TryStopRegisteredTaskInstance();
                Thread.Sleep(300);
                TryStopProcessesUsingImagePath(toolPath);
                Thread.Sleep(500);

                toolPath = AdminElevationService.ExtractEmbeddedTool("MftScanner.exe", "MftScanner.exe");
                if (string.IsNullOrWhiteSpace(toolPath)
                    || !File.Exists(toolPath)
                    || !AdminElevationService.IsEmbeddedToolUpToDate("MftScanner.exe", "MftScanner.exe"))
                {
                    return 2;
                }

                if (!TaskExists() || !TaskDefinitionMatches(toolPath))
                {
                    var registerExitCode = RunAdminRegister(toolPath);
                    if (registerExitCode != 0)
                    {
                        return registerExitCode;
                    }
                }

                if (!TryRunRegisteredTaskSilently()
                    && !SharedIndexServiceClient.TryWaitForHostAvailability(HostAvailabilityWaitMilliseconds))
                {
                    return 3;
                }

                return SharedIndexServiceClient.TryWaitForHostAvailability(HostAvailabilityWaitMilliseconds) ? 0 : 4;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "管理员确保后台索引宿主失败");
                return 5;
            }
        }

        private static ProcessResult RunSchtasks(string arguments, bool hidden)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    return new ProcessResult(-1, string.Empty, "Process start failed.");
                }

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    LoggingService.LogWarning($"schtasks failed. ExitCode={process.ExitCode}, Args={arguments}, Error={stderr}");
                }

                return new ProcessResult(process.ExitCode, stdout, stderr);
            }
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "\"\"";
            }

            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private readonly struct ProcessResult
        {
            public ProcessResult(int exitCode, string stdout, string stderr)
            {
                ExitCode = exitCode;
                Stdout = stdout;
                Stderr = stderr;
            }

            public int ExitCode { get; }
            public string Stdout { get; }
            public string Stderr { get; }
        }
    }
}
