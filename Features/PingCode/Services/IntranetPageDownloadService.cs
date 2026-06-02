using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public async Task<List<DownloadedPage>> DownloadPagesAsync(
        List<PingCodePromptLink> links, string outputDirectory)
    {
        if (links == null || links.Count == 0)
        {
            return new List<DownloadedPage>();
        }

        var intranetLinks = links
            .Where(l => !string.IsNullOrWhiteSpace(l?.Url) && IsIntranetUrl(l.Url))
            .ToList();
        if (intranetLinks.Count == 0)
        {
            return new List<DownloadedPage>();
        }

        Directory.CreateDirectory(outputDirectory);
        var results = new List<DownloadedPage>();
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
                    var fileName = $"page_{pageIndex:D3}.html";
                    var localPath = Path.Combine(outputDirectory, fileName);
                    var page = new DownloadedPage
                    {
                        OriginalUrl = link.Url,
                        LocalPath = localPath,
                        FileName = fileName,
                        SourceContext = sourceContext,
                    };
                    page.Title = ExtractTitle(html);
                    html = await EmbedImagesAsBase64Async(http, html, link.Url);
                    File.WriteAllText(localPath, html, Encoding.UTF8);
                    page.Success = true;
                    results.Add(page);
                }
            }
            catch (Exception ex)
            {
                results.Add(new DownloadedPage
                {
                    OriginalUrl = link.Url,
                    LocalPath = Path.Combine(outputDirectory, $"page_{pageIndex:D3}.html"),
                    FileName = $"page_{pageIndex:D3}.html",
                    SourceContext = sourceContext,
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

    private async Task<List<DownloadedPage>> DownloadAxurePrototypeAsync(
        HttpClient http, string startUrl, string startHtml, string outputDirectory, string sourceContext, int startIndex)
    {
        var results = new List<DownloadedPage>();
        var baseUrl = startUrl;
        var lastSlash = startUrl.LastIndexOf('/');
        if (lastSlash > 0)
        {
            baseUrl = startUrl.Substring(0, lastSlash + 1);
        }

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
            var fallbackPage = new DownloadedPage
            {
                OriginalUrl = startUrl,
                LocalPath = Path.Combine(outputDirectory, $"page_{startIndex:D3}.html"),
                FileName = $"page_{startIndex:D3}.html",
                SourceContext = sourceContext,
                Title = ExtractTitle(startHtml),
                Success = false,
                Error = "Axure 原型：无法下载 data/document.js",
            };
            results.Add(fallbackPage);
            return results;
        }

        var subPages = ExtractAxureSubPages(documentJs);
        if (subPages.Count == 0)
        {
            var fallbackPage = new DownloadedPage
            {
                OriginalUrl = startUrl,
                LocalPath = Path.Combine(outputDirectory, $"page_{startIndex:D3}.html"),
                FileName = $"page_{startIndex:D3}.html",
                SourceContext = sourceContext,
                Title = ExtractTitle(startHtml),
                Success = false,
                Error = "Axure 原型：未从 document.js 中解析到子页面",
            };
            results.Add(fallbackPage);
            return results;
        }

        var pagesToDownload = subPages.Count > MaxAxureSubPages ? subPages.GetRange(0, MaxAxureSubPages) : subPages;
        var index = startIndex;
        foreach (var sub in pagesToDownload)
        {
            var subUrl = baseUrl + sub.Url;
            var fileName = $"page_{index:D3}.html";
            var localPath = Path.Combine(outputDirectory, fileName);
            var page = new DownloadedPage
            {
                OriginalUrl = subUrl,
                LocalPath = localPath,
                FileName = fileName,
                SourceContext = $"{sourceContext} - Axure: {sub.Name}",
                Title = sub.Name,
            };

            try
            {
                var subResponse = await http.GetAsync(subUrl);
                subResponse.EnsureSuccessStatusCode();
                var subHtml = await subResponse.Content.ReadAsStringAsync();
                subHtml = await EmbedImagesAsBase64Async(http, subHtml, subUrl);
                File.WriteAllText(localPath, subHtml, Encoding.UTF8);
                page.Success = true;
            }
            catch (Exception ex)
            {
                page.Success = false;
                page.Error = ex.Message;
            }

            results.Add(page);
            index++;
        }

        return results;
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

        return pages;
    }

    private static string ExtractTitle(string html)
    {
        var match = TitleRegex.Match(html);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
    }

    private static async Task<string> EmbedImagesAsBase64Async(HttpClient http, string html, string pageUrl)
    {
        var replacements = new Dictionary<string, string>();

        foreach (Match match in ImgSrcRegex.Matches(html))
        {
            var src = match.Groups[2].Value;
            if (!src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                await TryDownloadImageAsync(http, src, pageUrl, replacements);
            }
        }

        foreach (Match match in CssUrlRegex.Matches(html))
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

    private class AxureSubPage
    {
        public string Name { get; set; }

        public string Url { get; set; }
    }
}
