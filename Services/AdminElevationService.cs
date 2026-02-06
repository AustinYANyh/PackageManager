using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using Newtonsoft.Json;
using PackageManager.Models;

namespace PackageManager.Services
{
    public static class AdminElevationService
    {
        public static bool IsRunningAsAdministrator()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public static bool RequiresAdminForPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).ToLowerInvariant();
                var pfx = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).ToLowerInvariant();
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
                if (p.StartsWith(pf) || p.StartsWith(pfx) || p.StartsWith(windows)) return true;
                if (Path.GetPathRoot(p).Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            catch
            {
                return false;
            }
        }

        public class AdminUpdateConfig
        {
            public string ProductName { get; set; }
            public string DownloadUrl { get; set; }
            public string LocalPath { get; set; }
            public bool ForceUnlock { get; set; }
        }
        
        public class AdminUnlockUiConfig
        {
            public string[] Targets { get; set; }
            public int[] Pids { get; set; }
            
            public string ResultPath { get; set; }
        }

        public static async Task<bool> RunElevatedUpdateAsync(PackageInfo packageInfo, bool forceUnlock)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "PackageManager");
            Directory.CreateDirectory(tempDir);
            var jsonPath = Path.Combine(tempDir, "update_" + Guid.NewGuid().ToString("N") + ".json");
            var cfg = new AdminUpdateConfig
            {
                ProductName = packageInfo.ProductName,
                DownloadUrl = packageInfo.DownloadUrl,
                LocalPath = packageInfo.GetLocalPathForVersion(packageInfo.Version),
                ForceUnlock = forceUnlock
            };
            File.WriteAllText(jsonPath, JsonConvert.SerializeObject(cfg));
            var exePath = Process.GetCurrentProcess().MainModule.FileName;
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--pm-admin-update " + Quote(jsonPath)
            };
            try
            {
                var proc = Process.Start(psi);
                await Task.Run(() => proc.WaitForExit());
                var code = proc.ExitCode;
                try { File.Delete(jsonPath); } catch { }
                return code == 0;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "管理员更新进程启动失败");
                return false;
            }
        }

        private static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            return s.Contains(" ") ? "\"" + s + "\"" : s;
        }
        
        private static string EnsureEmbeddedToolExtracted(string resourceSuffix, string outputFileName)
        {
            try
            {
                var asm = typeof(AdminElevationService).Assembly;
                var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }
                var targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackageManager", "tools");
                Directory.CreateDirectory(targetDir);
                var targetPath = Path.Combine(targetDir, outputFileName);
                // if (File.Exists(targetPath))
                // {
                //     return targetPath;
                // }
                using (var stream = asm.GetManifestResourceStream(name))
                {
                    if (stream == null) return null;
                    using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        stream.CopyTo(fs);
                    }
                }
                return targetPath;
            }
            catch
            {
                return null;
            }
        }
        
        public static async Task<bool> RunElevatedUnlockUiAsync(string[] targets, int[] pids = null)
        {
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "PackageManager");
                Directory.CreateDirectory(tempDir);
                var jsonPath = Path.Combine(tempDir, "unlock_" + Guid.NewGuid().ToString("N") + ".json");
                var cfg = new AdminUnlockUiConfig
                {
                    Targets = (targets ?? Array.Empty<string>()),
                    Pids = (pids ?? Array.Empty<int>()),
                    ResultPath = null
                };
                File.WriteAllText(jsonPath, JsonConvert.SerializeObject(cfg));
                var adminExe = EnsureEmbeddedToolExtracted("UnlockProcessAdmin.exe", "UnlockProcessAdmin.exe");
                if (string.IsNullOrEmpty(adminExe))
                {
                    throw new FileNotFoundException("未找到 UnlockProcessAdmin.exe 的嵌入资源或回退文件");
                }
                var psi = new ProcessStartInfo
                {
                    FileName = adminExe,
                    UseShellExecute = true,
                    Arguments = "--config " + Quote(jsonPath)
                };
                var proc = Process.Start(psi);
                await Task.Delay(100);
                return proc != null;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "管理员占用进程程序启动失败");
                return false;
            }
        }
        
        public static async Task<string> RunElevatedUnlockUiWithResultAsync(string[] targets, int[] pids = null)
        {
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "PackageManager");
                Directory.CreateDirectory(tempDir);
                var resultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackageManager", "unlock_results");
                Directory.CreateDirectory(resultDir);
                var resultPath = Path.Combine(resultDir, "unlock_" + Guid.NewGuid().ToString("N") + ".jsonl");
                var jsonPath = Path.Combine(tempDir, "unlock_" + Guid.NewGuid().ToString("N") + ".json");
                var cfg = new AdminUnlockUiConfig
                {
                    Targets = (targets ?? Array.Empty<string>()),
                    Pids = (pids ?? Array.Empty<int>()),
                    ResultPath = resultPath
                };
                File.WriteAllText(jsonPath, JsonConvert.SerializeObject(cfg));
                var adminExe = EnsureEmbeddedToolExtracted("UnlockProcessAdmin.exe", "UnlockProcessAdmin.exe");
                if (string.IsNullOrEmpty(adminExe))
                {
                    throw new FileNotFoundException("未找到 UnlockProcessAdmin.exe 的嵌入资源或回退文件");
                }
                var psi = new ProcessStartInfo
                {
                    FileName = adminExe,
                    UseShellExecute = true,
                    Arguments = "--config " + Quote(jsonPath)
                };
                var proc = Process.Start(psi);
                await Task.Delay(100);
                return proc != null ? resultPath : null;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "管理员占用进程程序启动失败");
                return null;
            }
        }
    }
}
