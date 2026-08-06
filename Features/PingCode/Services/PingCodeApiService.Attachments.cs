namespace PackageManager.Services.PingCode;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

/// <summary>
/// PingCode 开放接口客户端的附件处理部分。
/// </summary>
public partial class PingCodeApiService
{
    private async Task<string> BuildAttachmentsHtmlAsync(JToken v)
    {
        try
        {
            var arr = v?["attachments"] as JArray;
            if ((arr == null) || (arr.Count == 0))
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var a in arr)
            {
                var url = ExtractString(a?["url"]);
                var title = FirstNonEmpty(ExtractString(a?["title"]), ExtractString(a?["name"]), ExtractString(a?["filename"]));
                var type = FirstNonEmpty(ExtractString(a?["type"]), ExtractString(a?["content_type"]), ExtractString(a?["file_type"]));
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var tt = string.IsNullOrWhiteSpace(title) ? url : title;
                var typeLower = (type ?? "").Trim().ToLowerInvariant();
                var nameLower = (tt ?? "").Trim().ToLowerInvariant();
                var extImg = nameLower.EndsWith(".png") || nameLower.EndsWith(".jpg") || nameLower.EndsWith(".jpeg") || nameLower.EndsWith(".gif") ||
                             nameLower.EndsWith(".bmp") || nameLower.EndsWith(".webp") || nameLower.EndsWith(".svg") || nameLower.EndsWith(".tif") ||
                             nameLower.EndsWith(".tiff") || nameLower.EndsWith(".avif");

                var isOpenAttachment = false;
                string finalUrl = null;
                bool isImg = false;

                try
                {
                    var uri = new Uri(url);
                    var host = (uri.Host ?? "").ToLowerInvariant();
                    var path = (uri.AbsolutePath ?? "").ToLowerInvariant();
                    isOpenAttachment = host.EndsWith(".pingcode.com") && path.Contains("/v1/attachments");
                }
                catch
                {
                }

                if (isOpenAttachment)
                {
                    try
                    {
                        var meta = await GetJsonAsync(AppendAccessTokenIfNeeded(url));
                        var fileType = FirstNonEmpty(meta.Value<string>("file_type"), type);
                        var dl = meta.Value<string>("download_url");
                        var ftLower = (fileType ?? "").Trim().ToLowerInvariant();
                        isImg = (ftLower == "image") || ftLower.StartsWith("image/");
                        if (isImg && !string.IsNullOrWhiteSpace(dl))
                        {
                            finalUrl = dl;
                        }
                    }
                    catch
                    {
                    }
                }

                if (string.IsNullOrWhiteSpace(finalUrl))
                {
                    var u = AppendAccessTokenIfNeeded(url);
                    isImg = (!string.IsNullOrWhiteSpace(typeLower) && typeLower.StartsWith("image/")) || extImg || LooksLikeImageUrl(u);
                    finalUrl = u;
                }

                if (isImg)
                {
                    sb.Append($"<div class=\"comment-attachment\"><img loading=\"lazy\" src=\"{WebUtility.HtmlEncode(finalUrl)}\" alt=\"{WebUtility.HtmlEncode(tt)}\"/></div>");
                }
                else
                {
                    sb.Append($"<div class=\"comment-attachment\"><a href=\"{WebUtility.HtmlEncode(finalUrl)}\" target=\"_blank\" rel=\"noopener\">{WebUtility.HtmlEncode(tt)}</a></div>");
                }
            }

            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    private string AppendAccessTokenIfNeeded(string url)
    {
        try
        {
            var u = (url ?? "").Trim();
            if (string.IsNullOrWhiteSpace(u))
            {
                return u;
            }

            var lower = u.ToLowerInvariant();
            var need = lower.Contains("pingcode.com") || lower.Contains(".pingcode.com");
            if (!need)
            {
                return u;
            }

            if (lower.Contains("access_token="))
            {
                return u;
            }

            var tk = token;
            if (string.IsNullOrWhiteSpace(tk))
            {
                return u;
            }

            if (u.Contains("?"))
            {
                return $"{u}&access_token={Uri.EscapeDataString(tk)}";
            }

            return $"{u}?access_token={Uri.EscapeDataString(tk)}";
        }
        catch
        {
            return url;
        }
    }

    private static string TryExtractAttachmentIdFromUrl(string url)
    {
        try
        {
            var u = (url ?? "").Trim();
            if (string.IsNullOrWhiteSpace(u))
            {
                return null;
            }
            Uri uri;
            if (!Uri.TryCreate(u, UriKind.Absolute, out uri))
            {
                return null;
            }
            var path = (uri.AbsolutePath ?? "").ToLowerInvariant();
            var idx = path.IndexOf("/v1/attachments/");
            if (idx >= 0)
            {
                var start = idx + "/v1/attachments/".Length;
                if (start < path.Length)
                {
                    var rest = path.Substring(start);
                    var slash = rest.IndexOf('/');
                    var id = (slash >= 0) ? rest.Substring(0, slash) : rest;
                    id = (id ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        return id;
                    }
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string GuessAttachmentType(string url)
    {
        try
        {
            var u = (url ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(u))
            {
                return "file";
            }
            if (u.EndsWith(".png") || u.EndsWith(".jpg") || u.EndsWith(".jpeg") || u.EndsWith(".gif") ||
                u.EndsWith(".bmp") || u.EndsWith(".webp") || u.EndsWith(".svg") || u.EndsWith(".tif") ||
                u.EndsWith(".tiff") || u.Contains("content_type=image") || u.Contains("file_type=image"))
            {
                return "image";
            }
            return "file";
        }
        catch
        {
            return "file";
        }
    }

    private static bool LooksLikeImageUrl(string url)
    {
        try
        {
            var u = (url ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(u))
            {
                return false;
            }

            if (u.StartsWith("data:image/"))
            {
                return true;
            }

            if (u.EndsWith(".png") || u.EndsWith(".jpg") || u.EndsWith(".jpeg") || u.EndsWith(".gif") || u.EndsWith(".bmp") ||
                u.EndsWith(".webp") || u.EndsWith(".svg"))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 上传附件到 PingCode（POST /v1/attachments）。传入 <paramref name="workItemId"/> 时关联到工作项，
    /// 便于后续在描述/示意图中引用其公开图片地址。
    /// </summary>
    /// <param name="data">文件二进制内容。</param>
    /// <param name="fileName">文件名（含扩展名），为空时按时间戳生成 png 名。</param>
    /// <param name="contentType">MIME 类型，如 image/png；为空则不设置 Content-Type。</param>
    /// <param name="workItemId">关联的工作项标识，传入时附加 principal_type=work_item。</param>
    /// <param name="commentId">关联的评论标识。</param>
    /// <returns>上传响应 JSON（含图片公开地址等字段），失败或异常返回 null。</returns>
    public async Task<JObject> UploadAttachmentViaApiAsync(byte[] data, string fileName, string contentType, string workItemId = null, string commentId = null)
    {
        try
        {
            if ((data == null) || (data.Length == 0))
            {
                return null;
            }

            await EnsureTokenAsync();
            var url = "https://open.pingcode.com/v1/attachments";
            var qs = new List<string>();
            if (!string.IsNullOrWhiteSpace(workItemId))
            {
                qs.Add("principal_type=work_item");
                qs.Add($"principal_id={Uri.EscapeDataString(workItemId)}");
            }

            if (!string.IsNullOrWhiteSpace(commentId))
            {
                qs.Add($"comment_id={Uri.EscapeDataString(commentId)}");
            }

            if (qs.Count > 0)
            {
                url = $"{url}?{string.Join("&", qs)}";
            }

            var req = new HttpRequestMessage(HttpMethod.Post, url);
            var mp = new MultipartFormDataContent();
            var fc = new ByteArrayContent(data);
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                fc.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            }

            var name = string.IsNullOrWhiteSpace(fileName) ? $"image_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png" : fileName;
            mp.Add(fc, "file", name);
            req.Content = mp;
            if (!string.IsNullOrWhiteSpace(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var resp = await http.SendAsync(req);
            var txt = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            try
            {
                return string.IsNullOrWhiteSpace(txt) ? new JObject() : JObject.Parse(txt);
            }
            catch
            {
                return new JObject();
            }
        }
        catch
        {
            return null;
        }
    }
}
