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
                // 更新状态为下载中
                packageInfo.Status = PackageStatus.Downloading;
                packageInfo.StatusText = "正在下载...";
                progressCallback?.Invoke(0, "开始下载");

                // 创建临时下载目录
                var tempDir = Path.Combine(Path.GetTempPath(), "PackageManager");
                if (!Directory.Exists(tempDir))
                    Directory.CreateDirectory(tempDir);

                var tempFilePath = Path.Combine(tempDir, $"{packageInfo.ProductName}.zip");

                // 下载文件
                var success = await DownloadFileAsync(packageInfo.DownloadUrl, tempFilePath, 
                    (progress) => {
                        packageInfo.Progress = progress * 0.8; // 下载占80%进度
                        progressCallback?.Invoke(progress * 0.8, $"下载中... {progress:F1}%");
                    });

                if (!success)
                {
                    packageInfo.Status = PackageStatus.Error;
                    packageInfo.StatusText = "下载失败";
                    return false;
                }

                // 解压文件
                packageInfo.Status = PackageStatus.Extracting;
                packageInfo.StatusText = "正在解压...";
                progressCallback?.Invoke(80, "开始解压");

                success = await ExtractPackageAsync(tempFilePath, packageInfo.LocalPath,
                    (progress) => {
                        var totalProgress = 80 + progress * 0.2; // 解压占20%进度
                        packageInfo.Progress = totalProgress;
                        progressCallback?.Invoke(totalProgress, $"解压中... {progress:F1}%");
                    });

                // 清理临时文件
                if (File.Exists(tempFilePath))
                    File.Delete(tempFilePath);

                if (success)
                {
                    packageInfo.Status = PackageStatus.Completed;
                    packageInfo.StatusText = "更新完成";
                    packageInfo.Progress = 100;
                    progressCallback?.Invoke(100, "更新完成");
                }
                else
                {
                    packageInfo.Status = PackageStatus.Error;
                    packageInfo.StatusText = "解压失败";
                }

                return success;
            }
            catch (Exception ex)
            {
                packageInfo.Status = PackageStatus.Error;
                packageInfo.StatusText = $"更新失败: {ex.Message}";
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
                using (var client = new WebClient())
                {
                    client.DownloadProgressChanged += (sender, e) =>
                    {
                        progressCallback?.Invoke(e.ProgressPercentage);
                    };

                    await client.DownloadFileTaskAsync(new Uri(ftpUrl), localPath);
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"下载失败: {ex.Message}");
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
                await Task.Run(() =>
                {
                    // 确保目标目录存在
                    if (Directory.Exists(extractPath))
                    {
                        Directory.Delete(extractPath, true);
                    }
                    
                    Directory.CreateDirectory(extractPath);

                    using (var archive = ZipFile.OpenRead(zipFilePath))
                    {
                        var totalEntries = archive.Entries.Count;
                        var processedEntries = 0;

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
                                entry.ExtractToFile(destinationPath, true);
                            }

                            processedEntries++;
                            var progress = (double)processedEntries / totalEntries * 100;
                            progressCallback?.Invoke(progress);
                        }
                    }
                });
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"解压失败: {ex.Message}");
                return false;
            }
        }
    }
}