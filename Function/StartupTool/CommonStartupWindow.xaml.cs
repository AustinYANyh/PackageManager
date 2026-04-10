using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;
using PackageManager.Services;

namespace PackageManager.Function.StartupTool;

public partial class CommonStartupWindow : Window
{
    private const int MaxDisplayedResults = 500;
    private static readonly string[] LaunchableExtensions = { ".exe", ".bat", ".cmd", ".ps1", ".lnk" };

    private readonly DataPersistenceService _persistence;
    private CancellationTokenSource _scanCts;
    private Process _scanProcess;
    private readonly ObservableCollection<ScanResultItem> _scanResults = new();
    private readonly ObservableCollection<StartupItemVm> _startupItems = new();

    public CommonStartupWindow(DataPersistenceService persistence)
    {
        InitializeComponent();
        _persistence = persistence;
        ScanResultList.ItemsSource = _scanResults;
        StartupItemList.ItemsSource = _startupItems;
        LoadSavedItems();
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

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            StartScan();
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e) => StartScan();

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        var proc = Interlocked.Exchange(ref _scanProcess, null);
        try
        {
            if (proc != null && !proc.HasExited)
                proc.Kill();
        }
        catch
        {
        }
    }

    private void StartScan()
    {
        var keyword = SearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            MessageBox.Show("请输入要搜索的文件名关键词。", "常用启动项", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        _scanResults.Clear();
        ScanCountText.Text = "";
        SetScanningState(true);
        StatusText.Text = "正在启动 MFT 检索服务…";

        _ = Task.Run(async () =>
        {
            string resultPath = null;
            try
            {
                var exePath = await Task.Run(() =>
                    AdminElevationService.ExtractEmbeddedTool("MftScanner.exe", "MftScanner.exe"), ct);

                if (string.IsNullOrEmpty(exePath))
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "未找到 MftScanner.exe，请检查安装。";
                        SetScanningState(false);
                    });
                    return;
                }

                resultPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PackageManager", "startup_search_" + Guid.NewGuid().ToString("N") + ".json");
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = BuildSearchArguments(resultPath, keyword),
                    UseShellExecute = true
                };

                Process proc;
                try
                {
                    proc = Process.Start(psi);
                    Interlocked.Exchange(ref _scanProcess, proc);
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "用户取消了 UAC 提权，扫描已中止。";
                        SetScanningState(false);
                    });
                    return;
                }

                if (proc == null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "扫描进程启动失败。";
                        SetScanningState(false);
                    });
                    return;
                }

                Dispatcher.Invoke(() => StatusText.Text = "正在通过最新 MFT 检索封装扫描（需要管理员权限）…");

                await Task.Run(() => proc?.WaitForExit(), ct);

                if (ct.IsCancellationRequested)
                {
                    try { proc?.Kill(); } catch { }
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "扫描已停止。";
                        SetScanningState(false);
                    });
                    return;
                }

                var response = await Task.Run(() => ReadSearchResults(resultPath), ct);
                if (response == null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "扫描结果读取失败。";
                        SetScanningState(false);
                    });
                    return;
                }

                if (!response.Success)
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"扫描失败：{response.ErrorMessage ?? "未知错误"}";
                        SetScanningState(false);
                    });
                    return;
                }

                var results = FilterStartupResults(response.Results);

                Dispatcher.Invoke(() =>
                {
                    _scanResults.Clear();
                    foreach (var r in results)
                        _scanResults.Add(r);
                    ScanCountText.Text = $"共 {results.Count} 项";
                    if (results.Count == 0)
                    {
                        StatusText.Text = response.TotalMatchedCount > 0
                            ? "检索到匹配对象，但没有可直接作为启动项的文件。"
                            : "未找到匹配项。";
                    }
                    else if (response.IsTruncated)
                    {
                        StatusText.Text = $"显示 {results.Count} 个启动项（共检索到 {response.TotalMatchedCount} 个对象，输入更多字符可继续缩小）。";
                    }
                    else
                    {
                        StatusText.Text = $"找到 {results.Count} 个启动项（索引对象 {response.TotalIndexedCount} 个）。";
                    }
                    SetScanningState(false);
                });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "扫描已停止。";
                    SetScanningState(false);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"扫描出错：{ex.Message}";
                    SetScanningState(false);
                });
            }
            finally
            {
                var proc = Interlocked.Exchange(ref _scanProcess, null);
                try
                {
                    if (!string.IsNullOrWhiteSpace(resultPath) && File.Exists(resultPath))
                        File.Delete(resultPath);
                }
                catch
                {
                }

                proc?.Dispose();
            }
        }, ct);
    }

    private static string BuildSearchArguments(string resultPath, string keyword)
    {
        var roots = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed)
            .Select(d => Quote($"{d.RootDirectory.FullName}|{d.Name.TrimEnd('\\', '/')}盘"));

        return $"--search-export {Quote(resultPath)} --keyword {Quote(keyword)} --max-results {MaxDisplayedResults} -- {string.Join(" ", roots)}";
    }

    private static StartupSearchResponse ReadSearchResults(string resultPath)
    {
        if (string.IsNullOrWhiteSpace(resultPath) || !File.Exists(resultPath))
        {
            return null;
        }

        var json = File.ReadAllText(resultPath);
        return JsonConvert.DeserializeObject<StartupSearchResponse>(json);
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
