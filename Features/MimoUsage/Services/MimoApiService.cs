using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PackageManager.Features.MimoUsage.Dto;
using PackageManager.Services;

namespace PackageManager.Features.MimoUsage.Services
{
    /// <summary>
    /// MiMo 平台 API 客户端。
    /// </summary>
    public class MimoApiService
    {
        private const string BaseUrl = "https://platform.xiaomimimo.com";

        private string _rawCookie;
        private string _userId;
        private string _platformPh;

        /// <summary>
        /// 设置请求 Cookie，同时提取 api-platform_ph 用于 URL 查询参数。
        /// </summary>
        public void ApplyCookies(string rawCookie, string userId)
        {
            _rawCookie = rawCookie;
            _userId = userId;
            _platformPh = ExtractCookieValue(rawCookie, "api-platform_ph");
            LoggingService.LogInfo($"[MiMo API] ApplyCookies: userId={userId}, platformPh={_platformPh ?? "(null)"}, cookie长度={rawCookie?.Length ?? 0}");
        }

        /// <summary>
        /// 从 cookie 字符串中提取指定 name 的 value。
        /// </summary>
        private static string ExtractCookieValue(string rawCookie, string name)
        {
            if (string.IsNullOrWhiteSpace(rawCookie))
            {
                return null;
            }

            var parts = rawCookie.Split(new[] { ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                var eqIndex = trimmed.IndexOf('=');
                if (eqIndex > 0)
                {
                    var key = trimmed.Substring(0, eqIndex).Trim();
                    if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        var val = trimmed.Substring(eqIndex + 1).Trim();
                        // WebView2 返回的 cookie 值可能带引号包裹，必须去掉
                        if (val.StartsWith("\"") && val.EndsWith("\"") && val.Length >= 2)
                        {
                            val = val.Substring(1, val.Length - 2);
                        }

                        return val;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 获取当月每日 Token 用量列表。
        /// </summary>
        public async Task<List<MimoUsageItem>> GetDailyUsageAsync(int year, int month)
        {
            var url = $"{BaseUrl}/api/v1/usage/token-plan/list";
            var body = new JObject
            {
                ["year"] = year,
                ["month"] = month
            };

            var resp = await PostJsonAsync(url, body);
            var data = resp["data"];
            if (data == null)
            {
                return new List<MimoUsageItem>();
            }

            return data.ToObject<List<MimoUsageItem>>();
        }

        /// <summary>
        /// 获取月度总用量（用于进度条显示）。
        /// </summary>
        public async Task<MimoMonthlyUsage> GetMonthlyUsageAsync()
        {
            var url = $"{BaseUrl}/api/v1/tokenPlan/usage";
            var resp = await PostJsonAsync(url, null);
            var data = resp["data"];
            if (data == null)
            {
                return null;
            }

            return data["monthUsage"]?.ToObject<MimoMonthlyUsage>();
        }

        /// <summary>
        /// 获取套餐详情（名称、到期日等）。
        /// </summary>
        public async Task<MimoPlanDetail> GetPlanDetailAsync()
        {
            var url = $"{BaseUrl}/api/v1/tokenPlan/detail";
            var resp = await PostJsonAsync(url, null);
            var data = resp["data"];
            if (data == null)
            {
                return null;
            }

            return data.ToObject<MimoPlanDetail>();
        }

        /// <summary>
        /// 带 Cookie 的 POST 请求，使用 HttpWebRequest 直接发送（绕过 HttpClient 的 header 处理）。
        /// </summary>
        private async Task<JObject> PostJsonAsync(string url, JObject body)
        {
            // 将 api-platform_ph 作为查询参数附加到 URL
            if (!string.IsNullOrWhiteSpace(_platformPh))
            {
                var separator = url.Contains("?") ? "&" : "?";
                url = $"{url}{separator}api-platform_ph={Uri.EscapeDataString(_platformPh)}";
            }

            var payload = body ?? new JObject();
            var payloadStr = payload.ToString(Formatting.None);
            var payloadBytes = Encoding.UTF8.GetBytes(payloadStr);

            // 使用 HttpWebRequest 直接控制所有 header
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.ContentLength = payloadBytes.Length;
            request.Accept = "*/*";
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";
            request.Referer = $"{BaseUrl}/console/plan-manage?userId={_userId}";
            request.Headers.Add("Origin", BaseUrl);
            request.Headers.Add("Accept-Language", "zh");
            request.Headers.Add("x-timezone", "Asia/Shanghai");

            // 关键：直接设置 Cookie header（HttpWebRequest 不会自动处理这个）
            var cookieHeader = _rawCookie;
            request.Headers.Add("Cookie", cookieHeader);

            // 禁用自动 Cookie 管理（避免和手动设置冲突）
            request.CookieContainer = new CookieContainer();

            // 记录完整请求信息用于诊断
            LoggingService.LogInfo($"[MiMo API] URL: {url}");
            LoggingService.LogInfo($"[MiMo API] Cookie header: {cookieHeader}");
            LoggingService.LogInfo($"[MiMo API] Body: {payloadStr}");

            // 写入请求体
            using (var stream = await request.GetRequestStreamAsync())
            {
                await stream.WriteAsync(payloadBytes, 0, payloadBytes.Length);
            }

            // 发送请求并读取响应
            string responseText;
            int statusCode;
            try
            {
                using var response = (HttpWebResponse)await request.GetResponseAsync();
                statusCode = (int)response.StatusCode;
                using var reader = new StreamReader(response.GetResponseStream());
                responseText = await reader.ReadToEndAsync();
            }
            catch (WebException wex) when (wex.Response is HttpWebResponse errorResponse)
            {
                statusCode = (int)errorResponse.StatusCode;
                using var reader = new StreamReader(errorResponse.GetResponseStream());
                responseText = await reader.ReadToEndAsync();
                LoggingService.LogError(wex, $"[MiMo API] HTTP {(int)errorResponse.StatusCode}");
            }

            LoggingService.LogInfo($"[MiMo API] Response ({statusCode}): {responseText?.Substring(0, Math.Min(200, responseText?.Length ?? 0))}");

            if (statusCode != 200)
            {
                throw new InvalidOperationException(
                    $"API 请求失败 ({statusCode}): {responseText?.Substring(0, Math.Min(200, responseText?.Length ?? 0))}");
            }

            var result = JObject.Parse(responseText);
            var code = result["code"]?.Value<int>() ?? -1;
            if (code != 0)
            {
                var msg = result["message"]?.Value<string>() ?? "未知错误";
                throw new InvalidOperationException($"API 返回错误 (code={code}): {msg}");
            }

            return result;
        }
    }
}
