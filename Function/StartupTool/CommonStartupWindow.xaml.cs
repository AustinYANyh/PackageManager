using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MftScanner;
using PackageManager.Services;

namespace PackageManager.Function.StartupTool;

public partial class CommonStartupWindow : Window
{
    private const int MaxDisplayedResults = 500;
    private static readonly string[] LaunchableExtensions = { ".exe", ".bat", ".cmd", ".ps1", ".lnk" };

    private readonly DataPersistenceService _persistence;
    private readonly DispatcherTimer _debounceTimer;
    private readonly DispatcherTimer _liveRefreshTimer;
    private readonly object _indexGate = new();
    private readonly ObservableCollection<ScanResultItem> _scanResults = new();
    private readonly ObservableCollection<StartupItemVm> _startupItems = new();
    private readonly IndexService _indexService = new();
    private CancellationTokenSource _scanCts;
    private CancellationTokenSource _indexCts;
    private Task<int> _indexTask;
    private bool _indexReady;
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
        _liveRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _liveRefreshTimer.Tick += LiveRefreshTimer_Tick;
        _indexService.IndexChanged += IndexService_IndexChanged;
        _indexService.IndexStatusChanged += IndexService_IndexStatusChanged;
    }

    public void FocusSearchBoxAndSelectAll()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void LoadSavedItems()
    {
        var settings = _persistence.LoadSettings();
        _startupItems.Clear();
        foreach (var item in settings.CommonStartupItems ?? Enumerable.Empty<CommonStartupItem>())
        {
            _startupItems.Add(new StartupItemVm
            {
                Name = item.Name,
                FullPath = item.FullPath,
                Arguments = item.Arguments,
                Note = item.Note
            });
        }
    }

    private void SaveItems()
    {
        var settings = _persistence.LoadSettings();
        settings.CommonStartupItems = _startupItems.Select(v => v.ToModel()).ToList();
        _persistence.SaveSettings(settings);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        StatusText.Text = "正在建立 MFT 索引，首次加载可能稍慢，请稍候。";
        _ = EnsureIndexAsync(forceRescan: false);
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _debounceTimer.Stop();
        _liveRefreshTimer.Stop();
        CancelActiveSearch();
        CancelIndexing();
        _indexService.IndexChanged -= IndexService_IndexChanged;
        _indexService.IndexStatusChanged -= IndexService_IndexStatusChanged;
        _indexService.Shutdown();
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
            ScanCountText.Text = string.Empty;
            StatusText.Text = _indexReady
                ? $"已索引 {_indexService.Index.TotalCount} 个对象，输入文件名关键词开始检索。"
                : "正在建立 MFT 索引，请稍候。";
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

    private void LiveRefreshTimer_Tick(object sender, EventArgs e)
    {
        _liveRefreshTimer.Stop();

        if (!IsLoaded || string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            return;
        }

        _ = StartScanAsync(forceRescan: false, preserveExistingResults: true, silentRefresh: true);
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

    private void CancelIndexing()
    {
        lock (_indexGate)
        {
            _indexCts?.Cancel();
            _indexCts?.Dispose();
            _indexCts = null;
            _indexTask = null;
            _indexReady = false;
        }
    }

    private Task<int> EnsureIndexAsync(bool forceRescan)
    {
        lock (_indexGate)
        {
            if (!forceRescan && _indexReady && _indexTask != null && !_indexTask.IsCanceled && !_indexTask.IsFaulted)
            {
                return _indexTask;
            }

            if (!forceRescan && _indexTask != null && !_indexTask.IsCompleted)
            {
                return _indexTask;
            }

            _indexCts?.Cancel();
            _indexCts?.Dispose();
            _indexCts = new CancellationTokenSource();
            _indexReady = false;

            var progress = new Progress<string>(message =>
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    StatusText.Text = message;
                }
            });

            _indexTask = forceRescan
                ? _indexService.RebuildIndexAsync(progress, _indexCts.Token)
                : _indexService.BuildIndexAsync(progress, _indexCts.Token);

            return _indexTask;
        }
    }

    private async Task<bool> WaitForIndexReadyAsync(bool forceRescan)
    {
        try
        {
            var indexedCount = await EnsureIndexAsync(forceRescan).ConfigureAwait(true);
            _indexReady = true;
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                StatusText.Text = $"已索引 {indexedCount} 个对象，输入文件名关键词开始检索。";
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            if (!IsLoaded)
            {
                return false;
            }

            StatusText.Text = forceRescan ? "索引重建已取消。" : "索引初始化已取消。";
            return false;
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "[StartupSearch] Initialize index failed");
            StatusText.Text = $"初始化索引失败：{ex.Message}";
            return false;
        }
    }

    private async Task StartScanAsync(bool forceRescan, bool preserveExistingResults = false, bool silentRefresh = false)
    {
        var keyword = SearchBox.Text?.Trim();
        CancelActiveSearch();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        var currentVersion = Interlocked.Increment(ref _searchVersion);

        if (!preserveExistingResults && (!string.IsNullOrEmpty(keyword) || forceRescan))
        {
            _scanResults.Clear();
            ScanCountText.Text = string.Empty;
        }

        if (!silentRefresh)
        {
            SetScanningState(true);
            StatusText.Text = forceRescan
                ? "正在重建启动项搜索索引…"
                : string.IsNullOrEmpty(keyword)
                    ? "正在准备索引…"
                    : $"正在搜索 \"{keyword}\"…";
        }

        try
        {
            var indexReady = await WaitForIndexReadyAsync(forceRescan).ConfigureAwait(true);
            if (!indexReady || ct.IsCancellationRequested || currentVersion != _searchVersion)
            {
                return;
            }

            if (string.IsNullOrEmpty(keyword))
            {
                StatusText.Text = $"已索引 {_indexService.Index.TotalCount} 个对象，输入文件名关键词开始检索。";
                return;
            }

            var queryProgress = new Progress<string>(message =>
            {
                if (!silentRefresh && !string.IsNullOrWhiteSpace(message) && currentVersion == _searchVersion && !ct.IsCancellationRequested)
                {
                    StatusText.Text = message;
                }
            });

            var response = await _indexService.SearchAsync(keyword, MaxDisplayedResults, 0, queryProgress, ct)
                .ConfigureAwait(true);

            if (ct.IsCancellationRequested || currentVersion != _searchVersion)
            {
                return;
            }

            ApplySearchResult(response);
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
                LoggingService.LogError(ex, $"[StartupSearch] StartScanAsync failed: {ex.Message}");
                StatusText.Text = $"检索出错：{ex.Message}";
            }
        }
        finally
        {
            if (!silentRefresh && currentVersion == _searchVersion)
            {
                SetScanningState(false);
            }
        }
    }

    private void ApplySearchResult(SearchQueryResult response)
    {
        var results = FilterStartupResults(response?.Results);
        ReconcileSearchResults(results);

        ScanCountText.Text = $"共 {results.Count} 项";

        if (results.Count == 0)
        {
            if ((response?.TotalMatchedCount ?? 0) > 0)
            {
                StatusText.Text = _indexService.IsBackgroundCatchUpInProgress
                    ? "检索到匹配对象，但没有可直接作为启动项的文件，后台仍在追平…"
                    : "检索到匹配对象，但没有可直接作为启动项的文件。";
            }
            else
            {
                StatusText.Text = _indexService.IsBackgroundCatchUpInProgress
                    ? $"未找到匹配项（共 {response?.TotalIndexedCount ?? _indexService.Index.TotalCount} 个对象，后台追平中）"
                    : $"未找到匹配项（共 {response?.TotalIndexedCount ?? _indexService.Index.TotalCount} 个对象）";
            }

            return;
        }

        if (_indexService.IsBackgroundCatchUpInProgress)
        {
            StatusText.Text = $"显示 {results.Count} 个启动项（共 {response?.TotalMatchedCount ?? 0} 个对象，后台追平中）";
            return;
        }

        if (response?.IsTruncated == true)
        {
            StatusText.Text = $"显示 {results.Count} 个启动项（共 {response.TotalMatchedCount} 个对象，输入更多字符可继续缩小）。";
            return;
        }

        StatusText.Text = $"{results.Count} 个启动项（共 {response?.TotalIndexedCount ?? _indexService.Index.TotalCount} 个对象）";
    }

    private void ReconcileSearchResults(IReadOnlyList<ScanResultItem> results)
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

        var existingPathSet = new HashSet<string>(
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

    private static List<ScanResultItem> FilterStartupResults(IEnumerable<ScannedFileInfo> results)
    {
        var filtered = new List<ScanResultItem>();
        var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in results ?? Enumerable.Empty<ScannedFileInfo>())
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

    private void IndexService_IndexStatusChanged(object sender, IndexStatusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (e == null)
            {
                return;
            }

            if (e.RequireSearchRefresh && _indexReady && !string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                _ = StartScanAsync(forceRescan: false);
                return;
            }

            if (_indexReady && !string.IsNullOrWhiteSpace(SearchBox.Text) && e.IsBackgroundCatchUpInProgress)
            {
                ScheduleLiveRefresh();
            }

            if (string.IsNullOrWhiteSpace(SearchBox.Text) && !string.IsNullOrWhiteSpace(e.Message))
            {
                StatusText.Text = e.Message;
            }
        }));
    }

    private void IndexService_IndexChanged(object sender, IndexChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_indexReady || string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                return;
            }

            ScheduleLiveRefresh();
        }));
    }

    private void ScheduleLiveRefresh()
    {
        _liveRefreshTimer.Stop();
        _liveRefreshTimer.Start();
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

    private void SetScanningState(bool scanning)
    {
        StopButton.IsEnabled = scanning;
    }

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
                Arguments = vm.Arguments ?? string.Empty,
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

    public CommonStartupItem ToModel() => new()
    {
        Name = Name, FullPath = FullPath, Arguments = Arguments ?? string.Empty, Note = Note ?? string.Empty
    };

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

