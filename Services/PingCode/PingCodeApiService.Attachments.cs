namespace PackageManager.Services.PingCode;

using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

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
}
