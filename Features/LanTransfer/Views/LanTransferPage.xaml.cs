using System;
using System.Collections.Generic;
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
    private readonly Dictionary<string, SecretChatWindow> _secretChatWindows = new Dictionary<string, SecretChatWindow>(StringComparer.OrdinalIgnoreCase);
    private string _manualAddress;

    /// <summary>
    /// 初始化 <see cref="LanTransferPage"/> 的新实例。
    /// </summary>
    /// <param name="service">局域网传输服务实例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> 为 null。</exception>
    public LanTransferPage(LanTransferService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        InitializeComponent();
        DataContext = this;
        HookServiceEvents();
    }

    /// <summary>
    /// 请求退出当前页面的导航事件。
    /// </summary>
    public event Action RequestExit;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// 获取局域网传输服务实例。
    /// </summary>
    public LanTransferService Service => _service;

    /// <summary>
    /// 获取待发送文件的队列集合。
    /// </summary>
    public ObservableCollection<QueuedTransferSource> QueuedItems { get; } = new ObservableCollection<QueuedTransferSource>();

    /// <summary>
    /// 获取或设置当前选中的远程对等端。
    /// </summary>
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

    /// <summary>
    /// 获取或设置当前选中的队列项。
    /// </summary>
    public QueuedTransferSource SelectedQueuedItem
    {
        get => _selectedQueuedItem;
        set => SetProperty(ref _selectedQueuedItem, value);
    }

    /// <summary>
    /// 获取或设置当前选中的活动传输会话。
    /// </summary>
    public LanTransferSession SelectedActiveTransfer
    {
        get => _selectedActiveTransfer;
        set => SetProperty(ref _selectedActiveTransfer, value);
    }

    /// <summary>
    /// 获取或设置手动输入的目标地址。
    /// </summary>
    public string ManualAddress
    {
        get => _manualAddress;
        set => SetProperty(ref _manualAddress, value);
    }

    /// <summary>
    /// 获取页面顶部的摘要文本。
    /// </summary>
    public string HeaderSummaryText => LocalSummaryText;

    /// <summary>
    /// 获取选中接收方的摘要标题。
    /// </summary>
    public string SelectedPeerSummaryTitle => SelectedPeer?.DisplayLabel ?? "未选择接收方";

    /// <summary>
    /// 获取选中接收方的详细信息摘要。
    /// </summary>
    public string SelectedPeerSummaryText => SelectedPeer == null
        ? "先在左侧选择在线设备，或手动输入 IP / 主机名连接。"
        : SelectedPeer.SecretUnreadCount > 0
            ? $"{SelectedPeer.EndpointDisplay} · {SelectedPeer.StatusSummaryText} · 密语未读 {SelectedPeer.SecretUnreadCount} 条"
            : $"{SelectedPeer.EndpointDisplay} · {SelectedPeer.StatusSummaryText}";

    /// <summary>
    /// 获取发送目标摘要文本。
    /// </summary>
    public string SendTargetSummaryText => SelectedPeer == null
        ? "请选择左侧设备后再发送。"
        : SelectedPeer.SecretUnreadCount > 0
            ? $"将发送到：{SelectedPeer.DisplayLabel} · 密语未读 {SelectedPeer.SecretUnreadCount} 条"
            : $"将发送到：{SelectedPeer.DisplayLabel}";

    /// <summary>
    /// 获取待确认请求的摘要文本。
    /// </summary>
    public string PendingRequestSummaryText => Service.PendingRequests.Count == 0
        ? "当前没有待确认请求。"
        : $"待确认 {Service.PendingRequests.Count} 项，保持现有通知和确认流程。";

    /// <summary>
    /// 获取活动传输的摘要文本。
    /// </summary>
    public string ActiveTransferSummaryText => Service.ActiveTransfers.Count == 0
        ? "当前没有进行中的传输。"
        : $"正在处理 {Service.ActiveTransfers.Count} 项传输。";

    /// <summary>
    /// 获取传输历史的摘要文本。
    /// </summary>
    public string HistorySummaryText => Service.TransferHistory.Count == 0
        ? "暂无历史记录，默认折叠显示。"
        : $"最近 {Service.TransferHistory.Count} 条记录，默认折叠显示。";

    /// <summary>
    /// 获取本机网络状态的摘要文本。
    /// </summary>
    public string LocalSummaryText => $"本机：{Service.DisplayName} · 机器名：{Service.MachineName} · 在线用户：{Service.OnlinePeerCount} · 监听端口：{Service.ListenPort}";

    /// <summary>
    /// 获取收件箱路径的摘要文本。
    /// </summary>
    public string InboxSummaryText => $"收件箱：{Service.InboxPath}";

    /// <summary>
    /// 获取发送队列的摘要文本。
    /// </summary>
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
            || (e.PropertyName == nameof(LanPeerInfo.StatusSummaryText))
            || (e.PropertyName == nameof(LanPeerInfo.SupportsSecretChat))
            || (e.PropertyName == nameof(LanPeerInfo.SecretUnreadCount)))
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

    private async void SecretChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPeer == null)
        {
            MessageBox.Show("请先选择一个在线同事。", "密语", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var session = await Service.RequestSecretChatAsync(SelectedPeer);
            OpenSecretChatWindow(session);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"密语请求失败：{ex.Message}", "密语", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenSecretChatWindow(SecretChatSession session)
    {
        if (session == null)
        {
            return;
        }

        var key = string.IsNullOrWhiteSpace(session.SessionKey) ? session.SessionId : session.SessionKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        _secretChatWindows.TryGetValue(key, out var existing);
        if (existing != null)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Show();
            existing.Activate();
            existing.Focus();
            return;
        }

        var window = new SecretChatWindow(Service, session)
        {
            Owner = Window.GetWindow(this) ?? Application.Current?.MainWindow,
        };
        _secretChatWindows[key] = window;
        window.Closed += (_, __) => _secretChatWindows.Remove(key);
        window.Show();
        window.Activate();
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

/// <summary>
/// 待发送文件/文件夹队列中的单个条目。
/// </summary>
public sealed class QueuedTransferSource
{
    /// <summary>
    /// 获取或设置文件或文件夹的完整路径。
    /// </summary>
    public string FullPath { get; set; }

    /// <summary>
    /// 获取或设置显示名称。
    /// </summary>
    public string DisplayName { get; set; }

    /// <summary>
    /// 获取或设置条目类型（如 "文件" 或 "文件夹"）。
    /// </summary>
    public string ItemType { get; set; }

    /// <summary>
    /// 获取或设置总字节数。
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// 获取或设置摘要描述文本。
    /// </summary>
    public string SummaryText { get; set; }
}
