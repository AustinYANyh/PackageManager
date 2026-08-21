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

/// <summary>
/// 局域网文件传输服务，整合设备发现、传输管理和密语聊天功能。
/// </summary>
public sealed class LanTransferService : LanTransferBindableBase, IDisposable
{
    private readonly DataPersistenceService _dataPersistenceService;
    private readonly ObservableCollection<LanPeerInfo> _peers = new ObservableCollection<LanPeerInfo>();
    private readonly ObservableCollection<LanTransferRequest> _pendingRequests = new ObservableCollection<LanTransferRequest>();
    private readonly ObservableCollection<LanTransferSession> _activeTransfers = new ObservableCollection<LanTransferSession>();
    private readonly ObservableCollection<LanTransferRecord> _transferHistory = new ObservableCollection<LanTransferRecord>();
    private readonly ObservableCollection<SecretChatSession> _secretChatSessions = new ObservableCollection<SecretChatSession>();
    private readonly Dictionary<SecretChatSession, SecretChatSession> _selfTestPairs = new Dictionary<SecretChatSession, SecretChatSession>();
    private readonly SecretMailboxClient _secretMailbox = new SecretMailboxClient();
    private readonly SecretContactStore _secretContactStore = new SecretContactStore(new DataPersistenceService());
    private readonly List<SecretContact> _secretContacts = new List<SecretContact>();
    private readonly object _secretContactSync = new object();
    private readonly Timer _secretSaveTimer;
    private readonly Timer _secretMailboxPollTimer;
    private int _secretMailboxPollSeconds;
    private int _secretSavePending;
    private readonly object _pullLock = new object();
    private Task _pullInFlight;
    private readonly object _peerSync = new object();
    private readonly object _secretChatSync = new object();
    private readonly RSACryptoServiceProvider _secretChatRsa = LoadOrCreateSecretRsa();
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
    private bool _silentOverwrite;
    private bool _autoAccept;

    /// <summary>
    /// 初始化 <see cref="LanTransferService"/> 的新实例，加载设置并启动服务。
    /// </summary>
    /// <param name="dataPersistenceService">数据持久化服务，用于读写设置和历史记录。</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataPersistenceService"/> 为 null。</exception>
    public LanTransferService(DataPersistenceService dataPersistenceService)
    {
        _dataPersistenceService = dataPersistenceService ?? throw new ArgumentNullException(nameof(dataPersistenceService));
        _peerCleanupTimer = new Timer(_ => RefreshPeerStates(), null, Timeout.Infinite, Timeout.Infinite);
        _secretSaveTimer = new Timer(_ => SaveSecretSnapshot(), null, Timeout.Infinite, Timeout.Infinite);
        _secretMailboxPollTimer = new Timer(_ => RunSecretMailboxPoll(), null, Timeout.Infinite, Timeout.Infinite);
        LoadSettingsAndInitialize();
        LoadHistory();
        EnsureRunningState();
    }

    /// <summary>已发现的局域网对端列表。</summary>
    public ObservableCollection<LanPeerInfo> Peers => _peers;

    /// <summary>等待审批的传入传输请求列表。</summary>
    public ObservableCollection<LanTransferRequest> PendingRequests => _pendingRequests;

    /// <summary>正在进行中的传输会话列表。</summary>
    public ObservableCollection<LanTransferSession> ActiveTransfers => _activeTransfers;

    /// <summary>传输历史记录列表。</summary>
    public ObservableCollection<LanTransferRecord> TransferHistory => _transferHistory;

    /// <summary>密语聊天会话列表。</summary>
    public ObservableCollection<SecretChatSession> SecretChatSessions => _secretChatSessions;

    /// <summary>是否启用局域网传输功能。</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        private set => SetProperty(ref _isEnabled, value);
    }

    /// <summary>本机显示名称。</summary>
    public string DisplayName    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    /// <summary>本机设备唯一标识。</summary>
    public string DeviceId    {
        get => _deviceId;
        private set => SetProperty(ref _deviceId, value);
    }

    /// <summary>收件箱目录路径。</summary>
    public string InboxPath    {
        get => _inboxPath;
        private set => SetProperty(ref _inboxPath, value);
    }

    /// <summary>接收文件时是否静默覆盖同名文件或目录。</summary>
    public bool SilentOverwrite
    {
        get => _silentOverwrite;
        private set => SetProperty(ref _silentOverwrite, value);
    }

    /// <summary>接收文件时是否自动接受传入请求。</summary>
    public bool AutoAccept
    {
        get => _autoAccept;
        private set => SetProperty(ref _autoAccept, value);
    }

    /// <summary>服务状态显示文本。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>应用程序版本号。</summary>
    public string AppVersion
    {
        get => _appVersion;
        private set => SetProperty(ref _appVersion, value);
    }

    /// <summary>当前文件传输监听端口号。</summary>
    public int ListenPort => _hostService?.ListenPort ?? 0;

    /// <summary>本机机器名称。</summary>
    public string MachineName => Environment.MachineName;

    /// <summary>日志目录路径。</summary>
    public string LogDirectory => LanTransferLogger.GetDirectoryPath();

    /// <summary>传输历史记录文件路径。</summary>
    public string HistoryFilePath => Path.Combine(_dataPersistenceService.GetDataFolderPath(), "lan_transfer_history.json");

    /// <summary>当前在线对端数量。</summary>
    public int OnlinePeerCount => _peers.Count(peer => peer.IsOnline);

    /// <summary>
    /// 释放服务资源，停止设备发现和传输监听。
    /// </summary>
    public void Dispose()
    {
        _peerCleanupTimer.Dispose();
        StopServices();
    }

    /// <summary>
    /// 应用新设置并更新运行状态。
    /// </summary>
    /// <param name="settings">应用程序设置。</param>
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
        SilentOverwrite = settings.LanTransferSilentOverwrite;
        AutoAccept = settings.LanTransferAutoAccept;

        EnsureRunningState();
        OnPropertyChanged(nameof(ListenPort));
        OnPropertyChanged(nameof(OnlinePeerCount));
    }

    /// <summary>
    /// 手动连接到指定 IP 或主机名的对端设备。
    /// </summary>
    /// <param name="hostOrAddress">目标 IP 地址或主机名。</param>
    /// <returns>连接成功的对端信息。</returns>
    /// <exception cref="InvalidOperationException">地址无效或无法连接。</exception>
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

    /// <summary>
    /// 向指定对端发送文件和目录。
    /// </summary>
    /// <param name="peer">目标对端。</param>
    /// <param name="paths">要发送的文件或目录路径集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">对端不可发送、无文件可传或对方拒绝。</exception>
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

                    var completeAck = await TryReadTransferCompleteAckAsync(activeStream, helloAck, cancellationToken, TimeSpan.FromSeconds(30));
                    if ((completeAck != null) && !completeAck.Success)
                    {
                        throw new InvalidOperationException(completeAck.Message ?? "对方接收落盘失败。");
                    }

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

    private static async Task<LanTransferCompleteAckFrame> TryReadTransferCompleteAckAsync(NetworkStream stream, LanHelloAckFrame helloAck, CancellationToken cancellationToken, TimeSpan timeout)
    {
        if (!LanTransferProtocol.SupportsTransferCompleteAck(helloAck?.Capabilities))
        {
            return null;
        }

        var oldReadTimeout = stream.ReadTimeout;
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutCts.CancelAfter(timeout);
        try
        {
            stream.ReadTimeout = (int)Math.Min(int.MaxValue, Math.Max(1000, timeout.TotalMilliseconds));
            var frame = await LanTransferWireProtocol.ReadFrameAsync(stream, timeoutCts.Token);
            if (!string.Equals(frame?.Value<string>("Type"), "transferCompleteAck", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return frame.ToObject<LanTransferCompleteAckFrame>();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            try
            {
                stream.ReadTimeout = oldReadTimeout;
            }
            catch
            {
            }
        }
        }
    }

    /// <summary>
    /// 打开收件箱目录，若不存在则创建。
    /// </summary>
    public void OpenInbox()
    {
        Directory.CreateDirectory(InboxPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = InboxPath,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// 打开日志目录，若不存在则创建。
    /// </summary>
    public void OpenLogs()
    {
        Directory.CreateDirectory(LogDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = LogDirectory,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// 取消指定的传输任务。
    /// </summary>
    /// <param name="transferId">传输标识。</param>
    public void CancelTransfer(string transferId)
    {
        _hostService?.CancelIncomingTransfer(transferId);
    }

    /// <summary>
    /// 向指定对端请求密语聊天会话。
    /// </summary>
    /// <param name="peer">目标对端。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>密语聊天会话。</returns>
    /// <exception cref="InvalidOperationException">对端不支持密语或不在线。</exception>
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

    /// <summary>
    /// 在指定密语会话中发送加密消息。
    /// </summary>
    /// <param name="session">密语会话。</param>
    /// <param name="text">消息文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">会话不可发送或对方密语能力不可用。</exception>
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
            SenderDeviceId = DeviceId,
            Text = text,
            State = SecretChatMessageState.Sending,
        };
        session.Messages.Add(message);

        if (session.IsSelfTest)
        {
            await DeliverSelfTestMessageAsync(session, message);
            message.State = SecretChatMessageState.Sent;
            session.StatusText = "密语已发送（自测线路），进入对方视线后开始计时";
            return;
        }

        var livePeer = FindPeerForSession(session);
        if (livePeer == null || !livePeer.IsOnline || string.IsNullOrWhiteSpace(session.PeerAddress))
        {
            await SendSecretMessageViaMailboxAsync(session, message, text);
            return;
        }

        try
        {
            using (var client = new TcpClient())
            {
                // 连接限时 2 秒：内网握手正常为个位毫秒，仅在对方刚下线（IP 不可达/丢包）时挂住；
                // 超时后立即降级信箱投递，等待无意义
                var connectTask = client.ConnectAsync(session.PeerAddress, GetPeerPort(session));
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2));
                if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                {
                    throw new TimeoutException("连接对方超时。");
                }

                await connectTask;
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
            ScheduleSecretSave();
        }
        catch
        {
            // 直连失败（刚下线盲区/超时/链路断开）：降级改投信箱，消息不再直接销毁。
            // 对端若已收到直连消息，信箱副本会按 MessageId 幂等去重，不会重复显示。
            LanTransferLogger.LogWarning($"密语直连失败，降级信箱投递：{SafeSecretSessionId(session.SessionId)}");
            await SendSecretMessageViaMailboxAsync(session, message, text);
        }
    }

    /// <summary>
    /// 对方离线时的发送路径：用其公钥加密后投递到 FTP 密语信箱，消息进入「已投递」状态。
    /// </summary>
    private async Task SendSecretMessageViaMailboxAsync(SecretChatSession session, SecretChatMessage message, string text)
    {
        var publicKey = await ResolvePeerPublicKeyAsync(session);
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            message.Text = string.Empty;
            message.State = SecretChatMessageState.Destroyed;
            throw new InvalidOperationException("对方离线且公钥不可用，无法投递密语信箱。");
        }

        var protectedMessage = ProtectSecretText(text, publicKey);
        var posted = await _secretMailbox.PostEnvelopeAsync(session.PeerDeviceId, new SecretMailboxEnvelope
        {
            Kind = "message",
            MessageId = message.MessageId,
            SessionId = session.SessionId,
            FromDeviceId = DeviceId,
            FromDisplayName = DisplayName,
            FromMachineName = MachineName,
            SenderPublicKey = _secretChatRsa.ToXmlString(false),
            PostedAtUtc = DateTime.UtcNow,
            CipherText = protectedMessage.CipherText,
            EncryptedKey = protectedMessage.EncryptedKey,
            Iv = protectedMessage.Iv,
            Hmac = protectedMessage.Hmac,
        });

        if (!posted)
        {
            message.Text = string.Empty;
            message.State = SecretChatMessageState.Destroyed;
            throw new InvalidOperationException("密语信箱投递失败，请稍后重试。");
        }

        message.State = SecretChatMessageState.Posted;
        session.StatusText = "对方离线，密语已投递信箱，对方上线后送达";
        UpsertSecretContact(session.PeerDeviceId, session.PeerDisplayName, null, null);
        ScheduleSecretSave();
    }

    /// <summary>
    /// 解析对方公钥：会话缓存（最近直连握手）→ 信箱公钥目录（对方最近启动发布）→ 本地联系人缓存。
    /// </summary>
    private async Task<string> ResolvePeerPublicKeyAsync(SecretChatSession session)
    {
        var cached = GetPeerPublicKey(session);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        if (!string.IsNullOrWhiteSpace(session?.PeerDeviceId))
        {
            var published = await _secretMailbox.GetPublicKeyAsync(session.PeerDeviceId);
            if (!string.IsNullOrWhiteSpace(published))
            {
                return published;
            }
        }

        lock (_secretContactSync)
        {
            var contact = _secretContacts.FirstOrDefault(item => string.Equals(item.DeviceId, session?.PeerDeviceId, StringComparison.OrdinalIgnoreCase));
            return contact?.PublicKeyXml;
        }
    }

    /// <summary>
    /// 将指定的密语消息标记为已读，并启动自毁倒计时。由窗口视口判定触发，表示消息真正进入视线。
    /// </summary>
    /// <param name="session">密语会话。</param>
    /// <param name="message">要标记已读的消息。</param>
    public async Task MarkSecretMessageReadAsync(SecretChatSession session, SecretChatMessage message)
    {
        if (session == null || message == null || message.Direction != SecretChatMessageDirection.Incoming || message.State != SecretChatMessageState.Unread)
        {
            return;
        }

        message.DestroyTotalSeconds = Math.Max(1, session.DestroyAfterReadSeconds);
        message.State = SecretChatMessageState.Read;
        message.ReadAtUtc = DateTime.UtcNow;
        DecrementSecretUnread(session);
        StartDestroyCountdown(session, message);
        ScheduleSecretSave();
        await SendSecretReceiptAsync(session, message, "read", CancellationToken.None);
    }

    /// <summary>
    /// 批量将进入视口的未读密语消息标记为已读。
    /// </summary>
    /// <param name="session">密语会话。</param>
    /// <param name="messages">窗口判定为已被看见的未读消息集合。</param>
    public async Task MarkSecretMessagesReadAsync(SecretChatSession session, IEnumerable<SecretChatMessage> messages)
    {
        if (session == null || messages == null)
        {
            return;
        }

        foreach (var message in messages
            .Where(message => message != null
                              && message.Direction == SecretChatMessageDirection.Incoming
                              && message.State == SecretChatMessageState.Unread)
            .ToList())
        {
            await MarkSecretMessageReadAsync(session, message);
        }
    }

    /// <summary>
    /// 立即销毁会话中所有已读（处于倒计时或已读状态）的密语消息，并通知对端。用于失焦与关窗即焚。
    /// </summary>
    /// <param name="session">密语会话。</param>
    public async Task DestroyReadSecretMessagesAsync(SecretChatSession session)
    {
        if (session == null)
        {
            return;
        }

        var read = session.Messages
            .Where(message => message.State == SecretChatMessageState.Read)
            .ToList();
        foreach (var message in read)
        {
            DestroySecretMessage(session, message);
            await SendSecretReceiptAsync(session, message, "destroy", CancellationToken.None);
        }
    }

    /// <summary>
    /// 设置密语聊天窗口的状态。失焦即焚已读消息；未读消息保留至真正阅读。
    /// </summary>
    /// <param name="session">密语会话。</param>
    /// <param name="isOpen">窗口是否打开。</param>
    /// <param name="isActive">窗口是否处于活动状态。</param>
    public void SetSecretChatWindowState(SecretChatSession session, bool isOpen, bool isActive)
    {
        if (session == null)
        {
            return;
        }

        session.IsWindowOpen = isOpen;
        session.IsWindowActive = isActive;
        if (!isActive)
        {
            _ = DestroyReadSecretMessagesAsync(session);
        }
    }

    /// <summary>
    /// 关闭密语聊天窗口并更新会话状态。
    /// </summary>
    /// <param name="session">密语会话。</param>
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
            _selfTestPairs.Remove(session);
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
        SilentOverwrite = settings.LanTransferSilentOverwrite;
        AutoAccept = settings.LanTransferAutoAccept;
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

        _ = InitializeSecretChatAsync();

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
            SilentOverwrite = SilentOverwrite,
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
        if (peer.IsCompatible && !string.IsNullOrWhiteSpace(peer.DeviceId))
        {
            UpsertSecretContact(peer.DeviceId, peer.DisplayName, peer.MachineName, peer.SecretChatPublicKey);
        }

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
        request.SaveDirectory = InboxPath;
        if (AutoAccept)
        {
            ToastService.ShowToast("自动接收文件", $"{request.SenderLabel} 发送 {request.ItemCount} 项，已自动接收。", "Info");
            request.StatusText = "已自动接收";
            return LanIncomingTransferDecision.Accept(InboxPath, SilentOverwrite);
        }

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
            return LanIncomingTransferDecision.Accept(InboxPath, SilentOverwrite);
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
                    DestroyAfterReadSeconds = GetSecretDestroySeconds(),
                };
                _secretChatSessions.Add(session);
            }
            else
            {
                session.DestroyAfterReadSeconds = GetSecretDestroySeconds();
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
            SenderDeviceId = frame.SenderDeviceId,
            Direction = SecretChatMessageDirection.Incoming,
            Text = UnprotectSecretText(frame),
            State = SecretChatMessageState.Unread,
        };
        session.Messages.Add(message);
        IncrementSecretUnread(session);
        UpsertSecretContact(peerDeviceId, session.PeerDisplayName, frame.SenderMachineName, frame.SenderPublicKey);
        ScheduleSecretSave();

        if (session.IsWindowActive)
        {
            session.StatusText = "收到新的密语，进入视线后开始计时";
        }
        else
        {
            session.StatusText = "收到新的密语，打开窗口阅读";
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

    /// <summary>
    /// 启动密语自测：在进程内创建一对互相配对的模拟会话（本机 与 影子同事），消息与回执直接路由到配对会话，不走真实网络。用于单机验证收发、已读判定与焚毁链路。
    /// </summary>
    /// <returns>二元组：本机会话与影子会话。</returns>
    public Task<SecretChatSession[]> StartSecretSelfTestAsync()
    {
        var seed = Guid.NewGuid().ToString("N");
        var self = new SecretChatSession
        {
            SessionId = "selftest-self-" + seed,
            SessionKey = "selftest-self-" + seed,
            PeerDisplayName = "自测·影子同事",
            PeerAddress = "127.0.0.1",
            StatusText = "密语自测已就绪",
            DestroyAfterReadSeconds = GetSecretDestroySeconds(),
            IsSelfTest = true,
        };
        var shadow = new SecretChatSession
        {
            SessionId = "selftest-shadow-" + seed,
            SessionKey = "selftest-shadow-" + seed,
            PeerDisplayName = "自测·本机",
            PeerAddress = "127.0.0.1",
            StatusText = "密语自测已就绪",
            DestroyAfterReadSeconds = GetSecretDestroySeconds(),
            IsSelfTest = true,
        };

        lock (_secretChatSync)
        {
            _secretChatSessions.Add(self);
            _secretChatSessions.Add(shadow);
            _selfTestPairs[self] = shadow;
            _selfTestPairs[shadow] = self;
        }

        return Task.FromResult(new[] { self, shadow });
    }

    private SecretChatSession GetSelfTestCounterpart(SecretChatSession session)
    {
        lock (_secretChatSync)
        {
            return session != null && _selfTestPairs.TryGetValue(session, out var counterpart) ? counterpart : null;
        }
    }

    private async Task DeliverSelfTestMessageAsync(SecretChatSession sender, SecretChatMessage outgoing)
    {
        var counterpart = GetSelfTestCounterpart(sender);
        if (counterpart == null)
        {
            return;
        }

        var incoming = new SecretChatMessage
        {
            MessageId = outgoing.MessageId,
            WireSessionId = sender.SessionId,
            Direction = SecretChatMessageDirection.Incoming,
            Text = outgoing.Text,
            State = SecretChatMessageState.Unread,
            DestroyTotalSeconds = Math.Max(1, counterpart.DestroyAfterReadSeconds),
        };
        await InvokeOnUiAsync(() =>
        {
            counterpart.Messages.Add(incoming);
            IncrementSecretUnread(counterpart);
            counterpart.StatusText = counterpart.IsWindowActive
                ? "收到新的密语（自测），进入视线后开始计时"
                : "收到新的密语（自测），打开窗口阅读";
        });
    }

    private async Task DeliverSelfTestReceiptAsync(SecretChatSession sender, SecretChatMessage message, string receipt)
    {
        var counterpart = GetSelfTestCounterpart(sender);
        if (counterpart == null)
        {
            return;
        }

        await InvokeOnUiAsync(() =>
        {
            var outgoing = counterpart.Messages.FirstOrDefault(item => string.Equals(item.MessageId, message.MessageId, StringComparison.OrdinalIgnoreCase));
            if (outgoing == null)
            {
                return;
            }

            if (string.Equals(receipt, "destroy", StringComparison.OrdinalIgnoreCase))
            {
                DestroySecretMessage(counterpart, outgoing);
                return;
            }

            MarkOutgoingSecretMessageRead(counterpart, outgoing);
        });
    }

    private async Task SendSecretReceiptAsync(SecretChatSession session, SecretChatMessage message, string receipt, CancellationToken cancellationToken)
    {
        if (session == null || message == null || string.IsNullOrWhiteSpace(message.MessageId))
        {
            return;
        }

        if (session.IsSelfTest)
        {
            await DeliverSelfTestReceiptAsync(session, message, receipt);
            return;
        }

        // 直连可达走 TCP；不可达或地址未知时改投对方信箱（对方上线后处理回执）
        if (string.IsNullOrWhiteSpace(session.PeerAddress))
        {
            await PostReceiptViaMailboxAsync(session, message, receipt);
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
            await PostReceiptViaMailboxAsync(session, message, receipt);
        }
    }

    /// <summary>
    /// 将回执以信封形式投递到原发送方的信箱（直连不可达时的兜底路径）。
    /// 回执文件名按种类分离，避免 read/destroy/delivered 相互覆盖。
    /// </summary>
    private async Task PostReceiptViaMailboxAsync(SecretChatSession session, SecretChatMessage message, string receipt)
    {
        var targetDevice = message?.SenderDeviceId;
        if (string.IsNullOrWhiteSpace(targetDevice) || string.Equals(targetDevice, DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await _secretMailbox.PostEnvelopeAsync(targetDevice, new SecretMailboxEnvelope
        {
            Kind = "receipt",
            MessageId = message.MessageId,
            FileName = Uri.EscapeDataString(message.MessageId) + "." + receipt + ".sec",
            SessionId = string.IsNullOrWhiteSpace(message.WireSessionId) ? session?.SessionId : message.WireSessionId,
            FromDeviceId = DeviceId,
            Receipt = receipt,
            PostedAtUtc = DateTime.UtcNow,
            CountdownSeconds = string.Equals(receipt, "read", StringComparison.OrdinalIgnoreCase) && message.DestroyTotalSeconds > 0
                ? message.DestroyTotalSeconds
                : null,
        });
    }

    /// <summary>
    /// 拉取本机密语信箱并处理信封（消息入会话、回执更新状态）。并发调用共享同一次在途拉取，
    /// 后到者等待其完成而非直接返回——页面/窗口触发的拉取不再空手而归。
    /// </summary>
    public Task PullSecretMailboxAsync()
    {
        lock (_pullLock)
        {
            if (_pullInFlight == null || _pullInFlight.IsCompleted)
            {
                _pullInFlight = PullSecretMailboxCoreAsync();
            }

            return _pullInFlight;
        }
    }

    private async Task PullSecretMailboxCoreAsync()
    {
        try
        {
            var envelopes = await _secretMailbox.PullEnvelopesAsync(DeviceId);
            foreach (var envelope in envelopes)
            {
                var local = envelope;
                await InvokeOnUiAsync(() =>
                {
                    if (string.Equals(local.Kind, "receipt", StringComparison.OrdinalIgnoreCase))
                    {
                        HandleMailboxReceipt(local);
                    }
                    else
                    {
                        HandleMailboxMessage(local);
                    }
                });

                // 消息信封处理完毕后，向原发送方回投 delivered 回执（对方 📦 升级为已拉取）
                if (string.Equals(local.Kind, "message", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(local.FromDeviceId)
                    && !string.Equals(local.FromDeviceId, DeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    await PostReceiptViaMailboxAsync(null, new SecretChatMessage
                    {
                        MessageId = local.MessageId,
                        WireSessionId = local.SessionId,
                        SenderDeviceId = local.FromDeviceId,
                    }, "delivered");
                }
            }
        }
        catch (Exception ex)
        {
            LanTransferLogger.LogError(ex, "密语信箱处理失败");
        }
    }

    private void HandleMailboxMessage(SecretMailboxEnvelope envelope)
    {
        if (envelope == null || string.IsNullOrWhiteSpace(envelope.MessageId) || string.IsNullOrWhiteSpace(envelope.FromDeviceId))
        {
            return;
        }

        var peerLabel = string.IsNullOrWhiteSpace(envelope.FromMachineName)
            ? envelope.FromDisplayName
            : $"{envelope.FromDisplayName} ({envelope.FromMachineName})";
        var session = OpenSecretChatSession(
            BuildSecretSessionKey(envelope.FromDeviceId, null, 0),
            envelope.SessionId,
            envelope.FromDeviceId,
            peerLabel,
            null,
            0);
        if (!string.IsNullOrWhiteSpace(envelope.SenderPublicKey))
        {
            SetPeerPublicKey(session, envelope.SenderPublicKey);
        }

        if (session.Messages.Any(message => string.Equals(message.MessageId, envelope.MessageId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        string text;
        try
        {
            text = UnprotectSecretText(new LanSecretMessageFrame
            {
                CipherText = envelope.CipherText,
                EncryptedKey = envelope.EncryptedKey,
                Iv = envelope.Iv,
                Hmac = envelope.Hmac,
            });
        }
        catch (Exception ex)
        {
            LanTransferLogger.LogError(ex, "密语信箱信封解密失败，已丢弃");
            return;
        }

        var mailboxMessage = new SecretChatMessage
        {
            MessageId = envelope.MessageId,
            WireSessionId = envelope.SessionId,
            SenderDeviceId = envelope.FromDeviceId,
            Direction = SecretChatMessageDirection.Incoming,
            Text = text,
            State = SecretChatMessageState.Unread,
            DestroyTotalSeconds = Math.Max(1, session.DestroyAfterReadSeconds),
        };
        session.Messages.Add(mailboxMessage);
        IncrementSecretUnread(session);
        UpsertSecretContact(envelope.FromDeviceId, session.PeerDisplayName, envelope.FromMachineName, envelope.SenderPublicKey);
        session.StatusText = session.IsWindowActive
            ? "收到离线密语（信箱），进入视线后开始计时"
            : "收到离线密语，打开窗口阅读";
        if (!session.IsWindowActive)
        {
            ToastService.ShowToast("收到密语", $"{session.PeerDisplayName} 的离线密语已从信箱送达", "Info");
        }

        ScheduleSecretSave();
    }

    private void HandleMailboxReceipt(SecretMailboxEnvelope envelope)
    {
        if (envelope == null || string.IsNullOrWhiteSpace(envelope.MessageId))
        {
            return;
        }

        SecretChatSession owner = null;
        SecretChatMessage target = null;
        lock (_secretChatSync)
        {
            foreach (var session in _secretChatSessions)
            {
                target = session.Messages.FirstOrDefault(message => string.Equals(message.MessageId, envelope.MessageId, StringComparison.OrdinalIgnoreCase));
                if (target != null)
                {
                    owner = session;
                    break;
                }
            }
        }

        if (owner == null || target == null)
        {
            return;
        }

        if (string.Equals(envelope.Receipt, "destroy", StringComparison.OrdinalIgnoreCase))
        {
            DestroySecretMessage(owner, target);
            return;
        }

        if (string.Equals(envelope.Receipt, "delivered", StringComparison.OrdinalIgnoreCase))
        {
            if (target.State == SecretChatMessageState.Posted)
            {
                target.State = SecretChatMessageState.Sent;
            }

            target.MailboxPulled = true;
            ScheduleSecretSave();
            return;
        }

        MarkOutgoingSecretMessageReadFromMailbox(owner, target, envelope);
    }

    /// <summary>
    /// 处理信箱送达的已读回执：按回执中的阅读时间与对方自毁时长计算剩余倒计时，与对方侧同步焚毁。
    /// </summary>
    private void MarkOutgoingSecretMessageReadFromMailbox(SecretChatSession session, SecretChatMessage message, SecretMailboxEnvelope envelope)
    {
        if (session == null || message == null || message.State == SecretChatMessageState.Destroyed)
        {
            return;
        }

        var theirTotal = envelope.CountdownSeconds.HasValue && envelope.CountdownSeconds.Value > 0
            ? envelope.CountdownSeconds.Value
            : Math.Max(1, session.DestroyAfterReadSeconds > 0 ? session.DestroyAfterReadSeconds : 5);
        var readAtUtc = envelope.PostedAtUtc;

        if (message.State != SecretChatMessageState.Read)
        {
            message.State = SecretChatMessageState.Read;
            message.ReadAtUtc = readAtUtc;
            message.DestroyTotalSeconds = theirTotal;
        }

        if (message.DestroyCountdownSeconds > 0)
        {
            // 已在倒计时（更早的回执已处理）
            return;
        }

        var remaining = (int)Math.Floor(theirTotal - (DateTime.UtcNow - readAtUtc).TotalSeconds);
        if (remaining <= 0)
        {
            // 对方倒计时已走完，本地立即焚毁
            DestroySecretMessage(session, message);
            return;
        }

        StartDestroyCountdown(session, message, remaining);
    }

    /// <summary>
    /// 密语异步初始化：恢复本地会话与联系人，发布公钥到信箱目录，并拉取待收信封。
    /// </summary>
    private async Task InitializeSecretChatAsync()
    {
        try
        {
            foreach (var contact in _secretContactStore.LoadContacts())
            {
                if (contact != null && !string.IsNullOrWhiteSpace(contact.DeviceId))
                {
                    _secretContacts.Add(contact);
                }
            }
        }
        catch
        {
        }

        RestoreSecretSessions();
        // 拉取提前到目录建设与公钥发布之前：信箱列取不依赖目录存在（550 按空箱处理），
        // 离线消息的可见时间不再被重型前置操作拖延
        await PullSecretMailboxAsync();
        await _secretMailbox.EnsureDirectoriesAsync();
        await _secretMailbox.PublishPublicKeyAsync(DeviceId, _secretChatRsa.ToXmlString(false));
        StartSecretMailboxPolling();
    }

    /// <summary>
    /// 启动信箱后台探测：按设置频率周期检查信箱（空箱仅一次目录列表，无下载）；有密语窗口打开或存在未读时加速到 20 秒。设置为 0 时不启动。
    /// </summary>
    private void StartSecretMailboxPolling()
    {
        _secretMailboxPollSeconds = LoadSecretMailboxPollSeconds();
        if (_secretMailboxPollSeconds <= 0)
        {
            return;
        }

        try
        {
            _secretMailboxPollTimer.Change(ComputeMailboxPollDelay(), Timeout.InfiniteTimeSpan);
        }
        catch
        {
            // 计时器已释放时忽略
        }
    }

    /// <summary>
    /// 读取信箱检查频率设置：仅接受 0（关闭）/30/60/120，非法值回退为 60。
    /// </summary>
    /// <returns>检查频率（秒）。</returns>
    private int LoadSecretMailboxPollSeconds()
    {
        try
        {
            var value = _dataPersistenceService.LoadSettings()?.LanTransferSecretMailboxPollSeconds ?? 60;
            return value == 0 || value == 30 || value == 60 || value == 120 ? value : 60;
        }
        catch
        {
            return 60;
        }
    }

    /// <summary>
    /// 计算下一次探测延迟：有密语窗口打开或任意未读时 20 秒（加速触达回执与后续消息），否则取设置频率。
    /// </summary>
    /// <returns>下次探测延迟；关闭时为无限。</returns>
    private TimeSpan ComputeMailboxPollDelay()
    {
        if (_secretMailboxPollSeconds <= 0)
        {
            return Timeout.InfiniteTimeSpan;
        }

        bool urgent;
        lock (_secretChatSync)
        {
            urgent = _secretChatSessions.Any(session => session.IsWindowOpen || session.UnreadCount > 0);
        }

        return TimeSpan.FromSeconds(urgent ? 20 : _secretMailboxPollSeconds);
    }

    private async void RunSecretMailboxPoll()
    {
        try
        {
            await PullSecretMailboxAsync();
        }
        catch
        {
        }
        finally
        {
            try
            {
                _secretMailboxPollTimer.Change(ComputeMailboxPollDelay(), Timeout.InfiniteTimeSpan);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 从本地加密快照恢复密语会话：未销毁消息按原状态恢复，已读未焚完的继续倒计时。
    /// </summary>
    private void RestoreSecretSessions()
    {
        foreach (var snapshot in _secretContactStore.LoadSessions())
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.SessionId))
            {
                continue;
            }

            try
            {
                var session = new SecretChatSession
                {
                    SessionId = snapshot.SessionId,
                    SessionKey = snapshot.SessionKey,
                    PeerDeviceId = snapshot.PeerDeviceId,
                    PeerDisplayName = string.IsNullOrWhiteSpace(snapshot.PeerDisplayName) ? "未知同事" : snapshot.PeerDisplayName,
                    PeerAddress = snapshot.PeerAddress,
                    StatusText = "会话已恢复",
                    DestroyAfterReadSeconds = GetSecretDestroySeconds(),
                };
                foreach (var item in snapshot.Messages)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.MessageId))
                    {
                        continue;
                    }

                    var message = new SecretChatMessage
                    {
                        MessageId = item.MessageId,
                        WireSessionId = item.WireSessionId,
                        SenderDeviceId = item.SenderDeviceId,
                        Direction = Enum.TryParse(item.Direction, out SecretChatMessageDirection direction) ? direction : SecretChatMessageDirection.Incoming,
                        Text = item.Text,
                        CreatedAtUtc = item.CreatedAtUtc == default ? DateTime.UtcNow : item.CreatedAtUtc,
                        ReadAtUtc = item.ReadAtUtc,
                        DestroyTotalSeconds = item.DestroyTotalSeconds > 0 ? item.DestroyTotalSeconds : 5,
                        MailboxPulled = item.MailboxPulled,
                    };
                    if (Enum.TryParse(item.State, out SecretChatMessageState state))
                    {
                        message.State = state;
                    }

                    session.Messages.Add(message);
                    if (message.Direction == SecretChatMessageDirection.Incoming && message.State == SecretChatMessageState.Unread)
                    {
                        session.UnreadCount++;
                    }
                }

                lock (_secretChatSync)
                {
                    _secretChatSessions.Add(session);
                }

                if (!string.IsNullOrWhiteSpace(snapshot.PeerPublicKeyXml))
                {
                    SetPeerPublicKey(session, snapshot.PeerPublicKeyXml);
                }

                SyncPeerUnreadCount(FindPeerForSession(session));
                foreach (var message in session.Messages.Where(item => item.State == SecretChatMessageState.Read).ToList())
                {
                    StartDestroyCountdown(session, message);
                }
            }
            catch (Exception ex)
            {
                LanTransferLogger.LogError(ex, "密语会话恢复失败");
            }
        }
    }

    /// <summary>
    /// 更新或新增密语联系人记录（设备号、名称、公钥缓存），随后统一随快照落盘。
    /// </summary>
    private void UpsertSecretContact(string deviceId, string displayName, string machineName, string publicKeyXml)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        lock (_secretContactSync)
        {
            var contact = _secretContacts.FirstOrDefault(item => string.Equals(item.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (contact == null)
            {
                contact = new SecretContact { DeviceId = deviceId };
                _secretContacts.Add(contact);
            }

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                contact.DisplayName = displayName;
            }

            if (!string.IsNullOrWhiteSpace(machineName))
            {
                contact.MachineName = machineName;
            }

            if (!string.IsNullOrWhiteSpace(publicKeyXml))
            {
                contact.PublicKeyXml = publicKeyXml;
            }

            contact.LastSeenUtc = DateTime.UtcNow;
        }

        ScheduleSecretSave();
    }

    /// <summary>
    /// 获取离线联系人（当前不在线的已通信联系人），供列表展示。
    /// </summary>
    /// <returns>离线联系人列表。</returns>
    public List<SecretContact> GetOfflineSecretContacts()
    {
        lock (_secretContactSync)
        {
            return _secretContacts
                .Where(contact => !_peers.Any(peer => peer.IsOnline && string.Equals(peer.DeviceId, contact.DeviceId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
    }

    /// <summary>
    /// 获取指定设备的密语未读总数（含离线联系人的会话）。
    /// </summary>
    /// <param name="deviceId">设备标识。</param>
    /// <returns>未读消息数。</returns>
    public int GetSecretUnreadCountForDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return 0;
        }

        lock (_secretChatSync)
        {
            return _secretChatSessions
                .Where(session => string.Equals(session.PeerDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                .Sum(session => session.UnreadCount);
        }
    }

    /// <summary>
    /// 与联系人发起密语会话：在线走直连会话，离线建立信箱模式会话（发送时投递信箱）。
    /// </summary>
    /// <param name="contact">目标联系人。</param>
    /// <returns>密语会话。</returns>
    public Task<SecretChatSession> RequestSecretChatWithContactAsync(SecretContact contact)
    {
        if (contact == null || string.IsNullOrWhiteSpace(contact.DeviceId))
        {
            throw new InvalidOperationException("联系人不可用。");
        }

        var peer = _peers.FirstOrDefault(item => string.Equals(item.DeviceId, contact.DeviceId, StringComparison.OrdinalIgnoreCase));
        if (peer != null && peer.IsOnline)
        {
            return RequestSecretChatAsync(peer);
        }

        var session = OpenSecretChatSession(
            BuildSecretSessionKey(contact.DeviceId, null, 0),
            null,
            contact.DeviceId,
            contact.DisplayLabel,
            null,
            0);
        if (!string.IsNullOrWhiteSpace(contact.PublicKeyXml))
        {
            SetPeerPublicKey(session, contact.PublicKeyXml);
        }

        return Task.FromResult(session);
    }

    /// <summary>
    /// 立即同步落盘密语联系人与会话快照，供应用退出前调用，防止防抖窗口内的最后变更丢失。
    /// </summary>
    public void FlushSecretData()
    {
        try
        {
            _secretSaveTimer?.Dispose();
        }
        catch
        {
        }

        SaveSecretSnapshot();
    }

    /// <summary>
    /// 安排一次防抖的密语快照落盘（2 秒内多次变更合并为一次）。
    /// 已有挂起的保存时不重置倒计时，避免高频广播把保存无限推迟（饿死）。
    /// </summary>
    private void ScheduleSecretSave()
    {
        if (Interlocked.Exchange(ref _secretSavePending, 1) == 1)
        {
            return;
        }

        try
        {
            _secretSaveTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
        catch
        {
            Interlocked.Exchange(ref _secretSavePending, 0);
        }
    }

    /// <summary>
    /// 将未销毁的密语会话与联系人加密落盘：消息内容整体 DPAPI 保护，已销毁消息只留空占位。
    /// </summary>
    private void SaveSecretSnapshot()
    {
        Interlocked.Exchange(ref _secretSavePending, 0);
        try
        {
            List<SecretSessionSnapshot> snapshot;
            lock (_secretChatSync)
            {
                snapshot = _secretChatSessions
                    .Where(session => !session.IsSelfTest)
                    .Select(session => new SecretSessionSnapshot
                    {
                        SessionId = session.SessionId,
                        SessionKey = session.SessionKey,
                        PeerDeviceId = session.PeerDeviceId,
                        PeerDisplayName = session.PeerDisplayName,
                        PeerAddress = session.PeerAddress,
                        PeerPublicKeyXml = GetPeerPublicKey(session),
                        IsSelfTest = false,
                        Messages = session.Messages.Select(message => new SecretMessageSnapshot
                        {
                            MessageId = message.MessageId,
                            WireSessionId = message.WireSessionId,
                            SenderDeviceId = message.SenderDeviceId,
                            Direction = message.Direction.ToString(),
                            State = message.State.ToString(),
                            Text = message.State == SecretChatMessageState.Destroyed ? string.Empty : message.Text,
                            CreatedAtUtc = message.CreatedAtUtc,
                            ReadAtUtc = message.ReadAtUtc,
                            DestroyTotalSeconds = message.DestroyTotalSeconds,
                            MailboxPulled = message.MailboxPulled,
                        }).ToList(),
                    })
                    .ToList();
            }

            _secretContactStore.SaveSessions(snapshot);
            lock (_secretContactSync)
            {
                _secretContactStore.SaveContacts(_secretContacts.ToList());
            }
        }
        catch (Exception ex)
        {
            LanTransferLogger.LogError(ex, "密语快照保存失败");
        }
    }

    private void StartDestroyCountdown(SecretChatSession session, SecretChatMessage message, int? remainingSeconds = null)
    {
        if (message == null || message.State == SecretChatMessageState.Destroyed || message.DestroyCountdownSeconds > 0)
        {
            return;
        }

        var total = Math.Max(1, session?.DestroyAfterReadSeconds > 0 ? session.DestroyAfterReadSeconds : 5);
        var remaining = remainingSeconds.HasValue
            ? Math.Min(total, Math.Max(1, remainingSeconds.Value))
            : total;
        message.DestroyTotalSeconds = total;
        message.DestroyCountdownSeconds = remaining;
        _ = Task.Run(async () =>
        {
            while (message.DestroyCountdownSeconds > 0 && message.State != SecretChatMessageState.Destroyed)
            {
                await Task.Delay(1000);
                await InvokeOnUiAsync(() => message.DestroyCountdownSeconds--);
            }

            if (message.State == SecretChatMessageState.Destroyed)
            {
                return;
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
        ScheduleSecretSave();
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

    /// <summary>
    /// 读取密语自毁时长设置：仅接受 5/10/30 秒，非法值回退为 5。
    /// </summary>
    /// <returns>自毁时长（秒）。</returns>
    private int GetSecretDestroySeconds()
    {
        try
        {
            var value = _dataPersistenceService.LoadSettings()?.LanTransferSecretDestroySeconds ?? 5;
            return value == 5 || value == 10 || value == 30 ? value : 5;
        }
        catch
        {
            return 5;
        }
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
        // 有设备标识时按设备收敛：同一同事在线直连与离线信箱共用一个会话
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            return "device:" + deviceId.Trim().ToLowerInvariant();
        }

        var normalizedAddress = (address ?? string.Empty).Trim().ToLowerInvariant();
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

    /// <summary>
    /// 加载或创建密语 RSA 密钥对：密钥对 DPAPI 加密落盘，跨重启保持稳定，
    /// 保证历史信箱信封与已发布公钥始终可解可验；仅在文件缺失或损坏时重新生成。
    /// </summary>
    /// <returns>RSA 密钥对。</returns>
    private static RSACryptoServiceProvider LoadOrCreateSecretRsa()
    {
        try
        {
            var keyPath = Path.Combine(
                new DataPersistenceService().GetDataFolderPath(),
                "secret-chat",
                "rsa-key.dat");
            if (File.Exists(keyPath))
            {
                var xml = CredentialProtectionService.Unprotect(File.ReadAllText(keyPath));
                if (!string.IsNullOrWhiteSpace(xml))
                {
                    var rsa = new RSACryptoServiceProvider();
                    rsa.FromXmlString(xml);
                    return rsa;
                }
            }
        }
        catch (Exception ex)
        {
            LanTransferLogger.LogError(ex, "加载密语 RSA 密钥失败，将重新生成");
        }

        var fresh = new RSACryptoServiceProvider(2048);
        try
        {
            var folder = Path.Combine(new DataPersistenceService().GetDataFolderPath(), "secret-chat");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "rsa-key.dat"), CredentialProtectionService.Protect(fresh.ToXmlString(true)));
        }
        catch (Exception ex)
        {
            // 落盘失败不阻断当次会话，仅下次启动会重新生成
            LanTransferLogger.LogError(ex, "保存密语 RSA 密钥失败");
        }

        return fresh;
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

/// <summary>
/// 已准备的传输条目，包含源路径和相对路径信息。
/// </summary>
internal sealed class LanPreparedTransferEntry
{
    /// <summary>源文件或目录的完整路径。</summary>
    public string SourcePath { get; set; }

    /// <summary>在传输包中的相对路径。</summary>
    public string RelativePath { get; set; }

    /// <summary>文件或目录名称。</summary>
    public string Name { get; set; }

    /// <summary>是否为目录。</summary>
    public bool IsDirectory { get; set; }

    /// <summary>文件字节长度，目录时为 0。</summary>
    public long Length { get; set; }
}

/// <summary>
/// 加密保护后的密语消息载荷。
/// </summary>
internal sealed class SecretProtectedPayload
{
    /// <summary>AES 加密后的密文（Base64）。</summary>
    public string CipherText { get; set; }

    /// <summary>RSA 加密后的 AES 密钥（Base64）。</summary>
    public string EncryptedKey { get; set; }

    /// <summary>AES 初始化向量（Base64）。</summary>
    public string Iv { get; set; }

    /// <summary>HMAC-SHA256 消息认证码（Base64）。</summary>
    public string Hmac { get; set; }
}
