using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using PackageManager.Features.MimoUsage.Dto;
using PackageManager.Features.MimoUsage.Services;
using PackageManager.Services;

namespace PackageManager.Features.MimoUsage.Views
{
    public partial class MimoUsagePage : Page, INotifyPropertyChanged
    {
        private readonly MimoCookieManager _cookieManager;
        private bool _isLoggedIn;
        private bool _isLoading;
        private bool _webViewReady;
        private int _currentYear = DateTime.Now.Year;
        private int _currentMonth = DateTime.Now.Month;

        public MimoUsagePage()
        {
            InitializeComponent();
            _cookieManager = new MimoCookieManager();
            DataContext = this;
            UpdateMonthText();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public ObservableCollection<UsageDisplayItem> DailyItems { get; } = new();
        public ObservableCollection<ModelSummaryItem> ModelSummaryItems { get; } = new();

        // ===== WebView2 初始化 =====

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            LoggingService.LogInfo("[MiMo] 页面加载");
            await InitWebViewAsync();
        }

        private async Task InitWebViewAsync()
        {
            try
            {
                var dataService = new DataPersistenceService();
                var userDataFolder = System.IO.Path.Combine(dataService.GetDataFolderPath(), "MimoWebView2Cache");
                System.IO.Directory.CreateDirectory(userDataFolder);
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await ApiWeb.EnsureCoreWebView2Async(env);

                LoggingService.LogInfo("[MiMo] WebView2 初始化完成，导航到 MiMo 平台");
                ApiWeb.Source = new Uri("https://platform.xiaomimimo.com/console/plan-manage");
                await Task.Delay(2000);

                var (rawCookie, userId) = await _cookieManager.LoadCookiesAsync();
                if (!string.IsNullOrWhiteSpace(rawCookie))
                {
                    LoggingService.LogInfo("[MiMo] 有已保存的 Cookie，注入到 WebView2");
                    var setCookieScript = BuildSetCookieScript(rawCookie);
                    await ApiWeb.CoreWebView2.ExecuteScriptAsync(setCookieScript);
                    await Task.Delay(500);
                    _isLoggedIn = true;
                    UpdateLoginState();
                    _webViewReady = true;
                    await LoadDataAsync();
                }
                else
                {
                    LoggingService.LogInfo("[MiMo] 无已保存 Cookie，等待用户登录");
                    _webViewReady = true;
                    UpdateLoginState();
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "[MiMo] WebView2 初始化失败");
                ShowError($"浏览器初始化失败: {ex.Message}");
            }
        }

        private static string BuildSetCookieScript(string rawCookie)
        {
            var js = "(function(){";
            foreach (var part in rawCookie.Split(new[] { "; " }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIdx = part.IndexOf('=');
                if (eqIdx > 0)
                {
                    var name = part.Substring(0, eqIdx).Trim();
                    var value = part.Substring(eqIdx + 1).Trim();
                    js += $"document.cookie=\"{name}={value};path=/;domain=.xiaomimimo.com\";";
                }
            }
            js += "return 'ok';})();";
            return js;
        }

        // ===== API 调用 =====

        /// <summary>
        /// 通过 WebView2 的 XMLHttpRequest 调用 API。
        /// list 端点用 POST，其他端点用 GET。
        /// </summary>
        private async Task<T> CallApiViaWebView<T>(string apiPath, object bodyParams = null, bool usePost = false)
        {
            var fullUrl = apiPath;

            // GET：把参数拼到 URL 查询字符串
            if (!usePost && bodyParams != null)
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(
                    JsonConvert.SerializeObject(bodyParams));
                if (dict != null)
                {
                    var qs = string.Join("&", dict.Select(kv =>
                        $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value?.ToString() ?? "")}"));
                    fullUrl += fullUrl.Contains("?") ? "&" + qs : "?" + qs;
                }
            }

            var method = usePost ? "POST" : "GET";
            var script = "";

            if (usePost)
            {
                var bodyJson = bodyParams != null ? JsonConvert.SerializeObject(bodyParams) : "{}";
                var escapedBody = bodyJson.Replace("\\", "\\\\").Replace("'", "\\'");
                script = $@"
                    (function() {{
                        var xhr = new XMLHttpRequest();
                        xhr.open('POST', '{fullUrl}', false);
                        xhr.setRequestHeader('Content-Type', 'application/json');
                        xhr.send('{escapedBody}');
                        return JSON.stringify({{status: xhr.status, body: xhr.responseText}});
                    }})();";
            }
            else
            {
                script = $@"
                    (function() {{
                        var xhr = new XMLHttpRequest();
                        xhr.open('GET', '{fullUrl}', false);
                        xhr.send();
                        return JSON.stringify({{status: xhr.status, body: xhr.responseText}});
                    }})();";
            }

            LoggingService.LogInfo($"[MiMo] XHR {method}: {fullUrl}");
            var rawResult = await ApiWeb.CoreWebView2.ExecuteScriptAsync(script);

            // ExecuteScriptAsync 对返回值做了 JSON 编码，需要解一层
            var decoded = JsonConvert.DeserializeObject<string>(rawResult);
            var outerObj = Newtonsoft.Json.Linq.JObject.Parse(decoded);
            var statusCode = (int)outerObj["status"];
            var bodyStr = (string)outerObj["body"];
            LoggingService.LogInfo($"[MiMo] XHR 响应 ({statusCode}): {bodyStr?.Substring(0, Math.Min(200, bodyStr?.Length ?? 0))}");

            if (statusCode != 200)
            {
                throw new InvalidOperationException($"API 请求失败 ({statusCode}): {bodyStr}");
            }

            // bodyStr 就是 API 返回的 JSON 字符串
            var dataToken = Newtonsoft.Json.Linq.JToken.Parse(bodyStr ?? "{}");
            if (dataToken is Newtonsoft.Json.Linq.JObject jObj && jObj.ContainsKey("code"))
            {
                var code = (int)jObj["code"];
                if (code != 0)
                {
                    throw new InvalidOperationException($"API 返回错误 (code={code}): {jObj["message"]}");
                }
                var innerData = jObj["data"];
                if (innerData == null) return default;
                return innerData.ToObject<T>();
            }
            return dataToken.ToObject<T>();
        }

        /// <summary>
        /// 调用 API 并返回原始 data JToken（不反序列化）。
        /// </summary>
        private async Task<Newtonsoft.Json.Linq.JToken> CallApiViaWebViewRaw(string apiPath)
        {
            var script = $@"
                (function() {{
                    var xhr = new XMLHttpRequest();
                    xhr.open('GET', '{apiPath}', false);
                    xhr.send();
                    return JSON.stringify({{status: xhr.status, body: xhr.responseText}});
                }})();";

            var rawResult = await ApiWeb.CoreWebView2.ExecuteScriptAsync(script);
            var decoded = JsonConvert.DeserializeObject<string>(rawResult);
            var outerObj = Newtonsoft.Json.Linq.JObject.Parse(decoded);
            var statusCode = (int)outerObj["status"];
            var bodyStr = (string)outerObj["body"];

            if (statusCode != 200)
            {
                throw new InvalidOperationException($"API 请求失败 ({statusCode}): {bodyStr}");
            }

            var bodyToken = Newtonsoft.Json.Linq.JToken.Parse(bodyStr ?? "{}");
            if (bodyToken is Newtonsoft.Json.Linq.JObject jObj && jObj.ContainsKey("code"))
            {
                var code = (int)jObj["code"];
                if (code != 0)
                {
                    throw new InvalidOperationException($"API 返回错误 (code={code}): {jObj["message"]}");
                }
                return jObj["data"];
            }

            return bodyToken;
        }

        // ===== 数据加载 =====

        private async Task LoadDataAsync()
        {
            if (_isLoading || !_isLoggedIn || !_webViewReady) return;

            _isLoading = true;
            LoadingPanel.Visibility = Visibility.Visible;
            UsageDataGrid.Visibility = Visibility.Collapsed;
            ModelSummaryBorder.Visibility = Visibility.Collapsed;
            StatusText.Text = "";

            try
            {
                LoggingService.LogInfo($"[MiMo] 开始加载数据: {_currentYear}-{_currentMonth:D2}");
                var ph = await GetPlatformPhAsync();
                var phParam = $"api-platform_ph={Uri.EscapeDataString(ph)}";

                // list 用 POST，usage 和 detail 用 GET
                var dailyTask = CallApiViaWebView<List<MimoUsageItem>>(
                    $"/api/v1/usage/token-plan/list?{phParam}",
                    new { year = _currentYear, month = _currentMonth },
                    usePost: true);

                // usage 和 detail 返回的 data 结构不直接匹配 DTO，需要手动提取
                var usageRaw = await CallApiViaWebViewRaw($"/api/v1/tokenPlan/usage?{phParam}");
                var monthlyUsage = usageRaw?["monthUsage"]?.ToObject<MimoMonthlyUsage>();

                var detailRaw = await CallApiViaWebViewRaw($"/api/v1/tokenPlan/detail?{phParam}");
                var planDetail = detailRaw?.ToObject<MimoPlanDetail>();

                var dailyItems = await dailyTask;

                LoggingService.LogInfo($"[MiMo] 数据加载成功: {dailyItems?.Count ?? 0} 条记录");

                UpdatePlanInfo(monthlyUsage, planDetail);

                DailyItems.Clear();
                if (dailyItems != null)
                {
                    foreach (var item in dailyItems.OrderBy(x => x.Date))
                        DailyItems.Add(new UsageDisplayItem(item));
                }

                UsageDataGrid.Visibility = Visibility.Visible;
                UpdateModelSummary(dailyItems);
                UpdateSummaryStats(dailyItems);
                FooterText.Text = $"数据更新于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}，共 {DailyItems.Count} 条记录";
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "[MiMo] 加载数据失败");
                ShowError($"加载失败: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
                LoadingPanel.Visibility = Visibility.Collapsed;
            }
        }

        private async Task<string> GetPlatformPhAsync()
        {
            var js = "(function(){var m=document.cookie.match(/api-platform_ph=([^;]+)/);if(!m)return'';var v=m[1];if(v.startsWith('\"')&&v.endsWith('\"'))v=v.slice(1,-1);return v;})();";
            var result = await ApiWeb.CoreWebView2.ExecuteScriptAsync(js);
            var ph = JsonConvert.DeserializeObject<string>(result);
            LoggingService.LogInfo($"[MiMo] 获取 platformPh: {ph}");
            return ph ?? "";
        }

        // ===== 登录 =====

        private int _loginCheckCount;

        private async void Login_Click(object sender, RoutedEventArgs e)
        {
            LoggingService.LogInfo("[MiMo] 用户点击登录按钮");
            ApiWeb.Source = new Uri("https://platform.xiaomimimo.com/console/plan-manage");

            _loginCheckCount = 0;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async (s, args) =>
            {
                _loginCheckCount++;
                if (_loginCheckCount > 60) { timer.Stop(); ShowError("登录超时，请重试"); return; }
                try
                {
                    var ph = await GetPlatformPhAsync();
                    if (!string.IsNullOrWhiteSpace(ph))
                    {
                        timer.Stop();
                        LoggingService.LogInfo("[MiMo] 检测到登录成功");
                        _isLoggedIn = true;
                        UpdateLoginState();
                        await LoadDataAsync();
                    }
                }
                catch { }
            };
            timer.Start();
        }

        // ===== UI =====

        private void ShowError(string message)
        {
            NotLoggedInPanel.Visibility = Visibility.Visible;
            NotLoggedInPanel.Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xF2, 0xF2));
            NotLoggedInPanel.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFC, 0xCA, 0xCA));
            HintText.Text = message;
            HintText.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x1B, 0x1B));
        }

        private void UpdateMonthText() => MonthText.Text = $"{_currentYear}年{_currentMonth}月";

        private void UpdateLoginState()
        {
            NotLoggedInPanel.Visibility = _isLoggedIn ? Visibility.Collapsed : Visibility.Visible;
            LoginBtn.Visibility = _isLoggedIn ? Visibility.Collapsed : Visibility.Visible;
            ReLoginBtn.Visibility = _isLoggedIn ? Visibility.Visible : Visibility.Collapsed;
            if (!_isLoggedIn)
            {
                NotLoggedInPanel.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF7, 0xED));
                NotLoggedInPanel.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFD, 0xBA, 0x74));
                HintText.Text = "请先登录小米 MiMo 平台，点击上方「登录」按钮";
                HintText.Foreground = new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E));
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) { if (_isLoggedIn) await LoadDataAsync(); }

        private async void ReLogin_Click(object sender, RoutedEventArgs e)
        {
            LoggingService.LogInfo("[MiMo] 用户点击切换账号");

            // 清除本地 Cookie
            _cookieManager.ClearCookies();
            _isLoggedIn = false;
            UpdateLoginState();

            // 清除 WebView2 缓存
            try
            {
                var dataService = new DataPersistenceService();
                var cachePath = System.IO.Path.Combine(dataService.GetDataFolderPath(), "MimoWebView2Cache");
                if (System.IO.Directory.Exists(cachePath))
                {
                    System.IO.Directory.Delete(cachePath, true);
                    LoggingService.LogInfo("[MiMo] 已清除 WebView2 缓存目录");
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "[MiMo] 清除 WebView2 缓存目录失败");
            }

            // 重新初始化 WebView2
            try
            {
                var dataService = new DataPersistenceService();
                var userDataFolder = System.IO.Path.Combine(dataService.GetDataFolderPath(), "MimoWebView2Cache");
                System.IO.Directory.CreateDirectory(userDataFolder);
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await ApiWeb.EnsureCoreWebView2Async(env);

                // 清除 WebView2 中所有 cookie
                ApiWeb.CoreWebView2.CookieManager.DeleteAllCookies();

                // 先导航到小米退出登录页面，销毁服务端 session
                LoggingService.LogInfo("[MiMo] 导航到退出登录页面");
                ApiWeb.Source = new Uri("https://account.xiaomi.com/pass/serviceLogout");
                await Task.Delay(2000);

                // 再清除一次 cookie（退出登录可能设置了新的 cookie）
                ApiWeb.CoreWebView2.CookieManager.DeleteAllCookies();
                LoggingService.LogInfo("[MiMo] 已清除所有 Cookie，准备重新登录");

                // 导航到登录页
                ApiWeb.Source = new Uri("https://platform.xiaomimimo.com/console/plan-manage");
                _webViewReady = true;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "[MiMo] 重新初始化 WebView2 失败");
                ShowError($"初始化失败: {ex.Message}");
            }
        }

        private async void PrevMonth_Click(object sender, RoutedEventArgs e) { _currentMonth--; if (_currentMonth < 1) { _currentMonth = 12; _currentYear--; } UpdateMonthText(); if (_isLoggedIn) await LoadDataAsync(); }
        private async void NextMonth_Click(object sender, RoutedEventArgs e) { _currentMonth++; if (_currentMonth > 12) { _currentMonth = 1; _currentYear++; } UpdateMonthText(); if (_isLoggedIn) await LoadDataAsync(); }

        private void UpdatePlanInfo(MimoMonthlyUsage monthlyUsage, MimoPlanDetail planDetail)
        {
            if (planDetail != null)
            {
                PlanInfoPanel.Visibility = Visibility.Visible;
                PlanNameText.Text = $"套餐: {planDetail.PlanName}";
                PlanPeriodText.Text = $"到期: {planDetail.CurrentPeriodEnd}";
            }
            if (monthlyUsage?.Items != null && monthlyUsage.Items.Count > 0)
            {
                var item = monthlyUsage.Items[0];
                PlanUsedText.Text = FormatChineseNumber(item.Used);
                PlanLimitText.Text = FormatChineseNumber(item.Limit);
                var remain = item.Limit - item.Used;
                PlanRemainText.Text = FormatChineseNumber(Math.Max(0, remain));
                PlanUsageText.Text = $"{monthlyUsage.Percent:P1}";
                PlanUsageBar.Width = Math.Min(300.0 * monthlyUsage.Percent, 300.0);
                // 剩余不足20%时标红
                if (monthlyUsage.Percent > 0.8)
                {
                    PlanRemainText.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x1B, 0x1B));
                }
            }
        }

        private void UpdateModelSummary(List<MimoUsageItem> items)
        {
            ModelSummaryItems.Clear();
            if (items == null || items.Count == 0) { ModelSummaryBorder.Visibility = Visibility.Collapsed; return; }
            foreach (var group in items.GroupBy(x => x.Model).OrderBy(g => g.Key))
            {
                var hit = group.Sum(x => x.InputHitToken);
                var miss = group.Sum(x => x.InputMissToken);
                var hr = (hit + miss) > 0 ? (double)hit / (hit + miss) * 100 : 0;
                ModelSummaryItems.Add(new ModelSummaryItem
                {
                    Model = group.Key,
                    TotalTokenChinese = FormatChineseNumber(group.Sum(x => x.TotalToken)),
                    InputHitChinese = FormatChineseNumber(hit),
                    InputMissChinese = FormatChineseNumber(miss),
                    RequestCount = group.Sum(x => x.RequestCount),
                    CacheHitRateDisplay = hr.ToString("F1") + "%"
                });
            }
            ModelSummaryBorder.Visibility = Visibility.Visible;
        }

        private void UpdateSummaryStats(List<MimoUsageItem> items)
        {
            if (items == null || items.Count == 0) { SummaryPanel.Visibility = Visibility.Collapsed; return; }
            var totalToken = items.Sum(x => x.TotalToken);
            var hit = items.Sum(x => x.InputHitToken);
            var miss = items.Sum(x => x.InputMissToken);
            var output = items.Sum(x => x.OutputToken);
            var req = items.Sum(x => x.RequestCount);
            var hr = (hit + miss) > 0 ? (double)hit / (hit + miss) * 100 : 0;
            TotalTokenText.Text = FormatChineseNumber(totalToken);
            CacheHitText.Text = FormatChineseNumber(hit);
            CacheMissText.Text = FormatChineseNumber(miss);
            OutputTokenText.Text = FormatChineseNumber(output);
            TotalRequestText.Text = req.ToString("N0");
            OverallHitRateText.Text = hr.ToString("F1") + "%";
            OverallHitRateText.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(hr >= 95 ? "#166534" : hr >= 80 ? "#92400E" : "#991B1B"));
            SummaryPanel.Visibility = Visibility.Visible;
        }

        public static string FormatChineseNumber(long value) => AiUsageHelper.FormatChineseNumber(value);

        private void RaisePropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class UsageDisplayItem
    {
        public UsageDisplayItem(MimoUsageItem item)
        {
            Date = item.Date?.Replace("2026-", "") ?? "";
            Model = item.Model ?? "";
            TotalTokenChinese = MimoUsagePage.FormatChineseNumber(item.TotalToken);
            InputHitChinese = MimoUsagePage.FormatChineseNumber(item.InputHitToken);
            InputMissChinese = MimoUsagePage.FormatChineseNumber(item.InputMissToken);
            OutputTokenChinese = MimoUsagePage.FormatChineseNumber(item.OutputToken);
            RequestCount = item.RequestCount;
            var rate = item.CacheHitRate;
            var label = rate >= 95 ? "🟢" : rate >= 80 ? "🟡" : "🔴";
            CacheHitRateDisplay = $"{label} {rate:F1}%";
        }

        [DataGridColumn(1, DisplayName = "日期", Width = "90", IsReadOnly = true)]
        public string Date { get; set; }

        [DataGridColumn(2, DisplayName = "模型", Width = "120", IsReadOnly = true)]
        public string Model { get; set; }

        [DataGridColumn(3, DisplayName = "总 Token", Width = "110", IsReadOnly = true)]
        public string TotalTokenChinese { get; set; }

        [DataGridColumn(4, DisplayName = "输入命中", Width = "110", IsReadOnly = true)]
        public string InputHitChinese { get; set; }

        [DataGridColumn(5, DisplayName = "输入未命中", Width = "110", IsReadOnly = true)]
        public string InputMissChinese { get; set; }

        [DataGridColumn(6, DisplayName = "输出 Token", Width = "100", IsReadOnly = true)]
        public string OutputTokenChinese { get; set; }

        [DataGridColumn(7, DisplayName = "请求数", Width = "70", IsReadOnly = true)]
        public int RequestCount { get; set; }

        [DataGridColumn(8, DisplayName = "缓存命中率", Width = "90", IsReadOnly = true)]
        public string CacheHitRateDisplay { get; set; }
    }

    public class ModelSummaryItem
    {
        [DataGridColumn(1, DisplayName = "模型", Width = "140", IsReadOnly = true)]
        public string Model { get; set; }

        [DataGridColumn(2, DisplayName = "总 Token", Width = "120", IsReadOnly = true)]
        public string TotalTokenChinese { get; set; }

        [DataGridColumn(3, DisplayName = "输入命中", Width = "120", IsReadOnly = true)]
        public string InputHitChinese { get; set; }

        [DataGridColumn(4, DisplayName = "输入未命中", Width = "120", IsReadOnly = true)]
        public string InputMissChinese { get; set; }

        [DataGridColumn(5, DisplayName = "请求数", Width = "80", IsReadOnly = true)]
        public int RequestCount { get; set; }

        [DataGridColumn(6, DisplayName = "缓存命中率", Width = "100", IsReadOnly = true)]
        public string CacheHitRateDisplay { get; set; }
    }
}
