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

    public LanDiscoveryService(Func<LanLocalIdentity> identityProvider)
    {
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        RefreshLocalAddresses();
    }

    public event Action<LanDiscoveryAnnouncement, IPEndPoint> AnnouncementReceived;

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

internal sealed class LanDiscoveryAnnouncement
{
    public int ProtocolVersion { get; set; }

    public string DeviceId { get; set; }

    public string DisplayName { get; set; }

    public string MachineName { get; set; }

    public int ListenPort { get; set; }

    public string AppVersion { get; set; }

    public List<string> Capabilities { get; set; } = new List<string>();

    public string SecretChatPublicKey { get; set; }
}

internal sealed class LanLocalIdentity
{
    public bool Enabled { get; set; }

    public string DeviceId { get; set; }

    public string DisplayName { get; set; }

    public string MachineName { get; set; }

    public int ListenPort { get; set; }

    public string AppVersion { get; set; }

    public List<string> Capabilities { get; set; } = new List<string>();

    public string SecretChatPublicKey { get; set; }
}

internal static class LanTransferProtocol
{
    public const int ProtocolVersion = 1;

    public const string SecretChatCapability = "secret-chat-v1";

    public static List<string> CurrentCapabilities => new List<string> { SecretChatCapability };

    public static bool SupportsSecretChat(IEnumerable<string> capabilities)
    {
        return capabilities != null && capabilities.Any(capability =>
            string.Equals(capability, SecretChatCapability, StringComparison.OrdinalIgnoreCase));
    }
}
