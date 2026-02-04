namespace PackageManager.Services.PingCode;

using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PackageManager.Services.PingCode.Exception;

public partial class PingCodeApiService
{
    private async Task<JObject> GetJsonAsync(string url)
    {
        await EnsureTokenAsync();
        using var resp = await http.GetAsync(url);
        var txt = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ApiAuthException($"GET 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
            }

            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new ApiForbiddenException($"GET 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
            }

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ApiNotFoundException($"GET 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
            }

            throw new InvalidOperationException($"GET 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
        }

        return JObject.Parse(txt);
    }

    private async Task<JObject> PatchJsonAsync(string url, JObject body)
    {
        await EnsureTokenAsync();
        var req = new HttpRequestMessage(new HttpMethod("PATCH"), url);
        var payload = body ?? new JObject();
        req.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req);
        var txt = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ApiAuthException($"PATCH 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
            }

            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new ApiForbiddenException($"PATCH 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
            }

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ApiNotFoundException($"PATCH 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
            }

            throw new InvalidOperationException($"PATCH 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
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

    private async Task<JObject> PostJsonAsync(string url, JObject body)
    {
        await EnsureTokenAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        var payload = body ?? new JObject();
        req.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req);
        var txt = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ApiAuthException($"POST 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
            }
            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new ApiForbiddenException($"POST 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
            }
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ApiNotFoundException($"POST 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
            }
            throw new InvalidOperationException($"POST 失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
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
}
