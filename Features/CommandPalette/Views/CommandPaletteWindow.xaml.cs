using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PackageManager.Features.CommandPalette.Models;
using PackageManager.Features.CommandPalette.Services;
using PackageManager.Services;
using PackageManager.Shell;

namespace PackageManager.Features.CommandPalette.Views
{
    /// <summary>
    /// 命令面板浮层：无边框透明置顶全屏窗口，内嵌 WebView2 承载 HTML 面板。
    /// 负责 WebView2 初始化、候选项推送、JS⇄C# 消息桥接与执行调度。
    /// </summary>
    public partial class CommandPaletteWindow : Window
    {
        private readonly PaletteCandidateService _svc = new PaletteCandidateService();
        private readonly Dictionary<string, PaletteItem> _byId = new Dictionary<string, PaletteItem>();
        private bool _webReady;
        private bool _preloading;

        public CommandPaletteWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Deactivated += (s, e) => Hide();
            Opacity = 0;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var dataService = new DataPersistenceService();
                var userDataFolder = Path.Combine(dataService.GetDataFolderPath(), "CommandPaletteCache");
                Directory.CreateDirectory(userDataFolder);
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await PaletteWeb.EnsureCoreWebView2Async(env);
                var core = PaletteWeb.CoreWebView2;
                core.Settings.IsWebMessageEnabled = true;
                try { core.Settings.AreDevToolsEnabled = false; } catch { }
                core.WebMessageReceived += OnWebMessage;
                core.NavigationCompleted += OnNavCompleted;
                core.NavigateToString(PaletteHtml.Build());
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "命令面板 WebView2 初始化失败");
            }
        }

        private void OnNavCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess) return;
            PushCandidates();
            _webReady = true;
            if (_preloading)
            {
                _preloading = false;
                Hide();
                return;
            }
            // 首次唤起（未预热）：等 WebView2 首帧渲染后再现形，避免深色背景一闪
            if (IsVisible && Opacity == 0)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Opacity = 1;
                    Activate();
                    PaletteWeb.Focus();
                    ExecJs("try{var __i=document.getElementById('q');if(__i){__i.focus();__i.select&&__i.select();}}catch(e){}");
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void PushCandidates()
        {
            try
            {
                var items = _svc.BuildCandidates();
                _byId.Clear();
                foreach (var it in items) _byId[it.Id] = it;
                var json = JsonConvert.SerializeObject(items);
                ExecJs("window.__pm&&window.__pm.setCandidates(" + json + ");");
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "命令面板推送候选项失败");
            }
        }

        private async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                var raw = args.WebMessageAsJson;
                if (string.IsNullOrWhiteSpace(raw)) return;
                var jo = JsonConvert.DeserializeObject<JObject>(raw);
                if (jo == null) return;
                var type = jo["type"]?.ToString();

                if (type == "execute")
                {
                    var id = jo["id"]?.ToString();
                    if (id != null && _byId.TryGetValue(id, out var item))
                    {
                        // 包操作命令/参数项：走参数收集向导（缺包选包、缺版本选版本、缺包文件选包文件）
                        if (item.Type == "pkg-cmd" || item.Type == "pkg-param")
                        {
                            var result = await _svc.CollectParameterAsync(item.ExecuteKey);
                            if (result.Show)
                            {
                                foreach (var it in result.Items) _byId[it.Id] = it;
                                ExecJs("window.__pm&&window.__pm.showActions(" + JsonConvert.SerializeObject(result.Items) + ",'" + (result.Title ?? "操作").Replace("'", " ") + "');");
                                return;
                            }
                            Hide();
                            return;
                        }
                        Hide();
                        await _svc.ExecuteAsync(item);
                    }
                }
                else if (type == "query")
                {
                    var text = jo["text"]?.ToString() ?? string.Empty;
                    if (text.StartsWith("/"))
                        await HandleFileQueryAsync(text.Substring(1).TrimStart());
                    else
                        ExecJs("window.__pm&&window.__pm.clearFileResults();");
                }
                else if (type == "close")
                {
                    Hide();
                }
                else if (type == "execute-default")
                {
                    var id = jo["id"]?.ToString();
                    if (id != null && _byId.TryGetValue(id, out var item))
                    {
                        Hide();
                        await _svc.CollectParameterDefaultAsync(item.ExecuteKey);
                    }
                }
                else if (type == "bridge")
                {
                    var id = jo["id"]?.ToString();
                    var bquery = jo["q"]?.ToString() ?? string.Empty;
                    if (id != null && _byId.TryGetValue(id, out var item))
                    {
                        Hide();
                        await BridgeAsync(item, bquery);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "命令面板消息处理失败");
            }
        }

        private async Task HandleFileQueryAsync(string keyword)
        {
            if (string.IsNullOrEmpty(keyword))
            {
                ExecJs("window.__pm&&window.__pm.setFileResults([]);");
                return;
            }
            ExecJs("window.__pm&&window.__pm.setFileLoading(true);");
            var results = await Task.Run(() => _svc.SearchFiles(keyword));
            foreach (var it in results) _byId[it.Id] = it;
            var json = JsonConvert.SerializeObject(results);
            await ExecJsAsync("window.__pm&&window.__pm.setFileResults(" + json + ");");
        }

        private void ExecJs(string script)
        {
            try { _ = PaletteWeb.CoreWebView2?.ExecuteScriptAsync(script); }
            catch { }
        }

        private Task ExecJsAsync(string script)
        {
            try { return PaletteWeb.CoreWebView2?.ExecuteScriptAsync(script) ?? Task.CompletedTask; }
            catch { return Task.CompletedTask; }
        }

        public void Preload()
        {
            _preloading = true;
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            // 屏外正坐标隐藏：WebView2 子 HWND 不受 Opacity 影响，靠屏外不可见；正坐标避免负坐标 DPI 模糊
            Left = SystemParameters.VirtualScreenWidth + 100;
            Top = 0;
            Opacity = 0;
            Show();
        }

        public void ShowPalette()
        {
            var wa = SystemParameters.WorkArea;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
            if (_webReady)
            {
                Opacity = 1;
                ShowActivated = true;
                Show();
                Activate();
                PaletteWeb.Focus();
                ExecJs("try{var __i=document.getElementById('q');if(__i){__i.value='';__i.focus();}}catch(e){}");
                PushCandidates();
            }
            else
            {
                // 首次未预热：不可见加载（WebView2 冷启动），加载完后由 OnNavCompleted 现形
                Opacity = 0;
                Show();
            }
        }

        private Task BridgeAsync(PaletteItem item, string query)
        {
            try
            {
                switch (item?.Type)
                {
                    case "file":
                        (Application.Current as App)?.FileSearchWindowManager?.ShowOrActivate();
                        if (!string.IsNullOrWhiteSpace(query))
                        {
                            try { Clipboard.SetText(query); } catch { }
                            ToastService.ShowToast("命令面板", "已打开文件搜索，关键词已复制，Ctrl+V 粘贴", "Info");
                        }
                        break;
                    case "pkg":
                        ServiceLocator.Resolve<NavigationService>()?.NavigateTo("packages-home");
                        break;
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "命令面板桥接失败");
            }
            return Task.CompletedTask;
        }

        public void ClosePalette()
        {
            try { Hide(); }
            catch { }
        }
    }
}
