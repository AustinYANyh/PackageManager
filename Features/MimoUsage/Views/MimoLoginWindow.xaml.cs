using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using PackageManager.Services;

namespace PackageManager.Features.MimoUsage.Views
{
    /// <summary>
    /// MiMo 平台登录窗口。使用 WebView2 加载小米平台页面，
    /// 尝试共享 Edge 浏览器的登录 session 实现静默登录。
    /// </summary>
    public partial class MimoLoginWindow : Window
    {
        private const string MiMoPlatformUrl = "https://platform.xiaomimimo.com/console/plan-manage";
        private const string MiMoDomain = "platform.xiaomimimo.com";

        /// <summary>
        /// 登录成功后获取到的原始 Cookie 字符串。
        /// </summary>
        public string ResultCookie { get; private set; }

        /// <summary>
        /// 登录成功后获取到的 userId。
        /// </summary>
        public string ResultUserId { get; private set; }

        /// <summary>
        /// 是否登录成功。
        /// </summary>
        public bool LoginSucceeded { get; private set; }

        /// <summary>
        /// 是否强制使用干净的独立缓存环境（切换账号用）。
        /// 为 true 时跳过 Edge profile 静默登录，改用独立的 MimoLoginFreshCache 目录，
        /// 确保不复用任何已有 session，强制用户重新输入新账号。
        /// </summary>
        public bool ForceFreshProfile { get; set; }

        public MimoLoginWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await InitializeWebViewAsync();
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                string userDataFolder;
                var useEdgeProfile = false;
                var useFreshProfile = ForceFreshProfile;

                if (useFreshProfile)
                {
                    // 切换账号：全新的独立缓存目录，不复用 Edge/旧 session，强制重输新账号
                    var dataService = new DataPersistenceService();
                    userDataFolder = Path.Combine(dataService.GetDataFolderPath(), "MimoLoginFreshCache");
                    TryPurgeDirectory(userDataFolder);
                    Directory.CreateDirectory(userDataFolder);
                    LoggingService.LogInfo($"[MiMo 登录] 强制干净环境，独立缓存: {userDataFolder}");
                }
                else
                {
                    // 正常登录：优先尝试使用 Edge 浏览器的 profile，共享其登录 session
                    userDataFolder = GetEdgeUserDataFolder();

                    if (userDataFolder != null && Directory.Exists(userDataFolder))
                    {
                        // 检查 Edge 是否正在运行（profile 目录可能被锁）
                        if (!IsEdgeRunning())
                        {
                            useEdgeProfile = true;
                        }
                    }

                    // 若 Edge profile 不可用，使用独立的缓存目录
                    if (!useEdgeProfile)
                    {
                        var dataService = new DataPersistenceService();
                        userDataFolder = Path.Combine(dataService.GetDataFolderPath(), "MimoWebView2Cache");
                        Directory.CreateDirectory(userDataFolder);
                    }
                }

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await LoginWeb.EnsureCoreWebView2Async(env);

                var core = LoginWeb.CoreWebView2;
                core.NavigationCompleted += OnNavigationCompleted;

                // 设置用户代理（避免被识别为自动化浏览器）
                core.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";

                StatusText.Text = useFreshProfile
                    ? "已使用干净环境，请输入新账号密码登录..."
                    : useEdgeProfile
                        ? "已关联 Edge 浏览器 session，尝试静默登录..."
                        : "正在打开小米平台登录页...";

                LoginWeb.Source = new Uri(MiMoPlatformUrl);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"浏览器初始化失败: {ex.Message}";
            }
        }

        private async void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var currentUrl = LoginWeb.CoreWebView2?.Source ?? "(null)";
            LoggingService.LogInfo($"[MiMo 登录] NavigationCompleted: isSuccess={e.IsSuccess}, url={currentUrl}");

            if (!e.IsSuccess)
            {
                return;
            }

            // 等待 500ms 让 cookie 完全设置（避免竞态）
            await Task.Delay(500);

            // 尝试提取 cookie
            await TryExtractCookiesAsync(currentUrl);
        }

        /// <summary>
        /// 从 WebView2 cookie 管理器中提取 cookie。
        /// 优先用当前 URL 域名获取，失败则尝试多个域名。
        /// </summary>
        private async Task TryExtractCookiesAsync(string currentUrl)
        {
            try
            {
                var cookieManager = LoginWeb.CoreWebView2.CookieManager;

                // 方式1：用 MiMo 平台 URL 获取
                var cookies = await cookieManager.GetCookiesAsync(MiMoPlatformUrl);
                LoggingService.LogInfo($"[MiMo 登录] GetCookiesAsync(平台URL) 返回 {cookies?.Count ?? 0} 个 cookie");

                // 方式2：如果方式1没拿到关键 cookie，用当前页面 URL 再试
                if (!HasRequiredCookies(cookies) && !string.IsNullOrWhiteSpace(currentUrl))
                {
                    LoggingService.LogInfo($"[MiMo 登录] 尝试用当前URL获取: {currentUrl}");
                    var moreCookies = await cookieManager.GetCookiesAsync(currentUrl);
                    LoggingService.LogInfo($"[MiMo 登录] GetCookiesAsync(当前URL) 返回 {moreCookies?.Count ?? 0} 个 cookie");

                    // 合并去重
                    if (moreCookies != null)
                    {
                        var existingNames = new HashSet<string>(cookies?.Select(c => c.Name) ?? Enumerable.Empty<string>());
                        foreach (var c in moreCookies)
                        {
                            if (!existingNames.Contains(c.Name))
                            {
                                cookies = cookies != null ? cookies.Concat(new[] { c }).ToList() : new List<CoreWebView2Cookie> { c };
                            }
                        }
                    }
                }

                // 方式3：再尝试用 base domain
                if (!HasRequiredCookies(cookies))
                {
                    LoggingService.LogInfo($"[MiMo 登录] 尝试用 base domain 获取");
                    var baseCookies = await cookieManager.GetCookiesAsync("https://xiaomimimo.com");
                    LoggingService.LogInfo($"[MiMo 登录] GetCookiesAsync(base domain) 返回 {baseCookies?.Count ?? 0} 个 cookie");
                    if (baseCookies != null)
                    {
                        var existingNames = new HashSet<string>(cookies?.Select(c => c.Name) ?? Enumerable.Empty<string>());
                        foreach (var c in baseCookies)
                        {
                            if (!existingNames.Contains(c.Name))
                            {
                                cookies = cookies != null ? cookies.Concat(new[] { c }).ToList() : new List<CoreWebView2Cookie> { c };
                            }
                        }
                    }
                }

                // 日志列出所有 cookie 名称
                if (cookies != null)
                {
                    foreach (var cookie in cookies)
                    {
                        LoggingService.LogInfo($"[MiMo 登录] cookie: name={cookie.Name}, domain={cookie.Domain}, value长度={cookie.Value?.Length ?? 0}");
                    }
                }

                // 提取关键 cookie
                string rawCookie = null;
                string userId = null;
                string serviceToken = null;

                if (cookies != null)
                {
                    foreach (var cookie in cookies)
                    {
                        if (cookie.Name == "userId")
                        {
                            userId = cookie.Value;
                        }

                        if (cookie.Name == "api-platform_serviceToken")
                        {
                            serviceToken = cookie.Value;
                        }

                        // 拼接所有 cookie（去掉值的引号包裹）
                        var domain = (cookie.Domain ?? "").ToLowerInvariant();
                        if (domain.Contains("xiaomimimo") || domain.Contains("mimo") || domain.StartsWith("."))
                        {
                            if (rawCookie != null)
                            {
                                rawCookie += "; ";
                            }

                            // 去掉值的引号包裹（WebView2 可能返回带引号的值）
                            var cookieValue = cookie.Value;
                            if (cookieValue != null && cookieValue.Length >= 2
                                && cookieValue.StartsWith("\"") && cookieValue.EndsWith("\""))
                            {
                                cookieValue = cookieValue.Substring(1, cookieValue.Length - 2);
                            }

                            rawCookie += $"{cookie.Name}={cookieValue}";
                        }
                    }
                }

                LoggingService.LogInfo($"[MiMo 登录] 提取结果: userId={userId}, serviceToken={(serviceToken != null ? "有(" + serviceToken.Length + "字符)" : "无")}, rawCookie长度={rawCookie?.Length ?? 0}");

                // 判断是否登录成功
                if (!string.IsNullOrWhiteSpace(serviceToken) && !string.IsNullOrWhiteSpace(userId))
                {
                    ResultCookie = rawCookie;
                    ResultUserId = userId;
                    LoginSucceeded = true;

                    LoggingService.LogInfo("[MiMo 登录] ✅ 登录成功，准备关闭窗口");
                    StatusText.Text = "登录成功！正在关闭...";

                    await Task.Delay(300);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    LoggingService.LogInfo("[MiMo 登录] ❌ 未检测到登录状态（缺少 serviceToken 或 userId）");
                    StatusText.Text = "未检测到登录状态，请确保已在浏览器中登录小米账号";
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "[MiMo 登录] 提取 cookie 异常");
                StatusText.Text = $"提取 Cookie 失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 检查 cookie 列表中是否包含必要的认证 cookie。
        /// </summary>
        private static bool HasRequiredCookies(IList<CoreWebView2Cookie> cookies)
        {
            if (cookies == null) return false;
            return cookies.Any(c => c.Name == "api-platform_serviceToken" || c.Name == "userId");
        }

        /// <summary>
        /// 从原始 Cookie 字符串中提取 userId。
        /// </summary>
        private static string ExtractUserIdFromCookie(string rawCookie)
        {
            var parts = rawCookie.Split(new[] { ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var kv = part.Trim().Split('=');
                if (kv.Length == 2 && kv[0].Trim() == "userId")
                {
                    return kv[1].Trim();
                }
            }

            return null;
        }

        /// <summary>
        /// 获取 Edge 浏览器的 User Data 目录路径。
        /// </summary>
        private static string GetEdgeUserDataFolder()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var edgePath = Path.Combine(localAppData, "Microsoft", "Edge", "User Data");
            return Directory.Exists(edgePath) ? edgePath : null;
        }

        /// <summary>
        /// 检测 Edge 浏览器是否正在运行。
        /// </summary>
        private static bool IsEdgeRunning()
        {
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName("msedge");
                return processes.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 清空指定目录下的所有内容（切换账号前清掉残留 session）。
        /// 逐项容错：单个文件/子目录被占用不影响其余，整体失败只记日志不抛。
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
                    catch (IOException) { /* 可能被占用，跳过 */ }
                    catch (UnauthorizedAccessException) { /* 跳过 */ }
                }

                // 按路径长度倒序，先删深层子目录再删浅层
                foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                                        .OrderByDescending(d => d.Length))
                {
                    try { Directory.Delete(dir, true); }
                    catch (IOException) { /* 跳过 */ }
                    catch (UnauthorizedAccessException) { /* 跳过 */ }
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "[MiMo 登录] 清空独立缓存目录失败（不影响登录）");
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
