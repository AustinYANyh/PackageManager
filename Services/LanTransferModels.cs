using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PackageManager.Services;

public abstract class LanTransferBindableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

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

    public string DeviceId { get; set; }

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

    public string MachineName
    {
        get => machineName;
        set
        {
            if (SetProperty(ref machineName, value))
            {
                OnPropertyChanged(nameof(DisplayLabel));
            }
        }
    }

    public string Address
    {
        get => address;
        set
        {
            if (SetProperty(ref address, value))
            {
                OnPropertyChanged(nameof(EndpointDisplay));
            }
        }
    }

    public int ListenPort
    {
        get => listenPort;
        set
        {
            if (SetProperty(ref listenPort, value))
            {
                OnPropertyChanged(nameof(EndpointDisplay));
            }
        }
    }

    public string AppVersion
    {
        get => appVersion;
        set => SetProperty(ref appVersion, value);
    }

    public bool IsCompatible
    {
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

    public bool IsOnline
    {
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

    public bool IsManual
    {
        get => isManual;
        set => SetProperty(ref isManual, value);
    }

    public DateTime LastSeenUtc
    {
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

    public string SecretChatPublicKey
    {
        get => secretChatPublicKey;
        set => SetProperty(ref secretChatPublicKey, value);
    }

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

    public string DisplayLabel => string.IsNullOrWhiteSpace(MachineName)
        ? (DisplayName ?? "未知设备")
        : $"{DisplayName} ({MachineName})";

    public string EndpointDisplay => string.IsNullOrWhiteSpace(Address) ? "-" : $"{Address}:{ListenPort}";

    public string OnlineText => IsOnline ? "在线" : "离线";

    public string CompatibilityText => IsCompatible ? "兼容" : "版本不兼容";

    public bool HasSecretUnread => SecretUnreadCount > 0;

    public string SecretChatText => SecretUnreadCount > 0
        ? $"密语未读 {SecretUnreadCount} 条"
        : (SupportsSecretChat ? "支持密语" : "不支持密语");

    public string SecretChatBadgeText => SecretUnreadCount > 0 ? SecretUnreadCount.ToString() : string.Empty;

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

    public bool CanSend => IsOnline && IsCompatible && ListenPort > 0 && !string.IsNullOrWhiteSpace(Address);

    public bool CanStartSecretChat => CanSend && SupportsSecretChat;
}

public enum SecretChatMessageDirection
{
    Incoming,
    Outgoing,
}

public enum SecretChatMessageState
{
    Sending,
    Sent,
    Unread,
    Read,
    Destroyed,
}

public sealed class SecretChatMessage : LanTransferBindableBase
{
    private SecretChatMessageState state;
    private int destroyCountdownSeconds;
    private string text;

    public string MessageId { get; set; }

    public string WireSessionId { get; set; }

    public SecretChatMessageDirection Direction { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ReadAtUtc { get; set; }

    public string Text
    {
        get => text;
        set => SetProperty(ref text, value);
    }

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

    public bool IsDestroyed => State == SecretChatMessageState.Destroyed;

    public bool IsOutgoing => Direction == SecretChatMessageDirection.Outgoing;

    public bool IsIncoming => Direction == SecretChatMessageDirection.Incoming;

    public string CreatedAtText => CreatedAtUtc.ToLocalTime().ToString("HH:mm:ss");

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

public sealed class SecretChatSession : LanTransferBindableBase
{
    private string statusText;
    private bool isOpen = true;
    private bool isProtected;
    private bool canSend = true;
    private int unreadCount;
    private bool isWindowOpen;
    private bool isWindowActive;

    public string SessionId { get; set; }

    public string SessionKey { get; set; }

    public string PeerDeviceId { get; set; }

    public string PeerDisplayName { get; set; }

    public string PeerAddress { get; set; }

    public ObservableCollection<SecretChatMessage> Messages { get; } = new ObservableCollection<SecretChatMessage>();

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

    public bool HasUnread => UnreadCount > 0;

    public string UnreadText => UnreadCount > 0 ? $"未读 {UnreadCount} 条" : string.Empty;

    public bool IsWindowOpen
    {
        get => isWindowOpen;
        set => SetProperty(ref isWindowOpen, value);
    }

    public bool IsWindowActive
    {
        get => isWindowActive;
        set => SetProperty(ref isWindowActive, value);
    }

    public string StatusText
    {
        get => statusText;
        set => SetProperty(ref statusText, value);
    }

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

    public bool IsProtected
    {
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

    public bool SendEnabled
    {
        get => canSend;
        set
        {
            if (SetProperty(ref canSend, value))
            {
                OnPropertyChanged(nameof(CanSend));
            }
        }
    }

    public bool CanSend => IsOpen && IsProtected && SendEnabled;

    public string PeerTitle => string.IsNullOrWhiteSpace(PeerAddress)
        ? PeerDisplayName
        : $"{PeerDisplayName} · {PeerAddress}";

    public string ProtectionText => IsProtected
        ? "系统截图/录屏将显示黑屏或排除该窗口"
        : "当前系统未启用截图保护，已禁止发送";

    public void RefreshPeerTitle()
    {
        OnPropertyChanged(nameof(PeerTitle));
    }
}

public sealed class LanTransferItem
{
    public string RelativePath { get; set; }

    public string Name { get; set; }

    public bool IsDirectory { get; set; }

    public long Length { get; set; }
}

public sealed class LanTransferRequest : LanTransferBindableBase
{
    private string statusText;
    private string saveDirectory;

    public string TransferId { get; set; }

    public string SenderDeviceId { get; set; }

    public string SenderDisplayName { get; set; }

    public string SenderMachineName { get; set; }

    public string SenderAddress { get; set; }

    public int SenderPort { get; set; }

    public List<LanTransferItem> Items { get; set; } = new List<LanTransferItem>();

    public List<string> TopLevelNames { get; set; } = new List<string>();

    public long TotalBytes { get; set; }

    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;

    public string StatusText
    {
        get => statusText;
        set => SetProperty(ref statusText, value);
    }

    public string SaveDirectory
    {
        get => saveDirectory;
        set => SetProperty(ref saveDirectory, value);
    }

    public int ItemCount => Items?.Count ?? 0;

    public string SenderLabel => string.IsNullOrWhiteSpace(SenderMachineName)
        ? (SenderDisplayName ?? "未知发送者")
        : $"{SenderDisplayName} ({SenderMachineName})";

    public string TopLevelSummary => (TopLevelNames == null) || (TopLevelNames.Count == 0)
        ? "-"
        : string.Join("、", TopLevelNames);

    public string ReceivedAtText => ReceivedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed class LanTransferRecord
{
    public string TransferId { get; set; }

    public string Direction { get; set; }

    public string PeerDisplayName { get; set; }

    public string PeerAddress { get; set; }

    public int ItemCount { get; set; }

    public long TotalBytes { get; set; }

    public string Status { get; set; }

    public string Summary { get; set; }

    public string TargetPath { get; set; }

    public string Detail { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public string DirectionText => string.Equals(Direction, "Receive", StringComparison.OrdinalIgnoreCase) ? "接收" : "发送";

    public string StatusText => string.IsNullOrWhiteSpace(Status) ? "未知" : Status;

    public string CompletedAtText => (CompletedAtUtc ?? StartedAtUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public string DirectionSummaryText => string.IsNullOrWhiteSpace(Summary)
        ? DirectionText
        : $"{DirectionText} · {Summary}";

    public string PeerSummaryText => string.IsNullOrWhiteSpace(PeerAddress)
        ? (PeerDisplayName ?? "-")
        : $"{PeerDisplayName} · {PeerAddress}";

    public string StatusTimeText => $"{StatusText} · {CompletedAtText}";
}

public sealed class LanTransferSession : LanTransferBindableBase
{
    private string statusText;
    private long bytesTransferred;
    private long totalBytes;
    private bool canCancel;

    public string TransferId { get; set; }

    public string Direction { get; set; }

    public string PeerDisplayName { get; set; }

    public string Summary { get; set; }

    public string StatusText
    {
        get => statusText;
        set => SetProperty(ref statusText, value);
    }

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

    public bool CanCancel
    {
        get => canCancel;
        set => SetProperty(ref canCancel, value);
    }

    public string DirectionText => string.Equals(Direction, "Receive", StringComparison.OrdinalIgnoreCase) ? "接收中" : "发送中";

    public string ProgressText => LanTransferFormatting.FormatSize(BytesTransferred) + " / " + LanTransferFormatting.FormatSize(TotalBytes);
}

public static class LanTransferFormatting
{
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
