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

        public static async Task<bool> RunElevatedUpdateAsync(PackageInfo packageInfo, bool forceUnlock)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "PackageManager");
            Directory.CreateDirectory(tempDir);
            var jsonPath = Path.Combine(tempDir, "update_" + Guid.NewGuid().ToString("N") + ".json");
            var cfg = new AdminUpdateConfig
            {
                ProductName = packageInfo.ProductName,
                DownloadUrl = packageInfo.DownloadUrl,
                LocalPath = packageInfo.LocalPath,
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
    }
}

