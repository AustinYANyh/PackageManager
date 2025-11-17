using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;

namespace PackageManager.Services
{
    public class AppUpdateService
    {
        private readonly FtpService _ftpService = new FtpService();

        public async Task CheckAndPromptUpdateAsync(Window owner = null)
        {
            string serverUrl = ConfigurationManager.AppSettings["UpdateServerUrl"]; // 例如：ftp://server/PackageManager/
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                LoggingService.LogWarning("未配置 UpdateServerUrl，跳过应用自动更新检查。");
                return;
            }

            Version current = GetCurrentVersion();
            Version latest = null;
            string latestDir = null;

            try
            {
                var dirs = await _ftpService.GetDirectoriesAsync(serverUrl);
                var candidates = dirs
                    .Select(d => new { d, ver = TryParseVersionFromDir(d) })
                    .Where(x => x.ver != null)
                    .OrderBy(x => x.ver)
                    .ToList();

                if (candidates.Count == 0)
                {
                    LoggingService.LogWarning("更新服务器上未发现版本目录，跳过自动更新。");
                    return;
                }

                latestDir = candidates.Last().d;
                latest = candidates.Last().ver;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "获取更新版本信息失败");
                return;
            }

            if (latest == null || current == null || latest <= current)
            {
                LoggingService.LogInfo($"当前已是最新版本：{current}");
                return;
            }

            var message = $"检测到新版本：{latest}，当前版本：{current}。是否立即更新？";
            var result = MessageBox.Show(owner ?? Application.Current.MainWindow, message, "发现新版本", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var exeUrl = CombineUrl(serverUrl, latestDir, "PackageManager.exe");
                var tempExe = Path.Combine(Path.GetTempPath(), "PackageManager_new.exe");

                await DownloadAsync(exeUrl, tempExe);

                // 切换到新版本：生成批处理脚本，在进程退出后替换并启动
                var oldExe = Process.GetCurrentProcess().MainModule.FileName;
                var scriptPath = Path.Combine(Path.GetTempPath(), "pm_update.cmd");
                var script = BuildReplaceScript(oldExe, tempExe);
                File.WriteAllText(scriptPath, script);

                ToastService.ShowToast("更新开始", "正在切换到新版本……", "Info", 3000);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{scriptPath}\"",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "下载或切换新版本失败");
                MessageBox.Show(owner ?? Application.Current.MainWindow, "更新失败，详细信息见错误日志。", "更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static Version GetCurrentVersion()
        {
            try
            {
                return Assembly.GetExecutingAssembly().GetName().Version;
            }
            catch
            {
                return new Version(0, 0, 0, 0);
            }
        }

        private static Version TryParseVersionFromDir(string dir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) return null;
                var cleaned = dir.Trim('/').Trim();
                if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                {
                    cleaned = cleaned.Substring(1);
                }
                // 去掉可能的后缀，如 _log
                var basePart = cleaned.Split(new[] { '_', '-' })[0];
                return Version.Parse(basePart);
            }
            catch
            {
                return null;
            }
        }

        private static string CombineUrl(string baseUrl, string path1, string file)
        {
            baseUrl = baseUrl.TrimEnd('/') + "/";
            path1 = path1.Trim('/');
            return baseUrl + path1 + "/" + file;
        }

        private static async Task DownloadAsync(string url, string localPath)
        {
            using (var client = new WebClient())
            {
                try
                {
                    var uri = new Uri(url);
                    if (uri.Scheme.Equals("ftp", StringComparison.OrdinalIgnoreCase))
                    {
                        client.Credentials = new NetworkCredential("hwclient", "hw_ftpa206");
                    }
                }
                catch
                {
                    // ignore
                }
                await client.DownloadFileTaskAsync(new Uri(url), localPath);
            }
        }

        private static string BuildReplaceScript(string oldExe, string newExe)
        {
            var lines = new[]
            {
                "@echo off",
                "setlocal",
                $"set OLD=\"{oldExe}\"",
                $"set NEW=\"{newExe}\"",
                ":wait",
                "del /F /Q %OLD% >nul 2>&1",
                "if exist %OLD% (",
                "  ping 127.0.0.1 -n 2 >nul",
                "  goto wait",
                ")",
                "copy /Y %NEW% %OLD% >nul",
                "start \"\" %OLD%",
                "del /F /Q \"%~f0\" >nul 2>&1",
                "endlocal"
            };
            return string.Join(Environment.NewLine, lines);
        }
    }
}