using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using System.Windows;

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

            var args = e.Args;
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
                var window = new RevitFileCleanupWindow();
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
    }
}
