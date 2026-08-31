using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PackageManager.Services;

namespace PackageManager.Features.SubmitDefect.Services
{
    /// <summary>
    /// PingCode 会话无感续期服务：先用轻量请求探测本地 cookie 是否有效；
    /// 失效时用隐藏 WebView2（复用 PingCodeLoginCache 缓存登录态，绝不清空）静默访问 PingCode，
    /// 提取缓存 session 换发的最新 cookie，经服务端验证后落盘——全程无需用户交互。
    /// 两层都失败（缓存登录态已彻底过期）才需要用户手动走登录窗口（清缓存换账号）。
    /// </summary>
    public class PingCodeSessionService
    {
        private const string PingCodeUrl = "https://hongwa.pingcode.com";

        /// <summary>并发门：同一时刻只允许一个静默 WebView2 续期（登录窗模态期间不会并发，此处再防一层）。</summary>
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        /// <summary>探测节流：间隔内不重复探测，避免每次切页都发请求。</summary>
        private static readonly TimeSpan ProbeThrottle = TimeSpan.FromMinutes(10);

        private static DateTime lastEnsureAt = DateTime.MinValue;

        /// <summary>
        /// 确保本地有可用的 PingCode cookie：先探测（探测请求本身会触发服务端会话滑动续期），
        /// 失效则走隐藏 WebView2 静默续期。返回可用 rawCookie；从未登录或彻底失效返回 null。
        /// </summary>
        /// <param name="force">忽略节流强制探测（上传失败重试场景用）。</param>
        /// <returns>可用的 rawCookie；不可用返回 null。</returns>
        public async Task<string> EnsureFreshCookieAsync(bool force = false)
        {
            var manager = new PingCodeCookieManager();
            var current = await manager.LoadCookiesAsync();
            if (string.IsNullOrWhiteSpace(current))
            {
                return null;
            }

            if (!force && (DateTime.Now - lastEnsureAt < ProbeThrottle))
            {
                return current;
            }

            lastEnsureAt = DateTime.Now;

            // 第 1 层：轻量探测。cookie 仍有效直接用；服务端换发的新 cookie 顺带落盘
            var probed = await new PingCodeAtlasUploader(current).ProbeCookieAsync();
            if (!string.IsNullOrWhiteSpace(probed))
            {
                if (!string.Equals(probed, current, StringComparison.Ordinal))
                {
                    LoggingService.LogInfo("[PingCode 续期] 探测发现服务端换发新 cookie，已落盘");
                    await manager.SaveCookiesAsync(probed);
                }
                else
                {
                    LoggingService.LogInfo("[PingCode 续期] cookie 有效，无需续期");
                }

                return probed;
            }

            LoggingService.LogInfo("[PingCode 续期] cookie 已失效，尝试 WebView2 静默续期…");

            // 第 2 层：隐藏 WebView2 复用缓存登录态，静默访问提取最新 cookie
            var refreshed = await RefreshViaHiddenWebViewAsync(TimeSpan.FromSeconds(20));
            if (string.IsNullOrWhiteSpace(refreshed))
            {
                LoggingService.LogInfo("[PingCode 续期] 静默续期失败（缓存登录态可能已彻底过期）");
                return null;
            }

            // WebView2 cookie store 里可能仍是过期的旧值，必须经服务端验证才算续期成功
            var verified = await new PingCodeAtlasUploader(refreshed).ProbeCookieAsync();
            if (string.IsNullOrWhiteSpace(verified))
            {
                LoggingService.LogInfo("[PingCode 续期] WebView2 提取的 cookie 验证未通过");
                return null;
            }

            await manager.SaveCookiesAsync(verified);
            LoggingService.LogInfo("[PingCode 续期] ✅ 静默续期成功，新 cookie 已落盘");
            return verified;
        }

        /// <summary>
        /// 隐藏 WebView2 静默续期：透明小窗口承载 WebView2（与登录窗共用 PingCodeLoginCache 用户数据目录），
        /// 导航到 PingCode 首页让缓存 session 自动续期/换发，提取 s- 开头 session cookie。
        /// </summary>
        /// <param name="timeout">整体超时（导航+提取）。</param>
        /// <returns>提取到的 rawCookie；失败/超时返回 null。</returns>
        private static async Task<string> RefreshViaHiddenWebViewAsync(TimeSpan timeout)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return null;
            }

            if (!dispatcher.CheckAccess())
            {
                return await await dispatcher.InvokeAsync(() => RefreshViaHiddenWebViewAsync(timeout));
            }

            await Gate.WaitAsync();
            Window host = null;
            try
            {
                // 1x1 透明窗口移到屏幕外：不抢焦点、不可见，但保证 WebView2 正常初始化与导航
                host = new Window
                {
                    Width = 1,
                    Height = 1,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Opacity = 0,
                    Left = -32000,
                    Top = -32000,
                };

                var webView = new WebView2();
                host.Content = webView;
                host.Show();

                var dataService = new DataPersistenceService();
                var userDataFolder = Path.Combine(dataService.GetDataFolderPath(), "PingCodeLoginCache");
                Directory.CreateDirectory(userDataFolder);

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);

                var core = webView.CoreWebView2;
                core.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";

                var tcs = new TaskCompletionSource<string>();
                core.NavigationCompleted += async (s, e) =>
                {
                    try
                    {
                        if (!e.IsSuccess)
                        {
                            tcs.TrySetResult(null);
                            return;
                        }

                        // 与登录窗一致：等 cookie 落定再提取
                        await Task.Delay(500);
                        var cookies = await core.CookieManager.GetCookiesAsync(PingCodeUrl);
                        tcs.TrySetResult(ExtractSessionCookie(cookies));
                    }
                    catch
                    {
                        tcs.TrySetResult(null);
                    }
                };

                core.Navigate(PingCodeUrl);

                var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
                var cookie = (done == tcs.Task) ? await tcs.Task : null;
                return cookie;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "[PingCode 续期] WebView2 静默续期异常");
                return null;
            }
            finally
            {
                try
                {
                    host?.Close();
                }
                catch
                {
                    // 关窗失败不影响结果
                }

                Gate.Release();
            }
        }

        /// <summary>
        /// 从 WebView2 cookie 列表提取 pingcode 域下 s- 开头的 session cookie（与登录窗提取规则一致，含去引号）。
        /// </summary>
        /// <param name="cookies">WebView2 cookie 管理器返回的 cookie 列表。</param>
        /// <returns>rawCookie 形式字符串；无 session cookie 返回 null。</returns>
        private static string ExtractSessionCookie(System.Collections.Generic.IReadOnlyList<CoreWebView2Cookie> cookies)
        {
            string raw = null;
            if (cookies == null)
            {
                return null;
            }

            foreach (var cookie in cookies)
            {
                var domain = (cookie.Domain ?? string.Empty).ToLowerInvariant();
                if (domain.Contains("pingcode") && !string.IsNullOrEmpty(cookie.Name) && cookie.Name.StartsWith("s-", StringComparison.Ordinal))
                {
                    var cookieValue = cookie.Value;
                    if ((cookieValue != null) && (cookieValue.Length >= 2) && cookieValue.StartsWith("\"") && cookieValue.EndsWith("\""))
                    {
                        cookieValue = cookieValue.Substring(1, cookieValue.Length - 2);
                    }

                    raw = (raw == null) ? $"{cookie.Name}={cookieValue}" : raw + "; " + $"{cookie.Name}={cookieValue}";
                }
            }

            return raw;
        }
    }
}
