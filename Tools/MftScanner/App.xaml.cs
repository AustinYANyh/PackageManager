using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MftScanner.Services;
using Newtonsoft.Json;

namespace MftScanner
{
    public partial class App : Application
    {
        private const uint MmfMagic = 0x4D4D4650; // "MMFP"
        private const ushort MmfVersion = 1;
        private const int MmfStatusSuccess = 1;
        private const int MmfStatusError = 2;
        private const int SwShow = 5;
        private const int SwRestore = 9;

        private DispatcherTimer _ownerMonitorTimer;
        private DispatcherTimer _searchUiHeartbeatTimer;
        private EventWaitHandle _showRequestEvent;
        private EventWaitHandle _searchUiReadyEvent;
        private EventWaitHandle _searchUiShownEvent;
        private CancellationTokenSource _showRequestCts;
        private Task _showRequestListenerTask;
        private Mutex _singleInstanceMutex;
        private Mutex _indexHostMutex;
        private IndexHostAgent _indexHostAgent;
        private MemoryMappedFile _searchUiStateMap;
        private int? _ownerProcessId;
        private long _searchUiReadyEpoch;
        private long _searchUiShownEpoch;
        private string _sessionId;
        private string _showRequestEventName;
        private string _singleInstanceMutexName;

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

            var indexAgentIndex = Array.IndexOf(args, "--index-agent");
            if (indexAgentIndex >= 0)
            {
                if (!TryAcquireIndexHostMutex())
                {
                    Shutdown(0);
                    return;
                }

                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _indexHostAgent = new IndexHostAgent(IndexHostAgent.ShowSearchUiFromHost);
                _indexHostAgent.Start();
                return;
            }

            var windowMode = GetInteractiveWindowMode(args);

            if (IsSearchWindowMode(windowMode))
            {
                _sessionId = ParseSessionId(args)
                    ?? (string.Equals(windowMode, "search-ui", StringComparison.OrdinalIgnoreCase)
                        ? SharedIndexConstants.SearchUiSessionId
                        : Guid.NewGuid().ToString("N"));
                _showRequestEventName = SharedIndexConstants.BuildSearchUiShowRequestEventName(_sessionId);
                _singleInstanceMutexName = SharedIndexConstants.BuildSearchUiSingleInstanceMutexName(_sessionId);

                if (!TryAcquireSingleInstance())
                {
                    Shutdown(0);
                    return;
                }

                _ownerProcessId = ParseOwnerProcessId(args);
                InitializeShowRequestListener();
            }

            // 交互模式：手动创建窗口，捕获初始化异常
            try
            {
                var window = CreateInteractiveWindow(windowMode);
                MainWindow = window;
                if (window is EverythingSearchWindow searchWindow
                    && !string.Equals(windowMode, "search-ui", StringComparison.OrdinalIgnoreCase)
                    && !_ownerProcessId.HasValue)
                {
                    searchWindow.PrepareForProcessExit();
                }

                window.Show();

                if (IsSearchWindowMode(windowMode))
                {
                    SignalSearchUiReady("window-shown");
                    SignalSearchUiShown("window-shown");
                    StartOwnerMonitor();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"窗口初始化失败：{ex.Message}\n\n{ex.StackTrace}",
                    "MftScanner 错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _ownerMonitorTimer?.Stop();
                StopShowRequestListener();
                if (MainWindow is EverythingSearchWindow window)
                {
                    window.PrepareForProcessExit();
                }

                if (_singleInstanceMutex != null)
                {
                    try
                    {
                        _singleInstanceMutex.ReleaseMutex();
                    }
                    catch
                    {
                    }

                    _singleInstanceMutex.Dispose();
                    _singleInstanceMutex = null;
                }

                if (_indexHostMutex != null)
                {
                    try
                    {
                        _indexHostMutex.ReleaseMutex();
                    }
                    catch
                    {
                    }

                    _indexHostMutex.Dispose();
                    _indexHostMutex = null;
                }

                _indexHostAgent?.Dispose();
                _indexHostAgent = null;
            }
            catch
            {
            }

            base.OnExit(e);
        }

        private static string GetInteractiveWindowMode(string[] args)
        {
            var windowArgIndex = Array.IndexOf(args, "--window");
            return windowArgIndex >= 0 && windowArgIndex + 1 < args.Length
                ? (args[windowArgIndex + 1] ?? string.Empty).Trim()
                : string.Empty;
        }

        private static Window CreateInteractiveWindow(string windowMode)
        {
            switch (windowMode.ToLowerInvariant())
            {
                case "cleanup":
                    return new RevitFileCleanupWindow();
                case "search-ui":
                case "":
                case "search":
                    return new EverythingSearchWindow();
                default:
                    throw new InvalidOperationException($"不支持的窗口模式：{windowMode}");
            }
        }

        private static bool IsSearchWindowMode(string windowMode)
        {
            return string.Equals(windowMode, "search", StringComparison.OrdinalIgnoreCase)
                || string.Equals(windowMode, "search-ui", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryAcquireIndexHostMutex()
        {
            bool createdNew;
            _indexHostMutex = new Mutex(true, SharedIndexConstants.IndexHostMutexName, out createdNew);
            if (createdNew)
            {
                return true;
            }

            try
            {
                _indexHostMutex.Dispose();
                _indexHostMutex = null;
            }
            catch
            {
            }

            return false;
        }

        private bool TryAcquireSingleInstance()
        {
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, _singleInstanceMutexName, out createdNew);
            if (createdNew)
            {
                return true;
            }

            try
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }
            catch
            {
            }

            SignalExistingInstance(_showRequestEventName);
            return false;
        }

        private static void SignalExistingInstance(string showRequestEventName)
        {
            if (string.IsNullOrWhiteSpace(showRequestEventName))
            {
                return;
            }

            for (var i = 0; i < 20; i++)
            {
                try
                {
                    using (var showEvent = EventWaitHandle.OpenExisting(showRequestEventName))
                    {
                        showEvent.Set();
                        return;
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(100);
                }
                catch
                {
                    return;
                }
            }
        }

        private void InitializeShowRequestListener()
        {
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
                AccessControlType.Allow));

            bool createdNew;
            _showRequestEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _showRequestEventName, out createdNew, security);
            _searchUiReadyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, SharedIndexConstants.BuildSearchUiReadyEventName(_sessionId), out createdNew, security);
            _searchUiShownEvent = new EventWaitHandle(false, EventResetMode.ManualReset, SharedIndexConstants.BuildSearchUiShownEventName(_sessionId), out createdNew, security);
            _searchUiReadyEvent.Reset();
            _searchUiShownEvent.Reset();
            _searchUiReadyEpoch = 0;
            _searchUiShownEpoch = 0;
            _searchUiStateMap = SharedIndexMemoryProtocol.CreateSearchUiStateMap(_sessionId);
            PublishSearchUiState(isReady: false);
            StartSearchUiHeartbeat();
            _showRequestCts = new CancellationTokenSource();
            _showRequestListenerTask = Task.Run(() => ListenForShowRequests(_showRequestCts.Token));
        }

        private void StopShowRequestListener()
        {
            try
            {
                _showRequestCts?.Cancel();
                _showRequestCts?.Dispose();
                _showRequestCts = null;
            }
            catch
            {
            }

            try
            {
                _searchUiHeartbeatTimer?.Stop();
                _searchUiHeartbeatTimer = null;
            }
            catch
            {
            }

            try
            {
                _searchUiReadyEpoch = 0;
                _searchUiShownEpoch = 0;
                PublishSearchUiState(isReady: false, heartbeatTicks: 0);
            }
            catch
            {
            }

            try
            {
                _showRequestEvent?.Dispose();
                _showRequestEvent = null;
            }
            catch
            {
            }

            try
            {
                _searchUiStateMap?.Dispose();
                _searchUiStateMap = null;
            }
            catch
            {
            }

            try
            {
                _searchUiShownEvent?.Dispose();
                _searchUiShownEvent = null;
            }
            catch
            {
            }

            try
            {
                _searchUiReadyEvent?.Dispose();
                _searchUiReadyEvent = null;
            }
            catch
            {
            }
        }

        private void StartSearchUiHeartbeat()
        {
            _searchUiHeartbeatTimer?.Stop();
            _searchUiHeartbeatTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _searchUiHeartbeatTimer.Tick += (sender, args) => PublishSearchUiState();
            _searchUiHeartbeatTimer.Start();
        }

        private void PublishSearchUiState(bool? isReady = null, long? heartbeatTicks = null)
        {
            if (_searchUiStateMap == null)
            {
                return;
            }

            var ready = isReady ?? _searchUiReadyEpoch > 0;
            long windowHandle = 0;
            try
            {
                if (MainWindow != null)
                {
                    windowHandle = new WindowInteropHelper(MainWindow).Handle.ToInt64();
                }
            }
            catch
            {
                windowHandle = 0;
            }

            SharedIndexMemoryProtocol.WriteSearchUiState(_searchUiStateMap, new SearchUiStateSnapshot
            {
                ProcessId = Process.GetCurrentProcess().Id,
                HeartbeatTicks = heartbeatTicks ?? DateTime.UtcNow.Ticks,
                IsReady = ready,
                ReadyEpoch = _searchUiReadyEpoch,
                ShownEpoch = _searchUiShownEpoch,
                MainWindowHandle = windowHandle
            });
        }

        private void ListenForShowRequests(CancellationToken cancellationToken)
        {
            if (_showRequestEvent == null)
            {
                return;
            }

            var waitHandles = new WaitHandle[] { _showRequestEvent, cancellationToken.WaitHandle };
            while (true)
            {
                var index = WaitHandle.WaitAny(waitHandles);
                if (index != 0)
                {
                    return;
                }

                Dispatcher.BeginInvoke(new Action(ShowAndActivateMainWindow));
            }
        }

        private void ShowAndActivateMainWindow()
        {
            if (!(MainWindow is EverythingSearchWindow window))
            {
                return;
            }

            if (!window.IsVisible)
            {
                window.Show();
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Show();
            window.Activate();
            window.Focus();
            window.FocusSearchBoxAndSelectAll();
            BringWindowToFront(window);
            SignalSearchUiReady("show-request");

            var delayedBringTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            delayedBringTimer.Tick += (sender, args) =>
            {
                delayedBringTimer.Stop();
                BringWindowToFront(window);
                window.FocusSearchBoxAndSelectAll();
                SignalSearchUiShown("show-request");
            };
            delayedBringTimer.Start();
        }

        private static void BringWindowToFront(Window window)
        {
            if (window == null)
            {
                return;
            }

            window.Topmost = true;
            window.Activate();
            window.Topmost = false;

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            if (IsIconic(handle))
            {
                ShowWindow(handle, SwRestore);
            }
            else
            {
                ShowWindow(handle, SwShow);
            }

            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }

        private void StartOwnerMonitor()
        {
            if (!_ownerProcessId.HasValue)
            {
                return;
            }

            _ownerMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _ownerMonitorTimer.Tick += OwnerMonitorTimer_Tick;
            _ownerMonitorTimer.Start();
        }

        private void OwnerMonitorTimer_Tick(object sender, EventArgs e)
        {
            if (!_ownerProcessId.HasValue || IsProcessAlive(_ownerProcessId.Value))
            {
                return;
            }

            _ownerMonitorTimer?.Stop();
            StopShowRequestListener();

            if (MainWindow is EverythingSearchWindow window)
            {
                window.PrepareForProcessExit();
                if (window.IsVisible)
                {
                    window.Hide();
                }

                Dispatcher.BeginInvoke(new Action(window.Close), DispatcherPriority.Background);
                return;
            }

            Shutdown();
        }

        private static int? ParseOwnerProcessId(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return null;
            }

            var ownerPidIndex = Array.FindIndex(args, arg => string.Equals(arg, "--owner-pid", StringComparison.OrdinalIgnoreCase));
            if (ownerPidIndex < 0 || ownerPidIndex + 1 >= args.Length)
            {
                return null;
            }

            return int.TryParse(args[ownerPidIndex + 1], out var pid) && pid > 0 ? pid : (int?)null;
        }

        private static string ParseSessionId(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return null;
            }

            var sessionIdIndex = Array.FindIndex(args, arg => string.Equals(arg, "--session-id", StringComparison.OrdinalIgnoreCase));
            if (sessionIdIndex < 0 || sessionIdIndex + 1 >= args.Length)
            {
                return null;
            }

            var sessionId = args[sessionIdIndex + 1];
            return string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
        }

        private void SignalSearchUiReady(string reason)
        {
            try
            {
                if (_searchUiReadyEpoch == 0)
                {
                    _searchUiReadyEpoch = DateTime.UtcNow.Ticks;
                }

                PublishSearchUiState(isReady: true);
                _searchUiReadyEvent?.Set();
                Services.LoggingService.LogDebug($"[SEARCH UI READY] session={_sessionId} reason={reason}");
            }
            catch
            {
            }
        }

        private void SignalSearchUiShown(string reason)
        {
            try
            {
                _searchUiShownEpoch = DateTime.UtcNow.Ticks;
                if (_searchUiReadyEpoch == 0)
                {
                    _searchUiReadyEpoch = _searchUiShownEpoch;
                }

                PublishSearchUiState(isReady: true);
                _searchUiShownEvent?.Set();
                Services.LoggingService.LogDebug($"[SEARCH UI SHOWN] session={_sessionId} reason={reason}");
            }
            catch
            {
            }
        }

        private static bool IsProcessAlive(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return !process.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

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
                }

                var queryResult = scanService
                    .SearchByKeywordAsync(roots, keyword, maxResults, null, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                WriteSearchExportResult(resultPath, new SearchExportResponse
                {
                    Success = true,
                    TotalIndexedCount = queryResult?.TotalIndexedCount ?? 0,
                    TotalMatchedCount = queryResult?.TotalMatchedCount ?? 0,
                    PhysicalMatchedCount = queryResult?.PhysicalMatchedCount ?? queryResult?.TotalMatchedCount ?? 0,
                    UniqueMatchedCount = queryResult?.UniqueMatchedCount ?? queryResult?.TotalMatchedCount ?? 0,
                    DuplicatePathCount = queryResult?.DuplicatePathCount ?? 0,
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

            var indexService = new IndexService();

            // 先完成 warmup，再进入连接循环，避免首次请求时阻塞管道
            try
            {
                indexService.BuildIndexAsync(null, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch { }

            try
            {
                while (true)
                {
                    NamedPipeServerStream server;
                    try
                    {
                        // 显式授权 Everyone 读写，允许非管理员的主进程连接
                        var security = new PipeSecurity();
                        security.AddAccessRule(new PipeAccessRule(
                            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                            PipeAccessRights.ReadWrite,
                            AccessControlType.Allow));
                        server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                            inBufferSize: 0, outBufferSize: 0, security);
                    }
                    catch
                    {
                        break;
                    }

                    bool shutdown = false;
                    using (server)
                    {
                        try
                        {
                            server.WaitForConnection();
                        }
                        catch (IOException)
                        {
                            continue;
                        }

                        using (var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true))
                        using (var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
                        {
                            try
                            {
                                var requestJson = reader.ReadLine();
                                if (string.IsNullOrWhiteSpace(requestJson))
                                {
                                    continue;
                                }

                                var request = JsonConvert.DeserializeObject<StartupHelperRequest>(requestJson);
                                if (request == null)
                                {
                                    WriteStartupHelperFrame(writer, new StartupHelperStreamFrame
                                    {
                                        Success = false,
                                        ErrorMessage = "请求格式无效。",
                                        IsFinal = true
                                    });
                                    continue;
                                }

                                if (string.Equals(request.Action, "shutdown", StringComparison.OrdinalIgnoreCase))
                                {
                                    WriteStartupHelperFrame(writer, new StartupHelperStreamFrame
                                    {
                                        Success = true,
                                        IsFinal = true
                                    });
                                    shutdown = true;
                                    break;
                                }

                                if (!string.Equals(request.Action, "search", StringComparison.OrdinalIgnoreCase))
                                {
                                    WriteStartupHelperFrame(writer, new StartupHelperStreamFrame
                                    {
                                        Success = false,
                                        ErrorMessage = $"不支持的请求：{request.Action}",
                                        IsFinal = true
                                    });
                                    continue;
                                }

                                StreamStartupSearchResults(indexService, request, reader, writer);
                            }
                            catch (IOException)
                            {
                            }
                            catch (Exception ex)
                            {
                                try
                                {
                                    WriteStartupHelperFrame(writer, new StartupHelperStreamFrame
                                    {
                                        Success = false,
                                        ErrorMessage = ex.Message,
                                        IsFinal = true
                                    });
                                }
                                catch
                                {
                                }
                            }
                        }
                    }

                    if (shutdown) break;
                }

                return 0;
            }
            catch
            {
                return 1;
            }
            finally
            {
                indexService.Shutdown();
            }
        }

        private static void StreamStartupSearchResults(IndexService indexService, StartupHelperRequest request,
            StreamReader reader, StreamWriter writer)
        {
            if (request.ForceRescan)
            {
                indexService.RebuildIndexAsync(null, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

            var currentStatusMessage = indexService.CurrentStatusMessage;
            var isCatchingUp = indexService.IsBackgroundCatchUpInProgress;

            var initialFrame = BuildStartupSearchFrame(indexService, request, currentStatusMessage, isCatchingUp, !isCatchingUp);
            if (!WriteStartupHelperFrame(writer, initialFrame))
                return;

            if (initialFrame.IsFinal)
                return;

            using (var updateSignal = new AutoResetEvent(false))
            {
                var refreshRequested = 0;
                EventHandler<IndexStatusChangedEventArgs> statusHandler = (s, e) =>
                {
                    currentStatusMessage = e.Message;
                    isCatchingUp = e.IsBackgroundCatchUpInProgress;
                    Interlocked.Exchange(ref refreshRequested, 1);
                    updateSignal.Set();
                };

                indexService.IndexStatusChanged += statusHandler;
                try
                {
                    currentStatusMessage = indexService.CurrentStatusMessage;
                    isCatchingUp = indexService.IsBackgroundCatchUpInProgress;
                    if (!isCatchingUp)
                    {
                        WriteStartupHelperFrame(writer,
                            BuildStartupSearchFrame(indexService, request, currentStatusMessage, false, true));
                        return;
                    }

                    var disconnectTask = reader.ReadLineAsync();
                    while (true)
                    {
                        if (disconnectTask.IsCompleted)
                            break;

                        updateSignal.WaitOne(TimeSpan.FromMilliseconds(250));

                        if (disconnectTask.IsCompleted)
                            break;

                        if (Interlocked.Exchange(ref refreshRequested, 0) == 0)
                            continue;
                        var frame = BuildStartupSearchFrame(indexService, request, currentStatusMessage, isCatchingUp, !isCatchingUp);
                        if (!WriteStartupHelperFrame(writer, frame))
                            break;

                        if (frame.IsFinal)
                            break;
                    }
                }
                finally
                {
                    indexService.IndexStatusChanged -= statusHandler;
                }
            }
        }

        private static StartupHelperStreamFrame BuildStartupSearchFrame(IndexService indexService, StartupHelperRequest request,
            string statusMessage, bool isCatchingUp, bool isFinal)
        {
            var queryResult = indexService.SearchAsync(
                    request.Keyword ?? string.Empty,
                    request.MaxResults > 0 ? request.MaxResults : 500,
                    0,
                    null,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return new StartupHelperStreamFrame
            {
                Success = true,
                StatusMessage = statusMessage,
                IsCatchingUp = isCatchingUp,
                IsFinal = isFinal,
                TotalIndexedCount = queryResult?.TotalIndexedCount ?? indexService.Index.TotalCount,
                TotalMatchedCount = queryResult?.TotalMatchedCount ?? 0,
                PhysicalMatchedCount = queryResult?.PhysicalMatchedCount ?? queryResult?.TotalMatchedCount ?? 0,
                UniqueMatchedCount = queryResult?.UniqueMatchedCount ?? queryResult?.TotalMatchedCount ?? 0,
                DuplicatePathCount = queryResult?.DuplicatePathCount ?? 0,
                IsTruncated = queryResult?.IsTruncated ?? false,
                ContainsBucketStatus = queryResult?.ContainsBucketStatus ?? indexService.ContainsBucketStatus,
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
            };
        }

        private static bool WriteStartupHelperFrame(StreamWriter writer, StartupHelperStreamFrame frame)
        {
            try
            {
                writer.WriteLine(JsonConvert.SerializeObject(frame));
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
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
            public int PhysicalMatchedCount { get; set; }
            public int UniqueMatchedCount { get; set; }
            public int DuplicatePathCount { get; set; }
            public bool IsTruncated { get; set; }
            public ContainsBucketStatus ContainsBucketStatus { get; set; }
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

        private sealed class StartupHelperStreamFrame
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public string StatusMessage { get; set; }
            public bool IsCatchingUp { get; set; }
            public bool IsFinal { get; set; }
            public int TotalIndexedCount { get; set; }
            public int TotalMatchedCount { get; set; }
            public int PhysicalMatchedCount { get; set; }
            public int UniqueMatchedCount { get; set; }
            public int DuplicatePathCount { get; set; }
            public bool IsTruncated { get; set; }
            public ContainsBucketStatus ContainsBucketStatus { get; set; }
            public List<SearchExportItem> Results { get; set; } = new List<SearchExportItem>();
        }
    }
}
