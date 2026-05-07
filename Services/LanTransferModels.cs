using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PackageManager.Services;

/// <summary>
/// 局域网传输模块的可绑定基类，提供属性变更通知支持。
/// </summary>
public abstract class LanTransferBindableBase : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// 触发指定属性名的变更通知。
    /// </summary>
    /// <param name="propertyName">属性名称，由编译器自动填充。</param>
    protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 设置属性值，若值发生变更则触发通知。
    /// </summary>
    /// <typeparam name="T">属性类型。</typeparam>
    /// <param name="field">属性后备字段的引用。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">属性名称，由编译器自动填充。</param>
    /// <returns>值是否发生变更。</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

/// <summary>
/// 局域网对端设备信息，包含设备标识、连接状态和密语聊天支持等。
/// </summary>
public sealed class LanPeerInfo : LanTransferBindableBase
{
    private string displayName;
    private string machineName;
    private string address;
    private int listenPort;
    private DateTime lastSeenUtc;
    private bool isCompatible = true;
    private string appVersion;
    private bool isOnline = true;
    private bool isManual;
    private string statusText;
    private bool supportsSecretChat;
    private string secretChatPublicKey;
    private int secretUnreadCount;

    /// <summary>设备唯一标识。</summary>
    public string DeviceId { get; set; }

    /// <summary>用户显示名称。</summary>
    public string DisplayName
    {
        get => displayName;
        set
        {
            if (SetProperty(ref displayName, value))
            {
                OnPropertyChanged(nameof(DisplayLabel));
            }
        }
    }

    /// <summary>机器名称。</summary>
    public string MachineName    {
        get => machineName;
        set
        {
            if (SetProperty(ref machineName, value))
            {
                OnPropertyChanged(nameof(DisplayLabel));
            }
        }
    }

    /// <summary>IP 地址。</summary>
    public string Address    {
        get => address;
        set
        {
            if (SetProperty(ref address, value))
            {
                OnPropertyChanged(nameof(EndpointDisplay));
            }
        }
    }

    /// <summary>文件传输监听端口。</summary>
    public int ListenPort    {
        get => listenPort;
        set
        {
            if (SetProperty(ref listenPort, value))
            {
                OnPropertyChanged(nameof(EndpointDisplay));
            }
        }
    }

    /// <summary>应用程序版本号。</summary>
    public string AppVersion    {
        get => appVersion;
        set => SetProperty(ref appVersion, value);
    }

    /// <summary>协议版本是否兼容。</summary>
    public bool IsCompatible    {
        get => isCompatible;
        set
        {
            if (SetProperty(ref isCompatible, value))
            {
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CompatibilityText));
            }
        }
    }

    /// <summary>是否在线。</summary>
    public bool IsOnline    {
        get => isOnline;
        set
        {
            if (SetProperty(ref isOnline, value))
            {
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(OnlineText));
                OnPropertyChanged(nameof(StatusSummaryText));
            }
        }
    }

    /// <summary>是否为手动添加的对端。</summary>
    public bool IsManual    {
        get => isManual;
        set => SetProperty(ref isManual, value);
    }

    /// <summary>最后一次收到广播的 UTC 时间。</summary>
    public DateTime LastSeenUtc    {
        get => lastSeenUtc;
        set
        {
            if (SetProperty(ref lastSeenUtc, value))
            {
                OnPropertyChanged(nameof(LastSeenText));
                OnPropertyChanged(nameof(StatusSummaryText));
            }
        }
    }

    /// <summary>状态显示文本。</summary>
    public string StatusText
    {
        get => statusText;
        set
        {
            if (SetProperty(ref statusText, value))
            {
                OnPropertyChanged(nameof(StatusSummaryText));
            }
        }
    }

    /// <summary>是否支持密语聊天。</summary>
    public bool SupportsSecretChat
    {
        get => supportsSecretChat;
        set
        {
            if (SetProperty(ref supportsSecretChat, value))
            {
                OnPropertyChanged(nameof(CanStartSecretChat));
                OnPropertyChanged(nameof(SecretChatText));
            }
        }
    }

    /// <summary>密语聊天的 RSA 公钥（XML 格式）。</summary>
    public string SecretChatPublicKey
    {
        get => secretChatPublicKey;
        set => SetProperty(ref secretChatPublicKey, value);
    }

    /// <summary>密语未读消息数量。</summary>
    public int SecretUnreadCount
    {
        get => secretUnreadCount;
        set
        {
            var count = Math.Max(0, value);
            if (SetProperty(ref secretUnreadCount, count))
            {
                OnPropertyChanged(nameof(HasSecretUnread));
                OnPropertyChanged(nameof(SecretChatText));
                OnPropertyChanged(nameof(SecretChatBadgeText));
                OnPropertyChanged(nameof(StatusSummaryText));
            }
        }
    }

    /// <summary>组合显示标签，优先显示 "显示名 (机器名)" 格式。</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(MachineName)
        ? (DisplayName ?? "未知设备")
        : $"{DisplayName} ({MachineName})";

    /// <summary>端点显示文本，格式为 "IP:端口"。</summary>
    public string EndpointDisplay => string.IsNullOrWhiteSpace(Address) ? "-" : $"{Address}:{ListenPort}";

    /// <summary>在线状态显示文本。</summary>
    public string OnlineText => IsOnline ? "在线" : "离线";

    /// <summary>兼容性显示文本。</summary>
    public string CompatibilityText => IsCompatible ? "兼容" : "版本不兼容";

    /// <summary>是否存在密语未读消息。</summary>
    public bool HasSecretUnread => SecretUnreadCount > 0;

    /// <summary>密语状态文本。</summary>
    public string SecretChatText => SecretUnreadCount > 0
        ? $"密语未读 {SecretUnreadCount} 条"
        : (SupportsSecretChat ? "支持密语" : "不支持密语");

    /// <summary>密语未读数角标文本，无未读时为空。</summary>
    public string SecretChatBadgeText => SecretUnreadCount > 0 ? SecretUnreadCount.ToString() : string.Empty;

    /// <summary>状态摘要文本，组合在线状态和最后活跃时间。</summary>
    public string StatusSummaryText
    {
        get
        {
            var status = string.IsNullOrWhiteSpace(StatusText) ? OnlineText : StatusText;
            var seen = LastSeenText;
            return string.IsNullOrWhiteSpace(seen) || (seen == "-")
                ? status
                : $"{status} · {seen}";
        }
    }

    /// <summary>最后活跃时间的友好显示文本。</summary>
    public string LastSeenText
    {
        get
        {
            if (LastSeenUtc == default(DateTime))
            {
                return "-";
            }

            var span = DateTime.UtcNow - LastSeenUtc;
            if (span.TotalSeconds < 1)
            {
                return "刚刚";
            }

            if (span.TotalMinutes < 1)
            {
                return $"{Math.Max(1, (int)span.TotalSeconds)} 秒前";
            }

            if (span.TotalHours < 1)
            {
                return $"{Math.Max(1, (int)span.TotalMinutes)} 分钟前";
            }

            return LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }
    }

    /// <summary>是否可以向该对端发送文件。</summary>
    public bool CanSend => IsOnline && IsCompatible && ListenPort > 0 && !string.IsNullOrWhiteSpace(Address);

    /// <summary>是否可以发起密语聊天。</summary>
    public bool CanStartSecretChat => CanSend && SupportsSecretChat;
}

/// <summary>
/// 密语消息方向。
/// </summary>
public enum SecretChatMessageDirection
{
    /// <summary>收到的消息。</summary>
    Incoming,
    /// <summary>发送的消息。</summary>
    Outgoing,
}

/// <summary>
/// 密语消息状态。
/// </summary>
public enum SecretChatMessageState
{
    /// <summary>发送中。</summary>
    Sending,
    /// <summary>已发送。</summary>
    Sent,
    /// <summary>未读。</summary>
    Unread,
    /// <summary>已读。</summary>
    Read,
    /// <summary>已销毁。</summary>
    Destroyed,
}

/// <summary>
/// 密语聊天消息，包含消息内容、状态和自毁倒计时。
/// </summary>
public sealed class SecretChatMessage : LanTransferBindableBase
{
    private SecretChatMessageState state;
    private int destroyCountdownSeconds;
    private string text;

    /// <summary>消息唯一标识。</summary>
    public string MessageId { get; set; }

    /// <summary>线路层会话标识。</summary>
    public string WireSessionId { get; set; }

    /// <summary>消息方向。</summary>
    public SecretChatMessageDirection Direction { get; set; }

    /// <summary>消息创建时间（UTC）。</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>消息已读时间（UTC），未读时为 null。</summary>
    public DateTime? ReadAtUtc { get; set; }

    /// <summary>消息文本内容。</summary>
    public string Text
    {
        get => text;
        set => SetProperty(ref text, value);
    }

    /// <summary>消息当前状态。</summary>
    public SecretChatMessageState State
    {
        get => state;
        set
        {
            if (SetProperty(ref state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(IsDestroyed));
            }
        }
    }

    /// <summary>自毁倒计时（秒）。</summary>
    public int DestroyCountdownSeconds
    {
        get => destroyCountdownSeconds;
        set
        {
            if (SetProperty(ref destroyCountdownSeconds, value))
            {
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    /// <summary>消息是否已销毁。</summary>
    public bool IsDestroyed => State == SecretChatMessageState.Destroyed;

    /// <summary>是否为发送的消息。</summary>
    public bool IsOutgoing => Direction == SecretChatMessageDirection.Outgoing;

    /// <summary>是否为收到的消息。</summary>
    public bool IsIncoming => Direction == SecretChatMessageDirection.Incoming;

    /// <summary>创建时间的本地化显示文本。</summary>
    public string CreatedAtText => CreatedAtUtc.ToLocalTime().ToString("HH:mm:ss");

    /// <summary>状态显示文本。</summary>
    public string StateText
    {
        get
        {
            if (State == SecretChatMessageState.Destroyed)
            {
                return "已销毁";
            }

            if (DestroyCountdownSeconds > 0)
            {
                return $"已读 · {DestroyCountdownSeconds}s 后销毁";
            }

            switch (State)
            {
                case SecretChatMessageState.Sending:
                    return "发送中";
                case SecretChatMessageState.Sent:
                    return "未读";
                case SecretChatMessageState.Unread:
                    return "未读";
                case SecretChatMessageState.Read:
                    return "已读";
                default:
                    return string.Empty;
            }
        }
    }
}

/// <summary>
/// 密语聊天会话，管理消息列表、会话状态和窗口信息。
/// </summary>
public sealed class SecretChatSession : LanTransferBindableBase
{
    private string statusText;
    private bool isOpen = true;
    private bool isProtected;
    private bool canSend = true;
    private int unreadCount;
    private bool isWindowOpen;
    private bool isWindowActive;

    /// <summary>会话唯一标识。</summary>
    public string SessionId { get; set; }

    /// <summary>会话键，用于匹配对端（基于设备ID和端点）。</summary>
    public string SessionKey { get; set; }

    /// <summary>对方设备标识。</summary>
    public string PeerDeviceId { get; set; }

    /// <summary>对方显示名称。</summary>
    public string PeerDisplayName { get; set; }

    /// <summary>对方 IP 地址。</summary>
    public string PeerAddress { get; set; }

    /// <summary>会话消息集合。</summary>
    public ObservableCollection<SecretChatMessage> Messages { get; } = new ObservableCollection<SecretChatMessage>();

    /// <summary>未读消息数量。</summary>
    public int UnreadCount
    {
        get => unreadCount;
        set
        {
            var count = Math.Max(0, value);
            if (SetProperty(ref unreadCount, count))
            {
                OnPropertyChanged(nameof(HasUnread));
                OnPropertyChanged(nameof(UnreadText));
            }
        }
    }

    /// <summary>是否存在未读消息。</summary>
    public bool HasUnread => UnreadCount > 0;

    /// <summary>未读数显示文本。</summary>
    public string UnreadText => UnreadCount > 0 ? $"未读 {UnreadCount} 条" : string.Empty;

    /// <summary>密语窗口是否已打开。</summary>
    public bool IsWindowOpen
    {
        get => isWindowOpen;
        set => SetProperty(ref isWindowOpen, value);
    }

    /// <summary>密语窗口是否处于活动状态。</summary>
    public bool IsWindowActive    {
        get => isWindowActive;
        set => SetProperty(ref isWindowActive, value);
    }

    /// <summary>会话状态显示文本。</summary>
    public string StatusText
    {
        get => statusText;
        set => SetProperty(ref statusText, value);
    }

    /// <summary>会话是否开启。</summary>
    public bool IsOpen
    {
        get => isOpen;
        set
        {
            if (SetProperty(ref isOpen, value))
            {
                OnPropertyChanged(nameof(CanSend));
            }
        }
    }

    /// <summary>是否启用截图保护。</summary>
    public bool IsProtected    {
        get => isProtected;
        set
        {
            if (SetProperty(ref isProtected, value))
            {
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(ProtectionText));
            }
        }
    }

    /// <summary>发送功能是否启用。</summary>
    public bool SendEnabled    {
        get => canSend;
        set
        {
            if (SetProperty(ref canSend, value))
            {
                OnPropertyChanged(nameof(CanSend));
            }
        }
    }

    /// <summary>是否可以发送消息（会话开启、截图保护启用、发送功能启用）。</summary>
    public bool CanSend => IsOpen && IsProtected && SendEnabled;

    /// <summary>对方标题，组合显示名称和地址。</summary>
    public string PeerTitle => string.IsNullOrWhiteSpace(PeerAddress)
        ? PeerDisplayName
        : $"{PeerDisplayName} · {PeerAddress}";

    /// <summary>截图保护状态显示文本。</summary>
    public string ProtectionText => IsProtected
        ? "系统截图/录屏将显示黑屏或排除该窗口"
        : "当前系统未启用截图保护，已禁止发送";

    /// <summary>
    /// 刷新对方标题的变更通知。
    /// </summary>
    public void RefreshPeerTitle()
    {
        OnPropertyChanged(nameof(PeerTitle));
    }
}

/// <summary>
/// 文件传输项，描述单个文件或目录。
/// </summary>
public sealed class LanTransferItem
{
    /// <summary>相对路径。</summary>
    public string RelativePath { get; set; }

    /// <summary>文件或目录名称。</summary>
    public string Name { get; set; }

    /// <summary>是否为目录。</summary>
    public bool IsDirectory { get; set; }

    /// <summary>文件字节长度，目录时为 0。</summary>
    public long Length { get; set; }
}

/// <summary>
/// 收到的文件传输请求，等待用户审批。
/// </summary>
public sealed class LanTransferRequest : LanTransferBindableBase
{
    private string statusText;
    private string saveDirectory;

    /// <summary>传输唯一标识。</summary>
    public string TransferId { get; set; }

    /// <summary>发送方设备标识。</summary>
    public string SenderDeviceId { get; set; }

    /// <summary>发送方显示名称。</summary>
    public string SenderDisplayName { get; set; }

    /// <summary>发送方机器名称。</summary>
    public string SenderMachineName { get; set; }

    /// <summary>发送方 IP 地址。</summary>
    public string SenderAddress { get; set; }

    /// <summary>发送方监听端口。</summary>
    public int SenderPort { get; set; }

    /// <summary>待传输的文件/目录项列表。</summary>
    public List<LanTransferItem> Items { get; set; } = new List<LanTransferItem>();

    /// <summary>顶层项名称列表。</summary>
    public List<string> TopLevelNames { get; set; } = new List<string>();

    /// <summary>传输总字节数。</summary>
    public long TotalBytes { get; set; }

    /// <summary>请求接收时间（UTC）。</summary>
    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>状态显示文本。</summary>
    public string StatusText
    {
        get => statusText;
        set => SetProperty(ref statusText, value);
    }

    /// <summary>保存目录路径。</summary>
    public string SaveDirectory
    {
        get => saveDirectory;
        set => SetProperty(ref saveDirectory, value);
    }

    /// <summary>传输项数量。</summary>
    public int ItemCount => Items?.Count ?? 0;

    /// <summary>发送方组合显示标签。</summary>
    public string SenderLabel => string.IsNullOrWhiteSpace(SenderMachineName)
        ? (SenderDisplayName ?? "未知发送者")
        : $"{SenderDisplayName} ({SenderMachineName})";

    /// <summary>顶层项名称摘要。</summary>
    public string TopLevelSummary => (TopLevelNames == null) || (TopLevelNames.Count == 0)
        ? "-"
        : string.Join("、", TopLevelNames);

    /// <summary>接收时间的本地化显示文本。</summary>
    public string ReceivedAtText => ReceivedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

/// <summary>
/// 文件传输历史记录。
/// </summary>
public sealed class LanTransferRecord
{
    /// <summary>传输唯一标识。</summary>
    public string TransferId { get; set; }

    /// <summary>传输方向，"Send" 或 "Receive"。</summary>
    public string Direction { get; set; }

    /// <summary>对方显示名称。</summary>
    public string PeerDisplayName { get; set; }

    /// <summary>对方 IP 地址。</summary>
    public string PeerAddress { get; set; }

    /// <summary>传输项数量。</summary>
    public int ItemCount { get; set; }

    /// <summary>传输总字节数。</summary>
    public long TotalBytes { get; set; }

    /// <summary>传输状态。</summary>
    public string Status { get; set; }

    /// <summary>传输摘要。</summary>
    public string Summary { get; set; }

    /// <summary>目标路径。</summary>
    public string TargetPath { get; set; }

    /// <summary>详细信息。</summary>
    public string Detail { get; set; }

    /// <summary>传输开始时间（UTC）。</summary>
    public DateTime StartedAtUtc { get; set; }

    /// <summary>传输完成时间（UTC），未完成时为 null。</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>方向的中文显示文本。</summary>
    public string DirectionText => string.Equals(Direction, "Receive", StringComparison.OrdinalIgnoreCase) ? "接收" : "发送";

    /// <summary>状态显示文本。</summary>
    public string StatusText => string.IsNullOrWhiteSpace(Status) ? "未知" : Status;

    /// <summary>完成时间的本地化显示文本。</summary>
    public string CompletedAtText => (CompletedAtUtc ?? StartedAtUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>方向与摘要的组合显示文本。</summary>
    public string DirectionSummaryText => string.IsNullOrWhiteSpace(Summary)
        ? DirectionText
        : $"{DirectionText} · {Summary}";

    /// <summary>对方信息摘要文本。</summary>
    public string PeerSummaryText => string.IsNullOrWhiteSpace(PeerAddress)
        ? (PeerDisplayName ?? "-")
        : $"{PeerDisplayName} · {PeerAddress}";

    /// <summary>状态与完成时间的组合显示文本。</summary>
    public string StatusTimeText => $"{StatusText} · {CompletedAtText}";
}

/// <summary>
/// 正在进行中的文件传输会话。
/// </summary>
public sealed class LanTransferSession : LanTransferBindableBase
{
    private string statusText;
    private long bytesTransferred;
    private long totalBytes;
    private bool canCancel;

    /// <summary>传输唯一标识。</summary>
    public string TransferId { get; set; }

    /// <summary>传输方向，"Send" 或 "Receive"。</summary>
    public string Direction { get; set; }

    /// <summary>对方显示名称。</summary>
    public string PeerDisplayName { get; set; }

    /// <summary>传输摘要。</summary>
    public string Summary { get; set; }

    /// <summary>状态显示文本。</summary>
    public string StatusText
    {
        get => statusText;
        set => SetProperty(ref statusText, value);
    }

    /// <summary>已传输字节数。</summary>
    public long BytesTransferred
    {
        get => bytesTransferred;
        set
        {
            if (SetProperty(ref bytesTransferred, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    /// <summary>总字节数。</summary>
    public long TotalBytes
    {
        get => totalBytes;
        set
        {
            if (SetProperty(ref totalBytes, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    /// <summary>是否可以取消传输。</summary>
    public bool CanCancel
    {
        get => canCancel;
        set => SetProperty(ref canCancel, value);
    }

    /// <summary>方向的中文显示文本（进行中）。</summary>
    public string DirectionText => string.Equals(Direction, "Receive", StringComparison.OrdinalIgnoreCase) ? "接收中" : "发送中";

    /// <summary>进度显示文本，格式为 "已传输 / 总大小"。</summary>
    public string ProgressText => LanTransferFormatting.FormatSize(BytesTransferred) + " / " + LanTransferFormatting.FormatSize(TotalBytes);
}

/// <summary>
/// 文件大小格式化工具类。
/// </summary>
public static class LanTransferFormatting
{
    /// <summary>
    /// 将字节数格式化为人类可读的文件大小字符串。
    /// </summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>格式化后的字符串，如 "1.5 MB"。</returns>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var value = (double)bytes;
        var unitIndex = 0;
        while ((value >= 1024d) && (unitIndex < (units.Length - 1)))
        {
            value /= 1024d;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes} {units[unitIndex]}" : $"{value:0.##} {units[unitIndex]}";
    }
}
