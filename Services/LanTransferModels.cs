using System;
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
            }
        }
    }

    public string StatusText
    {
        get => statusText;
        set => SetProperty(ref statusText, value);
    }

    public string DisplayLabel => string.IsNullOrWhiteSpace(MachineName)
        ? (DisplayName ?? "未知设备")
        : $"{DisplayName} ({MachineName})";

    public string EndpointDisplay => string.IsNullOrWhiteSpace(Address) ? "-" : $"{Address}:{ListenPort}";

    public string OnlineText => IsOnline ? "在线" : "离线";

    public string CompatibilityText => IsCompatible ? "兼容" : "版本不兼容";

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
