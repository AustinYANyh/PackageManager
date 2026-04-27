using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PackageManager.Services;

public sealed class LanTransferService : LanTransferBindableBase, IDisposable
{
    private readonly DataPersistenceService _dataPersistenceService;
    private readonly ObservableCollection<LanPeerInfo> _peers = new ObservableCollection<LanPeerInfo>();
    private readonly ObservableCollection<LanTransferRequest> _pendingRequests = new ObservableCollection<LanTransferRequest>();
    private readonly ObservableCollection<LanTransferSession> _activeTransfers = new ObservableCollection<LanTransferSession>();
    private readonly ObservableCollection<LanTransferRecord> _transferHistory = new ObservableCollection<LanTransferRecord>();
    private readonly ObservableCollection<SecretChatSession> _secretChatSessions = new ObservableCollection<SecretChatSession>();
    private readonly object _peerSync = new object();
    private readonly object _secretChatSync = new object();
    private readonly RSACryptoServiceProvider _secretChatRsa = new RSACryptoServiceProvider(2048);
    private readonly Timer _peerCleanupTimer;
    private static readonly Dictionary<string, int> sessionPeerPorts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> sessionPeerPublicKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private LanDiscoveryService _discoveryService;
    private LanTransferHostService _hostService;

    private bool _isEnabled;
    private string _displayName;
    private string _deviceId;
    private string _inboxPath;
    private string _statusText;
    private string _appVersion;

    public LanTransferService(DataPersistenceService dataPersistenceService)
    {
        _dataPersistenceService = dataPersistenceService ?? throw new ArgumentNullException(nameof(dataPersistenceService));
        _peerCleanupTimer = new Timer(_ => RefreshPeerStates(), null, Timeout.Infinite, Timeout.Infinite);
        LoadSettingsAndInitialize();
        LoadHistory();
        EnsureRunningState();
    }

    public ObservableCollection<LanPeerInfo> Peers => _peers;

    public ObservableCollection<LanTransferRequest> PendingRequests => _pendingRequests;

    public ObservableCollection<LanTransferSession> ActiveTransfers => _activeTransfers;

    public ObservableCollection<LanTransferRecord> TransferHistory => _transferHistory;

    public ObservableCollection<SecretChatSession> SecretChatSessions => _secretChatSessions;

    public bool IsEnabled
    {
        get => _isEnabled;
        private set => SetProperty(ref _isEnabled, value);
    }

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string DeviceId
    {
        get => _deviceId;
        private set => SetProperty(ref _deviceId, value);
    }

    public string InboxPath
    {
        get => _inboxPath;
        private set => SetProperty(ref _inboxPath, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string AppVersion
    {
        get => _appVersion;
        private set => SetProperty(ref _appVersion, value);
    }

    public int ListenPort => _hostService?.ListenPort ?? 0;

    public string MachineName => Environment.MachineName;

    public string LogDirectory => LanTransferLogger.GetDirectoryPath();

    public string HistoryFilePath => Path.Combine(_dataPersistenceService.GetDataFolderPath(), "lan_transfer_history.json");

    public int OnlinePeerCount => _peers.Count(peer => peer.IsOnline);

    public void Dispose()
    {
        _peerCleanupTimer.Dispose();
        StopServices();
    }

    public void ApplySettings(AppSettings settings)
    {
        if (settings == null)
        {
            return;
        }

        IsEnabled = settings.EnableLanTransfer;
        DisplayName = EnsureDisplayName(settings.LanTransferDisplayName);
        DeviceId = EnsureDeviceId(settings.LanTransferDeviceId);
        InboxPath = EnsureInboxPath(settings.LanTransferInboxPath);

        EnsureRunningState();
        OnPropertyChanged(nameof(ListenPort));
        OnPropertyChanged(nameof(OnlinePeerCount));
    }

    public async Task<LanPeerInfo> ConnectManualPeerAsync(string hostOrAddress)
    {
        if (string.IsNullOrWhiteSpace(hostOrAddress))
        {
            throw new InvalidOperationException("请输入 IP 或主机名。");
        }

        var addresses = new List<IPAddress>();
        if (IPAddress.TryParse(hostOrAddress, out var ipAddress))
        {
            addresses.Add(ipAddress);
        }
        else
        {
            addresses.AddRange((await Dns.GetHostAddressesAsync(hostOrAddress))
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Where(LanDiscoveryService.IsPrivateIpv4));
        }

        if (addresses.Count == 0)
        {
            throw new InvalidOperationException("未找到可用的局域网 IPv4 地址。");
        }

        Exception lastError = null;
        foreach (var address in addresses.Distinct())
        {
            for (var i = 0; i < LanTransferHostService.MaxPortProbeCount; i++)
            {
                var port = LanTransferHostService.DefaultPort + i;
                try
                {
                    var ack = await LanTransferHostService.ProbePeerAsync(address.ToString(), port, BuildHostConfiguration(), CancellationToken.None);
                    if (ack == null)
                    {
                        continue;
                    }

                    var peer = UpsertPeer(new LanDiscoveryAnnouncement
                    {
                        ProtocolVersion = ack.ProtocolVersion,
                        DeviceId = ack.DeviceId,
                        DisplayName = ack.DisplayName,
                        MachineName = ack.MachineName,
                        ListenPort = port,
                        AppVersion = ack.AppVersion,
                        Capabilities = ack.Capabilities,
                        SecretChatPublicKey = ack.SecretChatPublicKey,
                    }, address.ToString(), true);

                    peer.IsCompatible = ack.Compatible;
                    peer.StatusText = ack.Message;
                    return peer;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }
        }

        throw new InvalidOperationException("无法连接到目标 Packagemanager 实例。", lastError);
    }

    public async Task SendPathsAsync(LanPeerInfo peer, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default(CancellationToken))
    {
        if (peer == null)
        {
            throw new InvalidOperationException("请先选择一个在线用户。");
        }

        if (!peer.CanSend)
        {
            throw new InvalidOperationException("当前目标不可发送，请确认对方在线且协议兼容。");
        }

        var preparedItems = PrepareTransferEntries(paths);
        if (preparedItems.Count == 0)
        {
            throw new InvalidOperationException("没有可发送的文件或文件夹。");
        }

        var transferId = Guid.NewGuid().ToString("N");
        var totalBytes = preparedItems.Where(item => !item.IsDirectory).Sum(item => item.Length);
        var topLevelNames = preparedItems
            .Select(item => item.RelativePath.Split(new[] { '\\' }, 2)[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var session = new LanTransferSession
        {
            TransferId = transferId,
            Direction = "Send",
            PeerDisplayName = peer.DisplayLabel,
            Summary = string.Join("、", topLevelNames),
            StatusText = "正在发送",
            TotalBytes = totalBytes,
            BytesTransferred = 0,
            CanCancel = true,
        };

        await InvokeOnUiAsync(() => _activeTransfers.Add(session));

        NetworkStream activeStream = null;
        try
        {
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(peer.Address, peer.ListenPort);
                using (activeStream = client.GetStream())
                {
                    var hello = new LanHelloFrame
                    {
                        Type = "hello",
                        ProtocolVersion = LanTransferProtocol.ProtocolVersion,
                        DeviceId = DeviceId,
                        DisplayName = DisplayName,
                        MachineName = MachineName,
                        AppVersion = AppVersion,
                    };

                    await LanTransferWireProtocol.WriteFrameAsync(activeStream, hello, cancellationToken);
                    var helloAck = (await LanTransferWireProtocol.ReadFrameAsync(activeStream, cancellationToken))?.ToObject<LanHelloAckFrame>();
                    if ((helloAck == null) || !helloAck.Compatible)
                    {
                        throw new InvalidOperationException("对方版本不兼容，无法发送。");
                    }

                    var requestFrame = new LanTransferRequestFrame
                    {
                        Type = "transferRequest",
                        TransferId = transferId,
                        SenderDisplayName = DisplayName,
                        SenderMachineName = MachineName,
                        SenderAddress = GetLocalPrivateIpv4(),
                        SenderPort = ListenPort,
                        TotalBytes = totalBytes,
                        TopLevelNames = topLevelNames,
                        Items = preparedItems.Select(item => new LanTransferItem
                        {
                            RelativePath = item.RelativePath,
                            Name = item.Name,
                            IsDirectory = item.IsDirectory,
                            Length = item.Length,
                        }).ToList(),
                    };

                    await LanTransferWireProtocol.WriteFrameAsync(activeStream, requestFrame, cancellationToken);
                    var response = (await LanTransferWireProtocol.ReadFrameAsync(activeStream, cancellationToken))?.ToObject<LanTransferResponseFrame>();
                    if ((response == null) || !response.Accepted)
                    {
                        throw new InvalidOperationException(response?.Message ?? "对方拒绝接收。");
                    }

                    foreach (var item in preparedItems)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        await LanTransferWireProtocol.WriteFrameAsync(activeStream, new LanFileHeaderFrame
                        {
                            Type = "fileHeader",
                            RelativePath = item.RelativePath,
                            IsDirectory = item.IsDirectory,
                            Length = item.Length,
                        }, cancellationToken);

                        if (item.IsDirectory)
                        {
                            continue;
                        }

                        using (var fileStream = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                        {
                            var buffer = new byte[81920];
                            while (true)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var read = await fileStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                                if (read <= 0)
                                {
                                    break;
                                }

                                await activeStream.WriteAsync(buffer, 0, read, cancellationToken);
                                session.BytesTransferred += read;
                            }
                        }
                    }

                    await LanTransferWireProtocol.WriteFrameAsync(activeStream, new
                    {
                        Type = "complete",
                    }, cancellationToken);

                    session.StatusText = "发送完成";
                    session.CanCancel = false;
                    AddHistoryRecord(new LanTransferRecord
                    {
                        TransferId = transferId,
                        Direction = "Send",
                        PeerDisplayName = peer.DisplayLabel,
                        PeerAddress = peer.EndpointDisplay,
                        ItemCount = preparedItems.Count,
                        TotalBytes = totalBytes,
                        Status = "成功",
                        Summary = string.Join("、", topLevelNames),
                        TargetPath = response.SaveDirectory,
                        StartedAtUtc = DateTime.UtcNow,
                        CompletedAtUtc = DateTime.UtcNow,
                        Detail = "发送成功",
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (activeStream != null)
            {
                try
                {
                    await LanTransferWireProtocol.WriteFrameAsync(activeStream, new LanCancelFrame
                    {
                        Type = "cancel",
                        Message = "发送方取消",
                    }, CancellationToken.None);
                }
                catch
                {
                }
            }

            session.StatusText = "已取消";
            session.CanCancel = false;
            AddHistoryRecord(new LanTransferRecord
            {
                TransferId = transferId,
                Direction = "Send",
                PeerDisplayName = peer.DisplayLabel,
                PeerAddress = peer.EndpointDisplay,
                ItemCount = preparedItems.Count,
                TotalBytes = totalBytes,
                Status = "已取消",
                Summary = string.Join("、", topLevelNames),
                StartedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow,
                Detail = "发送已取消",
            });
            throw;
        }
        catch (Exception ex)
        {
            session.StatusText = "发送失败";
            session.CanCancel = false;
            AddHistoryRecord(new LanTransferRecord
            {
                TransferId = transferId,
                Direction = "Send",
                PeerDisplayName = peer.DisplayLabel,
                PeerAddress = peer.EndpointDisplay,
                ItemCount = preparedItems.Count,
                TotalBytes = totalBytes,
                Status = "失败",
                Summary = string.Join("、", topLevelNames),
                StartedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow,
                Detail = ex.Message,
            });
            LanTransferLogger.LogError(ex, $"发送文件失败：{peer.EndpointDisplay}");
            throw;
        }
        finally
        {
            session.CanCancel = false;
            await InvokeOnUiAsync(() => _activeTransfers.Remove(session));
        }
    }

    public void OpenInbox()
    {
        Directory.CreateDirectory(InboxPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = InboxPath,
            UseShellExecute = true,
        });
    }

    public void OpenLogs()
    {
        Directory.CreateDirectory(LogDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = LogDirectory,
            UseShellExecute = true,
        });
    }

    public void CancelTransfer(string transferId)
    {
        _hostService?.CancelIncomingTransfer(transferId);
    }

    public Task<SecretChatSession> RequestSecretChatAsync(LanPeerInfo peer, CancellationToken cancellationToken = default(CancellationToken))
    {
        if (peer == null)
        {
            throw new InvalidOperationException("请先选择一个在线同事。");
        }

        if (!peer.CanStartSecretChat)
        {
            throw new InvalidOperationException("当前同事不支持密语或不在线。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OpenSecretChatSession(peer));
    }

    public async Task SendSecretMessageAsync(SecretChatSession session, string text, CancellationToken cancellationToken = default(CancellationToken))
    {
        if (session == null || !session.CanSend)
        {
            throw new InvalidOperationException("密语会话不可发送，请确认截图保护已启用且会话未关闭。");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var message = new SecretChatMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            Direction = SecretChatMessageDirection.Outgoing,
            Text = text,
            State = SecretChatMessageState.Sending,
        };
        session.Messages.Add(message);

        try
        {
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(session.PeerAddress, GetPeerPort(session));
                using (var stream = client.GetStream())
                {
                    await LanTransferWireProtocol.WriteFrameAsync(stream, CreateHelloFrame(), cancellationToken);
                    var helloAck = (await LanTransferWireProtocol.ReadFrameAsync(stream, cancellationToken))?.ToObject<LanHelloAckFrame>();
                    if ((helloAck == null) || !helloAck.Compatible || !LanTransferProtocol.SupportsSecretChat(helloAck.Capabilities))
                    {
                        throw new InvalidOperationException("对方密语能力不可用。");
                    }

                    var protectedMessage = ProtectSecretText(text, GetPeerPublicKey(session));
                    await LanTransferWireProtocol.WriteFrameAsync(stream, new LanSecretMessageFrame
                    {
                        Type = "secretMessage",
                        SessionId = session.SessionId,
                        MessageId = message.MessageId,
                        SenderDeviceId = DeviceId,
                        SenderDisplayName = DisplayName,
                        SenderMachineName = MachineName,
                        SenderAddress = GetLocalPrivateIpv4(),
                        SenderPort = ListenPort,
                        CipherText = protectedMessage.CipherText,
                        EncryptedKey = protectedMessage.EncryptedKey,
                        Iv = protectedMessage.Iv,
                        Hmac = protectedMessage.Hmac,
                        SenderPublicKey = _secretChatRsa.ToXmlString(false),
                    }, cancellationToken);
                }
            }

            message.State = SecretChatMessageState.Sent;
            session.StatusText = "密语已发送，等待对方阅读";
        }
        catch
        {
            message.Text = string.Empty;
            message.State = SecretChatMessageState.Destroyed;
            throw;
        }
    }

    public async Task MarkSecretMessageReadAsync(SecretChatSession session, SecretChatMessage message)
    {
        if (session == null || message == null || message.Direction != SecretChatMessageDirection.Incoming || message.State != SecretChatMessageState.Unread)
        {
            return;
        }

        message.State = SecretChatMessageState.Read;
        message.ReadAtUtc = DateTime.UtcNow;
        DecrementSecretUnread(session);
        StartDestroyCountdown(session, message);
        await SendSecretReceiptAsync(session, message, "read", CancellationToken.None);
    }

    public void SetSecretChatWindowState(SecretChatSession session, bool isOpen, bool isActive)
    {
        if (session == null)
        {
            return;
        }

        session.IsWindowOpen = isOpen;
        session.IsWindowActive = isActive;
        if (isActive)
        {
            _ = MarkUnreadSecretMessagesReadAsync(session);
        }
    }

    public async Task MarkUnreadSecretMessagesReadAsync(SecretChatSession session)
    {
        if (session == null)
        {
            return;
        }

        var unread = session.Messages
            .Where(message => message.Direction == SecretChatMessageDirection.Incoming
                              && message.State == SecretChatMessageState.Unread)
            .ToList();
        foreach (var message in unread)
        {
            await MarkSecretMessageReadAsync(session, message);
        }
    }

    public void CloseSecretChatSession(SecretChatSession session)
    {
        if (session == null)
        {
            return;
        }

        SetSecretChatWindowState(session, false, false);
    }

    private void DestroySecretChatSession(SecretChatSession session)
    {
        if (session == null)
        {
            return;
        }

        session.IsOpen = false;
        foreach (var message in session.Messages.ToList())
        {
            DestroySecretMessage(session, message);
        }

        lock (_secretChatSync)
        {
            _secretChatSessions.Remove(session);
        }
    }

    private void LoadSettingsAndInitialize()
    {
        var settings = _dataPersistenceService.LoadSettings() ?? new AppSettings();
        var changed = false;

        if (string.IsNullOrWhiteSpace(settings.LanTransferDeviceId))
        {
            settings.LanTransferDeviceId = Guid.NewGuid().ToString("N");
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.LanTransferDisplayName))
        {
            settings.LanTransferDisplayName = EnsureDisplayName(null);
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.LanTransferInboxPath))
        {
            settings.LanTransferInboxPath = GetDefaultInboxPath();
            changed = true;
        }

        IsEnabled = settings.EnableLanTransfer;
        DisplayName = settings.LanTransferDisplayName;
        DeviceId = settings.LanTransferDeviceId;
        InboxPath = settings.LanTransferInboxPath;
        AppVersion = GetCurrentVersionText();
        StatusText = "文件传输服务未启动";

        if (changed)
        {
            _dataPersistenceService.SaveSettings(settings);
        }
    }

    private void EnsureRunningState()
    {
        if (!IsEnabled)
        {
            StopServices();
            StatusText = "文件传输已关闭";
            return;
        }

        if ((_hostService != null) && (_discoveryService != null))
        {
            StatusText = $"文件传输已启动，监听端口 {ListenPort}";
            OnPropertyChanged(nameof(ListenPort));
            return;
        }

        _hostService = new LanTransferHostService(BuildHostConfiguration, ApproveIncomingRequestAsync, ApproveIncomingSecretChatAsync, HandleSecretChatAccepted);
        _hostService.SessionStarted += session =>
        {
            InvokeOnUiAsync(() => _activeTransfers.Add(session)).GetAwaiter().GetResult();
        };
        _hostService.SessionCompleted += session =>
        {
            InvokeOnUiAsync(() => _activeTransfers.Remove(session)).GetAwaiter().GetResult();
        };
        _hostService.ReceiveRecorded += record => AddHistoryRecord(record);
        _hostService.SecretMessageReceived += frame => InvokeOnUiAsync(() => HandleIncomingSecretMessage(frame)).GetAwaiter().GetResult();
        _hostService.SecretReceiptReceived += frame => InvokeOnUiAsync(() => HandleIncomingSecretReceipt(frame)).GetAwaiter().GetResult();
        _hostService.Start();

        _discoveryService = new LanDiscoveryService(BuildLocalIdentity);
        _discoveryService.AnnouncementReceived += HandlePeerAnnouncement;
        _discoveryService.Start();

        _peerCleanupTimer.Change(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        StatusText = $"文件传输已启动，监听端口 {ListenPort}";
        OnPropertyChanged(nameof(ListenPort));
    }

    private void StopServices()
    {
        _peerCleanupTimer.Change(Timeout.Infinite, Timeout.Infinite);

        _discoveryService?.Dispose();
        _discoveryService = null;

        _hostService?.Dispose();
        _hostService = null;

        InvokeOnUiAsync(() =>
        {
            _activeTransfers.Clear();
            foreach (var peer in _peers)
            {
                peer.IsOnline = false;
                peer.StatusText = "离线";
            }
        }).GetAwaiter().GetResult();
    }

    private LanHostConfiguration BuildHostConfiguration()
    {
        return new LanHostConfiguration
        {
            DeviceId = DeviceId,
            DisplayName = DisplayName,
            MachineName = MachineName,
            AppVersion = AppVersion,
            InboxPath = InboxPath,
            Capabilities = LanTransferProtocol.CurrentCapabilities,
            SecretChatPublicKey = _secretChatRsa.ToXmlString(false),
        };
    }

    private LanLocalIdentity BuildLocalIdentity()
    {
        return new LanLocalIdentity
        {
            Enabled = IsEnabled,
            DeviceId = DeviceId,
            DisplayName = DisplayName,
            MachineName = MachineName,
            AppVersion = AppVersion,
            ListenPort = ListenPort,
            Capabilities = LanTransferProtocol.CurrentCapabilities,
            SecretChatPublicKey = _secretChatRsa.ToXmlString(false),
        };
    }

    private void HandlePeerAnnouncement(LanDiscoveryAnnouncement announcement, IPEndPoint remoteEndPoint)
    {
        var peer = UpsertPeer(announcement, remoteEndPoint.Address.ToString(), false);
        peer.StatusText = peer.IsCompatible ? "在线" : "版本不兼容";
        OnPropertyChanged(nameof(OnlinePeerCount));
    }

    private LanPeerInfo UpsertPeer(LanDiscoveryAnnouncement announcement, string address, bool isManual)
    {
        lock (_peerSync)
        {
            var peer = _peers.FirstOrDefault(item => string.Equals(item.DeviceId, announcement.DeviceId, StringComparison.OrdinalIgnoreCase))
                       ?? _peers.FirstOrDefault(item => string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase)
                                                       && (item.ListenPort == announcement.ListenPort));

            return InvokeOnUiAsync(() =>
            {
                peer ??= _peers.FirstOrDefault(item => string.Equals(item.DeviceId, announcement.DeviceId, StringComparison.OrdinalIgnoreCase))
                         ?? new LanPeerInfo();

                if (!_peers.Contains(peer))
                {
                    _peers.Add(peer);
                }

                peer.DeviceId = announcement.DeviceId;
                peer.DisplayName = announcement.DisplayName;
                peer.MachineName = announcement.MachineName;
                peer.Address = address;
                peer.ListenPort = announcement.ListenPort;
                peer.AppVersion = announcement.AppVersion;
                peer.IsCompatible = announcement.ProtocolVersion == LanTransferProtocol.ProtocolVersion;
                peer.SupportsSecretChat = LanTransferProtocol.SupportsSecretChat(announcement.Capabilities);
                peer.SecretChatPublicKey = announcement.SecretChatPublicKey;
                peer.IsOnline = true;
                peer.IsManual = isManual;
                peer.LastSeenUtc = DateTime.UtcNow;
                SyncPeerUnreadCount(peer);
                return peer;
            }).GetAwaiter().GetResult();
        }
    }

    private async Task<LanIncomingTransferDecision> ApproveIncomingRequestAsync(LanTransferRequest request)
    {
        request.SaveDirectory = BuildReceivePreviewPath(request);
        await InvokeOnUiAsync(() => _pendingRequests.Add(request));
        ToastService.ShowToast("收到文件传输", $"{request.SenderLabel} 请求发送 {request.ItemCount} 项，请确认是否接收。", "Info");

        try
        {
            var accepted = false;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dialog = new Views.LanTransferConfirmWindow(request)
                {
                    Owner = Application.Current?.MainWindow,
                };

                accepted = dialog.ShowDialog() == true;
            });

            if (!accepted)
            {
                request.StatusText = "已拒绝";
                AddHistoryRecord(new LanTransferRecord
                {
                    TransferId = request.TransferId,
                    Direction = "Receive",
                    PeerDisplayName = request.SenderLabel,
                    PeerAddress = request.SenderAddress,
                    ItemCount = request.ItemCount,
                    TotalBytes = request.TotalBytes,
                    Status = "已拒绝",
                    Summary = request.TopLevelSummary,
                    TargetPath = request.SaveDirectory,
                    StartedAtUtc = request.ReceivedAtUtc,
                    CompletedAtUtc = DateTime.UtcNow,
                    Detail = "接收方拒绝",
                });
                return LanIncomingTransferDecision.Reject("接收方拒绝");
            }

            request.StatusText = "已确认";
            return LanIncomingTransferDecision.Accept(InboxPath);
        }
        finally
        {
            await InvokeOnUiAsync(() => _pendingRequests.Remove(request));
        }
    }

    private Task<bool> ApproveIncomingSecretChatAsync(LanSecretChatSessionRequest request)
    {
        return Task.FromResult(true);
    }

    private void HandleSecretChatAccepted(LanSecretChatSessionRequest request)
    {
    }

    private SecretChatSession OpenSecretChatSession(LanPeerInfo peer)
    {
        var session = OpenSecretChatSession(
            BuildSecretSessionKey(peer?.DeviceId, peer?.Address, peer?.ListenPort ?? 0),
            null,
            peer?.DeviceId,
            peer?.DisplayLabel,
            peer?.Address,
            peer?.ListenPort ?? 0);
        SetPeerPublicKey(session, peer?.SecretChatPublicKey);
        return session;
    }

    private SecretChatSession OpenSecretChatSession(string sessionKey, string wireSessionId, string peerDeviceId, string peerDisplayName, string peerAddress, int peerPort)
    {
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            sessionKey = BuildSecretSessionKey(peerDeviceId, peerAddress, peerPort);
        }

        SecretChatSession session;
        lock (_secretChatSync)
        {
            session = _secretChatSessions.FirstOrDefault(item => string.Equals(item.SessionKey, sessionKey, StringComparison.OrdinalIgnoreCase));
            if (session == null && !string.IsNullOrWhiteSpace(wireSessionId))
            {
                session = _secretChatSessions.FirstOrDefault(item => string.Equals(item.SessionId, wireSessionId, StringComparison.OrdinalIgnoreCase));
            }

            if (session == null)
            {
                session = new SecretChatSession
                {
                    SessionId = string.IsNullOrWhiteSpace(wireSessionId) ? Guid.NewGuid().ToString("N") : wireSessionId,
                    SessionKey = sessionKey,
                    PeerDeviceId = peerDeviceId,
                    PeerDisplayName = string.IsNullOrWhiteSpace(peerDisplayName) ? "未知同事" : peerDisplayName,
                    PeerAddress = peerAddress,
                    StatusText = "密语会话已建立",
                };
                _secretChatSessions.Add(session);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(peerDeviceId))
                {
                    session.PeerDeviceId = peerDeviceId;
                }

                if (!string.IsNullOrWhiteSpace(peerDisplayName))
                {
                    session.PeerDisplayName = peerDisplayName;
                    session.RefreshPeerTitle();
                }

                if (!string.IsNullOrWhiteSpace(peerAddress))
                {
                    session.PeerAddress = peerAddress;
                    session.RefreshPeerTitle();
                }
            }

            SetPeerPort(session, peerPort);
        }

        return session;
    }

    private void HandleIncomingSecretMessage(LanSecretMessageFrame frame)
    {
        if (frame == null || string.IsNullOrWhiteSpace(frame.SessionId) || string.IsNullOrWhiteSpace(frame.MessageId))
        {
            return;
        }

        var peerDeviceId = string.IsNullOrWhiteSpace(frame.SenderDeviceId)
            ? FindPeerByEndpoint(frame.SenderAddress, frame.SenderPort)?.DeviceId
            : frame.SenderDeviceId;
        var sessionKey = BuildSecretSessionKey(peerDeviceId, frame.SenderAddress, frame.SenderPort);
        var peerLabel = string.IsNullOrWhiteSpace(frame.SenderMachineName) ? frame.SenderDisplayName : $"{frame.SenderDisplayName} ({frame.SenderMachineName})";
        var session = OpenSecretChatSession(
            sessionKey,
            frame.SessionId,
            peerDeviceId,
            peerLabel,
            frame.SenderAddress,
            frame.SenderPort);
        SetPeerPublicKey(session, frame.SenderPublicKey);

        if (session.Messages.Any(message => string.Equals(message.MessageId, frame.MessageId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var message = new SecretChatMessage
        {
            MessageId = frame.MessageId,
            WireSessionId = frame.SessionId,
            Direction = SecretChatMessageDirection.Incoming,
            Text = UnprotectSecretText(frame),
            State = SecretChatMessageState.Unread,
        };
        session.Messages.Add(message);
        IncrementSecretUnread(session);

        if (session.IsWindowActive)
        {
            _ = MarkSecretMessageReadAsync(session, message);
            session.StatusText = "收到新的密语，已自动标记已读";
        }
        else
        {
            session.StatusText = "收到新的密语，打开窗口后将自动已读";
            ToastService.ShowToast("收到密语", $"{session.PeerDisplayName} 发来新的密语", "Info");
        }
    }

    private void HandleIncomingSecretReceipt(LanSecretReceiptFrame frame)
    {
        if (frame == null || string.IsNullOrWhiteSpace(frame.SessionId) || string.IsNullOrWhiteSpace(frame.MessageId))
        {
            return;
        }

        var session = _secretChatSessions.FirstOrDefault(item => string.Equals(item.SessionId, frame.SessionId, StringComparison.OrdinalIgnoreCase));
        if (session == null && !string.IsNullOrWhiteSpace(frame.SenderDeviceId))
        {
            var sessionKey = BuildSecretSessionKey(frame.SenderDeviceId, frame.SenderAddress, frame.SenderPort);
            session = _secretChatSessions.FirstOrDefault(item => string.Equals(item.SessionKey, sessionKey, StringComparison.OrdinalIgnoreCase));
        }

        var message = session?.Messages.FirstOrDefault(item => string.Equals(item.MessageId, frame.MessageId, StringComparison.OrdinalIgnoreCase));
        if (message == null)
        {
            foreach (var candidateSession in _secretChatSessions)
            {
                message = candidateSession.Messages.FirstOrDefault(item => string.Equals(item.MessageId, frame.MessageId, StringComparison.OrdinalIgnoreCase));
                if (message != null)
                {
                    session = candidateSession;
                    break;
                }
            }
        }

        if (session == null || message == null)
        {
            return;
        }

        if (string.Equals(frame.Receipt, "destroy", StringComparison.OrdinalIgnoreCase))
        {
            DestroySecretMessage(session, message);
            return;
        }

        MarkOutgoingSecretMessageRead(session, message);
    }

    private async Task SendSecretReceiptAsync(SecretChatSession session, SecretChatMessage message, string receipt, CancellationToken cancellationToken)
    {
        if (session == null || message == null || string.IsNullOrWhiteSpace(message.MessageId))
        {
            return;
        }

        try
        {
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(session.PeerAddress, GetPeerPort(session));
                using (var stream = client.GetStream())
                {
                    await LanTransferWireProtocol.WriteFrameAsync(stream, CreateHelloFrame(), cancellationToken);
                    await LanTransferWireProtocol.ReadFrameAsync(stream, cancellationToken);
                    await LanTransferWireProtocol.WriteFrameAsync(stream, new LanSecretReceiptFrame
                    {
                        Type = string.Equals(receipt, "destroy", StringComparison.OrdinalIgnoreCase) ? "secretDestroy" : "secretReadReceipt",
                        SessionId = string.IsNullOrWhiteSpace(message.WireSessionId) ? session.SessionId : message.WireSessionId,
                        MessageId = message.MessageId,
                        Receipt = receipt,
                        SenderDeviceId = DeviceId,
                        SenderAddress = GetLocalPrivateIpv4(),
                        SenderPort = ListenPort,
                    }, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            LanTransferLogger.LogError(ex, $"密语回执发送失败：{SafeSecretSessionId(session.SessionId)}");
        }
    }

    private void StartDestroyCountdown(SecretChatSession session, SecretChatMessage message)
    {
        if (message == null || message.State == SecretChatMessageState.Destroyed || message.DestroyCountdownSeconds > 0)
        {
            return;
        }

        message.DestroyCountdownSeconds = 5;
        _ = Task.Run(async () =>
        {
            while (message.DestroyCountdownSeconds > 0 && message.State != SecretChatMessageState.Destroyed)
            {
                await Task.Delay(1000);
                await InvokeOnUiAsync(() => message.DestroyCountdownSeconds--);
            }

            await InvokeOnUiAsync(() => DestroySecretMessage(session, message));
            await SendSecretReceiptAsync(session, message, "destroy", CancellationToken.None);
        });
    }

    private void DestroySecretMessage(SecretChatSession session, SecretChatMessage message)
    {
        if (message == null || message.State == SecretChatMessageState.Destroyed)
        {
            return;
        }

        if (message.Direction == SecretChatMessageDirection.Incoming && message.State == SecretChatMessageState.Unread)
        {
            DecrementSecretUnread(session);
        }

        message.Text = string.Empty;
        message.DestroyCountdownSeconds = 0;
        message.State = SecretChatMessageState.Destroyed;
        session?.Messages.Remove(message);
    }

    private void MarkOutgoingSecretMessageRead(SecretChatSession session, SecretChatMessage message)
    {
        if (session == null || message == null || message.State == SecretChatMessageState.Destroyed)
        {
            return;
        }

        if (message.State != SecretChatMessageState.Read)
        {
            message.State = SecretChatMessageState.Read;
            message.ReadAtUtc = DateTime.UtcNow;
        }

        StartDestroyCountdown(session, message);
    }

    private void IncrementSecretUnread(SecretChatSession session)
    {
        if (session == null)
        {
            return;
        }

        session.UnreadCount++;
        SyncPeerUnreadCount(FindPeerForSession(session));
    }

    private void DecrementSecretUnread(SecretChatSession session)
    {
        if (session == null)
        {
            return;
        }

        session.UnreadCount = Math.Max(0, session.UnreadCount - 1);
        SyncPeerUnreadCount(FindPeerForSession(session));
    }

    private void SyncPeerUnreadCount(LanPeerInfo peer)
    {
        if (peer == null)
        {
            return;
        }

        peer.SecretUnreadCount = _secretChatSessions
            .Where(session => IsSessionForPeer(session, peer))
            .Sum(session => session.UnreadCount);
    }

    private LanPeerInfo FindPeerForSession(SecretChatSession session)
    {
        if (session == null)
        {
            return null;
        }

        return _peers.FirstOrDefault(peer => !string.IsNullOrWhiteSpace(session.PeerDeviceId)
                                            && string.Equals(peer.DeviceId, session.PeerDeviceId, StringComparison.OrdinalIgnoreCase))
               ?? FindPeerByEndpoint(session.PeerAddress, GetPeerPort(session));
    }

    private static bool IsSessionForPeer(SecretChatSession session, LanPeerInfo peer)
    {
        if (session == null || peer == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(session.PeerDeviceId)
            && !string.IsNullOrWhiteSpace(peer.DeviceId)
            && string.Equals(session.PeerDeviceId, peer.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(session.PeerAddress, peer.Address, StringComparison.OrdinalIgnoreCase)
               && (GetPeerPort(session) <= 0 || peer.ListenPort <= 0 || GetPeerPort(session) == peer.ListenPort);
    }

    private LanPeerInfo FindPeerByEndpoint(string address, int port)
    {
        return _peers.FirstOrDefault(peer => string.Equals(peer.Address, address, StringComparison.OrdinalIgnoreCase)
                                            && (port <= 0 || peer.ListenPort == port));
    }

    private static string BuildSecretSessionKey(string deviceId, string address, int port)
    {
        var normalizedAddress = (address ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            return $"device:{deviceId.Trim().ToLowerInvariant()}|endpoint:{normalizedAddress}:{port}";
        }

        return $"endpoint:{normalizedAddress}:{port}";
    }

    private List<LanPreparedTransferEntry> PrepareTransferEntries(IReadOnlyCollection<string> paths)
    {
        var entries = new List<LanPreparedTransferEntry>();
        if (paths == null)
        {
            return entries;
        }

        foreach (var rawPath in paths)
        {
            var path = rawPath?.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (File.Exists(path))
            {
                var fileInfo = new FileInfo(path);
                entries.Add(new LanPreparedTransferEntry
                {
                    SourcePath = fileInfo.FullName,
                    RelativePath = fileInfo.Name,
                    Name = fileInfo.Name,
                    Length = fileInfo.Length,
                    IsDirectory = false,
                });
                continue;
            }

            if (!Directory.Exists(path))
            {
                continue;
            }

            var rootDirectory = new DirectoryInfo(path);
            entries.Add(new LanPreparedTransferEntry
            {
                SourcePath = rootDirectory.FullName,
                RelativePath = rootDirectory.Name,
                Name = rootDirectory.Name,
                IsDirectory = true,
                Length = 0,
            });

            foreach (var directory in Directory.GetDirectories(rootDirectory.FullName, "*", SearchOption.AllDirectories))
            {
                var relative = rootDirectory.Name + "\\" + GetRelativePath(rootDirectory.FullName, directory);
                entries.Add(new LanPreparedTransferEntry
                {
                    SourcePath = directory,
                    RelativePath = relative,
                    Name = Path.GetFileName(directory),
                    IsDirectory = true,
                    Length = 0,
                });
            }

            foreach (var file in Directory.GetFiles(rootDirectory.FullName, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                var relative = rootDirectory.Name + "\\" + GetRelativePath(rootDirectory.FullName, file);
                entries.Add(new LanPreparedTransferEntry
                {
                    SourcePath = info.FullName,
                    RelativePath = relative,
                    Name = info.Name,
                    IsDirectory = false,
                    Length = info.Length,
                });
            }
        }

        var duplicate = entries
            .GroupBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
        {
            throw new InvalidOperationException($"存在重名冲突：{duplicate.Key}");
        }

        return entries
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryFilePath))
            {
                return;
            }

            var json = File.ReadAllText(HistoryFilePath);
            var records = JsonConvert.DeserializeObject<List<LanTransferRecord>>(json) ?? new List<LanTransferRecord>();
            foreach (var record in records.Take(200))
            {
                _transferHistory.Add(record);
            }
        }
        catch (Exception ex)
        {
            LanTransferLogger.LogError(ex, "加载局域网传输历史失败");
        }
    }

    private void AddHistoryRecord(LanTransferRecord record)
    {
        if (record == null)
        {
            return;
        }

        InvokeOnUiAsync(() =>
        {
            _transferHistory.Insert(0, record);
            while (_transferHistory.Count > 200)
            {
                _transferHistory.RemoveAt(_transferHistory.Count - 1);
            }

            SaveHistory();
        }).GetAwaiter().GetResult();
    }

    private void SaveHistory()
    {
        try
        {
            var directory = Path.GetDirectoryName(HistoryFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(HistoryFilePath, JsonConvert.SerializeObject(_transferHistory.ToList(), Formatting.Indented));
        }
        catch (Exception ex)
        {
            LanTransferLogger.LogError(ex, "保存局域网传输历史失败");
        }
    }

    private void RefreshPeerStates()
    {
        InvokeOnUiAsync(() =>
        {
            foreach (var peer in _peers)
            {
                var isOnline = (DateTime.UtcNow - peer.LastSeenUtc) <= TimeSpan.FromSeconds(10);
                peer.IsOnline = isOnline;
                peer.StatusText = !peer.IsCompatible ? "版本不兼容" : (isOnline ? "在线" : "离线");
            }

            OnPropertyChanged(nameof(OnlinePeerCount));
        }).GetAwaiter().GetResult();
    }

    private string BuildReceivePreviewPath(LanTransferRequest request)
    {
        var topLevel = (request.TopLevelNames != null) && (request.TopLevelNames.Count == 1)
            ? request.TopLevelNames[0]
            : $"{Math.Max(1, request.TopLevelNames?.Count ?? 0)}项";

        var sender = SanitizePathPart(request.SenderDisplayName ?? "Unknown");
        var summary = SanitizePathPart(topLevel);
        return Path.Combine(InboxPath, $"{DateTime.Now:yyyyMMdd_HHmmss}_{sender}_{summary}");
    }

    private static string SanitizePathPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Transfer";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (sanitized.Length > 32)
        {
            sanitized = sanitized.Substring(0, 32);
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "Transfer" : sanitized;
    }

    private static string EnsureDisplayName(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? $"{Environment.UserName}@{Environment.MachineName}"
            : value.Trim();
    }

    private static string EnsureDeviceId(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value.Trim();
    }

    private static string EnsureInboxPath(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? GetDefaultInboxPath() : value.Trim();
    }

    private static string GetDefaultInboxPath()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
        {
            return Path.Combine(downloads, "PackageManager 收件箱");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PackageManager 收件箱");
    }

    private static string GetCurrentVersionText()
    {
        try
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        }
        catch
        {
            return "0.0.0.0";
        }
    }

    private static string GetLocalPrivateIpv4()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Where(LanDiscoveryService.IsPrivateIpv4)
                .Select(address => address.ToString())
                .FirstOrDefault() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetRelativePath(string rootPath, string fullPath)
    {
        var root = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var rootUri = new Uri(root);
        var fullUri = new Uri(fullPath);
        var relative = Uri.UnescapeDataString(rootUri.MakeRelativeUri(fullUri).ToString());
        return relative.Replace('/', '\\');
    }

    private LanHelloFrame CreateHelloFrame()
    {
        return new LanHelloFrame
        {
            Type = "hello",
            ProtocolVersion = LanTransferProtocol.ProtocolVersion,
            DeviceId = DeviceId,
            DisplayName = DisplayName,
            MachineName = MachineName,
            AppVersion = AppVersion,
            Capabilities = LanTransferProtocol.CurrentCapabilities,
            SecretChatPublicKey = _secretChatRsa.ToXmlString(false),
        };
    }

    private SecretProtectedPayload ProtectSecretText(string text, string peerPublicKey)
    {
        if (string.IsNullOrWhiteSpace(peerPublicKey))
        {
            throw new InvalidOperationException("缺少对方密语公钥，无法发送。");
        }

        using (var aes = Aes.Create())
        using (var hmac = new HMACSHA256())
        using (var rsa = new RSACryptoServiceProvider(2048))
        {
            aes.GenerateKey();
            aes.GenerateIV();
            hmac.Key = aes.Key;
            rsa.FromXmlString(peerPublicKey);

            byte[] cipherBytes;
            using (var encryptor = aes.CreateEncryptor())
            {
                var plainBytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
                cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                Array.Clear(plainBytes, 0, plainBytes.Length);
            }

            var macBytes = hmac.ComputeHash(Combine(aes.IV, cipherBytes));
            var encryptedKey = rsa.Encrypt(aes.Key, false);
            var payload = new SecretProtectedPayload
            {
                CipherText = Convert.ToBase64String(cipherBytes),
                EncryptedKey = Convert.ToBase64String(encryptedKey),
                Iv = Convert.ToBase64String(aes.IV),
                Hmac = Convert.ToBase64String(macBytes),
            };

            Array.Clear(aes.Key, 0, aes.Key.Length);
            Array.Clear(cipherBytes, 0, cipherBytes.Length);
            Array.Clear(macBytes, 0, macBytes.Length);
            Array.Clear(encryptedKey, 0, encryptedKey.Length);
            return payload;
        }
    }

    private string UnprotectSecretText(LanSecretMessageFrame frame)
    {
        var encryptedKey = Convert.FromBase64String(frame.EncryptedKey ?? string.Empty);
        var iv = Convert.FromBase64String(frame.Iv ?? string.Empty);
        var cipherBytes = Convert.FromBase64String(frame.CipherText ?? string.Empty);
        var expectedMac = Convert.FromBase64String(frame.Hmac ?? string.Empty);

        var aesKey = _secretChatRsa.Decrypt(encryptedKey, false);
        try
        {
            using (var hmac = new HMACSHA256(aesKey))
            {
                var actualMac = hmac.ComputeHash(Combine(iv, cipherBytes));
                if (!FixedTimeEquals(actualMac, expectedMac))
                {
                    throw new InvalidOperationException("密语认证失败，消息已丢弃。");
                }
            }

            using (var aes = Aes.Create())
            using (var decryptor = aes.CreateDecryptor(aesKey, iv))
            {
                var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                var text = Encoding.UTF8.GetString(plainBytes);
                Array.Clear(plainBytes, 0, plainBytes.Length);
                return text;
            }
        }
        finally
        {
            Array.Clear(aesKey, 0, aesKey.Length);
            Array.Clear(encryptedKey, 0, encryptedKey.Length);
            Array.Clear(iv, 0, iv.Length);
            Array.Clear(cipherBytes, 0, cipherBytes.Length);
            Array.Clear(expectedMac, 0, expectedMac.Length);
        }
    }

    private static int GetPeerPort(SecretChatSession session)
    {
        return session == null || !sessionPeerPorts.TryGetValue(session.SessionId, out var port)
            ? LanTransferHostService.DefaultPort
            : port;
    }

    private static void SetPeerPort(SecretChatSession session, int port)
    {
        if (session != null && port > 0)
        {
            sessionPeerPorts[session.SessionId] = port;
        }
    }

    private static string GetPeerPublicKey(SecretChatSession session)
    {
        return session != null && sessionPeerPublicKeys.TryGetValue(session.SessionId, out var publicKey)
            ? publicKey
            : null;
    }

    private static void SetPeerPublicKey(SecretChatSession session, string publicKey)
    {
        if (session != null && !string.IsNullOrWhiteSpace(publicKey))
        {
            sessionPeerPublicKeys[session.SessionId] = publicKey;
        }
    }

    private static byte[] Combine(byte[] first, byte[] second)
    {
        var combined = new byte[(first?.Length ?? 0) + (second?.Length ?? 0)];
        if (first != null)
        {
            Buffer.BlockCopy(first, 0, combined, 0, first.Length);
        }

        if (second != null)
        {
            Buffer.BlockCopy(second, 0, combined, first?.Length ?? 0, second.Length);
        }

        return combined;
    }

    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < left.Length; i++)
        {
            diff |= left[i] ^ right[i];
        }

        return diff == 0;
    }

    private static string SafeSecretSessionId(string sessionId)
    {
        return string.IsNullOrWhiteSpace(sessionId)
            ? "<empty>"
            : sessionId.Substring(0, Math.Min(8, sessionId.Length));
    }

    private static Task InvokeOnUiAsync(Action action)
    {
        if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Application.Current.Dispatcher.InvokeAsync(action).Task;
    }

    private static Task<T> InvokeOnUiAsync<T>(Func<T> action)
    {
        if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.CheckAccess())
        {
            return Task.FromResult(action());
        }

        return Application.Current.Dispatcher.InvokeAsync(action).Task;
    }
}

internal sealed class LanPreparedTransferEntry
{
    public string SourcePath { get; set; }

    public string RelativePath { get; set; }

    public string Name { get; set; }

    public bool IsDirectory { get; set; }

    public long Length { get; set; }
}

internal sealed class SecretProtectedPayload
{
    public string CipherText { get; set; }

    public string EncryptedKey { get; set; }

    public string Iv { get; set; }

    public string Hmac { get; set; }
}
