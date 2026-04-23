using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PackageManager.Services;

namespace PackageManager.Views;

/// <summary>
/// 文件传输页面。
/// </summary>
public partial class LanTransferPage : Page, INotifyPropertyChanged, ICentralPage
{
    private readonly LanTransferService _service;
    private CancellationTokenSource _sendCancellationTokenSource;
    private LanPeerInfo _selectedPeer;
    private QueuedTransferSource _selectedQueuedItem;
    private LanTransferSession _selectedActiveTransfer;
    private string _manualAddress;

    public LanTransferPage(LanTransferService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        InitializeComponent();
        DataContext = this;
        HookServiceEvents();
    }

    public event Action RequestExit;

    public event PropertyChangedEventHandler PropertyChanged;

    public LanTransferService Service => _service;

    public ObservableCollection<QueuedTransferSource> QueuedItems { get; } = new ObservableCollection<QueuedTransferSource>();

    public LanPeerInfo SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            var previous = _selectedPeer;
            if (SetProperty(ref _selectedPeer, value))
            {
                if (previous != null)
                {
                    previous.PropertyChanged -= SelectedPeer_PropertyChanged;
                }

                if (_selectedPeer != null)
                {
                    _selectedPeer.PropertyChanged += SelectedPeer_PropertyChanged;
                }

                OnPropertyChanged(nameof(SelectedPeerSummaryTitle));
                OnPropertyChanged(nameof(SelectedPeerSummaryText));
                OnPropertyChanged(nameof(SendTargetSummaryText));
            }
        }
    }

    public QueuedTransferSource SelectedQueuedItem
    {
        get => _selectedQueuedItem;
        set => SetProperty(ref _selectedQueuedItem, value);
    }

    public LanTransferSession SelectedActiveTransfer
    {
        get => _selectedActiveTransfer;
        set => SetProperty(ref _selectedActiveTransfer, value);
    }

    public string ManualAddress
    {
        get => _manualAddress;
        set => SetProperty(ref _manualAddress, value);
    }

    public string HeaderSummaryText => LocalSummaryText;

    public string SelectedPeerSummaryTitle => SelectedPeer?.DisplayLabel ?? "未选择接收方";

    public string SelectedPeerSummaryText => SelectedPeer == null
        ? "先在左侧选择在线设备，或手动输入 IP / 主机名连接。"
        : $"{SelectedPeer.EndpointDisplay} · {SelectedPeer.StatusSummaryText}";

    public string SendTargetSummaryText => SelectedPeer == null
        ? "请选择左侧设备后再发送。"
        : $"将发送到：{SelectedPeer.DisplayLabel}";

    public string PendingRequestSummaryText => Service.PendingRequests.Count == 0
        ? "当前没有待确认请求。"
        : $"待确认 {Service.PendingRequests.Count} 项，保持现有通知和确认流程。";

    public string ActiveTransferSummaryText => Service.ActiveTransfers.Count == 0
        ? "当前没有进行中的传输。"
        : $"正在处理 {Service.ActiveTransfers.Count} 项传输。";

    public string HistorySummaryText => Service.TransferHistory.Count == 0
        ? "暂无历史记录，默认折叠显示。"
        : $"最近 {Service.TransferHistory.Count} 条记录，默认折叠显示。";

    public string LocalSummaryText => $"本机：{Service.DisplayName} · 机器名：{Service.MachineName} · 在线用户：{Service.OnlinePeerCount} · 监听端口：{Service.ListenPort}";

    public string InboxSummaryText => $"收件箱：{Service.InboxPath}";

    public string QueueSummaryText
    {
        get
        {
            var count = QueuedItems.Count;
            var totalBytes = QueuedItems.Sum(item => item.TotalBytes);
            return count == 0
                ? "将文件或文件夹拖到这里，或点击上方按钮添加。"
                : $"已加入 {count} 项，总大小 {LanTransferFormatting.FormatSize(totalBytes)}";
        }
    }

    private void HookServiceEvents()
    {
        Service.PropertyChanged += Service_PropertyChanged;
        Service.Peers.CollectionChanged += (_, __) => OnPropertyChanged(nameof(LocalSummaryText));
        Service.PendingRequests.CollectionChanged += (_, __) => OnPropertyChanged(nameof(PendingRequestSummaryText));
        Service.ActiveTransfers.CollectionChanged += (_, __) => OnPropertyChanged(nameof(ActiveTransferSummaryText));
        Service.TransferHistory.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HistorySummaryText));
        QueuedItems.CollectionChanged += (_, __) => OnPropertyChanged(nameof(QueueSummaryText));
    }

    private void SelectedPeer_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName == nameof(LanPeerInfo.DisplayLabel))
            || (e.PropertyName == nameof(LanPeerInfo.EndpointDisplay))
            || (e.PropertyName == nameof(LanPeerInfo.StatusText))
            || (e.PropertyName == nameof(LanPeerInfo.LastSeenText))
            || (e.PropertyName == nameof(LanPeerInfo.StatusSummaryText)))
        {
            OnPropertyChanged(nameof(SelectedPeerSummaryTitle));
            OnPropertyChanged(nameof(SelectedPeerSummaryText));
            OnPropertyChanged(nameof(SendTargetSummaryText));
        }
    }

    private void Service_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if ((e.PropertyName == nameof(LanTransferService.ListenPort))
            || (e.PropertyName == nameof(LanTransferService.DisplayName))
            || (e.PropertyName == nameof(LanTransferService.InboxPath))
            || (e.PropertyName == nameof(LanTransferService.StatusText))
            || (e.PropertyName == nameof(LanTransferService.IsEnabled))
            || (e.PropertyName == nameof(LanTransferService.OnlinePeerCount)))
        {
            OnPropertyChanged(nameof(HeaderSummaryText));
            OnPropertyChanged(nameof(LocalSummaryText));
            OnPropertyChanged(nameof(InboxSummaryText));
        }
    }

    private async void ManualConnectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var peer = await Service.ConnectManualPeerAsync(ManualAddress);
            SelectedPeer = peer;
            MessageBox.Show("手动连接成功。", "文件传输", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"手动连接失败：{ex.Message}", "文件传输", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            CheckFileExists = true,
            Title = "选择要发送的文件",
        };

        if (dialog.ShowDialog() == true)
        {
            AddPaths(dialog.FileNames);
        }
    }

    private void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderPickerService.PickFolder("选择要发送的文件夹");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            AddPaths(new[] { folder });
        }
    }

    private void RemoveQueuedButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQueuedItem != null)
        {
            QueuedItems.Remove(SelectedQueuedItem);
        }
    }

    private void ClearQueuedButton_Click(object sender, RoutedEventArgs e)
    {
        QueuedItems.Clear();
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPeer == null)
        {
            MessageBox.Show("请先选择一个在线设备。", "文件传输", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (QueuedItems.Count == 0)
        {
            MessageBox.Show("请先添加要发送的文件或文件夹。", "文件传输", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _sendCancellationTokenSource?.Dispose();
            _sendCancellationTokenSource = new CancellationTokenSource();
            await Service.SendPathsAsync(SelectedPeer, QueuedItems.Select(item => item.FullPath).ToList(), _sendCancellationTokenSource.Token);
            QueuedItems.Clear();
            MessageBox.Show("发送完成。", "文件传输", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show("发送已取消。", "文件传输", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"发送失败：{ex.Message}", "文件传输", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelTransferButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedActiveTransfer == null)
        {
            return;
        }

        if (string.Equals(SelectedActiveTransfer.Direction, "Send", StringComparison.OrdinalIgnoreCase))
        {
            _sendCancellationTokenSource?.Cancel();
            return;
        }

        Service.CancelTransfer(SelectedActiveTransfer.TransferId);
    }

    private void OpenInboxButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Service.OpenInbox();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开收件箱失败：{ex.Message}", "文件传输", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Service.OpenLogs();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开日志失败：{ex.Message}", "文件传输", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        RequestExit?.Invoke();
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        AddPaths(paths);
    }

    private void InnerScrollHost_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (RootScrollViewer == null)
        {
            return;
        }

        e.Handled = true;
        var forwardedEvent = new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        };

        RootScrollViewer.RaiseEvent(forwardedEvent);
    }

    private void AddPaths(string[] paths)
    {
        if (paths == null)
        {
            return;
        }

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (QueuedItems.Any(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                QueuedItems.Add(new QueuedTransferSource
                {
                    FullPath = info.FullName,
                    DisplayName = info.Name,
                    ItemType = "文件",
                    TotalBytes = info.Length,
                    SummaryText = $"文件 · {LanTransferFormatting.FormatSize(info.Length)}",
                });
            }
            else if (Directory.Exists(path))
            {
                var directory = new DirectoryInfo(path);
                var bytes = Directory.GetFiles(directory.FullName, "*", SearchOption.AllDirectories)
                    .Select(file => new FileInfo(file))
                    .Sum(file => file.Length);
                QueuedItems.Add(new QueuedTransferSource
                {
                    FullPath = directory.FullName,
                    DisplayName = directory.Name,
                    ItemType = "文件夹",
                    TotalBytes = bytes,
                    SummaryText = $"文件夹 · {LanTransferFormatting.FormatSize(bytes)}",
                });
            }
        }

        OnPropertyChanged(nameof(QueueSummaryText));
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class QueuedTransferSource
{
    public string FullPath { get; set; }

    public string DisplayName { get; set; }

    public string ItemType { get; set; }

    public long TotalBytes { get; set; }

    public string SummaryText { get; set; }
}
