using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PackageManager.Services;

internal sealed class LanTransferHostService : IDisposable
{
    internal const int DefaultPort = 48931;
    internal const int MaxPortProbeCount = 12;

    private readonly Func<LanHostConfiguration> _configurationProvider;
    private readonly Func<LanTransferRequest, Task<LanIncomingTransferDecision>> _requestApprovalAsync;
    private readonly Func<LanSecretChatSessionRequest, Task<bool>> _secretChatApprovalAsync;
    private readonly Action<LanSecretChatSessionRequest> _secretChatAccepted;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _incomingTransferCancels = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();

    private TcpListener _listener;
    private Task _acceptLoopTask;

    public LanTransferHostService(
        Func<LanHostConfiguration> configurationProvider,
        Func<LanTransferRequest, Task<LanIncomingTransferDecision>> requestApprovalAsync,
        Func<LanSecretChatSessionRequest, Task<bool>> secretChatApprovalAsync = null,
        Action<LanSecretChatSessionRequest> secretChatAccepted = null)
    {
        _configurationProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
        _requestApprovalAsync = requestApprovalAsync ?? throw new ArgumentNullException(nameof(requestApprovalAsync));
        _secretChatApprovalAsync = secretChatApprovalAsync;
        _secretChatAccepted = secretChatAccepted;
    }

    public int ListenPort { get; private set; }

    public event Action<LanTransferSession> SessionStarted;

    public event Action<LanTransferSession> SessionCompleted;

    public event Action<LanTransferRecord> ReceiveRecorded;

    public event Action<LanSecretMessageFrame> SecretMessageReceived;

    public event Action<LanSecretReceiptFrame> SecretReceiptReceived;

    public void Start()
    {
        if (_listener != null)
        {
            return;
        }

        Exception lastError = null;
        for (var i = 0; i < MaxPortProbeCount; i++)
        {
            var port = DefaultPort + i;
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                _listener = listener;
                ListenPort = port;
                _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
                LanTransferLogger.LogInfo($"文件传输监听已启动，端口 {ListenPort}");
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException("文件传输监听启动失败", lastError);
    }

    public void CancelIncomingTransfer(string transferId)
    {
        if (string.IsNullOrWhiteSpace(transferId))
        {
            return;
        }

        if (_incomingTransferCancels.TryGetValue(transferId, out var cts))
        {
            cts.Cancel();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch
        {
        }

        foreach (var pair in _incomingTransferCancels)
        {
            pair.Value.Cancel();
            pair.Value.Dispose();
        }

        _incomingTransferCancels.Clear();
    }

    public static async Task<LanHelloAckFrame> ProbePeerAsync(string hostOrAddress, int port, LanHostConfiguration localConfiguration, CancellationToken cancellationToken)
    {
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(hostOrAddress, port);
            using (var stream = client.GetStream())
            {
                var hello = new LanHelloFrame
                {
                    Type = "hello",
                    ProtocolVersion = LanTransferProtocol.ProtocolVersion,
                    DeviceId = localConfiguration?.DeviceId,
                    DisplayName = localConfiguration?.DisplayName,
                    MachineName = localConfiguration?.MachineName,
                    AppVersion = localConfiguration?.AppVersion,
                    Capabilities = localConfiguration?.Capabilities,
                    SecretChatPublicKey = localConfiguration?.SecretChatPublicKey,
                };

                await LanTransferWireProtocol.WriteFrameAsync(stream, hello, cancellationToken);
                var ackObject = await LanTransferWireProtocol.ReadFrameAsync(stream, cancellationToken);
                return ackObject?.ToObject<LanHelloAckFrame>();
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                client?.Dispose();
                if (!cancellationToken.IsCancellationRequested)
                {
                    LanTransferLogger.LogError(ex, "接受局域网连接失败");
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            stream.ReadTimeout = 30000;
            stream.WriteTimeout = 30000;

            try
            {
                var helloObject = await LanTransferWireProtocol.ReadFrameAsync(stream, cancellationToken);
                var hello = helloObject?.ToObject<LanHelloFrame>();
                if ((hello == null) || !string.Equals(hello.Type, "hello", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var config = _configurationProvider();
                var compatible = hello.ProtocolVersion == LanTransferProtocol.ProtocolVersion;
                var helloAck = new LanHelloAckFrame
                {
                    Type = "helloAck",
                    ProtocolVersion = LanTransferProtocol.ProtocolVersion,
                    Compatible = compatible,
                    DeviceId = config?.DeviceId,
                    DisplayName = config?.DisplayName,
                    MachineName = config?.MachineName,
                    AppVersion = config?.AppVersion,
                    Capabilities = config?.Capabilities,
                    SecretChatPublicKey = config?.SecretChatPublicKey,
                    Message = compatible ? "OK" : "协议版本不兼容",
                };

                await LanTransferWireProtocol.WriteFrameAsync(stream, helloAck, cancellationToken);
                if (!compatible)
                {
                    return;
                }

                var nextFrameObject = await LanTransferWireProtocol.ReadFrameAsync(stream, cancellationToken);
                if (nextFrameObject == null)
                {
                    return;
                }

                var frameType = nextFrameObject.Value<string>("Type");
                if (string.Equals(frameType, "transferRequest", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleTransferRequestAsync(stream, nextFrameObject, hello, cancellationToken);
                }
                else if (string.Equals(frameType, "secretSessionRequest", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleSecretSessionRequestAsync(stream, nextFrameObject, hello, cancellationToken);
                }
                else if (string.Equals(frameType, "secretMessage", StringComparison.OrdinalIgnoreCase))
                {
                    SecretMessageReceived?.Invoke(nextFrameObject.ToObject<LanSecretMessageFrame>());
                }
                else if (string.Equals(frameType, "secretReadReceipt", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(frameType, "secretDestroy", StringComparison.OrdinalIgnoreCase))
                {
                    SecretReceiptReceived?.Invoke(nextFrameObject.ToObject<LanSecretReceiptFrame>());
                }
            }
            catch (EndOfStreamException)
            {
            }
            catch (IOException)
            {
            }
            catch (Exception ex)
            {
                LanTransferLogger.LogError(ex, "处理局域网传输连接失败");
            }
        }
    }

    private async Task HandleSecretSessionRequestAsync(NetworkStream stream, JObject requestObject, LanHelloFrame hello, CancellationToken cancellationToken)
    {
        var requestFrame = requestObject.ToObject<LanSecretSessionRequestFrame>();
        if ((requestFrame == null) || string.IsNullOrWhiteSpace(requestFrame.SessionId))
        {
            return;
        }

        var request = new LanSecretChatSessionRequest
        {
            SessionId = requestFrame.SessionId,
            PeerDeviceId = hello.DeviceId,
            PeerDisplayName = requestFrame.SenderDisplayName ?? hello.DisplayName,
            PeerMachineName = requestFrame.SenderMachineName ?? hello.MachineName,
            PeerAddress = requestFrame.SenderAddress,
            PeerPort = requestFrame.SenderPort,
            PeerPublicKey = hello.SecretChatPublicKey,
        };

        var accepted = _secretChatApprovalAsync != null && await _secretChatApprovalAsync(request);
        await LanTransferWireProtocol.WriteFrameAsync(stream, new LanSecretSessionResponseFrame
        {
            Type = "secretSessionAccept",
            SessionId = request.SessionId,
            Accepted = accepted,
            Message = accepted ? "OK" : "对方拒绝密语请求",
        }, cancellationToken);

        if (accepted)
        {
            _secretChatAccepted?.Invoke(request);
        }
    }

    private async Task HandleTransferRequestAsync(NetworkStream stream, JObject requestObject, LanHelloFrame hello, CancellationToken serverCancellationToken)
    {
        var requestFrame = requestObject.ToObject<LanTransferRequestFrame>();
        if ((requestFrame == null) || string.IsNullOrWhiteSpace(requestFrame.TransferId))
        {
            return;
        }

        var request = new LanTransferRequest
        {
            TransferId = requestFrame.TransferId,
            SenderDeviceId = hello.DeviceId,
            SenderDisplayName = requestFrame.SenderDisplayName ?? hello.DisplayName,
            SenderMachineName = requestFrame.SenderMachineName ?? hello.MachineName,
            SenderAddress = requestFrame.SenderAddress,
            SenderPort = requestFrame.SenderPort,
            Items = requestFrame.Items ?? new List<LanTransferItem>(),
            TopLevelNames = requestFrame.TopLevelNames ?? new List<string>(),
            TotalBytes = requestFrame.TotalBytes,
            StatusText = "等待确认",
        };

        var decision = await _requestApprovalAsync(request) ?? LanIncomingTransferDecision.Reject("接收方拒绝");
        if (!decision.Accepted)
        {
            await LanTransferWireProtocol.WriteFrameAsync(stream, new LanTransferResponseFrame
            {
                Type = "transferResponse",
                Accepted = false,
                Message = string.IsNullOrWhiteSpace(decision.Message) ? "接收方拒绝" : decision.Message,
            }, serverCancellationToken);
            return;
        }

        var config = _configurationProvider();
        var inboxPath = string.IsNullOrWhiteSpace(decision.InboxPath) ? config?.InboxPath : decision.InboxPath;
        var preparedPath = PrepareReceiveDirectories(inboxPath, request);

        try
        {
            EnsureInboxReady(inboxPath, request.TotalBytes);
            Directory.CreateDirectory(preparedPath.TempDirectory);
        }
        catch (Exception ex)
        {
            await LanTransferWireProtocol.WriteFrameAsync(stream, new LanTransferResponseFrame
            {
                Type = "transferResponse",
                Accepted = false,
                Message = ex.Message,
            }, serverCancellationToken);
            LanTransferLogger.LogError(ex, "创建接收目录失败");
            return;
        }

        await LanTransferWireProtocol.WriteFrameAsync(stream, new LanTransferResponseFrame
        {
            Type = "transferResponse",
            Accepted = true,
            SaveDirectory = preparedPath.FinalDirectory,
            Message = "接收方已同意",
        }, serverCancellationToken);

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
        _incomingTransferCancels[request.TransferId] = linkedCts;

        var session = new LanTransferSession
        {
            TransferId = request.TransferId,
            Direction = "Receive",
            PeerDisplayName = request.SenderLabel,
            Summary = request.TopLevelSummary,
            StatusText = "正在接收",
            TotalBytes = request.TotalBytes,
            BytesTransferred = 0,
            CanCancel = true,
        };

        SessionStarted?.Invoke(session);

        try
        {
            await ReceiveFilesAsync(stream, preparedPath.TempDirectory, session, linkedCts.Token);

            if (Directory.Exists(preparedPath.FinalDirectory))
            {
                throw new IOException("目标目录已存在，无法完成接收。");
            }

            Directory.Move(preparedPath.TempDirectory, preparedPath.FinalDirectory);
            session.StatusText = "接收完成";
            session.CanCancel = false;

            ReceiveRecorded?.Invoke(new LanTransferRecord
            {
                TransferId = request.TransferId,
                Direction = "Receive",
                PeerDisplayName = request.SenderLabel,
                PeerAddress = request.SenderAddress,
                ItemCount = request.ItemCount,
                TotalBytes = request.TotalBytes,
                Status = "成功",
                Summary = request.TopLevelSummary,
                TargetPath = preparedPath.FinalDirectory,
                StartedAtUtc = request.ReceivedAtUtc,
                CompletedAtUtc = DateTime.UtcNow,
                Detail = "接收成功",
            });

            LanTransferLogger.LogInfo($"接收完成：{request.SenderLabel} -> {preparedPath.FinalDirectory}");
        }
        catch (OperationCanceledException)
        {
            session.StatusText = "已取消";
            session.CanCancel = false;
            TryDeleteDirectory(preparedPath.TempDirectory);
            await TrySendCancelAsync(stream, linkedCts.Token, "接收方取消");

            ReceiveRecorded?.Invoke(new LanTransferRecord
            {
                TransferId = request.TransferId,
                Direction = "Receive",
                PeerDisplayName = request.SenderLabel,
                PeerAddress = request.SenderAddress,
                ItemCount = request.ItemCount,
                TotalBytes = request.TotalBytes,
                Status = "已取消",
                Summary = request.TopLevelSummary,
                TargetPath = preparedPath.FinalDirectory,
                StartedAtUtc = request.ReceivedAtUtc,
                CompletedAtUtc = DateTime.UtcNow,
                Detail = "接收方取消",
            });
        }
        catch (Exception ex)
        {
            session.StatusText = "接收失败";
            session.CanCancel = false;
            TryDeleteDirectory(preparedPath.TempDirectory);
            await TrySendErrorAsync(stream, linkedCts.Token, ex.Message);

            ReceiveRecorded?.Invoke(new LanTransferRecord
            {
                TransferId = request.TransferId,
                Direction = "Receive",
                PeerDisplayName = request.SenderLabel,
                PeerAddress = request.SenderAddress,
                ItemCount = request.ItemCount,
                TotalBytes = request.TotalBytes,
                Status = "失败",
                Summary = request.TopLevelSummary,
                TargetPath = preparedPath.FinalDirectory,
                StartedAtUtc = request.ReceivedAtUtc,
                CompletedAtUtc = DateTime.UtcNow,
                Detail = ex.Message,
            });

            LanTransferLogger.LogError(ex, $"接收传输失败：{request.TransferId}");
        }
        finally
        {
            _incomingTransferCancels.TryRemove(request.TransferId, out _);
            linkedCts.Dispose();
            SessionCompleted?.Invoke(session);
        }
    }

    private static void EnsureInboxReady(string inboxPath, long totalBytes)
    {
        if (string.IsNullOrWhiteSpace(inboxPath))
        {
            throw new InvalidOperationException("未配置收件箱目录。");
        }

        Directory.CreateDirectory(inboxPath);
        var probeFile = Path.Combine(inboxPath, ".pm_write_test.tmp");
        File.WriteAllText(probeFile, "ok");
        File.Delete(probeFile);

        var root = Path.GetPathRoot(inboxPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < (totalBytes + (32L * 1024 * 1024)))
        {
            throw new IOException("目标磁盘剩余空间不足。");
        }
    }

    private static async Task ReceiveFilesAsync(NetworkStream stream, string tempDirectory, LanTransferSession session, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var frameObject = await LanTransferWireProtocol.ReadFrameAsync(stream, cancellationToken);
            var frameType = frameObject?.Value<string>("Type");
            if (string.Equals(frameType, "complete", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(frameType, "cancel", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(frameObject.Value<string>("Message") ?? "发送方已取消传输。");
            }

            if (string.Equals(frameType, "error", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(frameObject.Value<string>("Message") ?? "发送方发生错误。");
            }

            if (!string.Equals(frameType, "fileHeader", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("收到未知传输帧。");
            }

            var header = frameObject.ToObject<LanFileHeaderFrame>();
            var safeRelativePath = NormalizeRelativePath(header.RelativePath);
            var targetPath = Path.Combine(tempDirectory, safeRelativePath);
            if (header.IsDirectory)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            var parentDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            await LanTransferWireProtocol.CopyFixedLengthToFileAsync(stream, targetPath, header.Length, cancellationToken, transferredBytes =>
            {
                session.BytesTransferred += transferredBytes;
            });
        }
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new IOException("收到空的相对路径。");
        }

        var replaced = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(replaced))
        {
            throw new IOException("收到非法绝对路径。");
        }

        var full = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pm-lan-sandbox", replaced));
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pm-lan-sandbox"));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("收到越界相对路径。");
        }

        return replaced.TrimStart(Path.DirectorySeparatorChar);
    }

    private static LanPreparedReceivePath PrepareReceiveDirectories(string inboxPath, LanTransferRequest request)
    {
        var summary = request.TopLevelNames != null && request.TopLevelNames.Count == 1
            ? request.TopLevelNames[0]
            : $"{Math.Max(1, request.TopLevelNames?.Count ?? 0)}项";

        var sender = SanitizeFileName(request.SenderDisplayName ?? "Unknown");
        var targetName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{sender}_{SanitizeFileName(summary)}";
        var finalDirectory = Path.Combine(inboxPath, targetName);
        var tempDirectory = finalDirectory + ".tmp_" + Guid.NewGuid().ToString("N");
        return new LanPreparedReceivePath
        {
            FinalDirectory = finalDirectory,
            TempDirectory = tempDirectory,
        };
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Transfer";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        sanitized = sanitized.Trim();
        if (sanitized.Length > 40)
        {
            sanitized = sanitized.Substring(0, 40);
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "Transfer" : sanitized;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }

    private static async Task TrySendCancelAsync(NetworkStream stream, CancellationToken cancellationToken, string message)
    {
        try
        {
            await LanTransferWireProtocol.WriteFrameAsync(stream, new LanCancelFrame
            {
                Type = "cancel",
                Message = message,
            }, cancellationToken);
        }
        catch
        {
        }
    }

    private static async Task TrySendErrorAsync(NetworkStream stream, CancellationToken cancellationToken, string message)
    {
        try
        {
            await LanTransferWireProtocol.WriteFrameAsync(stream, new LanErrorFrame
            {
                Type = "error",
                Message = message,
            }, cancellationToken);
        }
        catch
        {
        }
    }
}

internal sealed class LanHostConfiguration
{
    public string DeviceId { get; set; }

    public string DisplayName { get; set; }

    public string MachineName { get; set; }

    public string AppVersion { get; set; }

    public string InboxPath { get; set; }

    public List<string> Capabilities { get; set; } = new List<string>();

    public string SecretChatPublicKey { get; set; }
}

internal sealed class LanIncomingTransferDecision
{
    public bool Accepted { get; set; }

    public string Message { get; set; }

    public string InboxPath { get; set; }

    public static LanIncomingTransferDecision Accept(string inboxPath)
    {
        return new LanIncomingTransferDecision
        {
            Accepted = true,
            InboxPath = inboxPath,
        };
    }

    public static LanIncomingTransferDecision Reject(string message)
    {
        return new LanIncomingTransferDecision
        {
            Accepted = false,
            Message = message,
        };
    }
}

internal sealed class LanPreparedReceivePath
{
    public string FinalDirectory { get; set; }

    public string TempDirectory { get; set; }
}

internal class LanHelloFrame
{
    public string Type { get; set; }

    public int ProtocolVersion { get; set; }

    public string DeviceId { get; set; }

    public string DisplayName { get; set; }

    public string MachineName { get; set; }

    public string AppVersion { get; set; }

    public List<string> Capabilities { get; set; } = new List<string>();

    public string SecretChatPublicKey { get; set; }
}

internal sealed class LanHelloAckFrame : LanHelloFrame
{
    public bool Compatible { get; set; }

    public string Message { get; set; }
}

internal sealed class LanTransferRequestFrame
{
    public string Type { get; set; }

    public string TransferId { get; set; }

    public string SenderDisplayName { get; set; }

    public string SenderMachineName { get; set; }

    public string SenderAddress { get; set; }

    public int SenderPort { get; set; }

    public List<LanTransferItem> Items { get; set; } = new List<LanTransferItem>();

    public List<string> TopLevelNames { get; set; } = new List<string>();

    public long TotalBytes { get; set; }
}

internal sealed class LanTransferResponseFrame
{
    public string Type { get; set; }

    public bool Accepted { get; set; }

    public string SaveDirectory { get; set; }

    public string Message { get; set; }
}

internal sealed class LanFileHeaderFrame
{
    public string Type { get; set; }

    public string RelativePath { get; set; }

    public bool IsDirectory { get; set; }

    public long Length { get; set; }
}

internal sealed class LanCancelFrame
{
    public string Type { get; set; }

    public string Message { get; set; }
}

internal sealed class LanErrorFrame
{
    public string Type { get; set; }

    public string Message { get; set; }
}

internal sealed class LanSecretSessionRequestFrame
{
    public string Type { get; set; }

    public string SessionId { get; set; }

    public string SenderDisplayName { get; set; }

    public string SenderMachineName { get; set; }

    public string SenderAddress { get; set; }

    public int SenderPort { get; set; }
}

internal sealed class LanSecretSessionResponseFrame
{
    public string Type { get; set; }

    public string SessionId { get; set; }

    public bool Accepted { get; set; }

    public string Message { get; set; }
}

internal sealed class LanSecretMessageFrame
{
    public string Type { get; set; }

    public string SessionId { get; set; }

    public string MessageId { get; set; }

    public string SenderDeviceId { get; set; }

    public string SenderDisplayName { get; set; }

    public string SenderMachineName { get; set; }

    public string SenderAddress { get; set; }

    public int SenderPort { get; set; }

    public string CipherText { get; set; }

    public string EncryptedKey { get; set; }

    public string Iv { get; set; }

    public string Hmac { get; set; }

    public string SenderPublicKey { get; set; }
}

internal sealed class LanSecretReceiptFrame
{
    public string Type { get; set; }

    public string SessionId { get; set; }

    public string MessageId { get; set; }

    public string Receipt { get; set; }

    public string SenderDeviceId { get; set; }

    public string SenderAddress { get; set; }

    public int SenderPort { get; set; }
}

internal sealed class LanSecretChatSessionRequest
{
    public string SessionId { get; set; }

    public string PeerDeviceId { get; set; }

    public string PeerDisplayName { get; set; }

    public string PeerMachineName { get; set; }

    public string PeerAddress { get; set; }

    public int PeerPort { get; set; }

    public string PeerPublicKey { get; set; }

    public string PeerLabel => string.IsNullOrWhiteSpace(PeerMachineName)
        ? (PeerDisplayName ?? "未知同事")
        : $"{PeerDisplayName} ({PeerMachineName})";
}

internal static class LanTransferWireProtocol
{
    public static async Task WriteFrameAsync(NetworkStream stream, object frame, CancellationToken cancellationToken)
    {
        var json = JsonConvert.SerializeObject(frame);
        var payload = System.Text.Encoding.UTF8.GetBytes(json);
        var lengthBuffer = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(lengthBuffer, 0, lengthBuffer.Length, cancellationToken);
        await stream.WriteAsync(payload, 0, payload.Length, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<JObject> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[sizeof(int)];
        await ReadExactAsync(stream, lengthBuffer, 0, lengthBuffer.Length, cancellationToken);
        var length = BitConverter.ToInt32(lengthBuffer, 0);
        if ((length <= 0) || (length > 4 * 1024 * 1024))
        {
            throw new IOException("收到非法控制帧长度。");
        }

        var payload = new byte[length];
        await ReadExactAsync(stream, payload, 0, payload.Length, cancellationToken);
        var json = System.Text.Encoding.UTF8.GetString(payload);
        return JObject.Parse(json);
    }

    public static async Task CopyFixedLengthToFileAsync(NetworkStream stream, string filePath, long length, CancellationToken cancellationToken, Action<long> progressCallback)
    {
        using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var remaining = length;
            var buffer = new byte[81920];
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = remaining > buffer.Length ? buffer.Length : (int)remaining;
                var read = await stream.ReadAsync(buffer, 0, count, cancellationToken);
                if (read <= 0)
                {
                    throw new EndOfStreamException("读取文件内容时连接已中断。");
                }

                await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
                remaining -= read;
                progressCallback?.Invoke(read);
            }
        }
    }

    public static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer, offset, count, cancellationToken);
            if (read <= 0)
            {
                throw new EndOfStreamException("连接已关闭。");
            }

            offset += read;
            count -= read;
        }
    }
}
