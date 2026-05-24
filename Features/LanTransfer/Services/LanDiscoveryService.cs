using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PackageManager.Services;

/// <summary>
/// 局域网设备发现服务，通过 UDP 广播和监听实现同网段设备的自动发现。
/// </summary>
internal sealed class LanDiscoveryService : IDisposable
{
    internal const int DiscoveryPort = 48930;

    private readonly Func<LanLocalIdentity> _identityProvider;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly HashSet<string> _localAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private UdpClient _receiver;
    private UdpClient _sender;
    private Task _receiveLoopTask;
    private Task _broadcastLoopTask;

    /// <summary>
    /// 初始化 <see cref="LanDiscoveryService"/> 的新实例。
    /// </summary>
    /// <param name="identityProvider">用于获取本机设备标识的延迟委托。</param>
    /// <exception cref="ArgumentNullException"><paramref name="identityProvider"/> 为 null。</exception>
    public LanDiscoveryService(Func<LanLocalIdentity> identityProvider)
    {
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        RefreshLocalAddresses();
    }

    /// <summary>
    /// 当收到其他设备的发现广播时触发，参数为广播内容和远程终结点。
    /// </summary>
    public event Action<LanDiscoveryAnnouncement, IPEndPoint> AnnouncementReceived;

    /// <summary>
    /// 启动广播和接收循环，开始局域网设备发现。
    /// 若已启动则直接返回。
    /// </summary>
    public void Start()
    {
        if ((_receiveLoopTask != null) || (_broadcastLoopTask != null))
        {
            return;
        }

        _receiver = new UdpClient(AddressFamily.InterNetwork);
        _receiver.ExclusiveAddressUse = false;
        _receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _receiver.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

        _sender = new UdpClient(AddressFamily.InterNetwork);
        _sender.EnableBroadcast = true;
        _sender.MulticastLoopback = false;

        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _broadcastLoopTask = Task.Run(() => BroadcastLoopAsync(_cts.Token));
    }

    /// <summary>
    /// 停止广播与接收循环，释放 UDP 套接字资源。
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();

        try
        {
            _receiver?.Close();
            _sender?.Close();
        }
        catch
        {
        }
    }

    private async Task BroadcastLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var identity = _identityProvider();
                if ((identity != null) && identity.Enabled && (identity.ListenPort > 0))
                {
                    var announcement = new LanDiscoveryAnnouncement
                    {
                        ProtocolVersion = LanTransferProtocol.ProtocolVersion,
                        DeviceId = identity.DeviceId,
                        DisplayName = identity.DisplayName,
                        MachineName = identity.MachineName,
                        ListenPort = identity.ListenPort,
                        AppVersion = identity.AppVersion,
                        Capabilities = identity.Capabilities,
                        SecretChatPublicKey = identity.SecretChatPublicKey,
                    };

                    var payload = JsonConvert.SerializeObject(announcement);
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    await _sender.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
                }
            }
            catch (Exception ex)
            {
                LanTransferLogger.LogError(ex, "局域网发现广播失败");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _receiver.ReceiveAsync();
                if (!IsPrivateIpv4(result.RemoteEndPoint.Address))
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(result.Buffer);
                var announcement = JsonConvert.DeserializeObject<LanDiscoveryAnnouncement>(json);
                if ((announcement == null) || string.IsNullOrWhiteSpace(announcement.DeviceId))
                {
                    continue;
                }

                var identity = _identityProvider();
                if ((identity != null) && string.Equals(identity.DeviceId, announcement.DeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (_localAddresses.Contains(result.RemoteEndPoint.Address.ToString()))
                {
                    continue;
                }

                AnnouncementReceived?.Invoke(announcement, result.RemoteEndPoint);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                LanTransferLogger.LogError(ex, "局域网发现接收失败");
            }
        }
    }

    private void RefreshLocalAddresses()
    {
        _localAddresses.Clear();

        try
        {
            foreach (var address in Dns.GetHostAddresses(Dns.GetHostName())
                         .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                         .Where(IsPrivateIpv4))
            {
                _localAddresses.Add(address.ToString());
            }
        }
        catch
        {
        }
    }

    internal static bool IsPrivateIpv4(IPAddress address)
    {
        if ((address == null) || (address.AddressFamily != AddressFamily.InterNetwork))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return (bytes[0] == 10)
               || ((bytes[0] == 172) && (bytes[1] >= 16) && (bytes[1] <= 31))
               || ((bytes[0] == 192) && (bytes[1] == 168));
    }
}

/// <summary>
/// 局域网发现广播消息，携带设备标识和能力信息。
/// </summary>
internal sealed class LanDiscoveryAnnouncement
{
    /// <summary>协议版本号。</summary>
    public int ProtocolVersion { get; set; }

    /// <summary>设备唯一标识。</summary>
    public string DeviceId { get; set; }

    /// <summary>用户显示名称。</summary>
    public string DisplayName { get; set; }

    /// <summary>机器名称。</summary>
    public string MachineName { get; set; }

    /// <summary>文件传输监听端口。</summary>
    public int ListenPort { get; set; }

    /// <summary>应用程序版本号。</summary>
    public string AppVersion { get; set; }

    /// <summary>设备支持的能力列表。</summary>
    public List<string> Capabilities { get; set; } = new List<string>();

    /// <summary>密语聊天的 RSA 公钥（XML 格式）。</summary>
    public string SecretChatPublicKey { get; set; }
}

/// <summary>
/// 本机设备标识信息，用于局域网发现广播。
/// </summary>
internal sealed class LanLocalIdentity
{
    /// <summary>是否启用局域网传输功能。</summary>
    public bool Enabled { get; set; }

    /// <summary>设备唯一标识。</summary>
    public string DeviceId { get; set; }

    /// <summary>用户显示名称。</summary>
    public string DisplayName { get; set; }

    /// <summary>机器名称。</summary>
    public string MachineName { get; set; }

    /// <summary>文件传输监听端口。</summary>
    public int ListenPort { get; set; }

    /// <summary>应用程序版本号。</summary>
    public string AppVersion { get; set; }

    /// <summary>设备支持的能力列表。</summary>
    public List<string> Capabilities { get; set; } = new List<string>();

    /// <summary>密语聊天的 RSA 公钥（XML 格式）。</summary>
    public string SecretChatPublicKey { get; set; }
}

/// <summary>
/// 局域网传输协议常量与工具方法。
/// </summary>
internal static class LanTransferProtocol
{
    /// <summary>当前协议版本号。</summary>
    public const int ProtocolVersion = 1;

    /// <summary>密语聊天能力标识。</summary>
    public const string SecretChatCapability = "secret-chat-v1";

    /// <summary>当前实例支持的能力列表。</summary>
    public static List<string> CurrentCapabilities => new List<string> { SecretChatCapability };

    /// <summary>
    /// 判断对方能力列表是否包含密语聊天支持。
    /// </summary>
    /// <param name="capabilities">对方能力列表。</param>
    /// <returns>若支持密语聊天则返回 true，否则返回 false。</returns>
    public static bool SupportsSecretChat(IEnumerable<string> capabilities)
    {
        return capabilities != null && capabilities.Any(capability =>
            string.Equals(capability, SecretChatCapability, StringComparison.OrdinalIgnoreCase));
    }
}
