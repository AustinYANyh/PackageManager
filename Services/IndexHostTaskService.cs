using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MftScanner;

namespace PackageManager.Services
{
    internal static class IndexHostTaskService
    {
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
                if (!taskExists || !taskMatches)
                {
                    if (taskExists && !taskMatches)
                    {
                        TryStopRegisteredTaskInstance();
                    }

                    if (!RunElevatedRegister(toolPath))
                    {
                        return false;
                    }
                }

                return TryRunRegisteredTaskSilently();
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

            LoggingService.LogInfo("检测到后台索引宿主文件缺失或版本已变化，准备同步最新宿主。");
            TryStopRegisteredTaskInstance();
            Thread.Sleep(300);

            toolPath = AdminElevationService.ExtractEmbeddedTool("MftScanner.exe", "MftScanner.exe");
            if (!string.IsNullOrWhiteSpace(toolPath)
                && File.Exists(toolPath)
                && AdminElevationService.IsEmbeddedToolUpToDate("MftScanner.exe", "MftScanner.exe"))
            {
                return toolPath;
            }

            LoggingService.LogInfo("后台索引宿主仍被占用，准备结束旧版 MftScanner 进程后重试同步。");
            TryStopProcessesUsingImagePath(toolPath);
            Thread.Sleep(500);

            toolPath = AdminElevationService.ExtractEmbeddedTool("MftScanner.exe", "MftScanner.exe");
            if (!string.IsNullOrWhiteSpace(toolPath)
                && File.Exists(toolPath)
                && AdminElevationService.IsEmbeddedToolUpToDate("MftScanner.exe", "MftScanner.exe"))
            {
                return toolPath;
            }

            LoggingService.LogWarning("同步后台索引宿主失败，提取后的 MftScanner.exe 仍不是最新版本。");
            return null;
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
                    .FirstOrDefault(item => item.StartsWith("Task To Run:", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(line))
                {
                    return false;
                }

                var actualCommand = line.Substring(line.IndexOf(':') + 1).Trim();
                return string.Equals(actualCommand, expectedCommand, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"校验后台索引宿主计划任务定义失败：{ex.Message}");
                return false;
            }
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
                        var mainModule = process.MainModule;
                        var processPath = mainModule?.FileName;
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
