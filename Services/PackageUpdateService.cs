using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PackageManager.Models;

namespace PackageManager.Services
{
    /// <summary>
    /// 包更新服务
    /// </summary>
    public class PackageUpdateService
    {
        private const int ParallelDownloadThreadCount = 10;
        private const int ParallelDownloadMinBytes = 20 * 1024 * 1024;
        private const int DownloadBufferSize = 256 * 1024;

        private sealed class DownloadProbeResult
        {
            public bool SupportsParallelDownload { get; set; }
            public long ContentLength { get; set; }
        }

        private sealed class DownloadProgressInfo
        {
            public double ProgressPercentage { get; set; }
            public long DownloadedBytes { get; set; }
            public long TotalBytes { get; set; }
            public double SpeedBytesPerSecond { get; set; }
            public TimeSpan? EstimatedRemaining { get; set; }
        }

        private sealed class DownloadProgressReporter
        {
            private readonly object syncRoot = new object();
            private readonly string logPrefix;
            private readonly Action<DownloadProgressInfo> progressCallback;
            private readonly Stopwatch stopwatch = Stopwatch.StartNew();
            private long totalBytes;
            private long lastSampleBytes;
            private double lastSampleSeconds;
            private double smoothedSpeedBytesPerSecond;
            private double lastUiProgress = -1;
            private double lastUiSeconds = -1;
            private int lastLoggedBucket = -1;

            public DownloadProgressReporter(string logPrefix, long totalBytes, Action<DownloadProgressInfo> progressCallback)
            {
                this.logPrefix = logPrefix;
                this.totalBytes = Math.Max(0, totalBytes);
                this.progressCallback = progressCallback;
            }

            public void Report(long downloadedBytes, long latestTotalBytes = 0, bool force = false)
            {
                DownloadProgressInfo info = null;
                string logMessage = null;

                lock (syncRoot)
                {
                    if (latestTotalBytes > 0)
                    {
                        totalBytes = Math.Max(totalBytes, latestTotalBytes);
                    }

                    downloadedBytes = Math.Max(0, downloadedBytes);
                    if ((totalBytes > 0) && (downloadedBytes > totalBytes))
                    {
                        downloadedBytes = totalBytes;
                    }

                    var elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001d);
                    var deltaSeconds = elapsedSeconds - lastSampleSeconds;

                    if (force || (deltaSeconds >= 0.25d))
                    {
                        var deltaBytes = downloadedBytes - lastSampleBytes;
                        var instantSpeed = deltaSeconds > 0 ? Math.Max(0d, deltaBytes / deltaSeconds) : 0d;
                        if (instantSpeed > 0)
                        {
                            smoothedSpeedBytesPerSecond = smoothedSpeedBytesPerSecond <= 0
                                ? instantSpeed
                                : ((smoothedSpeedBytesPerSecond * 0.65d) + (instantSpeed * 0.35d));
                        }
                        else if ((smoothedSpeedBytesPerSecond <= 0) && (downloadedBytes > 0))
                        {
                            smoothedSpeedBytesPerSecond = downloadedBytes / elapsedSeconds;
                        }

                        lastSampleBytes = downloadedBytes;
                        lastSampleSeconds = elapsedSeconds;
                    }

                    var speedBytesPerSecond = smoothedSpeedBytesPerSecond > 0
                        ? smoothedSpeedBytesPerSecond
                        : ((downloadedBytes > 0) ? (downloadedBytes / elapsedSeconds) : 0d);
                    var progress = totalBytes > 0
                        ? Math.Min(100d, (downloadedBytes * 100d) / totalBytes)
                        : 0d;

                    TimeSpan? eta = null;
                    if ((totalBytes > 0) && (downloadedBytes < totalBytes) && (speedBytesPerSecond > 1))
                    {
                        eta = TimeSpan.FromSeconds((totalBytes - downloadedBytes) / speedBytesPerSecond);
                    }

                    var shouldNotifyUi = force ||
                                         (progress >= 100d) ||
                                         (lastUiSeconds < 0) ||
                                         ((elapsedSeconds - lastUiSeconds) >= 0.25d) ||
                                         ((progress - lastUiProgress) >= 0.25d);
                    if (shouldNotifyUi)
                    {
                        lastUiSeconds = elapsedSeconds;
                        lastUiProgress = progress;
                        info = new DownloadProgressInfo
                        {
                            ProgressPercentage = progress,
                            DownloadedBytes = downloadedBytes,
                            TotalBytes = totalBytes,
                            SpeedBytesPerSecond = speedBytesPerSecond,
                            EstimatedRemaining = eta
                        };
                    }

                    var logBucket = (int)(progress / 10d);
                    var shouldLog = force || (progress >= 100d) || (logBucket > lastLoggedBucket);
                    if (shouldLog)
                    {
                        lastLoggedBucket = logBucket;
                        logMessage =
                            $"{logPrefix}：{progress:F1}% | {FormatByteSize(downloadedBytes)}/{FormatByteSize(totalBytes)} | {FormatSpeed(speedBytesPerSecond)} | ETA {FormatEta(eta)}";
                    }
                }

                if (info != null)
                {
                    progressCallback?.Invoke(info);
                }

                if (!string.IsNullOrWhiteSpace(logMessage))
                {
                    LoggingService.LogInfo(logMessage);
                }
            }
        }

        /// <summary>
        /// 对外暴露的校验完成提示入口（供签名/加密校验流程调用）。
        /// </summary>
        public static void NotifyVerificationCompleted(PackageInfo packageInfo, bool success, string detail = null)
        {
            var title = "校验完成";
            var msg = $"{packageInfo?.ProductName ?? "包"} 签名/加密校验" + (success ? "成功" : "失败");
            if (!string.IsNullOrWhiteSpace(detail))
            {
                msg += $"（{detail}）";
            }

            ToastService.ShowToast(title, msg, success ? "Success" : "Error");
        }

        /// <summary>
        /// 下载并更新包
        /// </summary>
        /// <param name="packageInfo">包信息</param>
        /// <param name="progressCallback">进度回调</param>
        /// <param name="forceUnlock"></param>
        /// <returns></returns>
        public async Task<bool> UpdatePackageAsync(PackageInfo packageInfo, Action<double, string> progressCallback = null, bool forceUnlock = false, CancellationToken cancellationToken = default)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    packageInfo.Status = PackageStatus.Ready;
                    packageInfo.StatusText = "已取消";
                    packageInfo.Progress = 0;
                    return false;
                }
                var targetLocalPath = packageInfo.GetLocalPathForVersion(packageInfo.Version);
                if (!AdminElevationService.IsRunningAsAdministrator() && AdminElevationService.RequiresAdminForPath(targetLocalPath))
                {
                    packageInfo.Status = PackageStatus.Downloading;
                    packageInfo.StatusText = "正在以管理员权限执行更新...";
                    progressCallback?.Invoke(0, "请求管理员权限");
                    var elevatedOk = await AdminElevationService.RunElevatedUpdateAsync(packageInfo, forceUnlock);
                    return elevatedOk;
                }
                // 记录开始更新
                LoggingService.LogInfo($"开始更新包：{packageInfo?.ProductName ?? "<unknown>"} | Url={packageInfo?.DownloadUrl} | Local={targetLocalPath}");

                // 更新状态为下载中
                packageInfo.Status = PackageStatus.Downloading;
                packageInfo.StatusText = "正在下载...";
                progressCallback?.Invoke(0, "开始下载");

                if (cancellationToken.IsCancellationRequested)
                {
                    packageInfo.Status = PackageStatus.Ready;
                    packageInfo.StatusText = "已取消";
                    packageInfo.Progress = 0;
                    return false;
                }

                // 创建临时下载目录
                var tempDir = Path.Combine(Path.GetTempPath(), "PackageManager");
                if (!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }

                LoggingService.LogInfo($"使用临时目录：{tempDir}");

                var tempFilePath = Path.Combine(tempDir, $"{packageInfo.ProductName}.zip");
                LoggingService.LogInfo($"临时下载文件：{tempFilePath}");

                // 下载文件
                LoggingService.LogInfo($"开始下载：{packageInfo.DownloadUrl} -> {tempFilePath}");
                bool success;
                try
                {
                    success = await DownloadFileAsync(packageInfo.DownloadUrl,
                                                      tempFilePath,
                                                      info =>
                                                      {
                                                          if (cancellationToken.IsCancellationRequested)
                                                          {
                                                              return;
                                                          }

                                                          var scaledProgress = info.ProgressPercentage * 0.8; // 下载占80%进度
                                                          packageInfo.Progress = scaledProgress;
                                                          progressCallback?.Invoke(scaledProgress, BuildDownloadProgressMessage(info));
                                                      },
                                                      cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    packageInfo.Status = PackageStatus.Ready;
                    packageInfo.StatusText = "已取消";
                    packageInfo.Progress = 0;
                    return false;
                }

                if (!success)
                {
                    packageInfo.Status = PackageStatus.Error;
                    packageInfo.StatusText = "下载失败";
                    LoggingService.LogWarning($"下载失败：Url={packageInfo?.DownloadUrl} -> {tempFilePath}");
                    return false;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    packageInfo.Status = PackageStatus.Ready;
                    packageInfo.StatusText = "已取消";
                    packageInfo.Progress = 0;
                    return false;
                }

                try
                {
                    var size = new FileInfo(tempFilePath).Length;
                    LoggingService.LogInfo($"下载完成：{tempFilePath} | 大小={size} bytes");
                    ToastService.ShowToast("下载完成", $"{packageInfo?.ProductName ?? "包"} 已下载完成", "Success");
                }
                catch (Exception ex)
                {
                    LoggingService.LogWarning($"下载完成后信息记录失败：{tempFilePath} | {ex.Message}");
                }

                // 解压文件
                packageInfo.Status = PackageStatus.Extracting;
                
                packageInfo.StatusText = "正在检测占用进程...";
                progressCallback?.Invoke(80, "检测占用进程");
                LoggingService.LogInfo($"检测占用 {targetLocalPath}内文件的占用进程");
                await TryUnlockProcessesAsync(targetLocalPath, forceUnlock, progressCallback, cancellationToken).ConfigureAwait(false);

                var extractLogGate = 0; // 每25%记录一次
                try
                {
                    packageInfo.StatusText = "正在解压...";
                    progressCallback?.Invoke(80, "开始解压");
                    LoggingService.LogInfo($"开始解压：{tempFilePath} -> {targetLocalPath}");
                    success = await ExtractPackageAsync(tempFilePath,
                                                        targetLocalPath,
                                                        progress =>
                                                        {
                                                            var totalProgress = 80 + (progress * 0.2); // 解压占20%进度
                                                            packageInfo.Progress = totalProgress;
                                                            progressCallback?.Invoke(totalProgress, $"解压中... {progress:F1}%");
                                                            if ((progress >= (extractLogGate + 25)) || (progress >= 100))
                                                            {
                                                                LoggingService.LogInfo($"解压进度：{progress:F0}%");
                                                                extractLogGate = (int)progress;
                                                            }
                                                        },
                                                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    packageInfo.Status = PackageStatus.Ready;
                    packageInfo.StatusText = "已取消";
                    packageInfo.Progress = 0;
                    return false;
                }

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
                    LoggingService.LogWarning($"解压失败：{tempFilePath} -> {targetLocalPath}");
                }

                return success;
            }
            catch (OperationCanceledException)
            {
                packageInfo.Status = PackageStatus.Ready;
                packageInfo.StatusText = "已取消";
                packageInfo.Progress = 0;
                LoggingService.LogInfo($"更新已取消：{packageInfo?.ProductName}");
                return false;
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
        /// 仅下载ZIP包到指定路径（不进行解压）。
        /// </summary>
        /// <param name="packageInfo">包信息</param>
        /// <param name="localZipPath">保存的本地ZIP路径</param>
        /// <param name="progressCallback">进度回调（0-100，消息文本）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>成功与否</returns>
        public async Task<bool> DownloadZipOnlyAsync(PackageInfo packageInfo, string localZipPath, Action<double, string> progressCallback = null, CancellationToken cancellationToken = default)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    packageInfo.Status = PackageStatus.Ready;
                    packageInfo.StatusText = "已取消";
                    packageInfo.Progress = 0;
                    return false;
                }

                // 更新状态为下载中
                packageInfo.Status = PackageStatus.Downloading;
                packageInfo.StatusText = "正在下载...";
                progressCallback?.Invoke(0, "开始下载");

                // 执行下载（完整进度显示到100）
                var success = await DownloadFileAsync(packageInfo.DownloadUrl,
                                                      localZipPath,
                                                      info =>
                                                      {
                                                          if (cancellationToken.IsCancellationRequested)
                                                          {
                                                              return;
                                                          }

                                                          packageInfo.Progress = info.ProgressPercentage;
                                                          progressCallback?.Invoke(info.ProgressPercentage, BuildDownloadProgressMessage(info));
                                                      },
                                                      cancellationToken).ConfigureAwait(false);

                if (!success)
                {
                    packageInfo.Status = PackageStatus.Error;
                    packageInfo.StatusText = "下载失败";
                    LoggingService.LogWarning($"仅下载失败：Url={packageInfo?.DownloadUrl} -> {localZipPath}");
                    return false;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    packageInfo.Status = PackageStatus.Ready;
                    packageInfo.StatusText = "已取消";
                    packageInfo.Progress = 0;
                    return false;
                }

                try
                {
                    var size = new FileInfo(localZipPath).Length;
                    LoggingService.LogInfo($"仅下载完成：{localZipPath} | 大小={size} bytes");
                    ToastService.ShowToast("下载完成", $"{Path.GetFileName(localZipPath)} 已保存", "Success");
                }
                catch (Exception ex)
                {
                    LoggingService.LogWarning($"仅下载完成后信息记录失败：{localZipPath} | {ex.Message}");
                }

                packageInfo.Status = PackageStatus.Completed;
                packageInfo.StatusText = "下载完成";
                packageInfo.Progress = 100;
                progressCallback?.Invoke(100, "下载完成");
                return true;
            }
            catch (OperationCanceledException)
            {
                packageInfo.Status = PackageStatus.Ready;
                packageInfo.StatusText = "已取消";
                packageInfo.Progress = 0;
                LoggingService.LogInfo($"仅下载已取消：{packageInfo?.ProductName}");
                return false;
            }
            catch (Exception ex)
            {
                packageInfo.Status = PackageStatus.Error;
                packageInfo.StatusText = $"下载失败: {ex.Message}";
                LoggingService.LogError(ex, $"仅下载失败：{packageInfo?.ProductName} -> {localZipPath}");
                return false;
            }
        }

        /// <summary>
        /// 安全删除目录：解除只读/隐藏/系统属性，尽可能修复ACL，重试并使用回退手段删除
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
                        var newAttr = attr & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
                        if (newAttr != attr)
                        {
                            File.SetAttributes(file, newAttr);
                        }
                        try
                        {
                            var user = WindowsIdentity.GetCurrent()?.User;
                            if (user != null)
                            {
                                var sec = File.GetAccessControl(file);
                                sec.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
                                File.SetAccessControl(file, sec);
                            }
                        }
                        catch (Exception aclEx)
                        {
                            LoggingService.LogWarning($"文件ACL调整失败：{file} | {aclEx.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogWarning($"文件属性处理失败：{file} | {ex.Message}");
                    }
                }

                foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var attr = File.GetAttributes(dir);
                        var newAttr = attr & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
                        if (newAttr != attr)
                        {
                            File.SetAttributes(dir, newAttr);
                        }
                        try
                        {
                            var user = WindowsIdentity.GetCurrent()?.User;
                            if (user != null)
                            {
                                var sec = Directory.GetAccessControl(dir);
                                sec.AddAccessRule(new FileSystemAccessRule(
                                    user,
                                    FileSystemRights.FullControl,
                                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                    PropagationFlags.None,
                                    AccessControlType.Allow));
                                Directory.SetAccessControl(dir, sec);
                            }
                        }
                        catch (Exception aclEx)
                        {
                            LoggingService.LogWarning($"目录ACL调整失败：{dir} | {aclEx.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogWarning($"目录属性处理失败：{dir} | {ex.Message}");
                    }
                }

                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        Directory.Delete(path, true);
                        return true;
                    }
                    catch (UnauthorizedAccessException uae)
                    {
                        LoggingService.LogWarning($"删除目录权限不足(第{attempt}次)：{path} | {uae.Message}");
                        Thread.Sleep(100);
                    }
                    catch (IOException ioe)
                    {
                        LoggingService.LogWarning($"删除目录IO异常(第{attempt}次)：{path} | {ioe.Message}");
                        Thread.Sleep(150);
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogWarning($"删除目录异常(第{attempt}次)：{path} | {ex.Message}");
                        Thread.Sleep(150);
                    }
                }

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c rd /s /q \"" + path + "\"",
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        Verb = AdminElevationService.IsRunningAsAdministrator() ? null : "runas"
                    };
                    var p = Process.Start(psi);
                    p?.WaitForExit(20000);
                    if (!Directory.Exists(path))
                    {
                        return true;
                    }
                    LoggingService.LogWarning($"命令回退删除失败：{path}");
                    return false;
                }
                catch (Exception ex)
                {
                    LoggingService.LogWarning($"命令回退删除异常：{path} | {ex.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"删除目录失败：{path} | {ex.Message}");
                return false;
            }
        }

        private static bool IsFileLocked(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    return false;
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"检测文件锁定失败：{path} | {ex.Message}");
                return false;
            }
        }

        private static void CopyZipEntryToFile(ZipArchiveEntry entry, string path)
        {
            using (var inStream = entry.Open())
            using (var outStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                inStream.CopyTo(outStream);
            }
        }

        /// <summary>
        /// 下载文件
        /// </summary>
        private async Task<bool> DownloadFileAsync(string ftpUrl, string localPath, Action<DownloadProgressInfo> progressCallback = null, CancellationToken cancellationToken = default)
        {
            try
            {
                LoggingService.LogInfo($"准备下载：Url={ftpUrl} -> {localPath}");
                DownloadProbeResult probe = null;
                var uri = new Uri(ftpUrl);
                if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    probe = await ProbeParallelDownloadAsync(uri, cancellationToken).ConfigureAwait(false);
                    if (probe != null && probe.SupportsParallelDownload)
                    {
                        LoggingService.LogInfo($"检测到服务器支持分片下载，启用{ParallelDownloadThreadCount}线程并行下载：{ftpUrl}");
                        return await DownloadFileInParallelAsync(uri, localPath, probe.ContentLength, ParallelDownloadThreadCount, progressCallback, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                LoggingService.LogInfo($"回退到单线程下载：{ftpUrl}");
                return await DownloadFileSingleAsync(uri, localPath, probe?.ContentLength ?? 0, progressCallback, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryDeleteFileQuietly(localPath);
                LoggingService.LogInfo("下载已取消");
                throw;
            }
            catch (WebException wex) when (wex.Status == WebExceptionStatus.RequestCanceled)
            {
                TryDeleteFileQuietly(localPath);
                LoggingService.LogInfo("下载已取消");
                throw new OperationCanceledException("下载取消", wex, cancellationToken);
            }
            catch (Exception ex) when (IsCancellationException(ex, cancellationToken))
            {
                TryDeleteFileQuietly(localPath);
                LoggingService.LogInfo("下载已取消");
                throw new OperationCanceledException("下载取消", ex, cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"下载失败: {ex.Message}");
                LoggingService.LogError(ex, $"下载失败：Url={ftpUrl} -> {localPath}");
                return false;
            }
        }

        private async Task<bool> DownloadFileSingleAsync(Uri uri, string localPath, long expectedContentLength, Action<DownloadProgressInfo> progressCallback, CancellationToken cancellationToken)
        {
            using (var client = new WebClient())
            {
                var reporter = new DownloadProgressReporter("下载进度(单线程)", expectedContentLength, progressCallback);
                client.DownloadProgressChanged += (sender, e) =>
                {
                    reporter.Report(e.BytesReceived, e.TotalBytesToReceive);
                };

                using (cancellationToken.Register(() =>
                {
                    try { client.CancelAsync(); } catch (Exception ex) { LoggingService.LogWarning($"取消下载失败：{ex.Message}"); }
                }))
                {
                    try
                    {
                        await client.DownloadFileTaskAsync(uri, localPath).ConfigureAwait(false);
                    }
                    catch (WebException ex) when (IsCancellationException(ex, cancellationToken))
                    {
                        throw new OperationCanceledException("下载取消", ex, cancellationToken);
                    }
                }

                var finalSize = File.Exists(localPath) ? new FileInfo(localPath).Length : 0;
                reporter.Report(finalSize, finalSize > 0 ? finalSize : expectedContentLength, true);
            }

            try
            {
                var size = new FileInfo(localPath).Length;
                LoggingService.LogInfo($"下载完成(内部)：{localPath} | 大小={size} bytes");
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"下载完成后信息记录失败(内部)：{localPath} | {ex.Message}");
            }

            return true;
        }

        private async Task<DownloadProbeResult> ProbeParallelDownloadAsync(Uri uri, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            try
            {
                var contentLength = await GetHttpContentLengthAsync(uri, cancellationToken).ConfigureAwait(false);
                if (contentLength < ParallelDownloadMinBytes)
                {
                    LoggingService.LogInfo($"文件大小{contentLength} bytes，小于并行下载阈值，继续单线程下载。");
                    return new DownloadProbeResult
                    {
                        SupportsParallelDownload = false,
                        ContentLength = contentLength
                    };
                }

                var supportsRange = await SupportsHttpRangeRequestAsync(uri, cancellationToken).ConfigureAwait(false);
                return new DownloadProbeResult
                {
                    SupportsParallelDownload = supportsRange,
                    ContentLength = contentLength
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"探测并行下载能力失败，回退单线程：{uri} | {ex.Message}");
                return null;
            }
        }

        private async Task<long> GetHttpContentLengthAsync(Uri uri, CancellationToken cancellationToken)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "HEAD";
            request.Proxy = null;

            using (cancellationToken.Register(() =>
            {
                try { request.Abort(); } catch { }
            }))
            using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            {
                return response.ContentLength;
            }
        }

        private async Task<bool> SupportsHttpRangeRequestAsync(Uri uri, CancellationToken cancellationToken)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.AddRange(0, 0);
            request.Proxy = null;

            using (cancellationToken.Register(() =>
            {
                try { request.Abort(); } catch { }
            }))
            using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
            {
                return response.StatusCode == HttpStatusCode.PartialContent;
            }
        }

        private async Task<bool> DownloadFileInParallelAsync(Uri uri, string localPath, long contentLength, int threadCount, Action<DownloadProgressInfo> progressCallback, CancellationToken cancellationToken)
        {
            var effectiveThreadCount = (int)Math.Max(1, Math.Min(threadCount, contentLength));
            var targetDirectory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            var totalDownloadedBytes = 0L;
            var reporter = new DownloadProgressReporter("下载进度(并行)", contentLength, progressCallback);
            Action<int> accumulateDownloadedBytes = bytesRead =>
            {
                var downloaded = Interlocked.Add(ref totalDownloadedBytes, bytesRead);
                reporter.Report(downloaded, contentLength);
            };

            try
            {
                ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, effectiveThreadCount + 2);

                var partSize = contentLength / effectiveThreadCount;
                var downloadTasks = new List<Task>();
                using (var initializer = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    initializer.SetLength(contentLength);
                }

                for (var index = 0; index < effectiveThreadCount; index++)
                {
                    var start = partSize * index;
                    var end = (index == effectiveThreadCount - 1) ? (contentLength - 1) : (start + partSize - 1);
                    downloadTasks.Add(DownloadRangeToFileAsync(uri, localPath, start, end, accumulateDownloadedBytes, cancellationToken));
                }

                await Task.WhenAll(downloadTasks).ConfigureAwait(false);
                reporter.Report(contentLength, contentLength, true);
                var finalSize = new FileInfo(localPath).Length;
                LoggingService.LogInfo($"并行下载完成：{localPath} | 大小={finalSize} bytes");
                return true;
            }
            catch (OperationCanceledException)
            {
                TryDeleteFileQuietly(localPath);
                LoggingService.LogInfo("并行下载已取消");
                throw;
            }
            catch (Exception ex) when (IsCancellationException(ex, cancellationToken))
            {
                TryDeleteFileQuietly(localPath);
                LoggingService.LogInfo("并行下载已取消");
                throw new OperationCanceledException("并行下载取消", ex, cancellationToken);
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"并行下载失败，回退单线程：{uri} | {ex.Message}");
                TryDeleteFileQuietly(localPath);
                return await DownloadFileSingleAsync(uri, localPath, contentLength, progressCallback, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task DownloadRangeToFileAsync(Uri uri, string localPath, long start, long end, Action<int> accumulateDownloadedBytes, CancellationToken cancellationToken)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.AddRange(start, end);
            request.Proxy = null;
            request.ReadWriteTimeout = 30000;
            request.Timeout = 30000;

            try
            {
                using (cancellationToken.Register(() =>
                {
                    try { request.Abort(); } catch { }
                }))
                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                using (var input = response.GetResponseStream())
                using (var output = new FileStream(localPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, DownloadBufferSize, true))
                {
                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        throw new InvalidOperationException($"服务器未返回206分段响应，实际状态：{(int)response.StatusCode} {response.StatusCode}");
                    }

                    output.Seek(start, SeekOrigin.Begin);
                    var buffer = new byte[DownloadBufferSize];
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }

                        await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                        accumulateDownloadedBytes?.Invoke(read);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsCancellationException(ex, cancellationToken))
            {
                throw new OperationCanceledException($"分片下载取消：{start}-{end}", ex, cancellationToken);
            }
        }

        private static string BuildDownloadProgressMessage(DownloadProgressInfo info)
        {
            if (info == null)
            {
                return "下载中...";
            }

            return $"下载中... {info.ProgressPercentage:F1}% | {FormatSpeed(info.SpeedBytesPerSecond)} | ETA {FormatEta(info.EstimatedRemaining)}";
        }

        private static string FormatSpeed(double speedBytesPerSecond)
        {
            if (speedBytesPerSecond <= 0)
            {
                return "0.00 MB/s";
            }

            return $"{(speedBytesPerSecond / 1024d / 1024d):F2} MB/s";
        }

        private static string FormatByteSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 MB";
            }

            var units = new[] { "B", "KB", "MB", "GB", "TB" };
            var value = (double)bytes;
            var unitIndex = 0;
            while ((value >= 1024d) && (unitIndex < (units.Length - 1)))
            {
                value /= 1024d;
                unitIndex++;
            }

            return unitIndex == 0 ? $"{value:F0} {units[unitIndex]}" : $"{value:F1} {units[unitIndex]}";
        }

        private static string FormatEta(TimeSpan? eta)
        {
            if (!eta.HasValue)
            {
                return "--";
            }

            var value = eta.Value;
            if (value.TotalHours >= 1)
            {
                return value.ToString(@"hh\:mm\:ss");
            }

            return value.ToString(@"mm\:ss");
        }

        private static void TryDeleteFileQuietly(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore cleanup failures
            }
        }

        private static bool IsCancellationException(Exception ex, CancellationToken cancellationToken)
        {
            if (ex == null)
            {
                return false;
            }

            if (ex is OperationCanceledException)
            {
                return true;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return true;
            }

            if (ex is AggregateException aggregateException)
            {
                return aggregateException.Flatten().InnerExceptions.Any(inner => IsCancellationException(inner, cancellationToken));
            }

            if (ex is WebException webException)
            {
                if (webException.Status == WebExceptionStatus.RequestCanceled)
                {
                    return true;
                }

                return IsCancellationException(webException.InnerException, cancellationToken);
            }

            return IsCancellationException(ex.InnerException, cancellationToken);
        }

        /// <summary>
        /// 解压包文件
        /// </summary>
        private async Task<bool> ExtractPackageAsync(string zipFilePath, string extractPath, Action<double> progressCallback = null, CancellationToken cancellationToken = default)
        {
            try
            {
                LoggingService.LogInfo($"准备解压：{zipFilePath} -> {extractPath}");
                var pendingReplacements = new List<Tuple<string, string>>();
                var pendingRemain = 0;
                await Task.Run(() =>
                {
                    if (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(); }
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
                            if (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(); }
                            var destinationPath = Path.Combine(extractPath, entry.FullName);

                            // 确保目录存在
                            var directory = Path.GetDirectoryName(destinationPath);
                            if (!Directory.Exists(directory))
                            {
                                Directory.CreateDirectory(directory);
                            }

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
                                catch (Exception ex)
                                    {
                                    LoggingService.LogWarning($"解除只读失败：{destinationPath} | {ex.Message}");
                                    }
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
                                            catch
                                            {
                                            }
                                        }

                                        entry.ExtractToFile(destinationPath, true);
                                    }
                                    catch (Exception ex2)
                                    {
                                        LoggingService.LogError(ex2, $"文件解压失败：{destinationPath}");
                                    }
                                }
                                catch (IOException ioe)
                                {
                                    var locked = IsFileLocked(destinationPath);
                                    if (locked)
                                    {
                                        var pendingPath = destinationPath + ".pm.pending";
                                        try
                                        {
                                            CopyZipEntryToFile(entry, pendingPath);
                                            pendingReplacements.Add(Tuple.Create(pendingPath, destinationPath));
                                            LoggingService.LogWarning($"目标被占用，已暂存：{destinationPath} -> {pendingPath}");
                                        }
                                        catch (Exception ex3)
                                        {
                                            LoggingService.LogError(ex3, $"暂存失败：{pendingPath}");
                                        }
                                    }
                                    else
                                    {
                                        LoggingService.LogError(ioe, $"文件解压失败：{destinationPath}");
                                    }
                                }
                            }

                            processedEntries++;
                            var progress = ((double)processedEntries / totalEntries) * 100;
                            progressCallback?.Invoke(progress);
                            if ((progress >= (lastLoggedProgress + 25)) || (progress >= 100) || (progress <= 0))
                            {
                                LoggingService.LogInfo($"解压进度(内部)：{progress:F0}% ({processedEntries}/{totalEntries})");
                                lastLoggedProgress = progress;
                            }
                        }

                        foreach (var item in pendingReplacements)
                        {
                            if (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(); }
                            var temp = item.Item1;
                            var dest = item.Item2;
                            try
                            {
                                if (!IsFileLocked(dest))
                                {
                                    File.Copy(temp, dest, true);
                                    try
                                    {
                                        File.Delete(temp);
                                    }
                                    catch (Exception ex)
                                    {
                                        LoggingService.LogWarning($"删除暂存文件失败：{temp} | {ex.Message}");
                                    }
                                }
                            }
                            catch (Exception ex4)
                            {
                                LoggingService.LogWarning($"替换失败：{dest} | {ex4.Message}");
                            }
                        }

                        pendingRemain = pendingReplacements.Count(t => File.Exists(t.Item1));
                    }
                }, cancellationToken).ConfigureAwait(false);
                LoggingService.LogInfo($"解压完成：{zipFilePath} -> {extractPath}");
                if (pendingRemain > 0)
                {
                    ToastService.ShowToast("解压完成", $"部分文件被占用，已暂存为 .pm.pending（{pendingRemain} 项）", "Warning");
                }
                else
                {
                    ToastService.ShowToast("解压完成", $"{Path.GetFileName(zipFilePath)} 已解压到目标目录", "Success");
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                LoggingService.LogInfo("解压已取消");
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"解压失败: {ex.Message}");
                LoggingService.LogError(ex, $"解压失败：{zipFilePath} -> {extractPath}");
                return false;
            }
        }

        private async Task TryUnlockProcessesAsync(string targetDirectory, bool forceUnlock, Action<double, string> progressCallback, CancellationToken cancellationToken)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                
                var procs = await Task.Run(() =>
                {
                    var filesLocal = new List<string>();
                    try
                    {
                        if (Directory.Exists(targetDirectory))
                        {
                            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dll", ".exe", ".addin" };
                            filesLocal = Directory.EnumerateFiles(targetDirectory, "*.*", SearchOption.AllDirectories)
                                                  .Where(p => exts.Contains(Path.GetExtension(p)))
                                                  .Take(1000)
                                                  .ToList();
                        }
                    }
                    catch (Exception ex) { LoggingService.LogWarning($"列举待解锁文件失败：{targetDirectory} | {ex.Message}"); }
                    return GetLockingProcesses(filesLocal);
                }, cancellationToken).ConfigureAwait(false);
                if (procs.Count == 0)
                {
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                progressCallback?.Invoke(80, "检测到占用进程，准备解锁");
                var proceed = forceUnlock;
                if (!forceUnlock)
                {
                    try
                    {
                        var msg = "检测到下列进程可能占用更新文件：" + string.Join(", ", procs.Select(p => p.ProcessName).Distinct()) + "\n是否关闭以继续更新？";
                        proceed = MessageBox.Show(msg, "解锁占用", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogWarning($"显示解锁提示失败：{ex.Message}");
                        proceed = false;
                    }
                }

                if (!proceed)
                {
                    return;
                }

                var ids = procs.Select(x =>
                {
                    try { return x.Id; } catch (Exception ex) { LoggingService.LogWarning($"读取进程ID失败：{ex.Message}"); return 0; }
                }).Where(id => id > 0).Distinct().ToArray();
                if (ids.Length > 0)
                {
                    await KillProcessesAsync(ids, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"解锁占用进程失败：{targetDirectory} | {ex.Message}");
            }
        }
        
        public class LockingProcessInfo
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string ExecutablePath { get; set; }
            public string Title { get; set; }
        }
        
        public async Task<List<LockingProcessInfo>> ListLockingProcessesForTargetsAsync(IEnumerable<string> targets, CancellationToken cancellationToken = default)
        {
            if (targets == null) return new List<LockingProcessInfo>();
            var files = new List<string>();
            try
            {
                foreach (var t in targets)
                {
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    try
                    {
                        if (Directory.Exists(t))
                        {
                            var dirFiles = Directory.EnumerateFiles(t, "*.*", SearchOption.AllDirectories).Take(10000);
                            files.AddRange(dirFiles);
                        }
                        else if (File.Exists(t))
                        {
                            files.Add(t);
                        }
                    }
                    catch (Exception ex) { LoggingService.LogWarning($"列举目标文件失败：{t} | {ex.Message}"); }
                }
            }
            catch (Exception ex) { LoggingService.LogWarning($"聚合目标文件失败：{ex.Message}"); }
            var procs = GetLockingProcesses(files);
            var result = new List<LockingProcessInfo>();
            await Task.Run(() =>
            {
                foreach (var p in procs)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    try
                    {
                        var info = new LockingProcessInfo
                        {
                            Id = p.Id,
                            Name = p.ProcessName,
                            Title = p.MainWindowTitle,
                            ExecutablePath = TryGetMainModulePath(p)
                        };
                        if (!result.Any(x => x.Id == info.Id))
                        {
                            result.Add(info);
                        }
                    }
                    catch (Exception ex) { LoggingService.LogWarning($"读取进程信息失败：{ex.Message}"); }
                }
            }, cancellationToken).ConfigureAwait(false);
            return result;
        }
        
        public async Task<bool> KillProcessesAsync(IEnumerable<int> pids, CancellationToken cancellationToken = default)
        {
            var list = pids == null ? new List<int>() : pids.Where(id => id > 0).Distinct().ToList();
            if (list.Count == 0) return false;
            try
            {
                return await Task.Run(() =>
                {
                    var args = "/c " + ("taskkill " + string.Join(" ", list.Select(id => "/PID " + id)) + " /F /T");
                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = args,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        Verb = AdminElevationService.IsRunningAsAdministrator() ? null : "runas"
                    };
                    var proc = Process.Start(psi);
                    proc?.WaitForExit(20000);
                    return true;
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"结束占用进程失败：{string.Join(",", list)} | {ex.Message}");
                return false;
            }
        }
        
        private static string TryGetMainModulePath(Process p)
        {
            try
            {
                return p?.MainModule?.FileName;
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"读取进程主模块路径失败：{ex.Message}");
                return null;
            }
        }

        private static List<Process> GetLockingProcesses(List<string> files)
        {
            var result = new List<Process>();
            if (files == null || files.Count == 0) return result;
            uint handle;
            var key = Guid.NewGuid().ToString();
            if (RmStartSession(out handle, 0, key) != 0) return result;
            try
            {
                var rc = RmRegisterResources(handle, (uint)files.Count, files.ToArray(), 0, null, 0, null);
                if (rc != 0) return result;
                uint needed;
                uint count = 0;
                uint reasons = 0;
                rc = RmGetList(handle, out needed, ref count, null, ref reasons);
                if (rc == 234 && needed > 0)
                {
                    var infos = new RM_PROCESS_INFO[needed];
                    count = needed;
                    rc = RmGetList(handle, out needed, ref count, infos, ref reasons);
                    if (rc == 0)
                    {
                        for (var i = 0; i < count; i++)
                        {
                            try
                            {
                                var pid = infos[i].Process.dwProcessId;
                                var p = Process.GetProcessById(pid);
                                result.Add(p);
                            }
                            catch (Exception ex) { LoggingService.LogWarning($"获取占用进程失败：{ex.Message}"); }
                        }
                    }
                }
            }
            finally
            {
                RmEndSession(handle);
            }
            return result;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        private enum RM_APP_TYPE
        {
            RmUnknownApp = 0,
            RmMainWindow = 1,
            RmOtherWindow = 2,
            RmService = 3,
            RmExplorer = 4,
            RmConsole = 5,
            RmCritical = 1000
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string strServiceShortName;
            public RM_APP_TYPE ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFileNames, uint nApplications, RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmGetList(uint pSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmEndSession(uint pSessionHandle);
    }
}
