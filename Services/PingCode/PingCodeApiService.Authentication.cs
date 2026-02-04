namespace PackageManager.Services.PingCode;

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PackageManager.Services.PingCode.Exception;

public partial class PingCodeApiService
{
    private string GetClientId()
    {
        var settings = data.LoadSettings();
        var env = Environment.GetEnvironmentVariable("PINGCODE_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(settings?.PingCodeClientId))
        {
            return settings.PingCodeClientId;
        }

        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        return null;
    }

    private string GetClientSecret()
    {
        var settings = data.LoadSettings();
        var env = Environment.GetEnvironmentVariable("PINGCODE_CLIENT_SECRET");
        if (!string.IsNullOrWhiteSpace(settings?.PingCodeClientSecret))
        {
            return settings.PingCodeClientSecret;
        }

        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        return null;
    }

    private async Task EnsureTokenAsync()
    {
        if (!string.IsNullOrWhiteSpace(token) && (tokenExpiresAt > DateTime.UtcNow.AddMinutes(1)))
        {
            return;
        }

        var clientId = GetClientId();
        var clientSecret = GetClientSecret();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("未配置 PingCode ClientId 或 Secret");
        }

        var authGetUrl =
            $"https://open.pingcode.com/v1/auth/token?grant_type=client_credentials&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(clientSecret)}";
        try
        {
            using var resp = await http.GetAsync(authGetUrl);
            var txt = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                var jobj = JObject.Parse(txt);
                var access = jobj.Value<string>("access_token");
                var expires = jobj.Value<int?>("expires_in");
                if (!string.IsNullOrWhiteSpace(access))
                {
                    token = access;
                    tokenExpiresAt = DateTime.UtcNow.AddSeconds(expires ?? 3600);
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
            }
            else
            {
                if ((resp.StatusCode == HttpStatusCode.Unauthorized) || (resp.StatusCode == HttpStatusCode.BadRequest))
                {
                    throw new ApiAuthException($"Token 请求失败: {(int)resp.StatusCode} {resp.StatusCode} {txt}");
                }
            }
        }
        catch (System.Exception)
        {
        }
    }
}
