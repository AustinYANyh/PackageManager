using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading.Tasks;
using PackageManager.Models;

namespace PackageManager.Services
{
    /// <summary>
    /// 包更新服务
    /// </summary>
    public class PackageUpdateService
    {
        /// <summary>
        /// 下载并更新包
        /// </summary>
        /// <param name="packageInfo">包信息</param>
        /// <param name="progressCallback">进度回调</param>
        /// <returns></returns>
        public async Task<bool> UpdatePackageAsync(PackageInfo packageInfo, Action<double, string> progressCallback = null)
        {
            try
            {
                // 记录开始更新
                LoggingService.LogInfo($"开始更新包：{packageInfo?.ProductName ?? "<unknown>"} | Url={packageInfo?.DownloadUrl} | Local={packageInfo?.LocalPath}");
                // 更新状态为下载中
                packageInfo.Status = PackageStatus.Downloading;
                packageInfo.StatusText = "正在下载...";
                progressCallback?.Invoke(0, "开始下载");

                // 创建临时下载目录
                var tempDir = Path.Combine(Path.GetTempPath(), "PackageManager");
                if (!Directory.Exists(tempDir))
                    Directory.CreateDirectory(tempDir);
                LoggingService.LogInfo($"使用临时目录：{tempDir}");

                var tempFilePath = Path.Combine(tempDir, $"{packageInfo.ProductName}.zip");
                LoggingService.LogInfo($"临时下载文件：{tempFilePath}");

                // 下载文件
                LoggingService.LogInfo($"开始下载：{packageInfo.DownloadUrl} -> {tempFilePath}");
                var downloadLogGate = -10; // 每10%记录一次
                var success = await DownloadFileAsync(packageInfo.DownloadUrl, tempFilePath, 
                    (progress) => {
                        packageInfo.Progress = progress * 0.8; // 下载占80%进度
                        progressCallback?.Invoke(progress * 0.8, $"下载中... {progress:F1}%");
                        if (progress >= downloadLogGate + 10 || progress >= 100 || progress <= 0)
                        {
                            LoggingService.LogInfo($"下载进度：{progress:F0}%");
                            downloadLogGate = (int)progress;
                        }
                    });

                if (!success)
                {
                    packageInfo.Status = PackageStatus.Error;
                    packageInfo.StatusText = "下载失败";
                    LoggingService.LogWarning($"下载失败：Url={packageInfo?.DownloadUrl} -> {tempFilePath}");
                    return false;
                }

                try
                {
                    var size = new FileInfo(tempFilePath).Length;
                    LoggingService.LogInfo($"下载完成：{tempFilePath} | 大小={size} bytes");
                }
                catch { }

                // 解压文件
                packageInfo.Status = PackageStatus.Extracting;
                packageInfo.StatusText = "正在解压...";
                progressCallback?.Invoke(80, "开始解压");
                LoggingService.LogInfo($"开始解压：{tempFilePath} -> {packageInfo.LocalPath}");

                var extractLogGate = 0; // 每25%记录一次
                success = await ExtractPackageAsync(tempFilePath, packageInfo.LocalPath,
                    (progress) => {
                        var totalProgress = 80 + progress * 0.2; // 解压占20%进度
                        packageInfo.Progress = totalProgress;
                        progressCallback?.Invoke(totalProgress, $"解压中... {progress:F1}%");
                        if (progress >= extractLogGate + 25 || progress >= 100)
                        {
                            LoggingService.LogInfo($"解压进度：{progress:F0}%");
                            extractLogGate = (int)progress;
                        }
                    });

                // 清理临时文件
                if (File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                        LoggingService.LogInfo($"已删除临时文件：{tempFilePath}");
                    }
                    catch (Exception delEx)
                    {
                        LoggingService.LogWarning($"删除临时文件失败：{tempFilePath} | {delEx.Message}");
                    }
                }

                if (success)
                {
                    packageInfo.Status = PackageStatus.Completed;
                    packageInfo.StatusText = "更新完成";
                    packageInfo.Progress = 100;
                    progressCallback?.Invoke(100, "更新完成");
                    LoggingService.LogInfo($"包更新完成：{packageInfo?.ProductName}");
                }
                else
                {
                    packageInfo.Status = PackageStatus.Error;
                    packageInfo.StatusText = "解压失败";
                    LoggingService.LogWarning($"解压失败：{tempFilePath} -> {packageInfo?.LocalPath}");
                }

                return success;
            }
            catch (Exception ex)
            {
                packageInfo.Status = PackageStatus.Error;
                packageInfo.StatusText = $"更新失败: {ex.Message}";
                LoggingService.LogError(ex, $"更新失败：{packageInfo?.ProductName}");
                return false;
            }
        }

        /// <summary>
        /// 下载文件
        /// </summary>
        private async Task<bool> DownloadFileAsync(string ftpUrl, string localPath, Action<double> progressCallback = null)
        {
            try
            {
                LoggingService.LogInfo($"准备下载：Url={ftpUrl} -> {localPath}");
                using (var client = new WebClient())
                {
                    var lastLoggedProgress = -10; // 每10%记录一次
                    client.DownloadProgressChanged += (sender, e) =>
                    {
                        progressCallback?.Invoke(e.ProgressPercentage);
                        if (e.ProgressPercentage >= lastLoggedProgress + 10 || e.ProgressPercentage >= 100 || e.ProgressPercentage <= 0)
                        {
                            LoggingService.LogInfo($"下载进度(内部)：{e.ProgressPercentage}%");
                            lastLoggedProgress = e.ProgressPercentage;
                        }
                    };

                    await client.DownloadFileTaskAsync(new Uri(ftpUrl), localPath);
                }
                try
                {
                    var size = new FileInfo(localPath).Length;
                    LoggingService.LogInfo($"下载完成(内部)：{localPath} | 大小={size} bytes");
                }
                catch { }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"下载失败: {ex.Message}");
                LoggingService.LogError(ex, $"下载失败：Url={ftpUrl} -> {localPath}");
                return false;
            }
        }

        /// <summary>
        /// 解压包文件
        /// </summary>
        private async Task<bool> ExtractPackageAsync(string zipFilePath, string extractPath, Action<double> progressCallback = null)
        {
            try
            {
                LoggingService.LogInfo($"准备解压：{zipFilePath} -> {extractPath}");
                await Task.Run(() =>
                {
                    // 清理目标目录（尽量安全删除，无法删除则改为覆盖写入）
                    if (Directory.Exists(extractPath))
                    {
                        if (!TrySafeDeleteDirectory(extractPath))
                        {
                            LoggingService.LogWarning($"无法完全删除目标目录，改为覆盖解压：{extractPath}");
                        }
                    }
                    
                    Directory.CreateDirectory(extractPath);

                    using (var archive = ZipFile.OpenRead(zipFilePath))
                    {
                        var totalEntries = archive.Entries.Count;
                        var processedEntries = 0;
                        var lastLoggedProgress = -25.0; // 每25%记录一次

                        foreach (var entry in archive.Entries)
                        {
                            var destinationPath = Path.Combine(extractPath, entry.FullName);
                            
                            // 确保目录存在
                            var directory = Path.GetDirectoryName(destinationPath);
                            if (!Directory.Exists(directory))
                                Directory.CreateDirectory(directory);

                            // 解压文件
                            if (!string.IsNullOrEmpty(entry.Name))
                            {
                                // 如果目标已存在且为只读，先解除只读
                                if (File.Exists(destinationPath))
                                {
                                    try
                                    {
                                        var attr = File.GetAttributes(destinationPath);
                                        if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                                        {
                                            File.SetAttributes(destinationPath, attr & ~FileAttributes.ReadOnly);
                                        }
                                    }
                                    catch { }
                                }

                                // 写入文件（失败则记录并重试一次）
                                try
                                {
                                    entry.ExtractToFile(destinationPath, true);
                                }
                                catch (UnauthorizedAccessException uae)
                                {
                                    LoggingService.LogWarning($"写入受限，尝试重试：{destinationPath} | {uae.Message}");
                                    try
                                    {
                                        // 再次尝试解除只读后写入
                                        if (File.Exists(destinationPath))
                                        {
                                            try
                                            {
                                                var attr = File.GetAttributes(destinationPath);
                                                File.SetAttributes(destinationPath, attr & ~FileAttributes.ReadOnly);
                                            }
                                            catch { }
                                        }
                                        entry.ExtractToFile(destinationPath, true);
                                    }
                                    catch (Exception ex2)
                                    {
                                        LoggingService.LogError(ex2, $"文件解压失败：{destinationPath}");
                                    }
                                }
                            }

                            processedEntries++;
                            var progress = (double)processedEntries / totalEntries * 100;
                            progressCallback?.Invoke(progress);
                            if (progress >= lastLoggedProgress + 25 || progress >= 100 || progress <= 0)
                            {
                                LoggingService.LogInfo($"解压进度(内部)：{progress:F0}% ({processedEntries}/{totalEntries})");
                                lastLoggedProgress = progress;
                            }
                        }
                    }
                });
                LoggingService.LogInfo($"解压完成：{zipFilePath} -> {extractPath}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"解压失败: {ex.Message}");
                LoggingService.LogError(ex, $"解压失败：{zipFilePath} -> {extractPath}");
                return false;
            }
        }

        /// <summary>
        /// 安全删除目录：去除只读属性，逐项删除，失败时返回false
        /// </summary>
        private static bool TrySafeDeleteDirectory(string path)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var attr = File.GetAttributes(file);
                        if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
                        }
                    }
                    catch { }
                }

                foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var attr = File.GetAttributes(dir);
                        if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            File.SetAttributes(dir, attr & ~FileAttributes.ReadOnly);
                        }
                    }
                    catch { }
                }

                try
                {
                    Directory.Delete(path, true);
                    return true;
                }
                catch (UnauthorizedAccessException uae)
                {
                    LoggingService.LogWarning($"删除目录权限不足：{path} | {uae.Message}");
                    return false;
                }
                catch (IOException ioe)
                {
                    LoggingService.LogWarning($"删除目录IO异常：{path} | {ioe.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"删除目录失败：{path} | {ex.Message}");
                return false;
            }
        }
    }
}