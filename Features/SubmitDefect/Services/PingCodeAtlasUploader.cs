using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PackageManager.Services;

namespace PackageManager.Features.SubmitDefect.Services
{
    /// <summary>
    /// PingCode 内部 atlas 上传（cookie → upload-token → file/upload → atlas URL）。
    /// 生成的 atlas URL 永久公开，写进 shiyitu 后 PingCode 网页端永久显示（大图/动图/多图均可，无字段限制）。
    /// </summary>
    /// <remarks>
    /// 链路（从 PingCode 网页端插图 F12 复刻）：
    /// ① GET hongwa.pingcode.com/api/typhon/secret/file/upload-token?scope=3&amp;size=xxx（带 Cookie）→ 返回上传 JWT（20 分钟有效）
    /// ② POST atlas.pingcode.com/file/upload?token=JWT&amp;scope=3（multipart: title+file）→ 返回 atlas 文件 id
    /// ③ 拼 atlas.pingcode.com/files/public/{id} → 永久 URL
    /// </remarks>
    public class PingCodeAtlasUploader
    {
        private const string UploadTokenUrl = "https://hongwa.pingcode.com/api/typhon/secret/file/upload-token";
        private const string FileUploadUrl = "https://atlas.pingcode.com/file/upload";

        private readonly string _cookie;

        /// <summary>
        /// 初始化 <see cref="PingCodeAtlasUploader"/> 的新实例。
        /// </summary>
        /// <param name="cookie">PingCode 登录 Cookie（rawCookie 字符串，key=value; key=value 形式）。</param>
        public PingCodeAtlasUploader(string cookie)
        {
            _cookie = cookie ?? string.Empty;
        }

        /// <summary>
        /// 是否已就绪（有 Cookie）。
        /// </summary>
        public bool IsReady => !string.IsNullOrWhiteSpace(_cookie);

        /// <summary>
        /// 上传图片字节，返回 atlas 永久公开 URL。
        /// </summary>
        /// <param name="data">图片字节。</param>
        /// <param name="fileName">文件名（含扩展名）。</param>
        /// <param name="contentType">MIME 类型。</param>
        /// <returns>atlas 永久 URL（atlas.pingcode.com/files/public/{id}）；失败返回 null。</returns>
        public async Task<string> UploadAsync(byte[] data, string fileName, string contentType)
        {
            if ((data == null) || (data.Length == 0) || !IsReady)
            {
                return null;
            }

            var jwt = await GetUploadTokenAsync(data.Length);
            if (string.IsNullOrWhiteSpace(jwt))
            {
                LoggingService.LogWarning("[AtlasUploader] 获取 upload-token 失败（Cookie 可能已过期）");
                return null;
            }

            var fileId = await PostFileUploadAsync(jwt, data, fileName, contentType);
            if (string.IsNullOrWhiteSpace(fileId))
            {
                LoggingService.LogWarning("[AtlasUploader] file/upload 未返回文件 id");
                return null;
            }

            return $"https://atlas.pingcode.com/files/public/{fileId}";
        }

        /// <summary>
        /// 用 Cookie 调 upload-token 获取上传 JWT。
        /// </summary>
        private async Task<string> GetUploadTokenAsync(long size)
        {
            try
            {
                LoggingService.LogInfo($"[AtlasUploader] upload-token 请求：cookie长度={_cookie?.Length ?? 0}, size={size}");
                var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var url = $"{UploadTokenUrl}?scope=3&size={size}&t={t}";

                // 用 HttpWebRequest + CookieContainer.Add（不用手动 Cookie header——.NET FW 4.7 的 HttpWebRequest
                // 手动 Cookie header 会被拦截，curl 能传 C# 传不了，导致 code=401 token not found）
                var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                request.Method = "GET";
                request.Accept = "application/json, text/plain, */*";
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";
                request.Referer = "https://hongwa.pingcode.com/";

                // 用 CookieContainer 管理 cookie（标准方式，确保 cookie 发出）
                request.CookieContainer = new System.Net.CookieContainer();
                if (!string.IsNullOrWhiteSpace(_cookie))
                {
                    var added = 0;
                    foreach (var part in _cookie.Split(';'))
                    {
                        var trimmed = part.Trim();
                        var eq = trimmed.IndexOf('=');
                        if (eq > 0)
                        {
                            var name = trimmed.Substring(0, eq).Trim();
                            var value = trimmed.Substring(eq + 1).Trim();
                            try
                            {
                                request.CookieContainer.Add(new Uri(url), new System.Net.Cookie(name, value));
                                added++;
                            }
                            catch (Exception cex)
                            {
                                LoggingService.LogInfo($"[AtlasUploader] cookie 添加失败 name={name}: {cex.Message}");
                            }
                        }
                    }
                    LoggingService.LogInfo($"[AtlasUploader] CookieContainer 添加 {added} 个 cookie");
                }

                using (var resp = await Task.Factory.FromAsync(request.BeginGetResponse, request.EndGetResponse, null))
                {
                    var httpResp = (System.Net.HttpWebResponse)resp;
                    var txt = new System.IO.StreamReader(resp.GetResponseStream()).ReadToEnd();
                    var preview = txt.Length > 300 ? txt.Substring(0, 300) : txt;
                    LoggingService.LogInfo($"[AtlasUploader] upload-token 响应：HTTP={(int)httpResp.StatusCode}, body={preview}");
                    if (httpResp.StatusCode != System.Net.HttpStatusCode.OK)
                    {
                        LoggingService.LogWarning($"[AtlasUploader] upload-token 失败 HTTP {(int)httpResp.StatusCode}: {preview}");
                        return null;
                    }

                    var jobj = string.IsNullOrWhiteSpace(txt) ? null : JObject.Parse(txt);
                    var value = jobj?["data"]?.Value<string>("value");
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        LoggingService.LogWarning($"[AtlasUploader] upload-token 响应未含 data.value：{preview}");
                    }
                    return value;
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "[AtlasUploader] 获取 upload-token 异常");
                return null;
            }
        }

        /// <summary>
        /// 用 JWT POST atlas/file/upload（multipart title+file），返回 atlas 文件 id。
        /// </summary>
        private async Task<string> PostFileUploadAsync(string jwt, byte[] data, string fileName, string contentType)
        {
            try
            {
                using (var http = new HttpClient())
                {
                    http.DefaultRequestHeaders.ExpectContinue = false;
                    var url = $"{FileUploadUrl}?token={Uri.EscapeDataString(jwt)}&scope=3";
                    var mp = new MultipartFormDataContent();
                    var title = string.IsNullOrWhiteSpace(fileName) ? "image.png" : fileName;
                    mp.Add(new StringContent(title), "title");
                    var fc = new ByteArrayContent(data);
                    if (!string.IsNullOrWhiteSpace(contentType))
                    {
                        fc.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                    }
                    mp.Add(fc, "file", title);

                    var req = new HttpRequestMessage(HttpMethod.Post, url);
                    req.Content = mp;
                    req.Headers.Add("Origin", "https://hongwa.pingcode.com");
                    req.Headers.Add("Referer", "https://hongwa.pingcode.com/");
                    req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36");

                    using (var resp = await http.SendAsync(req))
                    {
                        var txt = await resp.Content.ReadAsStringAsync();
                        if (!resp.IsSuccessStatusCode)
                        {
                            LoggingService.LogWarning($"[AtlasUploader] file/upload HTTP {(int)resp.StatusCode}: {txt}");
                            return null;
                        }

                        // 解析 atlas 文件 id（响应字段名待首次实测确认，多字段兜底）
                        var jobj = string.IsNullOrWhiteSpace(txt) ? null : JObject.Parse(txt);
                        var id = FirstNonEmpty(
                            jobj?.Value<string>("id"),
                            jobj?.Value<string>("_id"),
                            jobj?["data"]?.Value<string>("id"),
                            jobj?["data"]?.Value<string>("_id"),
                            jobj?["value"]?.Value<string>("id"));
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            LoggingService.LogWarning($"[AtlasUploader] file/upload 响应未含 id，原文：{(txt.Length > 300 ? txt.Substring(0, 300) : txt)}");
                        }
                        return id;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "[AtlasUploader] file/upload 异常");
                return null;
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return null;
            }

            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    return v;
                }
            }

            return null;
        }
    }
}
