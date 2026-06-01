using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PackageManager.Features.PingCode.Models;
using PackageManager.Services.PingCode.Model;

namespace PackageManager.Features.PingCode.Services;

public class PingCodeImageDownloadService
{
    private static readonly Regex ImgTagRegex = new Regex("<img\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SrcAttrRegex = new Regex("\\bsrc\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OriginUrlAttrRegex = new Regex("\\boriginUrl\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<List<DownloadedImage>> DownloadImagesAsync(
        WorkItemDetails details, string accessToken, string outputDirectory)
    {
        if (details == null)
        {
            return new List<DownloadedImage>();
        }

        var imageUrls = new List<(string Url, string Context)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ExtractImageUrls(imageUrls, seen, details.DescriptionHtml, "工作项描述");
        ExtractImageUrls(imageUrls, seen, details.SketchHtml, "示意图");
        if (details.Comments != null)
        {
            foreach (var comment in details.Comments)
            {
                if (comment == null) continue;
                var author = string.IsNullOrWhiteSpace(comment.AuthorName) ? "评论" : $"评论-{comment.AuthorName}";
                ExtractImageUrls(imageUrls, seen, comment.ContentHtml, author);
            }
        }

        if (imageUrls.Count == 0)
        {
            return new List<DownloadedImage>();
        }

        Directory.CreateDirectory(outputDirectory);
        return await DownloadAllAsync(imageUrls, details.PublicImageToken, accessToken, outputDirectory);
    }

    private async Task<List<DownloadedImage>> DownloadAllAsync(
        List<(string Url, string Context)> imageUrls, string publicImageToken, string accessToken, string outputDirectory)
    {
        var results = new List<DownloadedImage>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var index = 0;
        foreach (var (url, context) in imageUrls)
        {
            index++;
            var ext = GuessExtension(url);
            var fileName = $"img_{index:D3}{ext}";
            var localPath = Path.Combine(outputDirectory, fileName);
            var image = new DownloadedImage
            {
                OriginalUrl = url,
                LocalPath = localPath,
                FileName = fileName,
                SourceContext = context,
            };
            try
            {
                var authenticatedUrl = AppendTokens(url, publicImageToken, accessToken);
                var response = await http.GetAsync(authenticatedUrl);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync();
                File.WriteAllBytes(localPath, bytes);
                image.Success = true;
            }
            catch (Exception ex)
            {
                image.Success = false;
                image.Error = ex.Message;
            }

            results.Add(image);
        }

        return results;
    }

    private static void ExtractImageUrls(List<(string Url, string Context)> results, HashSet<string> seen, string html, string context)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return;
        }

        foreach (Match match in ImgTagRegex.Matches(html))
        {
            var tag = match.Value;
            var originUrl = OriginUrlAttrRegex.Match(tag);
            var src = SrcAttrRegex.Match(tag);
            var url = src.Success ? src.Groups[1].Value : (originUrl.Success ? originUrl.Groups[1].Value : null);
            url = WebUtility.HtmlDecode(url ?? "").Trim();
            if (string.IsNullOrWhiteSpace(url) || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(url))
            {
                results.Add((url, context));
            }
        }
    }

    private static string AppendTokens(string url, string publicImageToken, string accessToken)
    {
        var result = url;
        var lower = result.ToLowerInvariant();
        if ((lower.Contains("atlas.pingcode.com") || lower.Contains("/files/public/")) &&
            !lower.Contains("token=") && !string.IsNullOrWhiteSpace(publicImageToken))
        {
            result = result.Contains("?")
                ? $"{result}&token={Uri.EscapeDataString(publicImageToken)}"
                : $"{result}?token={Uri.EscapeDataString(publicImageToken)}";
        }

        lower = result.ToLowerInvariant();
        if (lower.Contains("pingcode.com") && !lower.Contains("access_token=") && !string.IsNullOrWhiteSpace(accessToken))
        {
            result = result.Contains("?")
                ? $"{result}&access_token={Uri.EscapeDataString(accessToken)}"
                : $"{result}?access_token={Uri.EscapeDataString(accessToken)}";
        }

        return result;
    }

    private static string GuessExtension(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath.ToLowerInvariant();
            if (path.EndsWith(".png")) return ".png";
            if (path.EndsWith(".jpg") || path.EndsWith(".jpeg")) return ".jpg";
            if (path.EndsWith(".gif")) return ".gif";
            if (path.EndsWith(".webp")) return ".webp";
            if (path.EndsWith(".bmp")) return ".bmp";
            if (path.EndsWith(".svg")) return ".svg";
        }
        catch
        {
        }

        return ".png";
    }
}
