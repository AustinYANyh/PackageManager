using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Text.RegularExpressions;
using System.Text;

namespace PackageManager.Services
{
    public class AppUpdateService
    {
        private readonly FtpService ftpService = new FtpService();

        private readonly DataPersistenceService dataPersistenceService = new DataPersistenceService();

        public async Task CheckAndPromptUpdateAsync(Window owner = null)
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
                    return;
                }

                latestDir = candidates.Last().d;
                latest = NormalizeVersion(candidates.Last().ver);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "获取更新版本信息失败");
                return;
            }

            if ((latest == null) || (current == null) || (latest <= current))
            {
                LoggingService.LogInfo($"当前已是最新版本：{current}");
                return;
            }

            var message = await BuildUpdatePromptContentAsync(current, latest);
            var result = MessageBox.Show(owner ?? Application.Current.MainWindow,
                                         message,
                                         "发现新版本",
                                         MessageBoxButton.YesNo,
                                         MessageBoxImage.Information);
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
                File.WriteAllText(scriptPath, script, Encoding.Default);

                ToastService.ShowToast("更新开始", "正在切换到新版本……");

                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{scriptPath}\"",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });

                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "下载或切换新版本失败");
                MessageBox.Show(owner ?? Application.Current.MainWindow, "更新失败，详细信息见错误日志。", "更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 直接升级到最新版本：与“发现新版本后选择立即更新”一致，但不弹窗提示。
        /// 哪怕版本号是最新的也要执行，同版本号本地的exe也不一定是最新
        /// </summary>
        public async Task UpgradeToLatestAsync(Window owner = null)
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
                var exeUrl = CombineUrl(serverUrl, latestDir, "PackageManager.exe");
                var tempExe = Path.Combine(Path.GetTempPath(), "PackageManager_new.exe");

                await DownloadAsync(exeUrl, tempExe);

                var oldExe = Process.GetCurrentProcess().MainModule.FileName;
                var scriptPath = Path.Combine(Path.GetTempPath(), "pm_update.cmd");
                var script = BuildReplaceScript(oldExe, tempExe);
                File.WriteAllText(scriptPath, script, Encoding.Default);

                ToastService.ShowToast("升级开始", $"正在切换到新版本：{latest}");

                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{scriptPath}\"",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });

                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "下载或切换新版本失败");
                MessageBox.Show(owner ?? Application.Current.MainWindow, "升级失败，详细信息见错误日志。", "更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
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
        
        private async Task<string> BuildUpdatePromptContentAsync(Version current, Version latest)
        {
            var header = $"检测到新版本：{latest}，当前版本：{current}\n\n主要更新点：";

            try
            {
                var summaries = await LoadVersionSummariesAsync();
                var select = summaries.Keys.Select(k => new { key = k, ver = TryParseVersion(k) });
                var targets = select
                              .Where(x => x.ver != null && x.ver > current && x.ver <= latest)
                              .OrderBy(x => x.ver)
                              .ToList();

                if (targets.Count > 0)
                {
                    var lines = targets.SelectMany(t =>
                    {
                        var items = summaries[t.key];
                        var title = $"\n• v{t.ver}:";
                        var bullets = items.Select(i => $"  - {i}");
                        return new[] { title }.Concat(bullets);
                    });
                    return header + "\n" + string.Join("\n", lines) + "\n\n是否立即更新？";
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"读取更新摘要失败，将使用默认提示。详情：{ex.Message}");
            }
            
            return $"检测到新版本：{latest}，当前版本：{current}。是否立即更新？";
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
