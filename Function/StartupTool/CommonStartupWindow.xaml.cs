using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
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
    private readonly object _activeClientGate = new();
    private readonly ObservableCollection<ScanResultItem> _scanResults = new();
    private readonly ObservableCollection<StartupItemVm> _startupItems = new();
    private CancellationTokenSource _scanCts;
    private Task<bool> _ensureHelperTask;
    private Process _helperProcess;
    private string _helperPipeName;
    private NamedPipeServerStream _activeServer;
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
        DisposeActiveServer();
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

            var response = await QueryHelperAsync(new StartupHelperRequest
            {
                Action = "search",
                Keyword = keyword,
                MaxResults = MaxDisplayedResults,
                ForceRescan = forceRescan
            }, ct).ConfigureAwait(true);

            if (ct.IsCancellationRequested || (currentVersion != _searchVersion))
            {
                return;
            }

            if (response == null)
            {
                StatusText.Text = "未收到检索结果。";
                return;
            }

            if (!response.Success)
            {
                StatusText.Text = $"检索失败：{response.ErrorMessage ?? "未知错误"}";
                return;
            }

            var results = FilterStartupResults(response.Results);
            _scanResults.Clear();
            foreach (var r in results)
                _scanResults.Add(r);

            ScanCountText.Text = $"共 {results.Count} 项";
            if (results.Count == 0)
            {
                StatusText.Text = response.TotalMatchedCount > 0
                    ? "检索到匹配对象，但没有可直接作为启动项的文件。"
                    : $"未找到匹配项（共 {response.TotalIndexedCount} 个对象）";
            }
            else if (response.IsTruncated)
            {
                StatusText.Text = $"显示 {results.Count} 个启动项（共 {response.TotalMatchedCount} 个对象，输入更多字符可继续缩小）。";
            }
            else
            {
                StatusText.Text = $"{results.Count} 个启动项（共 {response.TotalIndexedCount} 个对象）";
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
            var exePath = await Task.Run(() =>
                AdminElevationService.ExtractEmbeddedTool("MftScanner.exe", "MftScanner.exe")).ConfigureAwait(true);

            if (string.IsNullOrEmpty(exePath))
            {
                StatusText.Text = "未找到 MftScanner.exe，请检查安装。";
                return false;
            }

            var pipeName = "PackageManager.StartupSearch." + Guid.NewGuid().ToString("N");
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--startup-helper {Quote(pipeName)}",
                UseShellExecute = true
            };

            var proc = Process.Start(psi);
            if (proc == null)
            {
                StatusText.Text = "检索服务启动失败。";
                return false;
            }

            _helperPipeName = pipeName;
            _helperProcess = proc;
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            StatusText.Text = "用户取消了管理员授权，无法启用 MFT 快速检索。";
            return false;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"启动检索服务失败：{ex.Message}";
            return false;
        }
    }

    private async Task<StartupSearchResponse> QueryHelperAsync(StartupHelperRequest request, CancellationToken cancellationToken, string pipeNameOverride = null)
    {
        var pipeName = string.IsNullOrWhiteSpace(pipeNameOverride) ? _helperPipeName : pipeNameOverride;
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new InvalidOperationException("检索服务未启动。");
        }

        NamedPipeServerStream server = null;
        try
        {
            server = CreateServer(pipeName);
            SetActiveServer(server);
            await WaitForHelperConnectionAsync(server, cancellationToken).ConfigureAwait(false);

            using (var writer = new StreamWriter(server, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
            using (var reader = new StreamReader(server, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true))
            {
                var payload = JsonConvert.SerializeObject(request);
                await writer.WriteLineAsync(payload).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var responseJson = await reader.ReadLineAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(responseJson))
                {
                    return null;
                }

                return JsonConvert.DeserializeObject<StartupSearchResponse>(responseJson);
            }
        }
        finally
        {
            ClearActiveServer(server);
            server?.Dispose();
        }
    }

    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    private static async Task WaitForHelperConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using (cancellationToken.Register(() =>
               {
                   try { server.Dispose(); } catch { }
               }))
        {
            try
            {
                await Task.Run(() => server.WaitForConnection(), cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
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

    private void SetActiveServer(NamedPipeServerStream server)
    {
        lock (_activeClientGate)
        {
            _activeServer = server;
        }
    }

    private void ClearActiveServer(NamedPipeServerStream server)
    {
        lock (_activeClientGate)
        {
            if (ReferenceEquals(_activeServer, server))
            {
                _activeServer = null;
            }
        }
    }

    private void DisposeActiveServer()
    {
        lock (_activeClientGate)
        {
            try
            {
                _activeServer?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _activeServer = null;
            }
        }
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

public class ScanResultItem
{
    public string FileName { get; set; }
    public string FullPath { get; set; }
}

internal sealed class StartupSearchResponse
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; }
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
