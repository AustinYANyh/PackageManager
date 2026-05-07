using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json;
using PackageManager.Models;

namespace PackageManager.Services
{
    /// <summary>
    /// 管理员权限提升服务，提供权限检测、以管理员身份运行进程等功能
    /// </summary>
    public static class AdminElevationService
    {
        /// <summary>
        /// 判断当前进程是否以管理员身份运行
        /// </summary>
        /// <returns>如果当前进程具有管理员权限则返回 <c>true</c>，否则返回 <c>false</c></returns>
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

        /// <summary>
        /// 判断指定路径是否需要管理员权限才能写入（如 Program Files、Windows 目录或驱动器根目录）
        /// </summary>
        /// <param name="path">要检查的文件或目录路径</param>
        /// <returns>如果该路径位于受保护目录下则返回 <c>true</c>，否则返回 <c>false</c></returns>
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

        /// <summary>
        /// 管理员更新操作的配置数据模型
        /// </summary>
        public class AdminUpdateConfig
        {
            /// <summary>
            /// 产品名称
            /// </summary>
            public string ProductName { get; set; }

            /// <summary>
            /// 下载地址
            /// </summary>
            public string DownloadUrl { get; set; }

            /// <summary>
            /// 本地安装路径
            /// </summary>
            public string LocalPath { get; set; }

            /// <summary>
            /// 是否强制解除文件占用
            /// </summary>
            public bool ForceUnlock { get; set; }
        }
        
        /// <summary>
        /// 管理员进程解锁操作的 UI 配置数据模型
        /// </summary>
        public class AdminUnlockUiConfig
        {
            /// <summary>
            /// 要解锁的目标路径数组
            /// </summary>
            public string[] Targets { get; set; }

            /// <summary>
            /// 要解锁的进程 ID 数组
            /// </summary>
            public int[] Pids { get; set; }

            /// <summary>
            /// 解锁结果的输出文件路径
            /// </summary>
            public string ResultPath { get; set; }
        }

        /// <summary>
        /// 以管理员权限启动更新进程
        /// </summary>
        /// <param name="packageInfo">要更新的包信息</param>
        /// <param name="forceUnlock">是否强制解除文件占用</param>
        /// <returns>如果管理员更新进程成功执行并返回退出码 0 则返回 <c>true</c>，否则返回 <c>false</c></returns>
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
            return ExtractEmbeddedTool(resourceSuffix, outputFileName);
        }

        /// <summary>
        /// 从嵌入资源中提取工具 EXE 到本地目录。
        /// </summary>
        /// <param name="resourceSuffix">嵌入资源名称后缀。</param>
        /// <param name="outputFileName">输出文件名。</param>
        /// <returns>提取后的文件路径；失败时返回 <c>null</c>。</returns>
        public static string ExtractEmbeddedTool(string resourceSuffix, string outputFileName)
        {
            string targetPath = null;
            string tempPath = null;
            try
            {
                var asm = typeof(AdminElevationService).Assembly;
                var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(name))
                {
                    LoggingService.LogWarning($"嵌入工具资源未找到：ResourceSuffix={resourceSuffix}，OutputFileName={outputFileName}");
                    return null;
                }
                var targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackageManager", "tools");
                Directory.CreateDirectory(targetDir);
                targetPath = Path.Combine(targetDir, outputFileName);
                tempPath = Path.Combine(targetDir, outputFileName + "." + Guid.NewGuid().ToString("N") + ".tmp");

                using (var stream = asm.GetManifestResourceStream(name))
                {
                    if (stream == null)
                    {
                        LoggingService.LogWarning($"嵌入工具资源流为空：ResourceName={name}，TargetPath={targetPath}");
                        return null;
                    }

                    using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        stream.CopyTo(fs);
                    }
                }

                if (File.Exists(targetPath))
                {
                    var backupPath = Path.Combine(targetDir, outputFileName + "." + Guid.NewGuid().ToString("N") + ".bak");
                    File.Replace(tempPath, targetPath, backupPath, true);
                    try { File.Delete(backupPath); } catch { }
                }
                else
                {
                    File.Move(tempPath, targetPath);
                }

                foreach (var sidecarSuffix in GetEmbeddedToolSidecars(outputFileName))
                {
                    try
                    {
                        ExtractEmbeddedSidecar(asm, targetDir, sidecarSuffix);
                    }
                    catch (Exception sidecarEx)
                    {
                        LoggingService.LogWarning($"嵌入工具 sidecar 提取失败，已继续使用主工具：OutputFileName={outputFileName}，Sidecar={sidecarSuffix}，{sidecarEx.Message}");
                    }
                }

                LoggingService.LogInfo($"嵌入工具提取完成：ResourceName={name}，TargetPath={targetPath}");
                return targetPath;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"提取嵌入工具失败：ResourceSuffix={resourceSuffix}，OutputFileName={outputFileName}，TargetPath={targetPath ?? "<null>"}");
                try
                {
                    if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }

                var existingPath = targetPath ?? GetExtractedToolPath(outputFileName);
                return File.Exists(existingPath) ? existingPath : null;
            }
        }

        /// <summary>
        /// 检查嵌入工具是否已是最新版本（通过 SHA256 对比）。
        /// </summary>
        /// <param name="resourceSuffix">嵌入资源名称后缀。</param>
        /// <param name="outputFileName">输出文件名。</param>
        /// <returns>已是最新返回 true，否则 false。</returns>
        public static bool IsEmbeddedToolUpToDate(string resourceSuffix, string outputFileName)
        {
            try
            {
                var asm = typeof(AdminElevationService).Assembly;
                if (!IsEmbeddedToolFileCurrent(asm, resourceSuffix, GetExtractedToolPath(outputFileName)))
                {
                    return false;
                }

                foreach (var sidecarSuffix in GetEmbeddedToolSidecars(outputFileName))
                {
                    if (!IsEmbeddedToolFileCurrent(asm, sidecarSuffix, GetExtractedToolPath(Path.GetFileName(sidecarSuffix))))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取嵌入工具提取后的目标文件路径（不执行提取）。
        /// </summary>
        /// <param name="outputFileName">输出文件名。</param>
        /// <returns>目标路径；文件名无效时返回 null。</returns>
        public static string GetExtractedToolPath(string outputFileName)
        {
            if (string.IsNullOrWhiteSpace(outputFileName))
            {
                return null;
            }

            var targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackageManager", "tools");
            return Path.Combine(targetDir, outputFileName);
        }

        private static IEnumerable<string> GetEmbeddedToolSidecars(string outputFileName)
        {
            if (string.Equals(outputFileName, "MftScanner.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(outputFileName, "CommonStartupTool.exe", StringComparison.OrdinalIgnoreCase))
            {
                yield return "MftScanner.Core.dll";
            }
        }

        private static void ExtractEmbeddedSidecar(System.Reflection.Assembly asm, string targetDir, string resourceSuffix)
        {
            if (asm == null || string.IsNullOrWhiteSpace(targetDir) || string.IsNullOrWhiteSpace(resourceSuffix))
            {
                return;
            }

            var targetPath = Path.Combine(targetDir, Path.GetFileName(resourceSuffix));
            if (IsEmbeddedToolFileCurrent(asm, resourceSuffix, targetPath))
            {
                return;
            }

            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(resourceName))
            {
                return;
            }

            using (var stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    return;
                }

                using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    stream.CopyTo(fs);
                }
            }
        }

        private static bool IsEmbeddedToolFileCurrent(System.Reflection.Assembly asm, string resourceSuffix, string targetPath)
        {
            return IsEmbeddedResourceCurrent(asm, resourceSuffix, targetPath);
        }

        private static bool IsEmbeddedResourceCurrent(System.Reflection.Assembly asm, string resourceSuffix, string targetPath)
        {
            if (asm == null || string.IsNullOrWhiteSpace(resourceSuffix) || string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
            {
                return false;
            }

            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(resourceName))
            {
                return false;
            }

            using (var stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    return false;
                }

                var resourceHash = ComputeSha256(stream);
                using (var fs = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var fileHash = ComputeSha256(fs);
                    return string.Equals(resourceHash, fileHash, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        private static string ComputeSha256(Stream stream)
        {
            if (stream == null)
            {
                return string.Empty;
            }

            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(stream);
                return BitConverter.ToString(bytes).Replace("-", string.Empty);
            }
        }
        
        /// <summary>
        /// 以管理员权限启动进程解锁工具的 UI 界面
        /// </summary>
        /// <param name="targets">要解锁的目标路径数组</param>
        /// <param name="pids">要解锁的进程 ID 数组，默认为 <c>null</c></param>
        /// <returns>如果进程成功启动则返回 <c>true</c>，否则返回 <c>false</c></returns>
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
        
        /// <summary>
        /// 以管理员权限启动进程解锁工具并返回结果文件路径
        /// </summary>
        /// <param name="targets">要解锁的目标路径数组</param>
        /// <param name="pids">要解锁的进程 ID 数组，默认为 <c>null</c></param>
        /// <returns>解锁结果的 JSONL 文件路径；如果启动失败则返回 <c>null</c></returns>
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
