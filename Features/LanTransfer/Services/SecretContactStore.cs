using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace PackageManager.Services
{
    /// <summary>
    /// 密语联系人（脱机可达）：记录通信过的设备，公钥本地缓存，支持离线投递。
    /// </summary>
    public sealed class SecretContact
    {
        /// <summary>获取或设置对方设备标识。</summary>
        public string DeviceId { get; set; }

        /// <summary>获取或设置对方显示名称。</summary>
        public string DisplayName { get; set; }

        /// <summary>获取或设置对方机器名。</summary>
        public string MachineName { get; set; }

        /// <summary>获取或设置最后一次通信/上线的 UTC 时间。</summary>
        public DateTime LastSeenUtc { get; set; }

        /// <summary>获取或设置对方 RSA 公钥（XML）缓存。</summary>
        public string PublicKeyXml { get; set; }

        /// <summary>获取显示标签。</summary>
        public string DisplayLabel => string.IsNullOrWhiteSpace(MachineName)
            ? (DisplayName ?? "未知同事")
            : $"{DisplayName} ({MachineName})";
    }

    /// <summary>
    /// 密语本地存储：联系人清单明文保存；未销毁的会话消息整体 DPAPI 加密落盘，
    /// 重启后恢复未读/已投递/已送达状态，已销毁消息只保留无内容的占位记录。
    /// </summary>
    public sealed class SecretContactStore
    {
        private readonly string folderPath;
        private readonly object sync = new object();

        /// <summary>
        /// 初始化 <see cref="SecretContactStore"/> 并准备存储目录。
        /// </summary>
        /// <param name="dataService">数据持久化服务，用于定位应用数据目录。</param>
        public SecretContactStore(DataPersistenceService dataService)
        {
            folderPath = Path.Combine(
                (dataService ?? new DataPersistenceService()).GetDataFolderPath(),
                "secret-chat");
            Directory.CreateDirectory(folderPath);
        }

        private string ContactsPath => Path.Combine(folderPath, "contacts.json");

        private string SessionsPath => Path.Combine(folderPath, "sessions.dat");

        /// <summary>
        /// 加载全部联系人。
        /// </summary>
        /// <returns>联系人列表；无记录时返回空列表。</returns>
        public List<SecretContact> LoadContacts()
        {
            lock (sync)
            {
                try
                {
                    return File.Exists(ContactsPath)
                        ? JsonConvert.DeserializeObject<List<SecretContact>>(File.ReadAllText(ContactsPath)) ?? new List<SecretContact>()
                        : new List<SecretContact>();
                }
                catch
                {
                    return new List<SecretContact>();
                }
            }
        }

        /// <summary>
        /// 保存联系人清单（全量覆盖）。
        /// </summary>
        /// <param name="contacts">联系人列表。</param>
        public void SaveContacts(List<SecretContact> contacts)
        {
            lock (sync)
            {
                try
                {
                    File.WriteAllText(ContactsPath, JsonConvert.SerializeObject(contacts ?? new List<SecretContact>()));
                }
                catch
                {
                    // 保存失败不影响运行
                }
            }
        }

        /// <summary>
        /// 保存未销毁的会话消息快照：整体 DPAPI（当前用户）加密后落盘。
        /// </summary>
        /// <param name="snapshot">会话快照 DTO 列表。</param>
        public void SaveSessions(List<SecretSessionSnapshot> snapshot)
        {
            lock (sync)
            {
                try
                {
                    var json = JsonConvert.SerializeObject(snapshot ?? new List<SecretSessionSnapshot>());
                    var protectedText = CredentialProtectionService.Protect(json);
                    File.WriteAllText(SessionsPath, protectedText ?? string.Empty);
                }
                catch
                {
                    // 保存失败不影响运行
                }
            }
        }

        /// <summary>
        /// 加载会话消息快照（DPAPI 解密）。
        /// </summary>
        /// <returns>会话快照列表；无记录或解密失败返回空列表。</returns>
        public List<SecretSessionSnapshot> LoadSessions()
        {
            lock (sync)
            {
                try
                {
                    if (!File.Exists(SessionsPath))
                    {
                        return new List<SecretSessionSnapshot>();
                    }

                    var json = CredentialProtectionService.Unprotect(File.ReadAllText(SessionsPath));
                    return string.IsNullOrWhiteSpace(json)
                        ? new List<SecretSessionSnapshot>()
                        : JsonConvert.DeserializeObject<List<SecretSessionSnapshot>>(json) ?? new List<SecretSessionSnapshot>();
                }
                catch
                {
                    return new List<SecretSessionSnapshot>();
                }
            }
        }
    }

    /// <summary>
    /// 会话持久化快照：只保存未销毁消息的内容与状态；已销毁消息保留空占位。
    /// </summary>
    public sealed class SecretSessionSnapshot
    {
        /// <summary>获取或设置会话标识。</summary>
        public string SessionId { get; set; }

        /// <summary>获取或设置会话键。</summary>
        public string SessionKey { get; set; }

        /// <summary>获取或设置对方设备标识。</summary>
        public string PeerDeviceId { get; set; }

        /// <summary>获取或设置对方显示名称。</summary>
        public string PeerDisplayName { get; set; }

        /// <summary>获取或设置对方地址（可能已失效，仅作展示）。</summary>
        public string PeerAddress { get; set; }

        /// <summary>获取或设置对方公钥缓存。</summary>
        public string PeerPublicKeyXml { get; set; }

        /// <summary>获取或设置是否自测会话。</summary>
        public bool IsSelfTest { get; set; }

        /// <summary>获取或设置消息快照列表。</summary>
        public List<SecretMessageSnapshot> Messages { get; set; } = new List<SecretMessageSnapshot>();
    }

    /// <summary>
    /// 消息持久化快照。
    /// </summary>
    public sealed class SecretMessageSnapshot
    {
        /// <summary>获取或设置消息唯一标识。</summary>
        public string MessageId { get; set; }

        /// <summary>获取或设置线路层会话标识。</summary>
        public string WireSessionId { get; set; }

        /// <summary>获取或设置发送方设备标识（回信路由用）。</summary>
        public string SenderDeviceId { get; set; }

        /// <summary>获取或设置消息方向文本：Incoming/Outgoing。</summary>
        public string Direction { get; set; }

        /// <summary>获取或设置消息状态文本：Sending/Sent/Posted/Unread/Read/Destroyed。</summary>
        public string State { get; set; }

        /// <summary>获取或设置消息内容（已销毁时为空）。</summary>
        public string Text { get; set; }

        /// <summary>获取或设置创建时间（UTC）。</summary>
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>获取或设置已读时间（UTC），可空。</summary>
        public DateTime? ReadAtUtc { get; set; }

        /// <summary>获取或设置自毁总时长（秒）。</summary>
        public int DestroyTotalSeconds { get; set; }

        /// <summary>获取或设置信箱投递的消息是否已被对方拉取。</summary>
        public bool MailboxPulled { get; set; }
    }
}
