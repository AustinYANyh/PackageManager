using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using MftScanner;

namespace PackageManager.Services
{
    internal static class IndexHostTaskService
    {
        public static void EnsureRegisteredAndRunningOnStartup()
        {
            try
            {
                var toolPath = AdminElevationService.ExtractEmbeddedTool("MftScanner.exe", "MftScanner.exe");
                if (string.IsNullOrWhiteSpace(toolPath))
                {
                    return;
                }

                if (!TaskExists())
                {
                    var result = MessageBox.Show(
                        "文件搜索后台索引宿主尚未注册。\n\n是否现在授权创建“登录后自动启动、最高权限运行”的后台索引任务？\n\n创建后，Ctrl+E 和 Ctrl+Q 可优先复用后台索引，减少首次等待。",
                        "启用后台索引宿主",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (result != MessageBoxResult.Yes)
                    {
                        return;
                    }

                    if (!RunElevatedRegister(toolPath))
                    {
                        return;
                    }
                }

                TryRunRegisteredTaskSilently();
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "确保后台索引宿主计划任务失败");
            }
        }

        private static bool TaskExists()
        {
            return RunSchtasks($"/Query /TN \"{SharedIndexConstants.IndexHostTaskName}\"", false).ExitCode == 0;
        }

        internal static bool TryRunRegisteredTaskSilently()
        {
            try
            {
                if (!TaskExists())
                {
                    return false;
                }

                return RunSchtasks($"/Run /TN \"{SharedIndexConstants.IndexHostTaskName}\"", false).ExitCode == 0;
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
