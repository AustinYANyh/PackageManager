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
using System.Windows.Threading;
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
        Loaded += LanTransferPage_Loaded;
        _peerListRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _peerListRefreshTimer.Tick += (_, __) => RebuildPeerList();
        _peerListRefreshTimer.Start();
        RebuildPeerList();
    }

    private async void LanTransferPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Service.PullSecretMailboxAsync();
        }
        catch
        {
        }
    }

    private readonly DispatcherTimer _peerListRefreshTimer;
    private PeerListItem _selectedPeerItem;
    private readonly Dictionary<string, PeerListItem> _peerItemPool = new Dictionary<string, PeerListItem>(StringComparer.OrdinalIgnoreCase);
    private bool _syncingPeerList;

    /// <summary>
    /// 获取统一同事列表（在线设备 + 离线联系人，徽标区分）。
    /// </summary>
    public ObservableCollection<PeerListItem> UnifiedPeers { get; } = new ObservableCollection<PeerListItem>();

    /// <summary>
    /// 获取或设置当前选中的同事条目（在线或离线）。
    /// </summary>
    public PeerListItem SelectedPeerItem
    {
        get => _selectedPeerItem;
        set
        {
            var previous = _selectedPeerItem;
            if (SetProperty(ref _selectedPeerItem, value))
            {
                if (previous != null)
                {
                    previous.PropertyChanged -= PeerItem_PropertyChanged;
                }

                if (_selectedPeerItem != null)
                {
                    _selectedPeerItem.PropertyChanged += PeerItem_PropertyChanged;
                }

                OnPropertyChanged(nameof(SelectedPeer));
                OnPropertyChanged(nameof(SelectedPeerSummaryTitle));
                OnPropertyChanged(nameof(SelectedPeerSummaryText));
                OnPropertyChanged(nameof(SendTargetSummaryText));
            }
        }
    }

    /// <summary>
    /// 获取当前选中条目对应的在线对端；离线联系人时为 null。
    /// </summary>
    public LanPeerInfo SelectedPeer => SelectedPeerItem?.Peer;

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
    public string SelectedPeerSummaryTitle => SelectedPeerItem?.DisplayLabel ?? "未选择接收方";

    /// <summary>
    /// 获取选中接收方的详细信息摘要。
    /// </summary>
    public string SelectedPeerSummaryText => SelectedPeerItem == null
        ? "先在左侧选择在线同事，或手动输入 IP / 主机名连接。"
        : SelectedPeerItem.IsOnline
            ? $"{SelectedPeerItem.SubLineText} · {SelectedPeerItem.StatusLineText}"
            : $"离线 · 密语走信箱投递 · {SelectedPeerItem.StatusLineText}";

    /// <summary>
    /// 获取发送目标摘要文本。
    /// </summary>
    public string SendTargetSummaryText => SelectedPeerItem == null
        ? "请选择左侧同事后再发送。"
        : SelectedPeerItem.IsOnline
            ? $"将发送到：{SelectedPeerItem.DisplayLabel}"
            : $"将发送到：{SelectedPeerItem.DisplayLabel} · 离线，仅支持密语信箱";

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
    /// 获取同事列表的计数摘要（在线 x · 离线 y）。
    /// </summary>
    public string PeerListSummaryText
    {
        get
        {
            var online = UnifiedPeers.Count(item => item.IsOnline);
            var offline = UnifiedPeers.Count - online;
            return offline > 0 ? $"在线 {online} · 离线 {offline}" : $"在线 {online}";
        }
    }

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
        Service.Peers.CollectionChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(LocalSummaryText));
            OnPropertyChanged(nameof(PeerListSummaryText));
            RebuildPeerList();
        };
        Service.PendingRequests.CollectionChanged += (_, __) => OnPropertyChanged(nameof(PendingRequestSummaryText));
        Service.ActiveTransfers.CollectionChanged += (_, __) => OnPropertyChanged(nameof(ActiveTransferSummaryText));
        Service.TransferHistory.CollectionChanged += (_, __) => OnPropertyChanged(nameof(HistorySummaryText));
        QueuedItems.CollectionChanged += (_, __) => OnPropertyChanged(nameof(QueueSummaryText));
    }

    private void PeerItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (!_syncingPeerList && e.PropertyName == nameof(PeerListItem.IsOnline))
        {
            // 在线/离线翻转：立即重排分组（Move 保持选中）
            RebuildPeerList();
            return;
        }

        OnPropertyChanged(nameof(SelectedPeerSummaryTitle));
        OnPropertyChanged(nameof(SelectedPeerSummaryText));
        OnPropertyChanged(nameof(SendTargetSummaryText));
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
            RebuildPeerList();
            SelectedPeerItem = UnifiedPeers.FirstOrDefault(item => item.IsOnline
                                                                   && string.Equals(item.DeviceId, peer.DeviceId, StringComparison.OrdinalIgnoreCase))
                               ?? UnifiedPeers.FirstOrDefault(item => item.IsOnline);
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
        if (SelectedPeerItem == null)
        {
            MessageBox.Show("请先选择一个同事。", "文件传输", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!SelectedPeerItem.IsOnline)
        {
            MessageBox.Show("当前选中的是离线同事，仅支持密语；文件传输请选择在线设备。", "文件传输", MessageBoxButton.OK, MessageBoxImage.Information);
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

    /// <summary>
    /// 同步统一同事列表：包装对象按设备号常驻复用，在线/离线翻转仅更新同一对象，
    /// 选中状态因此跨状态切换存活；排序用 Move 保持选中不丢失。
    /// </summary>
    public void RebuildPeerList()
    {
        if (_syncingPeerList)
        {
            return;
        }

        _syncingPeerList = true;
        try
        {
            var desired = new List<PeerListItem>();

            // 全部已发现设备（在线与刚离线的），包装常驻
            foreach (var peer in Service.Peers.Where(peer => peer != null && !string.IsNullOrWhiteSpace(peer.DeviceId)))
            {
                var wrapper = GetOrCreatePeerItem(peer.DeviceId);
                wrapper.UpdatePeer(peer);
                desired.Add(wrapper);
            }

            // 离线联系人：设备未出现在 Peers 中的才补（同设备已在 peer 包装里）
            foreach (var contact in Service.GetOfflineSecretContacts())
            {
                if (desired.Any(item => string.Equals(item.DeviceId, contact.DeviceId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var wrapper = GetOrCreatePeerItem(contact.DeviceId);
                wrapper.UpdateContact(contact, Service.GetSecretUnreadCountForDevice(contact.DeviceId));
                desired.Add(wrapper);
            }

            // 在线在前、其余按最近活跃降序
            desired = desired
                .OrderByDescending(item => item.IsOnline)
                .ThenByDescending(item => item.LastSeenUtc)
                .ToList();

            // 移除彻底消失的设备
            foreach (var gone in UnifiedPeers.Except(desired).ToList())
            {
                UnifiedPeers.Remove(gone);
                gone.Detach();
                _peerItemPool.Remove(gone.DeviceId);
            }

            // Move/Insert 对齐目标顺序（Move 不打断选中）
            for (var i = 0; i < desired.Count; i++)
            {
                var index = UnifiedPeers.IndexOf(desired[i]);
                if (index < 0)
                {
                    UnifiedPeers.Insert(Math.Min(i, UnifiedPeers.Count), desired[i]);
                }
                else if (index != i)
                {
                    UnifiedPeers.Move(index, i);
                }
            }

            OnPropertyChanged(nameof(PeerListSummaryText));
        }
        finally
        {
            _syncingPeerList = false;
        }
    }

    private PeerListItem GetOrCreatePeerItem(string deviceId)
    {
        if (!_peerItemPool.TryGetValue(deviceId, out var wrapper))
        {
            wrapper = new PeerListItem(deviceId);
            _peerItemPool[deviceId] = wrapper;
        }

        return wrapper;
    }

    private async void SecretChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPeerItem == null)
        {
            MessageBox.Show("请先选择一个同事。", "密语", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var session = SelectedPeerItem.IsOnline
                ? await Service.RequestSecretChatAsync(SelectedPeer)
                : await Service.RequestSecretChatWithContactAsync(ResolveContactForOfflineItem(SelectedPeerItem));
            OpenSecretChatWindow(session);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"密语请求失败：{ex.Message}", "密语", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 解析离线条目对应的联系人：常规取 Contact；在线条目刚翻离线的中间态（Contact 为空）用 peer 信息现场补建，避免误报「联系人不可用」。
    /// </summary>
    /// <param name="item">选中的同事条目。</param>
    /// <returns>用于信箱路径的联系人。</returns>
    private static SecretContact ResolveContactForOfflineItem(PeerListItem item)
    {
        if (item.Contact != null)
        {
            return item.Contact;
        }

        var peer = item.Peer;
        return new SecretContact
        {
            DeviceId = item.DeviceId,
            DisplayName = peer?.DisplayName,
            MachineName = peer?.MachineName,
            LastSeenUtc = peer?.LastSeenUtc ?? DateTime.UtcNow,
            PublicKeyXml = peer?.SecretChatPublicKey,
        };
    }

    private void OpenSecretChatWindow(SecretChatSession session)
    {
        OpenSecretChatWindowInternal(session);
    }

    private Window OpenSecretChatWindowInternal(SecretChatSession session)
    {
        if (session == null)
        {
            return null;
        }

        var key = string.IsNullOrWhiteSpace(session.SessionKey) ? session.SessionId : session.SessionKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
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
            return existing;
        }

        var window = new SecretChatWindow(Service, session)
        {
            Owner = Window.GetWindow(this) ?? Application.Current?.MainWindow,
        };
        _secretChatWindows[key] = window;
        window.Closed += (_, __) => _secretChatWindows.Remove(key);
        window.Show();
        window.Activate();
        return window;
    }

    private async void SecretSelfTestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sessions = await Service.StartSecretSelfTestAsync();
            var selfWindow = OpenSecretChatWindowInternal(sessions[0]);
            var shadowWindow = OpenSecretChatWindowInternal(sessions[1]);
            if (selfWindow != null && shadowWindow != null)
            {
                shadowWindow.Left = selfWindow.Left + selfWindow.ActualWidth + 16;
                shadowWindow.Top = selfWindow.Top;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"密语自测启动失败：{ex.Message}", "密语", MessageBoxButton.OK, MessageBoxImage.Warning);
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

/// <summary>
/// 统一同事列表条目：按设备号常驻，包装在线设备（LanPeerInfo）或离线联系人（SecretContact），
/// 在线/离线翻转仅切换同一对象的数据源，徽标与选中状态因此平滑过渡。
/// </summary>
public sealed class PeerListItem : INotifyPropertyChanged
{
    /// <summary>在线对端属性变更时需要转发到本条目绑定属性的映射。</summary>
    private static readonly Dictionary<string, string[]> ForwardMap = new Dictionary<string, string[]>
    {
        [nameof(LanPeerInfo.DisplayLabel)] = new[] { nameof(DisplayLabel) },
        [nameof(LanPeerInfo.EndpointDisplay)] = new[] { nameof(SubLineText) },
        [nameof(LanPeerInfo.StatusSummaryText)] = new[] { nameof(StatusLineText) },
        [nameof(LanPeerInfo.SecretUnreadCount)] = new[] { nameof(SecretUnread), nameof(SecretStatusText), nameof(HasSecretUnread), nameof(SecretBadgeText) },
        [nameof(LanPeerInfo.SupportsSecretChat)] = new[] { nameof(SecretStatusText) },
        [nameof(LanPeerInfo.IsOnline)] = new[] { nameof(IsOnline), nameof(IsOffline), nameof(SubLineText), nameof(SecretStatusText) },
        [nameof(LanPeerInfo.OnlineText)] = new[] { nameof(IsOnline), nameof(IsOffline) },
        [nameof(LanPeerInfo.CanSend)] = new[] { nameof(IsOnline), nameof(IsOffline) },
    };

    private LanPeerInfo peer;
    private SecretContact contact;
    private int offlineUnread;

    /// <summary>
    /// 初始化指定设备的常驻条目（初始无数据源，随后经 UpdatePeer/UpdateContact 填充）。
    /// </summary>
    /// <param name="deviceId">设备标识。</param>
    public PeerListItem(string deviceId)
    {
        DeviceId = deviceId;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>设备标识（常驻键）。</summary>
    public string DeviceId { get; }

    /// <summary>当前关联的在线对端；设备已从发现列表消失时为 null。</summary>
    public LanPeerInfo Peer => peer;

    /// <summary>当前关联的离线联系人；设备在线时为 null。</summary>
    public SecretContact Contact => contact;

    /// <summary>是否在线。</summary>
    public bool IsOnline => peer != null && peer.IsOnline;

    /// <summary>是否离线。</summary>
    public bool IsOffline => !IsOnline;

    /// <summary>最近活跃时间（UTC），在线取对端，离线取联系人。</summary>
    public DateTime LastSeenUtc => peer?.LastSeenUtc ?? contact?.LastSeenUtc ?? DateTime.MinValue;

    /// <summary>显示标签。</summary>
    public string DisplayLabel => peer != null ? peer.DisplayLabel : (contact?.DisplayLabel ?? "未知同事");

    /// <summary>副标题：在线显示端点，离线显示信箱提示。</summary>
    public string SubLineText => IsOnline ? peer.EndpointDisplay : "离线 · 密语走信箱投递";

    /// <summary>状态行：在线显示最近活跃，离线显示上次活跃时间。</summary>
    public string StatusLineText
    {
        get
        {
            if (IsOnline)
            {
                return peer.StatusSummaryText;
            }

            var lastSeen = LastSeenUtc.ToLocalTime();
            return lastSeen.Date == DateTime.Today
                ? $"上次活跃 {lastSeen:HH:mm}"
                : $"上次活跃 {lastSeen:MM-dd HH:mm}";
        }
    }

    /// <summary>密语未读数。</summary>
    public int SecretUnread => peer != null ? peer.SecretUnreadCount : offlineUnread;

    /// <summary>是否存在密语未读。</summary>
    public bool HasSecretUnread => SecretUnread > 0;

    /// <summary>密语状态文本。</summary>
    public string SecretStatusText => HasSecretUnread
        ? $"密语未读 {SecretUnread} 条"
        : (IsOnline ? peer.SecretChatText : "支持密语（信箱）");

    /// <summary>未读角标文本。</summary>
    public string SecretBadgeText => SecretUnread > 0 ? SecretUnread.ToString() : string.Empty;

    /// <summary>
    /// 绑定在线对端（切换数据源并刷新全部显示属性）。
    /// </summary>
    /// <param name="value">在线对端。</param>
    public void UpdatePeer(LanPeerInfo value)
    {
        if (peer != null)
        {
            peer.PropertyChanged -= Peer_PropertyChanged;
        }

        peer = value;
        if (peer != null)
        {
            peer.PropertyChanged += Peer_PropertyChanged;
        }

        RaiseAll();
    }

    /// <summary>
    /// 绑定离线联系人（切换数据源并刷新全部显示属性）。
    /// </summary>
    /// <param name="value">离线联系人。</param>
    /// <param name="unreadCount">该联系人的密语未读数。</param>
    public void UpdateContact(SecretContact value, int unreadCount)
    {
        contact = value;
        offlineUnread = unreadCount;
        RaiseAll();
    }

    /// <summary>
    /// 仅更新离线未读计数（设备仍在发现列表内的离线态）。
    /// </summary>
    /// <param name="unreadCount">密语未读数。</param>
    public void UpdateOfflineUnread(int unreadCount)
    {
        offlineUnread = unreadCount;
        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var name in new[]
        {
            nameof(IsOnline), nameof(IsOffline), nameof(LastSeenUtc), nameof(DisplayLabel),
            nameof(SubLineText), nameof(StatusLineText), nameof(SecretUnread), nameof(HasSecretUnread),
            nameof(SecretStatusText), nameof(SecretBadgeText),
        })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private void Peer_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != null && ForwardMap.TryGetValue(e.PropertyName, out var targets))
        {
            foreach (var target in targets)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(target));
            }
        }
    }

    /// <summary>
    /// 解除对在线对端事件订阅，供设备消失时释放条目。
    /// </summary>
    public void Detach()
    {
        if (peer != null)
        {
            peer.PropertyChanged -= Peer_PropertyChanged;
            peer = null;
        }
    }
}
