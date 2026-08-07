using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using PackageManager.Services;

namespace PackageManager.Features.SubmitDefect.Views
{
    /// <summary>
    /// PingCode 登录窗口（WebView2）。用户登录后自动获取 session cookie，
    /// 用于内部 atlas 上传链路（示意图永久显示）。
    /// </summary>
    public partial class PingCodeLoginWindow : Window
    {
        private const string PingCodeUrl = "https://hongwa.pingcode.com";

        /// <summary>
        /// 初始化 <see cref="PingCodeLoginWindow"/> 的新实例。
        /// </summary>
        public PingCodeLoginWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        /// <summary>登录成功后获取到的原始 Cookie 字符串。</summary>
        public string ResultCookie { get; private set; }

        /// <summary>是否登录成功。</summary>
        public bool LoginSucceeded { get; private set; }

        /// <summary>
        /// 是否强制使用干净缓存（切换账号用）。为 true 时清空 PingCodeLoginCache，强制重新输入新账号。
        /// </summary>
        public bool ForceFreshProfile { get; set; }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await InitializeWebViewAsync();
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                var dataService = new DataPersistenceService();
                var userDataFolder = Path.Combine(dataService.GetDataFolderPath(), "PingCodeLoginCache");
                if (ForceFreshProfile)
                {
                    TryPurgeDirectory(userDataFolder);
                    LoggingService.LogInfo("[PingCode 登录] 切换账号：已清空缓存，强制重新登录");
                }
                Directory.CreateDirectory(userDataFolder);

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await LoginWeb.EnsureCoreWebView2Async(env);

                var core = LoginWeb.CoreWebView2;
                core.NavigationCompleted += OnNavigationCompleted;
                core.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";

                StatusText.Text = "正在打开 PingCode 登录页…";
                LoginWeb.Source = new Uri(PingCodeUrl);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"浏览器初始化失败: {ex.Message}";
                LoggingService.LogError(ex, "[PingCode 登录] WebView2 初始化失败");
            }
        }

        private async void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var currentUrl = LoginWeb.CoreWebView2?.Source ?? "(null)";
            LoggingService.LogInfo($"[PingCode 登录] NavigationCompleted: isSuccess={e.IsSuccess}, url={currentUrl}");

            if (!e.IsSuccess)
            {
                return;
            }

            await Task.Delay(500);
            await TryExtractCookiesAsync(currentUrl);
        }

        /// <summary>
        /// 从 WebView2 cookie 管理器提取 session cookie（pingcode 域）。
        /// </summary>
        private async Task TryExtractCookiesAsync(string currentUrl)
        {
            try
            {
                var cookieManager = LoginWeb.CoreWebView2.CookieManager;

                var cookies = await cookieManager.GetCookiesAsync(PingCodeUrl);
                LoggingService.LogInfo($"[PingCode 登录] GetCookiesAsync({PingCodeUrl}) 返回 {cookies?.Count ?? 0} 个 cookie");

                if (((cookies == null) || (cookies.Count == 0)) && !string.IsNullOrWhiteSpace(currentUrl))
                {
                    cookies = await cookieManager.GetCookiesAsync(currentUrl);
                    LoggingService.LogInfo($"[PingCode 登录] GetCookiesAsync(当前URL) 返回 {cookies?.Count ?? 0} 个 cookie");
                }

                if (cookies != null)
                {
                    foreach (var cookie in cookies)
                    {
                        LoggingService.LogInfo($"[PingCode 登录] cookie: name={cookie.Name}, domain={cookie.Domain}");
                    }
                }

                // 只拼 session cookie（s-{team_id}）为 rawCookie
                // upload-token 只认 session token；gdp_* 分析 cookie 全量传会干扰（实测 712 字符全量 → code=401 token not found）
                string rawCookie = null;
                if (cookies != null)
                {
                    foreach (var cookie in cookies)
                    {
                        var domain = (cookie.Domain ?? string.Empty).ToLowerInvariant();
                        if (domain.Contains("pingcode") && !string.IsNullOrEmpty(cookie.Name) && cookie.Name.StartsWith("s-", StringComparison.Ordinal))
                        {
                            if (rawCookie != null)
                            {
                                rawCookie += "; ";
                            }

                            // 去 cookie 值的引号包裹（学 MimoLoginWindow：WebView2 返回的值可能带引号）
                            var cookieValue = cookie.Value;
                            if ((cookieValue != null) && (cookieValue.Length >= 2) && cookieValue.StartsWith("\"") && cookieValue.EndsWith("\""))
                            {
                                cookieValue = cookieValue.Substring(1, cookieValue.Length - 2);
                            }

                            rawCookie += $"{cookie.Name}={cookieValue}";
                        }
                    }
                }

                // session cookie（s-{team_id}）存在 = 登录成功
                var hasSession = (cookies != null) && cookies.Any(c => !string.IsNullOrEmpty(c.Name) && c.Name.StartsWith("s-", StringComparison.Ordinal));

                if (hasSession && !string.IsNullOrWhiteSpace(rawCookie))
                {
                    ResultCookie = rawCookie;
                    LoginSucceeded = true;
                    LoggingService.LogInfo("[PingCode 登录] ✅ 登录成功，获取到 session cookie");
                    StatusText.Text = "登录成功！正在关闭…";
                    await Task.Delay(300);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    LoggingService.LogInfo("[PingCode 登录] ❌ 未检测到 session cookie");
                    StatusText.Text = "未检测到登录状态，请在浏览器中登录 PingCode";
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "[PingCode 登录] 提取 cookie 异常");
                StatusText.Text = $"提取 Cookie 失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 清空指定目录（切换账号前清残留 session）。逐项容错：单个文件/子目录被占用不影响其余。
        /// </summary>
        private static void TryPurgeDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }

                foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                                        .OrderByDescending(d => d.Length))
                {
                    try { Directory.Delete(dir, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "[PingCode 登录] 清空缓存目录失败（不影响登录）");
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
