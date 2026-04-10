using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PackageManager.Services;

namespace PackageManager.Function.StartupTool;

public partial class CommonStartupWindow : Window
{
    private readonly DataPersistenceService _persistence;
    private CancellationTokenSource _scanCts;
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
        StatusText.Text = "正在提取扫描工具…";

        _ = Task.Run(async () =>
        {
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

                Dispatcher.Invoke(() => StatusText.Text = "正在扫描（需要管理员权限）…");

                // 构建扩展名列表：若关键词含扩展名则直接用，否则扫全盘常见可执行类型
                var extensions = GetExtensionsForKeyword(keyword);
                var mmfName = Guid.NewGuid().ToString("N");
                var doneEvent = new EventWaitHandle(false, EventResetMode.ManualReset, mmfName + "_done");

                // 使用 MemoryMappedFile 协议（与 MftIndexProvider 相同）
                using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(mmfName, 32 * 1024 * 1024);

                // 动态枚举所有固定磁盘
                var drives = System.IO.DriveInfo.GetDrives()
                    .Where(d => d.DriveType == System.IO.DriveType.Fixed)
                    .Select(d => $"\"{d.RootDirectory.FullName.TrimEnd('\\')}|{d.Name.TrimEnd('\\', '/')}盘\"");
                var args = $"--mmf {mmfName} {string.Join(" ", extensions)} -- {string.Join(" ", drives)}";
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    UseShellExecute = true   // MftScanner.exe 自带 requireAdministrator 清单
                };

                Process proc;
                try
                {
                    proc = Process.Start(psi);
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "用户取消了 UAC 提权，扫描已中止。";
                        SetScanningState(false);
                    });
                    doneEvent.Dispose();
                    return;
                }

                // 等待完成事件（最多 5 分钟）
                await Task.Run(() => doneEvent.WaitOne(TimeSpan.FromMinutes(5)), ct);
                doneEvent.Dispose();

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

                // 读取结果
                var results = ReadMmfResults(mmf, keyword);

                Dispatcher.Invoke(() =>
                {
                    _scanResults.Clear();
                    foreach (var r in results)
                        _scanResults.Add(r);
                    ScanCountText.Text = $"共 {results.Count} 项";
                    StatusText.Text = results.Count == 0 ? "未找到匹配项。" : $"找到 {results.Count} 个文件。";
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
        }, ct);
    }

    private static string[] GetExtensionsForKeyword(string keyword)
    {
        var ext = System.IO.Path.GetExtension(keyword);
        if (!string.IsNullOrEmpty(ext) && ext != ".*")
            return new[] { ext.TrimStart('.') };
        return new[] { "exe", "bat", "cmd", "ps1", "lnk" };
    }

    private static System.Collections.Generic.List<ScanResultItem> ReadMmfResults(
        System.IO.MemoryMappedFiles.MemoryMappedFile mmf, string keyword)
    {
        var results = new System.Collections.Generic.List<ScanResultItem>();
        using var accessor = mmf.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);

        long pos = 0;
        var magic = accessor.ReadInt32(pos); pos += 4;
        if (magic != 0x4D4D4650) return results;
        var version = accessor.ReadInt32(pos); pos += 4;
        if (version != 1) return results;
        var status = accessor.ReadInt32(pos); pos += 4;
        if (status != 0) return results;
        var count = accessor.ReadInt32(pos); pos += 4;

        var kw = keyword.Replace("*", "").ToLowerInvariant();

        for (int i = 0; i < count; i++)
        {
            var pathLen = accessor.ReadInt32(pos); pos += 4;
            var pathBytes = new byte[pathLen];
            accessor.ReadArray(pos, pathBytes, 0, pathLen); pos += pathLen;
            var fullPath = System.Text.Encoding.Unicode.GetString(pathBytes);

            var sizeBytes = accessor.ReadInt64(pos); pos += 8;
            var modTicks = accessor.ReadInt64(pos); pos += 8;

            var nameLen = accessor.ReadInt32(pos); pos += 4;
            pos += nameLen; // skip display name

            var fileName = System.IO.Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(kw) || fileName.ToLowerInvariant().Contains(kw))
                results.Add(new ScanResultItem { FileName = fileName, FullPath = fullPath });
        }

        return results;
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
