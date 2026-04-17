using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Newtonsoft.Json;
using PackageManager.Services;

namespace PackageManager.Function.StartupTool;

public partial class CommonStartupWindow : Window
{
    private const int MaxDisplayedResults = 500;
    private static readonly string[] LaunchableExtensions = { ".exe", ".bat", ".cmd", ".ps1", ".lnk" };

    private readonly DataPersistenceService _persistence;
    private readonly DispatcherTimer _debounceTimer;
    private readonly object _helperGate = new();
    private readonly ObservableCollection<ScanResultItem> _scanResults = new();
    private readonly ObservableCollection<StartupItemVm> _startupItems = new();
    private CancellationTokenSource _scanCts;
    private Task<bool> _ensureHelperTask;
    private Process _helperProcess;
    private string _helperPipeName;
    private int _searchVersion;

    public CommonStartupWindow(DataPersistenceService persistence)
    {
        InitializeComponent();
        _persistence = persistence;
        ScanResultList.ItemsSource = _scanResults;
        StartupItemList.ItemsSource = _startupItems;
        LoadSavedItems();

        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounceTimer.Tick += DebounceTimer_Tick;
    }

    // ──────────────────────────────────────────────
    //  数据加载 / 保存
    // ──────────────────────────────────────────────

    private void LoadSavedItems()
    {
        var settings = _persistence.LoadSettings();
        _startupItems.Clear();
        foreach (var item in settings.CommonStartupItems)
            _startupItems.Add(new StartupItemVm { Name = item.Name, FullPath = item.FullPath, Arguments = item.Arguments, Note = item.Note });
    }

    private void SaveItems()
    {
        var settings = _persistence.LoadSettings();
        settings.CommonStartupItems = _startupItems
            .Select(v => v.ToModel())
            .ToList();
        _persistence.SaveSettings(settings);
    }

    // ──────────────────────────────────────────────
    //  扫描
    // ──────────────────────────────────────────────

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        StatusText.Text = "输入文件名关键词后自动检索。首次检索会请求一次管理员权限。";
    }

    private async void Window_Closed(object sender, EventArgs e)
    {
        _debounceTimer.Stop();
        CancelActiveSearch();
        await ShutdownHelperAsync();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _debounceTimer.Stop();
            _ = StartScanAsync(forceRescan: false);
        }
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _debounceTimer.Stop();

        var keyword = SearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            CancelActiveSearch();
            _scanResults.Clear();
            ScanCountText.Text = "";
            StatusText.Text = "请输入文件名关键词开始检索。";
            SetScanningState(false);
            return;
        }

        _debounceTimer.Start();
    }

    private void DebounceTimer_Tick(object sender, EventArgs e)
    {
        _debounceTimer.Stop();
        _ = StartScanAsync(forceRescan: false);
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e) => _ = StartScanAsync(forceRescan: true);

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _debounceTimer.Stop();
        CancelActiveSearch();
        StatusText.Text = "检索已停止。";
        SetScanningState(false);
    }

    private void CancelActiveSearch()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
    }

    private async Task StartScanAsync(bool forceRescan)
    {
        var keyword = SearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            _scanResults.Clear();
            ScanCountText.Text = "";
            StatusText.Text = "请输入文件名关键词开始检索。";
            SetScanningState(false);
            return;
        }

        CancelActiveSearch();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        var currentVersion = Interlocked.Increment(ref _searchVersion);

        _scanResults.Clear();
        ScanCountText.Text = "";
        SetScanningState(true);
        StatusText.Text = forceRescan ? "正在重建启动项搜索索引…" : $"正在搜索 \"{keyword}\"…";

        try
        {
            var helperReady = await EnsureSearchHelperAsync().ConfigureAwait(true);
            if (!helperReady || ct.IsCancellationRequested || (currentVersion != _searchVersion))
            {
                return;
            }

            var receivedFrameCount = 0;
            await QueryHelperStreamAsync(new StartupHelperRequest
            {
                Action = "search",
                Keyword = keyword,
                MaxResults = MaxDisplayedResults,
                ForceRescan = forceRescan
            },
            response =>
            {
                if (ct.IsCancellationRequested || (currentVersion != _searchVersion) || response == null)
                {
                    return;
                }

                receivedFrameCount++;
                ApplySearchFrame(response);
            }, ct).ConfigureAwait(true);

            if (!ct.IsCancellationRequested && (currentVersion == _searchVersion) && (receivedFrameCount == 0))
            {
                StatusText.Text = "未收到检索结果。";
            }
        }
        catch (OperationCanceledException)
        {
            if (currentVersion == _searchVersion)
            {
                StatusText.Text = "检索已停止。";
            }
        }
        catch (Exception ex)
        {
            if (currentVersion == _searchVersion)
            {
                LoggingService.LogError(ex, $"[StartupSearch] StartScanAsync failed: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
                StatusText.Text = $"检索出错：{ex.Message}";
            }
        }
        finally
        {
            if (currentVersion == _searchVersion)
            {
                SetScanningState(false);
            }
        }
    }

    private void ApplySearchFrame(StartupSearchResponse response)
    {
        if (response == null)
        {
            return;
        }

        if (!response.Success)
        {
            StatusText.Text = $"检索失败：{response.ErrorMessage ?? "未知错误"}";
            return;
        }

        var results = FilterStartupResults(response.Results);
        ReconcileSearchResults(results);

        ScanCountText.Text = $"共 {results.Count} 项";

        if (results.Count == 0)
        {
            if (response.TotalMatchedCount > 0)
            {
                StatusText.Text = response.IsCatchingUp
                    ? "检索到匹配对象，但没有可直接作为启动项的文件，后台仍在追平…"
                    : "检索到匹配对象，但没有可直接作为启动项的文件。";
            }
            else
            {
                StatusText.Text = response.IsCatchingUp
                    ? $"未找到匹配项（共 {response.TotalIndexedCount} 个对象，后台追平中）"
                    : $"未找到匹配项（共 {response.TotalIndexedCount} 个对象）";
            }

            return;
        }

        if (response.IsCatchingUp)
        {
            StatusText.Text = $"显示 {results.Count} 个启动项（共 {response.TotalMatchedCount} 个对象，后台追平中）";
            return;
        }

        if (response.IsTruncated)
        {
            StatusText.Text = $"显示 {results.Count} 个启动项（共 {response.TotalMatchedCount} 个对象，输入更多字符可继续缩小）。";
            return;
        }

        StatusText.Text = $"{results.Count} 个启动项（共 {response.TotalIndexedCount} 个对象）";
    }

    private void ReconcileSearchResults(System.Collections.Generic.IReadOnlyList<ScanResultItem> results)
    {
        var incoming = results ?? Array.Empty<ScanResultItem>();
        var incomingMap = incoming
            .Where(item => item != null && !string.IsNullOrWhiteSpace(item.FullPath))
            .ToDictionary(item => item.FullPath, StringComparer.OrdinalIgnoreCase);

        for (var i = _scanResults.Count - 1; i >= 0; i--)
        {
            var existing = _scanResults[i];
            if ((existing == null) || string.IsNullOrWhiteSpace(existing.FullPath))
            {
                _scanResults.RemoveAt(i);
                continue;
            }

            if (!incomingMap.TryGetValue(existing.FullPath, out var incomingItem))
            {
                _scanResults.RemoveAt(i);
                continue;
            }

            if (!string.Equals(existing.FileName, incomingItem.FileName, StringComparison.Ordinal))
            {
                existing.FileName = incomingItem.FileName;
            }
        }

        var existingPathSet = new System.Collections.Generic.HashSet<string>(
            _scanResults
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.FullPath))
                .Select(item => item.FullPath),
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in incoming)
        {
            if ((item == null) || string.IsNullOrWhiteSpace(item.FullPath) || !existingPathSet.Add(item.FullPath))
            {
                continue;
            }

            _scanResults.Add(item);
        }
    }

    private static System.Collections.Generic.List<ScanResultItem> FilterStartupResults(System.Collections.Generic.IEnumerable<StartupSearchResultItem> results)
    {
        var filtered = new System.Collections.Generic.List<ScanResultItem>();
        var dedup = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in results ?? Enumerable.Empty<StartupSearchResultItem>())
        {
            if (item == null || item.IsDirectory || !IsLaunchableFile(item.FullPath))
            {
                continue;
            }

            if (!dedup.Add(item.FullPath))
            {
                continue;
            }

            filtered.Add(new ScanResultItem
            {
                FileName = string.IsNullOrWhiteSpace(item.FileName) ? System.IO.Path.GetFileName(item.FullPath) : item.FileName,
                FullPath = item.FullPath
            });
        }

        return filtered;
    }

    private Task<bool> EnsureSearchHelperAsync()
    {
        lock (_helperGate)
        {
            if (IsHelperAlive())
            {
                return Task.FromResult(true);
            }

            if ((_ensureHelperTask == null) || _ensureHelperTask.IsCompleted)
            {
                _ensureHelperTask = StartSearchHelperAsync();
            }

            return _ensureHelperTask;
        }
    }

    private async Task<bool> StartSearchHelperAsync()
    {
        try
        {
            StatusText.Text = "正在启动 MFT 检索服务，首次需要管理员权限…";
            LoggingService.LogInfo("[StartupSearch] StartSearchHelperAsync: begin");

            // 提取前先杀掉已有的 MftScanner 后台进程，避免文件被占用导致写入失败
            await Task.Run(() => KillExistingMftScannerProcesses()).ConfigureAwait(true);

            var exePath = await Task.Run(() =>
                AdminElevationService.ExtractEmbeddedTool("MftScanner.exe", "MftScanner.exe")).ConfigureAwait(true);

            LoggingService.LogInfo($"[StartupSearch] ExtractEmbeddedTool result: {exePath ?? "(null)"}");

            if (string.IsNullOrEmpty(exePath))
            {
                StatusText.Text = "未找到 MftScanner.exe，请检查安装。";
                return false;
            }

            var pipeName = "PackageManager.StartupSearch." + Guid.NewGuid().ToString("N");
            LoggingService.LogInfo($"[StartupSearch] Starting helper: {exePath} --startup-helper {pipeName}");

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--startup-helper {Quote(pipeName)}",
                UseShellExecute = true
            };

            var proc = Process.Start(psi);
            if (proc == null)
            {
                LoggingService.LogWarning("[StartupSearch] Process.Start returned null");
                StatusText.Text = "检索服务启动失败。";
                return false;
            }

            LoggingService.LogInfo($"[StartupSearch] Helper started, PID={proc.Id}");
            _helperPipeName = pipeName;
            _helperProcess = proc;
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            LoggingService.LogInfo("[StartupSearch] UAC cancelled by user");
            StatusText.Text = "用户取消了管理员授权，无法启用 MFT 快速检索。";
            return false;
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "[StartupSearch] StartSearchHelperAsync failed");
            StatusText.Text = $"启动检索服务失败：{ex.Message}";
            return false;
        }
    }

    private static void KillExistingMftScannerProcesses()
    {
        var targetPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PackageManager", "tools", "MftScanner.exe");

        var currentPid = Process.GetCurrentProcess().Id;

        try
        {
            foreach (var proc in Process.GetProcessesByName("MftScanner"))
            {
                try
                {
                    string procPath = null;
                    try { procPath = proc.MainModule?.FileName; } catch { }

                    bool shouldKill;
                    if (!string.IsNullOrEmpty(procPath))
                    {
                        // 能读到路径：只杀提取目录里的
                        shouldKill = string.Equals(procPath, targetPath, StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        // 读不到路径（通常是以管理员权限运行的 helper）：
                        // 检查父进程是否还存在，孤儿进程说明是上次遗留的 helper
                        int parentPid = GetParentProcessId(proc.Id);
                        bool parentAlive = parentPid > 0 && IsProcessAlive(parentPid);
                        // 父进程已死 = 遗留 helper；父进程是当前进程 = 本次刚启动的（不杀）
                        shouldKill = !parentAlive && parentPid != currentPid;
                    }

                    if (shouldKill)
                    {
                        proc.Kill();
                        proc.WaitForExit(3000);
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }
    }

    private static int GetParentProcessId(int pid)
    {
        try
        {
            using (var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}"))
            using (var results = searcher.Get())
            {
                foreach (System.Management.ManagementObject obj in results)
                    return Convert.ToInt32(obj["ParentProcessId"]);
            }
        }
        catch { }
        return -1;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    private async Task<StartupSearchResponse> QueryHelperAsync(StartupHelperRequest request, CancellationToken cancellationToken, string pipeNameOverride = null)
    {
        var pipeName = string.IsNullOrWhiteSpace(pipeNameOverride) ? _helperPipeName : pipeNameOverride;
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new InvalidOperationException("检索服务未启动。");
        }

        LoggingService.LogInfo($"[StartupSearch] QueryHelperAsync: action={request.Action} keyword={request.Keyword} pipe={pipeName}");

        // helper 是 server，主项目作为 client 连接；带重试以等待 helper 就绪
        var deadline = DateTime.UtcNow.AddSeconds(60);
        int attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;
            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                try
                {
                    await Task.Run(() => client.Connect(2000), cancellationToken).ConfigureAwait(false);
                    LoggingService.LogInfo($"[StartupSearch] Pipe connected (attempt {attempt})");
                }
                catch (TimeoutException)
                {
                    LoggingService.LogInfo($"[StartupSearch] Connect timeout (attempt {attempt}), retrying...");
                    if (DateTime.UtcNow >= deadline)
                    {
                        LoggingService.LogWarning("[StartupSearch] Connect deadline exceeded, giving up");
                        return null;
                    }
                    continue;
                }
                catch (Exception ex)
                {
                    LoggingService.LogError(ex, $"[StartupSearch] Connect failed (attempt {attempt}): {ex.GetType().Name}: {ex.Message}");
                    throw;
                }

                cancellationToken.ThrowIfCancellationRequested();

                using (var writer = new StreamWriter(client, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
                using (var reader = new StreamReader(client, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true))
                {
                    var payload = JsonConvert.SerializeObject(request);
                    await writer.WriteLineAsync(payload).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    var responseJson = await ReadPipeLineAsync(reader, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    LoggingService.LogInfo($"[StartupSearch] Response received, length={responseJson?.Length ?? -1}");

                    if (string.IsNullOrWhiteSpace(responseJson))
                        return null;

                    return JsonConvert.DeserializeObject<StartupSearchResponse>(responseJson);
                }
            }
        }
    }

    private async Task<int> QueryHelperStreamAsync(StartupHelperRequest request, Action<StartupSearchResponse> onFrame,
        CancellationToken cancellationToken, string pipeNameOverride = null)
    {
        var pipeName = string.IsNullOrWhiteSpace(pipeNameOverride) ? _helperPipeName : pipeNameOverride;
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new InvalidOperationException("检索服务未启动。");
        }

        LoggingService.LogInfo($"[StartupSearch] QueryHelperStreamAsync: action={request.Action} keyword={request.Keyword} pipe={pipeName}");

        var deadline = DateTime.UtcNow.AddSeconds(60);
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                try
                {
                    await Task.Run(() => client.Connect(2000), cancellationToken);
                    LoggingService.LogInfo($"[StartupSearch] Stream pipe connected (attempt {attempt})");
                }
                catch (TimeoutException)
                {
                    LoggingService.LogInfo($"[StartupSearch] Stream connect timeout (attempt {attempt}), retrying...");
                    if (DateTime.UtcNow >= deadline)
                    {
                        LoggingService.LogWarning("[StartupSearch] Stream connect deadline exceeded, giving up");
                        return 0;
                    }

                    continue;
                }
                catch (Exception ex)
                {
                    LoggingService.LogError(ex, $"[StartupSearch] Stream connect failed (attempt {attempt}): {ex.GetType().Name}: {ex.Message}");
                    throw;
                }

                using (var writer = new StreamWriter(client, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
                using (var reader = new StreamReader(client, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true))
                {
                    var payload = JsonConvert.SerializeObject(request);
                    await writer.WriteLineAsync(payload);
                    cancellationToken.ThrowIfCancellationRequested();

                    var frameCount = 0;
                    while (true)
                    {
                        var responseJson = await ReadPipeLineAsync(reader, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();

                        if (string.IsNullOrWhiteSpace(responseJson))
                        {
                            return frameCount;
                        }

                        StartupSearchResponse response;
                        try
                        {
                            response = JsonConvert.DeserializeObject<StartupSearchResponse>(responseJson);
                        }
                        catch (Exception ex)
                        {
                            LoggingService.LogError(ex, $"[StartupSearch] Stream frame deserialize failed: {responseJson}");
                            throw;
                        }

                        frameCount++;
                        onFrame?.Invoke(response);

                        if ((response != null && response.IsFinal) || (response != null && !response.Success))
                        {
                            return frameCount;
                        }
                    }
                }
            }
        }
    }

    private static async Task<string> ReadPipeLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var readTask = reader.ReadLineAsync();
        var completedTask = await Task.WhenAny(readTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        if (completedTask != readTask)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return await readTask.ConfigureAwait(false);
    }

    private static bool IsLaunchableFile(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        var ext = System.IO.Path.GetExtension(fullPath);
        return LaunchableExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private static string Quote(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "\"\"";
        }

        return text.Contains(" ") ? "\"" + text + "\"" : text;
    }

    private bool IsHelperAlive()
    {
        return (_helperProcess != null) && !_helperProcess.HasExited && !string.IsNullOrWhiteSpace(_helperPipeName);
    }

    private async Task ShutdownHelperAsync()
    {
        var process = _helperProcess;
        var pipeName = _helperPipeName;
        _helperProcess = null;
        _helperPipeName = null;

        if (process == null)
        {
            return;
        }

        try
        {
            if (!process.HasExited && !string.IsNullOrWhiteSpace(pipeName))
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                {
                    await QueryHelperAsync(new StartupHelperRequest { Action = "shutdown" }, cts.Token, pipeName).ConfigureAwait(true);
                }
            }
        }
        catch
        {
        }

        try
        {
            if (!process.HasExited)
            {
                await Task.Run(() => process.WaitForExit(3000)).ConfigureAwait(true);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private void SetScanningState(bool scanning)
    {
        StopButton.IsEnabled = scanning;
    }

    // ──────────────────────────────────────────────
    //  添加到常用启动项
    // ──────────────────────────────────────────────

    private void ScanResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ScanResultList.SelectedItem is ScanResultItem item)
            AddItem(item.FullPath);
    }

    private void AddSingleItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is ScanResultItem item)
            AddItem(item.FullPath);
    }

    private void AddSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (ScanResultItem item in ScanResultList.SelectedItems)
            AddItem(item.FullPath);
    }

    private void ManualAddButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要添加的程序或脚本",
            Filter = "可执行文件|*.exe;*.bat;*.cmd;*.ps1;*.lnk|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            AddItem(dlg.FileName);
    }

    private void AddItem(string fullPath)
    {
        if (_startupItems.Any(x => x.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = $"已存在：{System.IO.Path.GetFileName(fullPath)}";
            return;
        }

        var editWin = new StartupItemEditWindow(new StartupItemVm
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(fullPath),
            FullPath = fullPath
        }) { Owner = this };

        if (editWin.ShowDialog() == true)
        {
            _startupItems.Add(editWin.Result);
            SaveItems();
            StatusText.Text = $"已添加：{editWin.Result.Name}";
        }
    }

    // ──────────────────────────────────────────────
    //  常用启动项操作
    // ──────────────────────────────────────────────

    private void LaunchItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is StartupItemVm vm)
            LaunchItem(vm);
    }

    private void LaunchItem(StartupItemVm vm)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = vm.FullPath,
                Arguments = vm.Arguments ?? "",
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败：{ex.Message}", "常用启动项", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is StartupItemVm vm)
        {
            var editWin = new StartupItemEditWindow(vm.Clone()) { Owner = this };
            if (editWin.ShowDialog() == true)
            {
                var idx = _startupItems.IndexOf(vm);
                if (idx >= 0)
                    _startupItems[idx] = editWin.Result;
                SaveItems();
            }
        }
    }

    private void RemoveItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is StartupItemVm vm)
        {
            if (MessageBox.Show($"确认删除「{vm.Name}」？", "常用启动项",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _startupItems.Remove(vm);
                SaveItems();
            }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

// ──────────────────────────────────────────────
//  辅助类型
// ──────────────────────────────────────────────

public class ScanResultItem : INotifyPropertyChanged
{
    private string _fileName;
    private string _fullPath;

    public string FileName
    {
        get => _fileName;
        set
        {
            if (string.Equals(_fileName, value, StringComparison.Ordinal))
            {
                return;
            }

            _fileName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileName)));
        }
    }

    public string FullPath
    {
        get => _fullPath;
        set
        {
            if (string.Equals(_fullPath, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _fullPath = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullPath)));
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
}

internal sealed class StartupSearchResponse
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; }
    public string StatusMessage { get; set; }
    public bool IsCatchingUp { get; set; }
    public bool IsFinal { get; set; }
    public int TotalIndexedCount { get; set; }
    public int TotalMatchedCount { get; set; }
    public bool IsTruncated { get; set; }
    public System.Collections.Generic.List<StartupSearchResultItem> Results { get; set; } = new();
}

internal sealed class StartupSearchResultItem
{
    public string FullPath { get; set; }
    public string FileName { get; set; }
    public long SizeBytes { get; set; }
    public DateTime ModifiedTimeUtc { get; set; }
    public string RootPath { get; set; }
    public string RootDisplayName { get; set; }
    public bool IsDirectory { get; set; }
}

internal sealed class StartupHelperRequest
{
    public string Action { get; set; }
    public string Keyword { get; set; }
    public int MaxResults { get; set; }
    public bool ForceRescan { get; set; }
}

public class StartupItemVm : INotifyPropertyChanged
{
    private string _name;
    private string _fullPath;
    private string _arguments;
    private string _note;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(nameof(Name)); }
    }

    public string FullPath
    {
        get => _fullPath;
        set { _fullPath = value; OnPropertyChanged(nameof(FullPath)); }
    }

    public string Arguments
    {
        get => _arguments;
        set { _arguments = value; OnPropertyChanged(nameof(Arguments)); }
    }

    public string Note
    {
        get => _note;
        set { _note = value; OnPropertyChanged(nameof(Note)); OnPropertyChanged(nameof(NoteVisibility)); }
    }

    public Visibility NoteVisibility =>
        string.IsNullOrWhiteSpace(_note) ? Visibility.Collapsed : Visibility.Visible;

    public StartupItemVm Clone() => new()
    {
        Name = Name, FullPath = FullPath, Arguments = Arguments, Note = Note
    };

    public PackageManager.Services.CommonStartupItem ToModel() => new()
    {
        Name = Name, FullPath = FullPath, Arguments = Arguments ?? "", Note = Note ?? ""
    };

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
