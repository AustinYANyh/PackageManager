using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;

namespace PackageManager.Services;

public sealed class LanTransferService : LanTransferBindableBase, IDisposable
{
    private readonly DataPersistenceService _dataPersistenceService;
    private readonly ObservableCollection<LanPeerInfo> _peers = new ObservableCollection<LanPeerInfo>();
    private readonly ObservableCollection<LanTransferRequest> _pendingRequests = new ObservableCollection<LanTransferRequest>();
    private readonly ObservableCollection<LanTransferSession> _activeTransfers = new ObservableCollection<LanTransferSession>();
    private readonly ObservableCollection<LanTransferRecord> _transferHistory = new ObservableCollection<LanTransferRecord>();
    private readonly object _peerSync = new object();
    private readonly Timer _peerCleanupTimer;

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
        StatusText = "局域网传输服务未启动";

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
            StatusText = "局域网传输已关闭";
            return;
        }

        if ((_hostService != null) && (_discoveryService != null))
        {
            StatusText = $"局域网传输已启动，监听端口 {ListenPort}";
            OnPropertyChanged(nameof(ListenPort));
            return;
        }

        _hostService = new LanTransferHostService(BuildHostConfiguration, ApproveIncomingRequestAsync);
        _hostService.SessionStarted += session =>
        {
            InvokeOnUiAsync(() => _activeTransfers.Add(session)).GetAwaiter().GetResult();
        };
        _hostService.SessionCompleted += session =>
        {
            InvokeOnUiAsync(() => _activeTransfers.Remove(session)).GetAwaiter().GetResult();
        };
        _hostService.ReceiveRecorded += record => AddHistoryRecord(record);
        _hostService.Start();

        _discoveryService = new LanDiscoveryService(BuildLocalIdentity);
        _discoveryService.AnnouncementReceived += HandlePeerAnnouncement;
        _discoveryService.Start();

        _peerCleanupTimer.Change(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        StatusText = $"局域网传输已启动，监听端口 {ListenPort}";
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
                peer.IsOnline = true;
                peer.IsManual = isManual;
                peer.LastSeenUtc = DateTime.UtcNow;
                return peer;
            }).GetAwaiter().GetResult();
        }
    }

    private async Task<LanIncomingTransferDecision> ApproveIncomingRequestAsync(LanTransferRequest request)
    {
        request.SaveDirectory = BuildReceivePreviewPath(request);
        await InvokeOnUiAsync(() => _pendingRequests.Add(request));

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
