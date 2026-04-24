using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
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
                var toolPath = AdminElevationService.ExtractEmbeddedTool("MftScanner.exe", "MftScanner.exe");
                if (string.IsNullOrWhiteSpace(toolPath))
                {
                    LoggingService.LogWarning("提取 MftScanner.exe 失败，无法启动后台索引宿主。");
                    return false;
                }

                if (!TaskExists())
                {
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

        private static bool TaskExists()
        {
            return RunSchtasks($"/Query /TN \"{SharedIndexConstants.IndexHostTaskName}\"", true).ExitCode == 0;
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
