using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PackageManager.Services
{
    /// <summary>
    /// 密语信箱信封：经端到端加密的消息或回执，投递到 FTP 信箱中转。服务器只能看到密文与收发设备号。
    /// </summary>
    public sealed class SecretMailboxEnvelope
    {
        /// <summary>获取或设置信封种类：message 或 receipt。</summary>
        public string Kind { get; set; }

        /// <summary>获取或设置信箱文件名覆盖；回执类使用 {messageId}.{receipt}.sec，避免不同回执互相覆盖。</summary>
        public string FileName { get; set; }

        /// <summary>获取或设置已读回执附带的自毁时长（秒），供发送方按剩余时间同步倒计时。</summary>
        public int? CountdownSeconds { get; set; }

        /// <summary>获取或设置消息唯一标识。</summary>
        public string MessageId { get; set; }

        /// <summary>获取或设置线路层会话标识。</summary>
        public string SessionId { get; set; }

        /// <summary>获取或设置发送方设备标识。</summary>
        public string FromDeviceId { get; set; }

        /// <summary>获取或设置发送方显示名称。</summary>
        public string FromDisplayName { get; set; }

        /// <summary>获取或设置发送方机器名。</summary>
        public string FromMachineName { get; set; }

        /// <summary>获取或设置发送方 RSA 公钥（XML），供接收方回信加密。</summary>
        public string SenderPublicKey { get; set; }

        /// <summary>获取或设置回执内容（read/destroy），仅 Kind=receipt 时有值。</summary>
        public string Receipt { get; set; }

        /// <summary>获取或设置投递时间（UTC），用于 TTL 过期判断。</summary>
        public DateTime PostedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>获取或设置 AES 密文（Base64）。</summary>
        public string CipherText { get; set; }

        /// <summary>获取或设置 RSA 加密的 AES 密钥（Base64）。</summary>
        public string EncryptedKey { get; set; }

        /// <summary>获取或设置 AES 初始化向量（Base64）。</summary>
        public string Iv { get; set; }

        /// <summary>获取或设置 HMAC 校验值（Base64）。</summary>
        public string Hmac { get; set; }
    }

    /// <summary>
    /// 密语 FTP 信箱客户端：在更新服务器 UpdateSummary 目录下维护公钥目录与按设备分隔的密文信箱，
    /// 支持投递、拉取（即删）、公钥发布与 TTL 过期清理。所有操作失败静默记日志，不影响直连密语。
    /// </summary>
    public sealed class SecretMailboxClient
    {
        /// <summary>信箱根目录（用户指定挂在 UpdateSummary 下）。</summary>
        public const string MailboxBaseUrl = "ftp://192.168.0.215/UpdateSummary/SecretChat/";

        private static readonly TimeSpan EnvelopeTtl = TimeSpan.FromHours(24);
        private readonly string keyCacheFolder;

        /// <summary>
        /// 初始化 <see cref="SecretMailboxClient"/> 并准备本地公钥缓存目录。
        /// </summary>
        public SecretMailboxClient()
        {
            try
            {
                keyCacheFolder = Path.Combine(
                    (ServiceLocator.Resolve<DataPersistenceService>() ?? new DataPersistenceService()).GetDataFolderPath(),
                    "secret-chat-keys");
                Directory.CreateDirectory(keyCacheFolder);
            }
            catch
            {
                keyCacheFolder = null;
            }
        }

        // 信箱读操作复用写凭据：默认读账号（hwclient）已被服务器停用（530 未登录），
        // 写账号（hwuser）实测可列可读可写，且信箱目录本就由写账号创建。
        private static NetworkCredential ReadCredential => ServiceLocator.Resolve<CredentialStore>()?.GetFtpWriteCredential();
        private static NetworkCredential WriteCredential => ServiceLocator.Resolve<CredentialStore>()?.GetFtpWriteCredential();

        private static string KeysDir => MailboxBaseUrl.TrimEnd('/') + "/keys/";

        private static string BoxDir(string deviceId) => MailboxBaseUrl.TrimEnd('/') + "/boxes/" + deviceId + "/";

        /// <summary>
        /// 确保信箱目录结构存在（keys/ 与 boxes/）。
        /// </summary>
        /// <returns>异步任务。</returns>
        public async Task EnsureDirectoriesAsync()
        {
            await MakeDirectoryAsync(MailboxBaseUrl);
            await MakeDirectoryAsync(KeysDir);
            await MakeDirectoryAsync(MailboxBaseUrl.TrimEnd('/') + "/boxes/");
        }

        /// <summary>
        /// 发布本设备公钥到公钥目录（幂等覆盖）。
        /// </summary>
        /// <param name="deviceId">设备标识。</param>
        /// <param name="publicKeyXml">RSA 公钥（XML）。</param>
        /// <returns>发布成功返回 true。</returns>
        public async Task<bool> PublishPublicKeyAsync(string deviceId, string publicKeyXml)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(publicKeyXml))
            {
                return false;
            }

            try
            {
                await EnsureDirectoriesAsync();
                await UploadBytesAsync(new Uri(KeysDir + deviceId + ".pub"), Encoding.UTF8.GetBytes(publicKeyXml));
                CacheKey(deviceId, publicKeyXml);
                return true;
            }
            catch (Exception ex)
            {
                LanTransferLogger.LogError(ex, "密语公钥发布失败");
                return false;
            }
        }

        /// <summary>
        /// 获取指定设备的公钥：优先公钥目录（对方最近启动时发布，最新鲜），目录不可达时回退本地缓存。
        /// </summary>
        /// <param name="deviceId">设备标识。</param>
        /// <returns>RSA 公钥（XML）；取不到返回 null。</returns>
        public async Task<string> GetPublicKeyAsync(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return null;
            }

            try
            {
                var bytes = await DownloadBytesAsync(new Uri(KeysDir + deviceId + ".pub"));
                var key = bytes == null ? null : Encoding.UTF8.GetString(bytes).Trim();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    CacheKey(deviceId, key);
                    return key;
                }
            }
            catch
            {
                // 目录不可达：回退本地缓存
            }

            return LoadCachedKey(deviceId);
        }

        /// <summary>
        /// 向指定设备的信箱投递密文信封。
        /// </summary>
        /// <param name="toDeviceId">收件设备标识。</param>
        /// <param name="envelope">密文信封。</param>
        /// <returns>投递成功返回 true。</returns>
        public async Task<bool> PostEnvelopeAsync(string toDeviceId, SecretMailboxEnvelope envelope)
        {
            if (string.IsNullOrWhiteSpace(toDeviceId) || envelope == null || string.IsNullOrWhiteSpace(envelope.MessageId))
            {
                return false;
            }

            try
            {
                await MakeDirectoryAsync(BoxDir(toDeviceId));
                var json = JsonConvert.SerializeObject(envelope);
                var fileName = string.IsNullOrWhiteSpace(envelope.FileName)
                    ? Uri.EscapeDataString(envelope.MessageId) + ".sec"
                    : envelope.FileName;
                await UploadBytesAsync(new Uri(BoxDir(toDeviceId) + fileName), Encoding.UTF8.GetBytes(json));
                return true;
            }
            catch (Exception ex)
            {
                LanTransferLogger.LogError(ex, $"密语信箱投递失败：{SafeId(toDeviceId)}");
                return false;
            }
        }

        /// <summary>
        /// 拉取本设备信箱中的全部信封：下载后即删；超过 TTL 的信封直接清理不投递。
        /// </summary>
        /// <param name="deviceId">本机设备标识。</param>
        /// <returns>拉取到的有效信封列表；信箱不可达时返回空列表。</returns>
        public async Task<List<SecretMailboxEnvelope>> PullEnvelopesAsync(string deviceId)
        {
            var result = new List<SecretMailboxEnvelope>();
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return result;
            }

            try
            {
                var files = await ListFilesAsync(BoxDir(deviceId));
                foreach (var file in files.Where(f => f.EndsWith(".sec", StringComparison.OrdinalIgnoreCase)))
                {
                    var uri = new Uri(BoxDir(deviceId) + file);
                    SecretMailboxEnvelope envelope = null;
                    try
                    {
                        var bytes = await DownloadBytesAsync(uri);
                        envelope = bytes == null
                            ? null
                            : JsonConvert.DeserializeObject<SecretMailboxEnvelope>(Encoding.UTF8.GetString(bytes));
                    }
                    catch
                    {
                        envelope = null;
                    }
                    finally
                    {
                        await TryDeleteAsync(uri);
                    }

                    if (envelope == null)
                    {
                        continue;
                    }

                    if (DateTime.UtcNow - envelope.PostedAtUtc > EnvelopeTtl)
                    {
                        continue;
                    }

                    result.Add(envelope);
                }
            }
            catch (Exception ex)
            {
                LanTransferLogger.LogError(ex, $"密语信箱拉取失败：{SafeId(deviceId)}");
            }

            return result;
        }

        private static string SafeId(string deviceId)
        {
            return string.IsNullOrWhiteSpace(deviceId) ? "<empty>" : deviceId.Substring(0, Math.Min(8, deviceId.Length));
        }

        private void CacheKey(string deviceId, string key)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(keyCacheFolder) || string.IsNullOrWhiteSpace(key))
                {
                    return;
                }

                File.WriteAllText(Path.Combine(keyCacheFolder, deviceId + ".pub"), key);
            }
            catch
            {
                // 缓存失败不影响功能
            }
        }

        private string LoadCachedKey(string deviceId)
        {
            try
            {
                var path = Path.Combine(keyCacheFolder ?? string.Empty, deviceId + ".pub");
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task MakeDirectoryAsync(string remoteDir)
        {
            var uri = new Uri(remoteDir);
            var segments = uri.AbsolutePath.Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var current = uri.Scheme + "://" + uri.Host + ":" + uri.Port + "/";
            foreach (var segment in segments)
            {
                current += segment + "/";
                var req = (FtpWebRequest)WebRequest.Create(current);
                req.Credentials = WriteCredential;
                req.Method = WebRequestMethods.Ftp.MakeDirectory;
                req.UseBinary = true;
                req.KeepAlive = false;
                try
                {
                    using var resp = (FtpWebResponse)await req.GetResponseAsync();
                }
                catch (WebException ex)
                {
                    var resp = ex.Response as FtpWebResponse;
                    if (resp == null || resp.StatusCode != FtpStatusCode.ActionNotTakenFileUnavailable)
                    {
                        throw;
                    }
                }
            }
        }

        private static async Task UploadBytesAsync(Uri uri, byte[] bytes)
        {
            var req = (FtpWebRequest)WebRequest.Create(uri);
            req.Credentials = WriteCredential;
            req.Method = WebRequestMethods.Ftp.UploadFile;
            req.UseBinary = true;
            req.KeepAlive = false;
            using (var stream = await req.GetRequestStreamAsync())
            {
                await stream.WriteAsync(bytes, 0, bytes.Length);
            }

            using (await req.GetResponseAsync())
            {
            }
        }

        private static async Task<byte[]> DownloadBytesAsync(Uri uri)
        {
            var req = (FtpWebRequest)WebRequest.Create(uri);
            req.Credentials = ReadCredential;
            req.Method = WebRequestMethods.Ftp.DownloadFile;
            req.UseBinary = true;
            req.KeepAlive = false;
            using var resp = (FtpWebResponse)await req.GetResponseAsync();
            using var stream = resp.GetResponseStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            return memory.ToArray();
        }

        private static async Task<List<string>> ListFilesAsync(string remoteDir)
        {
            var files = new List<string>();
            try
            {
                var req = (FtpWebRequest)WebRequest.Create(remoteDir);
                req.Credentials = ReadCredential;
                req.Method = WebRequestMethods.Ftp.ListDirectory;
                req.UseBinary = true;
                req.KeepAlive = false;
                using var resp = (FtpWebResponse)await req.GetResponseAsync();
                using var stream = resp.GetResponseStream();
                using var reader = new StreamReader(stream);
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    var name = line.Trim();
                    if (!string.IsNullOrEmpty(name) && name != "." && name != "..")
                    {
                        files.Add(name);
                    }
                }
            }
            catch (WebException ex)
            {
                var resp = ex.Response as FtpWebResponse;
                if (resp == null || resp.StatusCode != FtpStatusCode.ActionNotTakenFileUnavailable)
                {
                    throw;
                }

                // 信箱目录尚不存在（从未收到过投递），视为空箱
            }

            return files;
        }

        private static async Task TryDeleteAsync(Uri uri)
        {
            try
            {
                var req = (FtpWebRequest)WebRequest.Create(uri);
                req.Credentials = WriteCredential;
                req.Method = WebRequestMethods.Ftp.DeleteFile;
                req.UseBinary = true;
                req.KeepAlive = false;
                using var resp = (FtpWebResponse)await req.GetResponseAsync();
            }
            catch
            {
                // 删除失败：下次拉取会重试清理
            }
        }
    }
}
