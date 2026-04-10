using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;

namespace MftScanner
{
    public partial class App : Application
    {
        private const uint MmfMagic = 0x4D4D4650; // "MMFP"
        private const ushort MmfVersion = 1;
        private const int MmfStatusSuccess = 1;
        private const int MmfStatusError = 2;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var args = e.Args ?? Array.Empty<string>();

            // --self-test DingtalkLauncher.exe
            var selfTestIndex = Array.IndexOf(args, "--self-test");
            if (selfTestIndex >= 0)
            {
                // 自测模式：自己创建 MMF + Event，跑无头扫描，读结果并弹窗显示
                var keyword = selfTestIndex + 1 < args.Length ? args[selfTestIndex + 1] : "*.exe";
                RunSelfTest(keyword);
                Shutdown(0);
                return;
            }

            var searchExportIndex = Array.IndexOf(args, "--search-export");
            if (searchExportIndex >= 0 && searchExportIndex + 1 < args.Length)
            {
                var resultPath = args[searchExportIndex + 1];
                var exitCode = RunHeadlessSearchExport(resultPath, args);
                Shutdown(exitCode);
                return;
            }

            var startupHelperIndex = Array.IndexOf(args, "--startup-helper");
            if (startupHelperIndex >= 0 && startupHelperIndex + 1 < args.Length)
            {
                var pipeName = args[startupHelperIndex + 1];
                var exitCode = RunStartupSearchHelper(pipeName);
                Shutdown(exitCode);
                return;
            }

            var mmfArgIndex = Array.IndexOf(args, "--mmf");
            if (mmfArgIndex >= 0 && mmfArgIndex + 1 < args.Length)
            {
                // 无头 CLI 模式：在 StartupUri 窗口创建前 Shutdown，阻止任何窗口显示
                var mmfName = args[mmfArgIndex + 1];
                RunHeadlessScan(mmfName, args);
                Shutdown(0);
                return;
            }

            // 交互模式：手动创建窗口，捕获初始化异常
            try
            {
                var window = CreateInteractiveWindow(args);
                MainWindow = window;
                window.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"窗口初始化失败：{ex.Message}\n\n{ex.StackTrace}",
                    "MftScanner 错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        private static Window CreateInteractiveWindow(string[] args)
        {
            var windowArgIndex = Array.IndexOf(args, "--window");
            var windowMode = windowArgIndex >= 0 && windowArgIndex + 1 < args.Length
                ? (args[windowArgIndex + 1] ?? string.Empty).Trim()
                : string.Empty;

            switch (windowMode.ToLowerInvariant())
            {
                case "cleanup":
                    return new RevitFileCleanupWindow();
                case "":
                case "search":
                    return new EverythingSearchWindow();
                default:
                    throw new InvalidOperationException($"不支持的窗口模式：{windowMode}");
            }
        }

        private static void RunHeadlessScan(string mmfName, string[] args)
        {
            MemoryMappedFile mmf = null;
            EventWaitHandle doneEvent = null;

            try
            {
                mmf = MemoryMappedFile.OpenExisting(mmfName);
                doneEvent = EventWaitHandle.OpenExisting(mmfName + "_Done");

                // 解析参数：扩展名和根目录
                var extensions = new List<string>();
                var roots = new List<ScanRoot>();
                var inRoots = false;

                for (var i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--mmf") { i++; continue; } // 跳过 --mmf <name>
                    if (args[i] == "--") { inRoots = true; continue; }
                    if (inRoots)
                    {
                        // 格式：path|displayName
                        var parts = args[i].Split(new[] { '|' }, 2);
                        roots.Add(new ScanRoot
                        {
                            Path = parts[0],
                            DisplayName = parts.Length > 1 ? parts[1] : "自定义",
                        });
                    }
                    else
                    {
                        extensions.Add(args[i]);
                    }
                }

                if (roots.Count == 0 || extensions.Count == 0)
                {
                    WriteErrorHeader(mmf);
                    doneEvent.Set();
                    return;
                }

                var scanService = new MftScanService();
                List<ScannedFileInfo> results;
                try
                {
                    results = scanService.ScanAsync(roots, extensions, null, CancellationToken.None)
                                         .GetAwaiter().GetResult();
                    scanService.SaveAllCaches();
                }
                catch
                {
                    WriteErrorHeader(mmf);
                    doneEvent.Set();
                    return;
                }

                WriteResults(mmf, results ?? new List<ScannedFileInfo>());
                doneEvent.Set();
            }
            catch
            {
                // MMF/Event 打开失败或其他致命错误
                try { if (mmf != null) WriteErrorHeader(mmf); } catch { }
                doneEvent?.Set(); // 始终 Set，避免主进程挂起
            }
            finally
            {
                doneEvent?.Dispose();
                mmf?.Dispose();
            }
        }

        private static void WriteResults(MemoryMappedFile mmf, List<ScannedFileInfo> results)
        {
            using (var stream = mmf.CreateViewStream())
            using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: false))
            {
                // Header
                writer.Write(MmfMagic);         // offset 0
                writer.Write(MmfVersion);       // offset 4
                writer.Write(MmfStatusSuccess); // offset 6
                writer.Write(results.Count);    // offset 10
                writer.Write(new byte[6]);      // offset 14: reserved

                // Records
                foreach (var r in results)
                {
                    var pathBytes = Encoding.Unicode.GetBytes(r.FullPath ?? string.Empty);
                    writer.Write(pathBytes.Length);
                    writer.Write(pathBytes);
                    writer.Write(r.SizeBytes);
                    writer.Write(r.ModifiedTimeUtc.Ticks);
                    var dispBytes = Encoding.Unicode.GetBytes(r.RootDisplayName ?? string.Empty);
                    writer.Write(dispBytes.Length);
                    writer.Write(dispBytes);
                }
            }
        }

        private static void RunSelfTest(string keyword)
        {
            var mmfName = "SelfTest_" + Guid.NewGuid().ToString("N");
            var ext = Path.GetExtension(keyword).TrimStart('.');
            if (string.IsNullOrEmpty(ext)) ext = "exe";

            var drives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed)
                .Select(d => $"{d.RootDirectory.FullName}|{d.Name.TrimEnd('\\', '/')}盘")
                .ToArray();

            // 构造与主进程相同的 args 数组
            var scanArgs = new List<string> { "--mmf", mmfName, ext, "--" };
            scanArgs.AddRange(drives);

            using (var mmf = MemoryMappedFile.CreateNew(mmfName, 32 * 1024 * 1024))
            using (var doneEvent = new EventWaitHandle(false, EventResetMode.ManualReset, mmfName + "_Done"))
            {
                RunHeadlessScan(mmfName, scanArgs.ToArray());

                // 读取结果
                var sb = new StringBuilder();
                sb.AppendLine($"关键词：{keyword}  扩展名：{ext}");
                sb.AppendLine();

                using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                {
                    long pos = 0;
                    var magic = accessor.ReadInt32(pos); pos += 4;
                    var version = accessor.ReadInt16(pos); pos += 2;
                    var status = accessor.ReadInt32(pos); pos += 4;
                    var count = accessor.ReadInt32(pos); pos += 4;
                    pos += 6; // reserved

                    if (magic != (int)MmfMagic || status != MmfStatusSuccess)
                    {
                        sb.AppendLine($"扫描失败或无结果（magic=0x{magic:X8} status={status}）");
                    }
                    else
                    {
                        sb.AppendLine($"共 {count} 条结果：");
                        var kw = keyword.Replace("*", "").ToLowerInvariant();
                        int shown = 0;
                        for (int i = 0; i < count; i++)
                        {
                            var pathLen = accessor.ReadInt32(pos); pos += 4;
                            var pathBytes = new byte[pathLen];
                            accessor.ReadArray(pos, pathBytes, 0, pathLen); pos += pathLen;
                            var fullPath = Encoding.Unicode.GetString(pathBytes);

                            pos += 8; // SizeBytes
                            pos += 8; // ModifiedTimeUtc.Ticks

                            var dispLen = accessor.ReadInt32(pos); pos += 4;
                            pos += dispLen; // RootDisplayName

                            var fileName = Path.GetFileName(fullPath);
                            if (string.IsNullOrEmpty(kw) || fileName.ToLowerInvariant().Contains(kw))
                            {
                                sb.AppendLine(fullPath);
                                shown++;
                            }
                        }
                        if (shown == 0) sb.AppendLine("（无匹配项）");
                    }
                }

                MessageBox.Show(sb.ToString(), "MftScanner 自测结果",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private static void WriteErrorHeader(MemoryMappedFile mmf)
        {
            using (var stream = mmf.CreateViewStream(0, 20))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(MmfMagic);
                writer.Write(MmfVersion);
                writer.Write(MmfStatusError);
                writer.Write(0); // RecordCount = 0
                writer.Write(new byte[6]);
            }
        }

        private static int RunHeadlessSearchExport(string resultPath, string[] args)
        {
            try
            {
                var keyword = GetOptionValue(args, "--keyword") ?? string.Empty;
                var maxResultsText = GetOptionValue(args, "--max-results");
                var maxResults = 500;
                if (!string.IsNullOrWhiteSpace(maxResultsText) && int.TryParse(maxResultsText, out var parsedMaxResults) && parsedMaxResults > 0)
                {
                    maxResults = parsedMaxResults;
                }

                var forceRescan = args.Any(a => string.Equals(a, "--force-rescan", StringComparison.OrdinalIgnoreCase));
                var roots = ParseRoots(args);
                if (roots.Count == 0)
                {
                    roots = DriveInfo.GetDrives()
                        .Where(d => d.DriveType == DriveType.Fixed)
                        .Select(d => new ScanRoot
                        {
                            Path = d.RootDirectory.FullName,
                            DisplayName = d.Name.TrimEnd('\\', '/')
                        })
                        .ToList();
                }

                var scanService = new MftScanService();
                if (forceRescan)
                {
                    scanService.InvalidateCache();
                }

                var queryResult = scanService
                    .SearchByKeywordAsync(roots, keyword, maxResults, null, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                scanService.SaveAllCaches();

                WriteSearchExportResult(resultPath, new SearchExportResponse
                {
                    Success = true,
                    TotalIndexedCount = queryResult?.TotalIndexedCount ?? 0,
                    TotalMatchedCount = queryResult?.TotalMatchedCount ?? 0,
                    IsTruncated = queryResult?.IsTruncated ?? false,
                    Results = (queryResult?.Results ?? new List<ScannedFileInfo>())
                        .Select(r => new SearchExportItem
                        {
                            FullPath = r.FullPath,
                            FileName = r.FileName,
                            SizeBytes = r.SizeBytes,
                            ModifiedTimeUtc = r.ModifiedTimeUtc,
                            RootPath = r.RootPath,
                            RootDisplayName = r.RootDisplayName,
                            IsDirectory = r.IsDirectory
                        })
                        .ToList()
                });

                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    WriteSearchExportResult(resultPath, new SearchExportResponse
                    {
                        Success = false,
                        ErrorMessage = ex.Message
                    });
                }
                catch
                {
                }

                return 1;
            }
        }

        private static int RunStartupSearchHelper(string pipeName)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                return 1;
            }

            var roots = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed)
                .Select(d => new ScanRoot
                {
                    Path = d.RootDirectory.FullName,
                    DisplayName = d.Name.TrimEnd('\\', '/')
                })
                .ToList();

            var scanService = new MftScanService();
            var warmupTask = scanService.PrepareSearchIndexAsync(roots, null, CancellationToken.None);

            try
            {
                while (true)
                {
                    using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
                    {
                        try
                        {
                            client.Connect(500);
                        }
                        catch (TimeoutException)
                        {
                            Thread.Sleep(100);
                            continue;
                        }

                        using (var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true))
                        using (var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
                        {
                            StartupHelperRequest request = null;

                            try
                            {
                                var requestJson = reader.ReadLine();
                                if (string.IsNullOrWhiteSpace(requestJson))
                                {
                                    continue;
                                }

                                request = JsonConvert.DeserializeObject<StartupHelperRequest>(requestJson);
                                if (request == null)
                                {
                                    writer.WriteLine(JsonConvert.SerializeObject(new SearchExportResponse
                                    {
                                        Success = false,
                                        ErrorMessage = "请求格式无效。"
                                    }));
                                    continue;
                                }

                                if (string.Equals(request.Action, "shutdown", StringComparison.OrdinalIgnoreCase))
                                {
                                    writer.WriteLine(JsonConvert.SerializeObject(new SearchExportResponse { Success = true }));
                                    break;
                                }

                                if (!string.Equals(request.Action, "search", StringComparison.OrdinalIgnoreCase))
                                {
                                    writer.WriteLine(JsonConvert.SerializeObject(new SearchExportResponse
                                    {
                                        Success = false,
                                        ErrorMessage = $"不支持的请求：{request.Action}"
                                    }));
                                    continue;
                                }

                                if (request.ForceRescan)
                                {
                                    scanService.InvalidateCache();
                                    warmupTask = scanService.PrepareSearchIndexAsync(roots, null, CancellationToken.None);
                                }

                                warmupTask.GetAwaiter().GetResult();

                                var queryResult = scanService
                                    .SearchByKeywordAsync(roots, request.Keyword ?? string.Empty, request.MaxResults > 0 ? request.MaxResults : 500, null, CancellationToken.None)
                                    .GetAwaiter()
                                    .GetResult();

                                writer.WriteLine(JsonConvert.SerializeObject(new SearchExportResponse
                                {
                                    Success = true,
                                    TotalIndexedCount = queryResult?.TotalIndexedCount ?? 0,
                                    TotalMatchedCount = queryResult?.TotalMatchedCount ?? 0,
                                    IsTruncated = queryResult?.IsTruncated ?? false,
                                    Results = (queryResult?.Results ?? new List<ScannedFileInfo>())
                                        .Select(r => new SearchExportItem
                                        {
                                            FullPath = r.FullPath,
                                            FileName = r.FileName,
                                            SizeBytes = r.SizeBytes,
                                            ModifiedTimeUtc = r.ModifiedTimeUtc,
                                            RootPath = r.RootPath,
                                            RootDisplayName = r.RootDisplayName,
                                            IsDirectory = r.IsDirectory
                                        })
                                        .ToList()
                                }));
                            }
                            catch (IOException)
                            {
                            }
                            catch (Exception ex)
                            {
                                try
                                {
                                    writer.WriteLine(JsonConvert.SerializeObject(new SearchExportResponse
                                    {
                                        Success = false,
                                        ErrorMessage = ex.Message
                                    }));
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                }

                scanService.SaveAllCaches();
                return 0;
            }
            catch
            {
                try { scanService.SaveAllCaches(); } catch { }
                return 1;
            }
        }

        private static List<ScanRoot> ParseRoots(string[] args)
        {
            var roots = new List<ScanRoot>();
            var separatorIndex = Array.IndexOf(args, "--");
            if (separatorIndex < 0)
            {
                return roots;
            }

            for (var i = separatorIndex + 1; i < args.Length; i++)
            {
                var part = args[i];
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                var sections = part.Split(new[] { '|' }, 2);
                roots.Add(new ScanRoot
                {
                    Path = sections[0],
                    DisplayName = sections.Length > 1 ? sections[1] : sections[0]
                });
            }

            return roots;
        }

        private static string GetOptionValue(string[] args, string optionName)
        {
            var index = Array.IndexOf(args, optionName);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static void WriteSearchExportResult(string resultPath, SearchExportResponse response)
        {
            if (string.IsNullOrWhiteSpace(resultPath))
            {
                throw new ArgumentException("结果文件路径不能为空。", nameof(resultPath));
            }

            var directory = Path.GetDirectoryName(resultPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonConvert.SerializeObject(response);
            File.WriteAllText(resultPath, json, Encoding.UTF8);
        }

        private sealed class SearchExportResponse
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public int TotalIndexedCount { get; set; }
            public int TotalMatchedCount { get; set; }
            public bool IsTruncated { get; set; }
            public List<SearchExportItem> Results { get; set; } = new List<SearchExportItem>();
        }

        private sealed class SearchExportItem
        {
            public string FullPath { get; set; }
            public string FileName { get; set; }
            public long SizeBytes { get; set; }
            public DateTime ModifiedTimeUtc { get; set; }
            public string RootPath { get; set; }
            public string RootDisplayName { get; set; }
            public bool IsDirectory { get; set; }
        }

        private sealed class StartupHelperRequest
        {
            public string Action { get; set; }
            public string Keyword { get; set; }
            public int MaxResults { get; set; }
            public bool ForceRescan { get; set; }
        }
    }
}
