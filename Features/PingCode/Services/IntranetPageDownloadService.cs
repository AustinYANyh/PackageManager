using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PackageManager.Features.PingCode.Models;

namespace PackageManager.Features.PingCode.Services;

public class IntranetPageDownloadService
{
    private static readonly Regex ImgSrcRegex = new Regex(
        "(<img\\b[^>]*?\\bsrc\\s*=\\s*[\"'])([^\"']+)([\"'])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ImgSrcValueRegex = new Regex(
        "<img\\b[^>]*?\\bsrc\\s*=\\s*[\"'](?<src>[^\"']+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CssUrlRegex = new Regex(
        @"(url\s*\(\s*[""']?)([^""')]+\.(png|jpg|jpeg|gif|bmp|webp|svg))([""']?\s*\))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TitleRegex = new Regex(
        "<title[^>]*>([^<]+)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AxureScriptRegex = new Regex(
        @"<script[^>]+src\s*=\s*[""']data/document\.js[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AxurePageUrlRegex = new Regex(
        @"[""']?url[""']?\s*:\s*[""']([^""']+\.html)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AxurePageNameRegex = new Regex(
        @"[""']?pageName[""']?\s*:\s*[""']([^""']+)[""']\s*,\s*[""']?type[""']?\s*:\s*[""'][^""']*[""']\s*,\s*[""']?url[""']?\s*:\s*[""']([^""']+\.html)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JsStringAssignmentRegex = new Regex(
        @"\b[A-Za-z_$][\w$]*\s*=\s*""((?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    private const int MaxAxureSubPages = 30;

    private static readonly HashSet<string> PublicDomainSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pingcode.com",
        "github.com",
        "gitlab.com",
        "stackoverflow.com",
        "google.com",
        "microsoft.com",
        "nuget.org",
        "npmjs.com",
        "docker.com",
        "mozilla.org",
        "w3.org",
        "wikipedia.org",
    };

    public static bool IsIntranetUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            if (uri.Scheme != "http" && uri.Scheme != "https")
            {
                return false;
            }

            var host = uri.Host.ToLowerInvariant();
            if (host == "localhost" || host == "127.0.0.1")
            {
                return false;
            }

            foreach (var suffix in PublicDomainSuffixes)
            {
                if (host == suffix || host.EndsWith("." + suffix))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<DownloadedIntranetResource>> DownloadPagesAsync(
        List<PingCodePromptLink> links, string outputDirectory)
    {
        if (links == null || links.Count == 0)
        {
            return new List<DownloadedIntranetResource>();
        }

        var intranetLinks = links
            .Where(l => !string.IsNullOrWhiteSpace(l?.Url) && IsIntranetUrl(l.Url))
            .ToList();
        if (intranetLinks.Count == 0)
        {
            return new List<DownloadedIntranetResource>();
        }

        Directory.CreateDirectory(outputDirectory);
        var results = new List<DownloadedIntranetResource>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var pageIndex = 0;
        foreach (var link in intranetLinks)
        {
            pageIndex++;
            var sourceContext = link.Context ?? link.Category ?? "未知来源";

            try
            {
                var response = await http.GetAsync(link.Url);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync();

                if (IsAxurePrototype(html))
                {
                    var axurePages = await DownloadAxurePrototypeAsync(http, link.Url, html, outputDirectory, sourceContext, pageIndex);
                    results.AddRange(axurePages);
                    pageIndex += Math.Max(axurePages.Count - 1, 0);
                }
                else
                {
                    results.AddRange(await SaveHtmlPageAndImagesAsync(http, html, link.Url, outputDirectory, sourceContext, pageIndex, ExtractTitle(html)));
                }
            }
            catch (Exception ex)
            {
                results.Add(new DownloadedIntranetResource
                {
                    OriginalUrl = link.Url,
                    LocalPath = Path.Combine(outputDirectory, $"page_{pageIndex:D3}.html"),
                    FileName = $"page_{pageIndex:D3}.html",
                    SourceContext = sourceContext,
                    ResourceKind = "Html",
                    Success = false,
                    Error = ex.Message,
                });
            }
        }

        return results;
    }

    private static bool IsAxurePrototype(string html)
    {
        return AxureScriptRegex.IsMatch(html) || html.Contains("$axure.document.sitemap");
    }

    private async Task<List<DownloadedIntranetResource>> DownloadAxurePrototypeAsync(
        HttpClient http, string startUrl, string startHtml, string outputDirectory, string sourceContext, int startIndex)
    {
        var results = new List<DownloadedIntranetResource>();
        var baseUrl = GetBaseUrl(startUrl);
        var documentJsUrl = baseUrl + "data/document.js";
        string documentJs;
        try
        {
            var response = await http.GetAsync(documentJsUrl);
            response.EnsureSuccessStatusCode();
            documentJs = await response.Content.ReadAsStringAsync();
        }
        catch
        {
            results.Add(new DownloadedIntranetResource
            {
                OriginalUrl = startUrl,
                LocalPath = Path.Combine(outputDirectory, $"page_{startIndex:D3}.html"),
                FileName = $"page_{startIndex:D3}.html",
                SourceContext = sourceContext,
                Title = ExtractTitle(startHtml),
                ResourceKind = "Html",
                Success = false,
                Error = "Axure 原型：无法下载 data/document.js",
            });
            return results;
        }

        var subPages = ExtractAxureSubPages(documentJs);
        if (subPages.Count == 0)
        {
            results.Add(new DownloadedIntranetResource
            {
                OriginalUrl = startUrl,
                LocalPath = Path.Combine(outputDirectory, $"page_{startIndex:D3}.html"),
                FileName = $"page_{startIndex:D3}.html",
                SourceContext = sourceContext,
                Title = ExtractTitle(startHtml),
                ResourceKind = "Html",
                Success = false,
                Error = "Axure 原型：未从 document.js 中解析到子页面",
            });
            return results;
        }

        var pagesToDownload = SelectAxurePages(startUrl, subPages);
        var index = startIndex;
        foreach (var sub in pagesToDownload)
        {
            var subUrl = baseUrl + Uri.EscapeUriString(sub.Url);
            try
            {
                var subResponse = await http.GetAsync(subUrl);
                subResponse.EnsureSuccessStatusCode();
                var subHtml = await subResponse.Content.ReadAsStringAsync();
                var title = string.IsNullOrWhiteSpace(sub.Name) ? ExtractTitle(subHtml) : sub.Name;
                results.AddRange(await SaveHtmlPageAndImagesAsync(http, subHtml, subUrl, outputDirectory, $"{sourceContext} - Axure: {sub.Name}", index, title));
            }
            catch (Exception ex)
            {
                results.Add(new DownloadedIntranetResource
                {
                    OriginalUrl = subUrl,
                    LocalPath = Path.Combine(outputDirectory, $"page_{index:D3}.html"),
                    FileName = $"page_{index:D3}.html",
                    SourceContext = $"{sourceContext} - Axure: {sub.Name}",
                    Title = sub.Name,
                    ResourceKind = "Html",
                    Success = false,
                    Error = ex.Message,
                });
            }

            index++;
        }

        return results;
    }

    private async Task<List<DownloadedIntranetResource>> SaveHtmlPageAndImagesAsync(
        HttpClient http, string html, string pageUrl, string outputDirectory, string sourceContext, int pageIndex, string title)
    {
        var results = new List<DownloadedIntranetResource>();
        var fileName = $"page_{pageIndex:D3}.html";
        var localPath = Path.Combine(outputDirectory, fileName);
        var imageResources = await DownloadPageImagesAsync(http, html, pageUrl, outputDirectory, sourceContext, pageIndex, title);
        var embeddedHtml = await EmbedImagesAsBase64Async(http, html, pageUrl);
        File.WriteAllText(localPath, embeddedHtml, Encoding.UTF8);

        results.Add(new DownloadedIntranetResource
        {
            OriginalUrl = pageUrl,
            LocalPath = localPath,
            FileName = fileName,
            SourceContext = sourceContext,
            Title = title,
            ResourceKind = "Html",
            Success = true,
        });
        results.AddRange(imageResources);
        return results;
    }

    private async Task<List<DownloadedIntranetResource>> DownloadPageImagesAsync(
        HttpClient http, string html, string pageUrl, string outputDirectory, string sourceContext, int pageIndex, string title)
    {
        var results = new List<DownloadedIntranetResource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imageIndex = 0;
        foreach (var src in ExtractImageSources(html))
        {
            if (string.IsNullOrWhiteSpace(src) ||
                src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(src))
            {
                continue;
            }

            imageIndex++;
            var absoluteUrl = ToAbsoluteUrl(src, pageUrl);
            var ext = GuessExtension(src);
            var baseName = ToSafeFileNamePart(title ?? Path.GetFileNameWithoutExtension(pageUrl), $"page_{pageIndex:D3}");
            var fileName = $"{pageIndex:D3}_{baseName}_img_{imageIndex:D2}{ext}";
            var localPath = Path.Combine(outputDirectory, fileName);
            var resource = new DownloadedIntranetResource
            {
                OriginalUrl = absoluteUrl ?? src,
                LocalPath = localPath,
                FileName = fileName,
                SourceContext = sourceContext,
                Title = title,
                ResourceKind = "Image",
            };

            try
            {
                if (string.IsNullOrWhiteSpace(absoluteUrl))
                {
                    throw new InvalidOperationException("无法解析图片绝对地址");
                }

                var response = await http.GetAsync(absoluteUrl);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync();
                File.WriteAllBytes(localPath, bytes);
                resource.Success = true;
            }
            catch (Exception ex)
            {
                resource.Success = false;
                resource.Error = ex.Message;
            }

            results.Add(resource);
        }

        return results;
    }

    private static IEnumerable<string> ExtractImageSources(string html)
    {
        foreach (Match match in ImgSrcValueRegex.Matches(html ?? string.Empty))
        {
            yield return WebUtility.HtmlDecode(match.Groups["src"].Value ?? "").Trim();
        }

        foreach (Match match in CssUrlRegex.Matches(html ?? string.Empty))
        {
            yield return WebUtility.HtmlDecode(match.Groups[2].Value ?? "").Trim();
        }
    }

    private static List<AxureSubPage> SelectAxurePages(string startUrl, List<AxureSubPage> subPages)
    {
        var targetPage = ExtractAxureTargetPageName(startUrl);
        if (!string.IsNullOrWhiteSpace(targetPage))
        {
            var matched = subPages
                .Where(p => string.Equals(p.Name, targetPage, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Path.GetFileNameWithoutExtension(p.Url), targetPage, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matched.Count > 0)
            {
                return matched;
            }
        }

        return subPages.Count > MaxAxureSubPages ? subPages.GetRange(0, MaxAxureSubPages) : subPages;
    }

    private static string ExtractAxureTargetPageName(string startUrl)
    {
        try
        {
            var hashIndex = startUrl.IndexOf('#');
            if (hashIndex < 0 || hashIndex == startUrl.Length - 1)
            {
                return null;
            }

            var fragment = startUrl.Substring(hashIndex + 1);
            foreach (var part in fragment.Split('&'))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var key = part.Substring(0, eq);
                if (!string.Equals(key, "p", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return Uri.UnescapeDataString(part.Substring(eq + 1)).Trim();
            }
        }
        catch
        {
        }

        return null;
    }

    private static List<AxureSubPage> ExtractAxureSubPages(string documentJs)
    {
        var pages = new List<AxureSubPage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in AxurePageNameRegex.Matches(documentJs))
        {
            var name = match.Groups[1].Value.Trim();
            var url = match.Groups[2].Value.Trim();
            if (!string.IsNullOrWhiteSpace(url) && seen.Add(url))
            {
                pages.Add(new AxureSubPage { Name = name, Url = url });
            }
        }

        if (pages.Count == 0)
        {
            foreach (Match match in AxurePageUrlRegex.Matches(documentJs))
            {
                var url = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(url) && seen.Add(url))
                {
                    pages.Add(new AxureSubPage { Name = Path.GetFileNameWithoutExtension(url), Url = url });
                }
            }
        }

        if (pages.Count == 0)
        {
            foreach (var page in ExtractCompressedAxureSubPages(documentJs))
            {
                if (!string.IsNullOrWhiteSpace(page.Url) && seen.Add(page.Url))
                {
                    pages.Add(page);
                }
            }
        }

        return pages;
    }

    private static IEnumerable<AxureSubPage> ExtractCompressedAxureSubPages(string documentJs)
    {
        var values = JsStringAssignmentRegex.Matches(documentJs ?? string.Empty)
            .Cast<Match>()
            .Select(m => Regex.Unescape(m.Groups[1].Value))
            .ToList();

        for (var i = 0; i < values.Count - 1; i++)
        {
            var name = values[i]?.Trim();
            var next = values[i + 1]?.Trim();
            if (string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(next) ||
                !next.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                IsAxureMetadataString(name))
            {
                continue;
            }

            yield return new AxureSubPage
            {
                Name = name,
                Url = next,
            };
        }
    }

    private static bool IsAxureMetadataString(string value)
    {
        switch ((value ?? string.Empty).Trim())
        {
            case "Wireframe":
            case "Folder":
            case "Image":
            case "Axure:Page":
            case "":
                return true;
            default:
                return false;
        }
    }

    private static string ExtractTitle(string html)
    {
        var match = TitleRegex.Match(html ?? string.Empty);
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
    }

    private static async Task<string> EmbedImagesAsBase64Async(HttpClient http, string html, string pageUrl)
    {
        var replacements = new Dictionary<string, string>();

        foreach (Match match in ImgSrcRegex.Matches(html ?? string.Empty))
        {
            var src = match.Groups[2].Value;
            if (!src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                await TryDownloadImageAsync(http, src, pageUrl, replacements);
            }
        }

        foreach (Match match in CssUrlRegex.Matches(html ?? string.Empty))
        {
            var src = match.Groups[2].Value;
            if (!src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                await TryDownloadImageAsync(http, src, pageUrl, replacements);
            }
        }

        if (replacements.Count == 0)
        {
            return html;
        }

        html = ImgSrcRegex.Replace(html, m =>
        {
            var src = m.Groups[2].Value;
            return replacements.TryGetValue(src, out var dataUri)
                ? m.Groups[1].Value + dataUri + m.Groups[3].Value
                : m.Value;
        });

        html = CssUrlRegex.Replace(html, m =>
        {
            var src = m.Groups[2].Value;
            return replacements.TryGetValue(src, out var dataUri)
                ? m.Groups[1].Value + dataUri + m.Groups[4].Value
                : m.Value;
        });

        return html;
    }

    private static async Task TryDownloadImageAsync(HttpClient http, string src, string pageUrl, Dictionary<string, string> replacements)
    {
        if (replacements.ContainsKey(src))
        {
            return;
        }

        try
        {
            var absoluteUrl = ToAbsoluteUrl(src, pageUrl);
            if (string.IsNullOrWhiteSpace(absoluteUrl))
            {
                return;
            }

            var imgResponse = await http.GetAsync(absoluteUrl);
            if (!imgResponse.IsSuccessStatusCode)
            {
                return;
            }

            var bytes = await imgResponse.Content.ReadAsByteArrayAsync();
            var contentType = imgResponse.Content.Headers.ContentType?.MediaType ?? GuessMediaType(src);
            var base64 = Convert.ToBase64String(bytes);
            replacements[src] = $"data:{contentType};base64,{base64}";
        }
        catch
        {
        }
    }

    private static string GetBaseUrl(string startUrl)
    {
        var urlWithoutFragment = startUrl;
        var hashIndex = urlWithoutFragment.IndexOf('#');
        if (hashIndex >= 0)
        {
            urlWithoutFragment = urlWithoutFragment.Substring(0, hashIndex);
        }

        var lastSlash = urlWithoutFragment.LastIndexOf('/');
        return lastSlash > 0 ? urlWithoutFragment.Substring(0, lastSlash + 1) : urlWithoutFragment;
    }

    private static string ToAbsoluteUrl(string src, string pageUrl)
    {
        try
        {
            if (Uri.TryCreate(src, UriKind.Absolute, out _))
            {
                return src;
            }

            var baseUri = new Uri(pageUrl);
            return new Uri(baseUri, src).AbsoluteUri;
        }
        catch
        {
            return null;
        }
    }

    private static string GuessMediaType(string url)
    {
        var lower = (url ?? "").ToLowerInvariant();
        if (lower.Contains(".png")) return "image/png";
        if (lower.Contains(".gif")) return "image/gif";
        if (lower.Contains(".webp")) return "image/webp";
        if (lower.Contains(".svg")) return "image/svg+xml";
        if (lower.Contains(".bmp")) return "image/bmp";
        return "image/jpeg";
    }

    private static string GuessExtension(string url)
    {
        var lower = (url ?? "").ToLowerInvariant();
        if (lower.Contains(".png")) return ".png";
        if (lower.Contains(".gif")) return ".gif";
        if (lower.Contains(".webp")) return ".webp";
        if (lower.Contains(".svg")) return ".svg";
        if (lower.Contains(".bmp")) return ".bmp";
        if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return ".jpg";
        return ".png";
    }

    private static string ToSafeFileNamePart(string value, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = source.Select(ch => invalidChars.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch).ToArray();
        var safe = new string(chars).Trim('.', '_', ' ');
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }

    private class AxureSubPage
    {
        public string Name { get; set; }

        public string Url { get; set; }
    }
}
