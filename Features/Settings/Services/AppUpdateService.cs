using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Text.RegularExpressions;
using System.Text;
using PackageManager.Features.Settings.Views;
using PackageManager.Features.Notifications.Models;
using PackageManager.Features.Notifications.Services;

namespace PackageManager.Services
{
    /// <summary>
    /// 表示一次可用的应用更新，包含版本对比与按版本分组的更新要点。
    /// </summary>
    public class AppUpdateInfo
    {
        /// <summary>
        /// 获取或设置当前版本。
        /// </summary>
        public Version Current { get; set; }

        /// <summary>
        /// 获取或设置最新版本。
        /// </summary>
        public Version Latest { get; set; }

        /// <summary>
        /// 获取或设置最新版本在服务器上的目录名。
        /// </summary>
        public string LatestDir { get; set; }

        /// <summary>
        /// 获取或设置更新服务器基地址。
        /// </summary>
        public string ServerUrl { get; set; }

        /// <summary>
        /// 获取或设置按版本升序排列的更新要点分组（版本号 → 功能点列表）。
        /// </summary>
        public List<KeyValuePair<string, List<string>>> ChangeGroups { get; set; } = new List<KeyValuePair<string, List<string>>>();
    }

    /// <summary>
    /// 表示下载进度信息。
    /// </summary>
    public class UpdateDownloadProgress
    {
        /// <summary>
        /// 获取或设置进度百分比（0-100）。
        /// </summary>
        public double Percent { get; set; }

        /// <summary>
        /// 获取或设置已下载字节数。
        /// </summary>
        public long BytesReceived { get; set; }

        /// <summary>
        /// 获取或设置总字节数；未知时为 -1。
        /// </summary>
        public long TotalBytes { get; set; } = -1;

        /// <summary>
        /// 获取或设置当前下载速度（字节/秒）。
        /// </summary>
        public double Speed { get; set; }

        /// <summary>
        /// 获取或设置预计剩余秒数；未知时为 -1。
        /// </summary>
        public double RemainingSeconds { get; set; } = -1;
    }

    /// <summary>
    /// 应用程序自动更新服务，负责检查版本更新、下载并切换到新版本
    /// </summary>
    public class AppUpdateService
    {
        private readonly FtpService ftpService = new FtpService();

        private readonly DataPersistenceService dataPersistenceService = new DataPersistenceService();

        /// <summary>
        /// 检查服务器上是否存在新版本，如果有则弹出自定义更新窗口，支持立即更新、稍后提醒与跳过此版本
        /// </summary>
        /// <param name="owner">弹窗的父窗口，默认为 <c>null</c></param>
        /// <returns>异步任务</returns>
        public async Task CheckAndPromptUpdateAsync(Window owner = null)
        {
            var update = await CheckForUpdateAsync();
            if (update == null)
            {
                return;
            }

            var settings = dataPersistenceService.LoadSettings();
            if (IsSkipVersion(settings?.SkipUpdateVersion, update.Latest))
            {
                LoggingService.LogInfo($"用户已跳过版本 {update.Latest}，不再提示更新。");
                return;
            }

            var window = new UpdateAvailableWindow(update, this);
            window.Owner = owner ?? Application.Current?.MainWindow;
            window.ShowDialog();
        }

        /// <summary>
        /// 用户选择跳过指定版本：持久化后同版本不再提示，更高版本恢复提醒。
        /// </summary>
        /// <param name="version">要跳过的版本。</param>
        public void SkipVersion(Version version)
        {
            try
            {
                var settings = dataPersistenceService.LoadSettings() ?? new AppSettings();
                settings.SkipUpdateVersion = version?.ToString();
                dataPersistenceService.SaveSettings(settings);
                LoggingService.LogInfo($"已记录跳过更新版本：{version}");
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"记录跳过版本失败：{version}");
            }
        }

        /// <summary>
        /// 用户选择稍后提醒：推送一条通知中心消息保留更新入口。
        /// </summary>
        /// <param name="update">更新信息。</param>
        public void NotifyForLater(AppUpdateInfo update)
        {
            try
            {
                var version = update?.Latest?.ToString() ?? string.Empty;
                ServiceLocator.Resolve<NotificationService>()?.Push(
                    "发现新版本",
                    $"PackageManager v{version} 已发布，可稍后在设置页升级。",
                    NotificationLevel.Info);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "推送更新提醒通知失败");
            }
        }

        /// <summary>
        /// 检查服务器上的最新版本；存在比当前新的版本时返回更新信息，否则返回 null。
        /// </summary>
        /// <returns>更新信息；无更新或检查失败时返回 null。</returns>
        public async Task<AppUpdateInfo> CheckForUpdateAsync()
        {
            string serverUrl = GetUpdateServerUrl();

            Version current = GetCurrentVersion();
            Version latest = null;
            string latestDir = null;

            try
            {
                var dirs = await ftpService.GetDirectoriesAsync(serverUrl);
                var candidates = dirs
                                 .Select(d => new { d, ver = TryExtractVersionFromName(d) })
                                 .Where(x => x.ver != null)
                                 .OrderBy(x => NormalizeVersion(x.ver))
                                 .ToList();

                if (candidates.Count == 0)
                {
                    LoggingService.LogWarning("更新服务器上未发现版本目录，跳过自动更新。");
                    return null;
                }

                latestDir = candidates.Last().d;
                latest = NormalizeVersion(candidates.Last().ver);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "获取更新版本信息失败");
                return null;
            }

            if ((latest == null) || (current == null) || (latest <= current))
            {
                LoggingService.LogInfo($"当前已是最新版本：{current}");
                return null;
            }

            var changeGroups = await LoadChangeGroupsAsync(current, latest);
            return new AppUpdateInfo
            {
                Current = current,
                Latest = latest,
                LatestDir = latestDir,
                ServerUrl = serverUrl,
                ChangeGroups = changeGroups,
            };
        }

        /// <summary>
        /// 下载新版本并切换：下载进度通过回调上报，完成后替换 exe 并重启应用。
        /// </summary>
        /// <param name="update">更新信息。</param>
        /// <param name="progress">进度回调，在 UI 线程外触发。</param>
        /// <returns>异步任务。</returns>
        public async Task ExecuteUpdateAsync(AppUpdateInfo update, IProgress<UpdateDownloadProgress> progress = null)
        {
            var exeUrl = CombineUrl(update.ServerUrl, update.LatestDir, "PackageManager.exe");
            var tempExe = Path.Combine(Path.GetTempPath(), "PackageManager_new.exe");

            await DownloadAsync(exeUrl, tempExe, progress);

            // 切换到新版本：生成批处理脚本，在进程退出后替换并启动
            var oldExe = Process.GetCurrentProcess().MainModule.FileName;
            var scriptPath = Path.Combine(Path.GetTempPath(), "pm_update.cmd");
            var script = BuildReplaceScript(oldExe, tempExe);
            File.WriteAllText(scriptPath, script, Encoding.Default);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            Application.Current.Shutdown();
        }

        /// <summary>
        /// 直接升级到最新版本：与”发现新版本后选择立即更新”一致，但不弹窗提示。
        /// 哪怕版本号是最新的也要执行，同版本号本地的exe也不一定是最新
        /// </summary>
        /// <param name="owner">弹窗的父窗口，默认为 null</param>
        /// <returns>异步任务</returns>
        public async Task UpgradeToLatestAsync(Window owner = null)
        {
            string serverUrl = GetUpdateServerUrl();

            Version latest = null;
            string latestDir = null;

            try
            {
                var dirs = await ftpService.GetDirectoriesAsync(serverUrl);
                var candidates = dirs
                                 .Select(d => new { d, ver = TryExtractVersionFromName(d) })
                                 .Where(x => x.ver != null)
                                 .OrderBy(x => NormalizeVersion(x.ver))
                                 .ToList();

                if (candidates.Count == 0)
                {
                    LoggingService.LogWarning("更新服务器上未发现版本目录，跳过升级。");
                    MessageBox.Show(owner ?? Application.Current.MainWindow, "未找到可用版本目录", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                latestDir = candidates.Last().d;
                latest = NormalizeVersion(candidates.Last().ver);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "获取更新版本信息失败");
                MessageBox.Show(owner ?? Application.Current.MainWindow, "读取更新服务器失败，详细信息见错误日志。", "更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                var update = new AppUpdateInfo
                {
                    Current = GetCurrentVersion(),
                    Latest = latest,
                    LatestDir = latestDir,
                    ServerUrl = serverUrl,
                };
                ToastService.ShowToast("升级开始", $"正在切换到新版本：{latest}");
                await ExecuteUpdateAsync(update);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "下载或切换新版本失败");
                MessageBox.Show(owner ?? Application.Current.MainWindow, "升级失败，详细信息见错误日志。", "更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static bool IsSkipVersion(string skipVersion, Version latest)
        {
            if (string.IsNullOrWhiteSpace(skipVersion) || latest == null)
            {
                return false;
            }

            try
            {
                return NormalizeVersion(Version.Parse(skipVersion.Trim())) == latest;
            }
            catch
            {
                return false;
            }
        }

        private static Version GetCurrentVersion()
        {
            try
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return NormalizeVersion(v);
            }
            catch
            {
                return new Version(0, 0, 0, 0);
            }
        }

        /// <summary>
        /// 规范化 Version 为四段（缺失的段填充为0），避免 1.0.3 与 1.0.3.0 比较偏差。
        /// </summary>
        private static Version NormalizeVersion(Version v)
        {
            if (v == null) return null;
            var build = v.Build < 0 ? 0 : v.Build;
            var rev = v.Revision < 0 ? 0 : v.Revision;
            return new Version(v.Major, v.Minor, build, rev);
        }

        /// <summary>
        /// 从目录名中提取版本号，兼容日期前缀与后缀（如：2025.09.30_v1.5.2、v1.5.2_log、v1.5.2）。
        /// </summary>
        private static Version TryExtractVersionFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            var match = Regex.Match(name, @"[vV](\d+(?:\.\d+){0,3})", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var verText = match.Groups[1].Value;
                try
                {
                    return NormalizeVersion(Version.Parse(verText));
                }
                catch
                {
                    return null;
                }
            }

            // 尝试整体解析（纯数字点分）
            var cleaned = name.Trim('/').Trim();
            var basePart = cleaned.Split('_', '-').FirstOrDefault();
            if (!string.IsNullOrEmpty(basePart))
            {
                try { return NormalizeVersion(Version.Parse(basePart.TrimStart('v', 'V'))); } catch { }
            }
            return null;
        }

        private static string CombineUrl(string baseUrl, string path1, string file)
        {
            baseUrl = baseUrl.TrimEnd('/') + "/";
            path1 = path1.Trim('/');
            return baseUrl + path1 + "/" + file;
        }

        private static async Task DownloadAsync(string url, string localPath, IProgress<UpdateDownloadProgress> progress = null)
        {
            using (var client = new WebClient())
            {
                try
                {
                    var uri = new Uri(url);
                    if (uri.Scheme.Equals("ftp", StringComparison.OrdinalIgnoreCase))
                    {
                        client.Credentials = ServiceLocator.Resolve<CredentialStore>().GetFtpReadCredential();
                    }
                }
                catch
                {
                    // ignore
                }

                if (progress != null)
                {
                    var watcher = new Stopwatch();
                    object sync = new object();
                    long lastBytes = 0;
                    var lastReportSeconds = 0d;
                    client.DownloadProgressChanged += (s, e) =>
                    {
                        double elapsed;
                        lock (sync)
                        {
                            elapsed = watcher.Elapsed.TotalSeconds;
                            if (elapsed - lastReportSeconds < 0.2 && e.ProgressPercentage < 100)
                            {
                                return;
                            }

                            lastReportSeconds = elapsed;
                        }

                        double speed = 0;
                        var remaining = -1d;
                        if (elapsed > 0.2)
                        {
                            speed = e.BytesReceived / elapsed;
                            if (speed > 0 && e.TotalBytesToReceive > 0)
                            {
                                remaining = (e.TotalBytesToReceive - e.BytesReceived) / speed;
                            }
                        }

                        lock (sync)
                        {
                            lastBytes = e.BytesReceived;
                        }

                        progress.Report(new UpdateDownloadProgress
                        {
                            Percent = e.ProgressPercentage,
                            BytesReceived = e.BytesReceived,
                            TotalBytes = e.TotalBytesToReceive,
                            Speed = speed,
                            RemainingSeconds = remaining,
                        });
                    };
                    watcher.Start();
                    await client.DownloadFileTaskAsync(new Uri(url), localPath);
                    watcher.Stop();

                    progress.Report(new UpdateDownloadProgress
                    {
                        Percent = 100,
                        BytesReceived = lastBytes,
                        TotalBytes = lastBytes,
                        Speed = 0,
                        RemainingSeconds = 0,
                    });
                }
                else
                {
                    await client.DownloadFileTaskAsync(new Uri(url), localPath);
                }
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
                "endlocal",
            };
            return string.Join(Environment.NewLine, lines);
        }

        private string GetUpdateServerUrl()
        {
            try
            {
                var settings = dataPersistenceService.LoadSettings();
                var fromJson = settings?.UpdateServerUrl;
                if (!string.IsNullOrWhiteSpace(fromJson))
                {
                    LoggingService.LogInfo("使用本地设置文件中的 UpdateServerUrl。");
                    return fromJson;
                }
            }
            catch (Exception jsonEx)
            {
                LoggingService.LogInfo($"读取本地设置失败。详情：{jsonEx.Message}");
            }

            return FallbackUpdateServerUrl;
        }

        private const string FallbackUpdateServerUrl = "http://192.168.0.215:8001/PackageManager/";
        private const string UpdateSummaryBaseUrl = "http://192.168.0.215:8001/UpdateSummary/";

        private async Task<List<KeyValuePair<string, List<string>>>> LoadChangeGroupsAsync(Version current, Version latest)
        {
            var result = new List<KeyValuePair<string, List<string>>>();
            try
            {
                var summaries = await LoadVersionSummariesAsync();
                var targets = summaries.Keys
                    .Select(k => new { key = k, ver = TryParseVersion(k) })
                    .Where(x => x.ver != null && x.ver > current && x.ver <= latest)
                    .OrderBy(x => x.ver)
                    .ToList();

                foreach (var t in targets)
                {
                    result.Add(new KeyValuePair<string, List<string>>(t.key, summaries[t.key]));
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"读取更新摘要失败，将使用默认提示。详情：{ex.Message}");
            }

            return result;
        }

        private static Version TryParseVersion(string text)
        {
            try { return NormalizeVersion(Version.Parse(text)); } catch { return null; }
        }

        private async Task<System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>> LoadVersionSummariesAsync()
        {
            string content = null;
            try
            {
                content = await TryReadRemoteSummaryAsync();
            }
            catch { }

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException("未找到更新摘要内容。");
            }

            var map = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>();
            using (var reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    // 支持 "- 1.0.13.0：..." 或 "1.0.13.0: ..." 等格式
                    var m = Regex.Match(line, @"^\s*(?:[-*•]\s*)?(\d+\.\d+\.\d+(?:\.\d+)?)\s*[：:]\s*(.+)\s*$");
                    if (!m.Success) continue;
                    var ver = m.Groups[1].Value.Trim();
                    var rest = m.Groups[2].Value.Trim();
                    var items = rest.Split(new[] { '、', ',', ';', '，', '；' }, StringSplitOptions.RemoveEmptyEntries)
                                    .Select(s => s.Trim())
                                    .Where(s => !string.IsNullOrWhiteSpace(s))
                                    .ToList();
                    map[ver] = items;
                }
            }
            return map;
        }

        private static async Task<string> TryReadRemoteSummaryAsync()
        {
            try
            {
                var url = UpdateSummaryBaseUrl.TrimEnd('/') + "/UpdateSummary.txt";
                using (var client = new WebClient())
                {
                    var data = await client.DownloadDataTaskAsync(new Uri(url));
                    return DecodeSummaryBytes(data);
                }
            }
            catch
            {
                return null;
            }
        }

        private static string DecodeSummaryBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }
            try
            {
                var utf8Strict = new UTF8Encoding(false, true);
                return utf8Strict.GetString(bytes);
            }
            catch
            {
            }
            try
            {
                return Encoding.Default.GetString(bytes);
            }
            catch
            {
            }
            try
            {
                var enc = Encoding.GetEncoding(936);
                return enc.GetString(bytes);
            }
            catch
            {
            }
            try
            {
                var enc = Encoding.GetEncoding(54936);
                return enc.GetString(bytes);
            }
            catch
            {
            }
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
