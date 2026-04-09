using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IOPath = System.IO.Path;

namespace PackageManager.Services.RevitCleanup
{
    internal sealed class MftIndexProvider
    {
        private const string ToolResourceSuffix = "MftScanner.exe";
        private const string ToolFileName = "MftScanner.exe";
        private const int MmfSize = 32 * 1024 * 1024; // 32 MB
        private const uint MmfMagic = 0x4D4D4650;     // "MMFP"
        private const ushort MmfVersion = 1;
        private const int MmfStatusSuccess = 1;
        private const int MmfStatusError = 2;

        private readonly object gate = new object();
        private bool availabilityChecked;
        private string toolPath;

        public bool IsAvailable()
        {
            lock (gate)
            {
                if (availabilityChecked) return !string.IsNullOrWhiteSpace(toolPath);
                availabilityChecked = true;
                toolPath = EnsureToolExtracted();
                if (!string.IsNullOrWhiteSpace(toolPath))
                {
                    LoggingService.LogInfo($"MFT 扫描工具已就绪：{toolPath}");
                }
                return !string.IsNullOrWhiteSpace(toolPath);
            }
        }

        public Task<RevitFileQueryResult> QueryAsync(RevitFileQueryOptions options,
            IProgress<RevitFileQueryProgress> progress, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                if (!IsAvailable()) return null;

                progress?.Report(new RevitFileQueryProgress
                {
                    Message = "正在通过 MFT 扫描文件（需要管理员权限）..."
                });

                var mmfName = "PackageManager_MftScan_" + Guid.NewGuid().ToString("N");
                var doneEventName = mmfName + "_Done";

                MemoryMappedFile mmf = null;
                EventWaitHandle doneEvent = null;

                try
                {
                    mmf = MemoryMappedFile.CreateNew(mmfName, MmfSize);
                    doneEvent = new EventWaitHandle(false, EventResetMode.ManualReset, doneEventName);

                    var args = BuildArguments(mmfName, options);
                    LoggingService.LogInfo($"MFT 扫描启动：{toolPath} {args}");

                    var psi = new ProcessStartInfo
                    {
                        FileName = toolPath,
                        Arguments = args,
                        UseShellExecute = true,
                        CreateNoWindow = false,
                    };

                    using (var process = Process.Start(psi))
                    {
                        if (process == null)
                        {
                            LoggingService.LogWarning("MFT 扫描进程启动失败（Process.Start 返回 null）");
                            return null;
                        }

                        process.WaitForExit();
                        cancellationToken.ThrowIfCancellationRequested();

                        if (process.ExitCode != 0)
                        {
                            LoggingService.LogWarning($"MFT 扫描进程退出码：{process.ExitCode}");
                            return null;
                        }
                    }

                    // 子进程已退出，Done 事件应已 Set；5 秒超时作为调度竞争的安全兜底
                    if (!doneEvent.WaitOne(TimeSpan.FromSeconds(5)))
                    {
                        LoggingService.LogWarning("MFT 扫描完成事件超时");
                        return null;
                    }

                    progress?.Report(new RevitFileQueryProgress { Message = "正在解析 MFT 扫描结果..." });

                    var files = ReadSharedMemoryResults(mmf, options);
                    if (files == null)
                    {
                        LoggingService.LogWarning("MFT 扫描共享内存读取失败");
                        return null;
                    }

                    LoggingService.LogInfo($"MFT 扫描完成，找到 {files.Count} 个文件");
                    return new RevitFileQueryResult
                    {
                        SourceKind = RevitFileQuerySourceKind.MftIndex,
                        ProviderDisplayText = "MFT索引",
                        Files = files,
                    };
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    LoggingService.LogInfo("用户取消了 MFT 扫描的 UAC 提权请求");
                    return null;
                }
                finally
                {
                    doneEvent?.Dispose();
                    mmf?.Dispose();
                }
            }, cancellationToken);
        }

        private List<RevitIndexedFileInfo> ReadSharedMemoryResults(MemoryMappedFile mmf, RevitFileQueryOptions options)
        {
            try
            {
                using (var stream = mmf.CreateViewStream(0, MmfSize, MemoryMappedFileAccess.Read))
                using (var reader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: false))
                {
                    if (reader.ReadUInt32() != MmfMagic) return null;
                    if (reader.ReadUInt16() != MmfVersion) return null;
                    var status = reader.ReadInt32();
                    var recordCount = reader.ReadInt32();
                    reader.ReadBytes(6); // reserved

                    if (status == MmfStatusError) return null;

                    var rootLookup = options.Roots.ToDictionary(
                        r => RevitCleanupPathUtility.NormalizePath(r.RootPath),
                        r => r,
                        StringComparer.OrdinalIgnoreCase);

                    var files = new List<RevitIndexedFileInfo>(recordCount);
                    var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    for (var i = 0; i < recordCount; i++)
                    {
                        var pathByteLen = reader.ReadInt32();
                        var fullPath = Encoding.Unicode.GetString(reader.ReadBytes(pathByteLen));
                        var sizeBytes = reader.ReadInt64();
                        var modifiedUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
                        var dispByteLen = reader.ReadInt32();
                        var rootDisplayName = Encoding.Unicode.GetString(reader.ReadBytes(dispByteLen));

                        fullPath = RevitCleanupPathUtility.NormalizePath(fullPath);
                        if (string.IsNullOrWhiteSpace(fullPath)) continue;
                        if (!dedup.Add(fullPath)) continue;

                        RevitFileQueryRoot matchingRoot = null;
                        foreach (var kvp in rootLookup)
                        {
                            if (RevitCleanupPathUtility.IsPathUnderRoot(fullPath, kvp.Key))
                            {
                                matchingRoot = kvp.Value;
                                break;
                            }
                        }

                        files.Add(new RevitIndexedFileInfo
                        {
                            FullPath = fullPath,
                            FileName = IOPath.GetFileName(fullPath),
                            SizeBytes = sizeBytes,
                            ModifiedTimeUtc = modifiedUtc,
                            RootPath = matchingRoot?.RootPath,
                            RootDisplayName = matchingRoot?.DisplayName ?? rootDisplayName,
                            SourceKind = RevitFileQuerySourceKind.MftIndex,
                        });
                    }

                    return files.OrderByDescending(f => f.ModifiedTimeUtc).ToList();
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning($"读取 MFT 共享内存失败：{ex.Message}");
                return null;
            }
        }

        private string BuildArguments(string mmfName, RevitFileQueryOptions options)
        {
            var sb = new StringBuilder();
            sb.Append("--mmf ");
            sb.Append(mmfName); // GUID 格式，无空格，无需引号

            foreach (var ext in options.Extensions ?? Array.Empty<string>())
            {
                sb.Append(' ');
                sb.Append(Quote(ext));
            }

            sb.Append(" --");

            foreach (var root in options.Roots)
            {
                if (!string.IsNullOrWhiteSpace(root.RootPath))
                {
                    // path|displayName，管道符分隔
                    var entry = root.RootPath + "|" + (root.DisplayName ?? "自定义");
                    sb.Append(' ');
                    sb.Append(Quote(entry));
                }
            }

            return sb.ToString();
        }

        private string EnsureToolExtracted()
        {
            try
            {
                var asm = typeof(MftIndexProvider).Assembly;
                var name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(ToolResourceSuffix, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(name)) return null;

                var targetDir = IOPath.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PackageManager", "tools");
                Directory.CreateDirectory(targetDir);
                var targetPath = IOPath.Combine(targetDir, ToolFileName);

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
            catch (Exception ex)
            {
                LoggingService.LogWarning($"MFT 扫描工具提取失败：{ex.Message}");
                return null;
            }
        }

        private static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            return s.Contains(" ") ? "\"" + s + "\"" : s;
        }
    }
}
