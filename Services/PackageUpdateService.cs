using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
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
                var downloadLogGate = -10; // 每10%记录一次
                bool success;
                try
                {
                    success = await DownloadFileAsync(packageInfo.DownloadUrl,
                                                      tempFilePath,
                                                      progress =>
                                                      {
                                                          packageInfo.Progress = progress * 0.8; // 下载占80%进度
                                                          progressCallback?.Invoke(progress * 0.8, $"下载中... {progress:F1}%");
                                                          if ((progress >= (downloadLogGate + 10)) || (progress >= 100) || (progress <= 0))
                                                          {
                                                              LoggingService.LogInfo($"下载进度：{progress:F0}%");
                                                              downloadLogGate = (int)progress;
                                                          }
                                                      },
                                                      cancellationToken);
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
                catch
                {
                }

                // 解压文件
                packageInfo.Status = PackageStatus.Extracting;
                packageInfo.StatusText = "正在解压...";
                progressCallback?.Invoke(80, "开始解压");
                LoggingService.LogInfo($"开始解压：{tempFilePath} -> {targetLocalPath}");
                await TryUnlockProcessesAsync(targetLocalPath, forceUnlock, progressCallback, cancellationToken);

                var extractLogGate = 0; // 每25%记录一次
                try
                {
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
                                                        cancellationToken);
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
                                                      progress =>
                                                      {
                                                          packageInfo.Progress = progress;
                                                          progressCallback?.Invoke(progress, $"下载中... {progress:F1}%");
                                                      },
                                                      cancellationToken);

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
                catch
                {
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
                    catch
                    {
                    }
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
                    catch
                    {
                    }
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
            catch
            {
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
        private async Task<bool> DownloadFileAsync(string ftpUrl, string localPath, Action<double> progressCallback = null, CancellationToken cancellationToken = default)
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
                        if ((e.ProgressPercentage >= (lastLoggedProgress + 10)) || (e.ProgressPercentage >= 100) || (e.ProgressPercentage <= 0))
                        {
                            LoggingService.LogInfo($"下载进度(内部)：{e.ProgressPercentage}%");
                            lastLoggedProgress = e.ProgressPercentage;
                        }
                    };

                    using (cancellationToken.Register(() =>
                    {
                        try { client.CancelAsync(); } catch { }
                    }))
                    {
                        await client.DownloadFileTaskAsync(new Uri(ftpUrl), localPath);
                    }
                }

                try
                {
                    var size = new FileInfo(localPath).Length;
                    LoggingService.LogInfo($"下载完成(内部)：{localPath} | 大小={size} bytes");
                }
                catch
                {
                }

                return true;
            }
            catch (WebException wex) when (wex.Status == WebExceptionStatus.RequestCanceled)
            {
                LoggingService.LogInfo("下载已取消");
                throw new OperationCanceledException("下载取消", wex);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"下载失败: {ex.Message}");
                LoggingService.LogError(ex, $"下载失败：Url={ftpUrl} -> {localPath}");
                return false;
            }
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
                                    catch
                                    {
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
                                    catch
                                    {
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
                }, cancellationToken);
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
                var files = new List<string>();
                try
                {
                    if (Directory.Exists(targetDirectory))
                    {
                        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dll", ".exe", ".addin" };
                        files = Directory.EnumerateFiles(targetDirectory, "*.*", SearchOption.AllDirectories)
                                         .Where(p => exts.Contains(Path.GetExtension(p)))
                                         .Take(1000)
                                         .ToList();
                    }
                }
                catch { }

                var procs = GetLockingProcesses(files);
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
                    catch
                    {
                        proceed = false;
                    }
                }

                if (!proceed)
                {
                    return;
                }

                await Task.Run(() =>
                {
                    if (cancellationToken.IsCancellationRequested) { return; }
                    foreach (var p in procs)
                    {
                        try
                        {
                            if (p.CloseMainWindow())
                            {
                                p.WaitForExit(5000);
                            }
                        }
                        catch
                        {
                        }

                        try
                        {
                            if (!p.HasExited)
                            {
                                p.Kill();
                                p.WaitForExit(2000);
                            }
                        }
                        catch
                        {
                        }
                    }
                }, cancellationToken);
            }
            catch
            {
            }
        }

        /// <summary>
        /// 解除占用：对指定目标（文件或文件夹）进行占用检测并尝试关闭相关进程。
        /// 返回尝试处理的进程数量。
        /// </summary>
        public async Task<int> UnlockLocksForTargetsAsync(IEnumerable<string> targets, CancellationToken cancellationToken = default)
        {
            if (targets == null) return 0;
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
                            var dirFiles = Directory.EnumerateFiles(t, "*.*", SearchOption.AllDirectories)
                                                    .Take(10000);
                            files.AddRange(dirFiles);
                        }
                        else if (File.Exists(t))
                        {
                            files.Add(t);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            var procs = GetLockingProcesses(files);
            if (procs.Count == 0) return 0;

            try
            {
                await Task.Run(() =>
                {
                    foreach (var p in procs)
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        try
                        {
                            if (p.CloseMainWindow())
                            {
                                p.WaitForExit(3000);
                            }
                        }
                        catch { }

                        try
                        {
                            if (!p.HasExited)
                            {
                                p.Kill();
                                p.WaitForExit(2000);
                            }
                        }
                        catch { }
                    }
                }, cancellationToken);
            }
            catch { }

            return procs.Count;
        }

        public Task<int> UnlockLocksForDirectoryAsync(string targetDirectory, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetDirectory)) return Task.FromResult(0);
            return UnlockLocksForTargetsAsync(new[] { targetDirectory }, cancellationToken);
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
                            catch { }
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
