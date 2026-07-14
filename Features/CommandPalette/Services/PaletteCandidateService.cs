using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MftScanner;
using PackageManager.Features.CommandPalette.Models;
using PackageManager.Models;
using PackageManager.Services;
using PackageManager.Shell;

namespace PackageManager.Features.CommandPalette.Services
{
    /// <summary>
    /// 构建命令面板候选项（命令/导航/包），并提供文件搜索（MFT）与执行调度。
    /// </summary>
    internal sealed class PaletteCandidateService
    {
        private const string FileSlot = "CtrlE.Palette";
        private int _idSeq;
        private SharedIndexServiceClient _fileClient;
        private int _fileReady;

        public List<PaletteItem> BuildCandidates()
        {
            var list = new List<PaletteItem>();
            BuildCommands(list);
            BuildNavigation(list);
            BuildPackages(list);
            return list;
        }

        private string NextId() => "i" + (++_idSeq);

        private PaletteItem New(string type, string title, string subtitle, string pinyin, string key = null, string hk = null)
        {
            return new PaletteItem
            {
                Id = NextId(),
                Type = type,
                Title = title,
                Subtitle = subtitle ?? string.Empty,
                Pinyin = pinyin ?? string.Empty,
                Hint = hk ?? string.Empty,
                ExecuteKey = key ?? title
            };
        }

        private void BuildCommands(List<PaletteItem> list)
        {
            list.Add(New("cmd", "更新所有包", "前往产品分类页批量更新", "gxsyb|gengxinsuoyoubao", key: "update-all", hk: "Ctrl+U"));
            list.Add(New("cmd", "开关 Git 代理", "切换 127.0.0.1:7897", "kaiguandaili|gitdaili|daili", key: "git-proxy"));
            list.Add(New("cmd", "打开软件设置", "", "ruanjianshezhi|shezhi", key: "open-settings"));
            list.Add(New("cmd", "生成今日工作日报", "汇总提交 + PingCode", "shengchengribao|gongzuoribao|ribao", key: "daily-log", hk: "Ctrl+D"));
            list.Add(New("cmd", "解锁占用文件", "查询并结束占用进程", "jiesuowenjian|unlock", key: "unlock-files"));
            list.Add(New("cmd", "Revit 文件清理", "三索引源清理", "revitwenjianqingli|qingli", key: "revit-cleanup"));
            list.Add(New("cmd", "CSV 加解密", "批量加解密工具", "csvjiajiemi|jm", key: "csv-crypto"));
            list.Add(New("cmd", "SLN 编译顺序更新", "", "sln|bianyishunxu", key: "sln-update"));
            list.Add(New("cmd", "刷新版本信息", "强制刷新 FTP 可见版本", "shuaxinbanben|shuaxin|refresh", key: "refresh-versions"));
            list.Add(New("cmd", "打开常用启动项", "同 Ctrl+Q", "changyongqidong|qidong|startup", key: "open-startup"));
        }

        private void BuildNavigation(List<PaletteItem> list)
        {
            var nav = ServiceLocator.Resolve<NavigationService>();
            if (nav?.Registry?.Tools == null) return;
            foreach (var t in nav.Registry.Tools)
            {
                if (string.IsNullOrEmpty(t.Key) || string.IsNullOrEmpty(t.DisplayName)) continue;
                list.Add(New("nav", t.DisplayName, t.Group ?? string.Empty, string.Empty, key: t.Key));
            }
        }

        private void BuildPackages(List<PaletteItem> list)
        {
            var mw = ServiceLocator.Resolve<MainWindow>();
            var packages = mw?.Packages;
            if (packages == null) return;
            foreach (var p in packages)
            {
                if (string.IsNullOrEmpty(p?.ProductName)) continue;
                var hasNew = !string.IsNullOrEmpty(p.LatestServerVersion)
                             && !string.Equals(p.Version, p.LatestServerVersion, StringComparison.OrdinalIgnoreCase);
                var sub = hasNew
                    ? $"v{p.Version} → v{p.LatestServerVersion}"
                    : (!string.IsNullOrEmpty(p.Version) ? $"v{p.Version}（已是最新）" : string.Empty);
                var item = New("pkg", p.ProductName, sub, string.Empty, key: p.ProductName);
                if (hasNew) item.AddTag("新版本 " + p.LatestServerVersion, "new");
                list.Add(item);
            }
        }

        /// <summary>同步阻塞式文件搜索（应在后台线程调用）。</summary>
        public List<PaletteItem> SearchFiles(string keyword)
        {
            var r = new List<PaletteItem>();
            if (string.IsNullOrWhiteSpace(keyword)) return r;
            try
            {
                EnsureFileClient();
                if (_fileClient == null) return r;
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                {
                    var res = _fileClient.SearchAsync(keyword.Trim(), 40, 0, SearchTypeFilter.All, null, cts.Token)
                                         .GetAwaiter().GetResult();
                    if (res?.Results != null)
                    {
                        foreach (var f in res.Results)
                        {
                            if (string.IsNullOrEmpty(f?.FullPath)) continue;
                            r.Add(New("file", f.FileName ?? System.IO.Path.GetFileName(f.FullPath), f.FullPath, string.Empty, key: f.FullPath));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "命令面板文件搜索失败");
            }
            return r;
        }

        private void EnsureFileClient()
        {
            if (_fileClient != null) return;
            try
            {
                var client = new SharedIndexServiceClient(FileSlot);
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    client.BuildIndexAsync(null, cts.Token).GetAwaiter().GetResult();
                }
                _fileClient = client;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "命令面板文件索引客户端初始化失败");
            }
        }

        public async Task ExecuteAsync(PaletteItem item)
        {
            if (item == null) return;
            try
            {
                switch (item.Type)
                {
                    case "cmd": await ExecuteCommandAsync(item.ExecuteKey); break;
                    case "nav": ServiceLocator.Resolve<NavigationService>()?.NavigateTo(item.ExecuteKey); break;
                    case "pkg": await UpdatePackageLatestAsync(item.ExecuteKey); break;
                    case "pkg-action": await ExecutePackageActionAsync(item.ExecuteKey); break;
                    case "file": OpenFile(item.ExecuteKey); break;
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "命令面板执行失败：" + item.Title);
                ToastService.ShowToast("命令面板", "执行失败：" + ex.Message, "Warning");
            }
        }

        private async Task ExecuteCommandAsync(string key)
        {
            var owner = Application.Current?.MainWindow;
            switch (key)
            {
                case "update-all":
                    ServiceLocator.Resolve<NavigationService>()?.NavigateTo("packages-home");
                    ToastService.ShowToast("命令面板", "已打开产品分类页，可批量更新", "Info");
                    break;
                case "git-proxy":
                    await GitProxyService.ToggleAsync();
                    break;
                case "open-settings":
                    ServiceLocator.Resolve<NavigationService>()?.NavigateTo("settings");
                    break;
                case "daily-log":
                    ServiceLocator.Resolve<NavigationService>()?.NavigateTo("daily-log");
                    break;
                case "unlock-files":
                    owner?.Dispatcher.Invoke(() => Features.DevTools.DevToolLauncher.OpenUnlockFiles(owner));
                    break;
                case "revit-cleanup":
                    Features.DevTools.DevToolLauncher.OpenRevitFileCleanup();
                    break;
                case "csv-crypto":
                    owner?.Dispatcher.Invoke(() => Features.DevTools.DevToolLauncher.OpenCsvCrypto(owner));
                    break;
                case "sln-update":
                    owner?.Dispatcher.Invoke(() => Features.DevTools.DevToolLauncher.OpenSlnUpdate(owner));
                    break;
                case "refresh-versions":
                    {
                        var mwR = ServiceLocator.Resolve<MainWindow>();
                        if (mwR != null)
                        {
                            await mwR.Dispatcher.InvokeAsync(() => mwR.RefreshCommand?.Execute(null));
                            ToastService.ShowToast("命令面板", "已刷新版本信息", "Info");
                        }
                        break;
                    }
                case "open-startup":
                    (Application.Current as App)?.ShowCommonStartupWindow();
                    break;
            }
        }

        private async Task UpdatePackageLatestAsync(string productName)
        {
            var mw = ServiceLocator.Resolve<MainWindow>();
            if (mw == null)
            {
                ToastService.ShowToast("命令面板", "主窗口未就绪", "Warning");
                return;
            }
            string latest = null;
            bool alreadyRunning = false;
            await mw.Dispatcher.InvokeAsync(() =>
            {
                var pkg = mw.Packages?.FirstOrDefault(p => string.Equals(p.ProductName, productName, StringComparison.OrdinalIgnoreCase));
                if (pkg == null)
                {
                    ToastService.ShowToast("命令面板", "未找到包：" + productName, "Warning");
                    return;
                }

                // 已在更新中：避免重复触发，仅选中并跳转查看进度
                if (pkg.IsUpdatingRunning)
                {
                    alreadyRunning = true;
                    mw.LatestActivePackage = pkg;
                    ServiceLocator.Resolve<NavigationService>()?.NavigateTo("packages-home");
                    return;
                }

                if (string.IsNullOrEmpty(pkg.LatestServerVersion))
                {
                    ToastService.ShowToast("命令面板", pkg.ProductName + "：暂无最新版本信息", "Warning");
                    return;
                }
                latest = pkg.LatestServerVersion;
                pkg.Version = pkg.LatestServerVersion;
                // 触发完整更新流程（与点界面“更新”按钮同链路：设 IsReadOnly/IsUpdatingRunning 互斥 + 进度回调）
                pkg.UpdateCommand?.Execute(null);
                // 选中该包：PackageGrid 行高亮，右侧详情面板实时显示其 StatusText/Progress
                mw.LatestActivePackage = pkg;
                // 跳转产品分类页，让进度与按钮互斥状态可见
                ServiceLocator.Resolve<NavigationService>()?.NavigateTo("packages-home");
            });
            if (alreadyRunning)
                ToastService.ShowToast("命令面板", $"{productName} 正在更新中，已跳转查看进度", "Info");
            else if (latest != null)
                ToastService.ShowToast("命令面板", $"正在更新 {productName} → v{latest}，已打开产品分类页查看进度", "Success");
        }

        public List<PaletteItem> BuildPackageActions(string productName)
        {
            var r = new List<PaletteItem>();
            var mw = ServiceLocator.Resolve<MainWindow>();
            var pkg = mw?.Packages?.FirstOrDefault(p => string.Equals(p.ProductName, productName, StringComparison.OrdinalIgnoreCase));
            if (pkg == null) return r;
            string K(string a) => productName + "\x1f" + a;

            if (pkg.IsUpdatingRunning)
            {
                r.Add(New("pkg-action", "取消更新", pkg.ProductName, "quxiaogengxin|qxgx|cancel", key: K("cancel-update")));
            }
            else
            {
                if (!string.IsNullOrEmpty(pkg.LatestServerVersion))
                    r.Add(New("pkg-action", "更新到最新版 " + pkg.LatestServerVersion, pkg.ProductName, "gengxinzx|gxzx|update", key: K("update")));
                r.Add(New("pkg-action", "解锁更新（强制关闭占用，选版本）", pkg.ProductName, "jiesuogengxin|jsgx|unlock", key: K("unlock")));
            }

            if (pkg.AvailableExecutableVersions != null && pkg.AvailableExecutableVersions.Count > 0)
                r.Add(New("pkg-action", "打开 Revit", pkg.ProductName, "dakairevit|dkrevit|revit", key: K("open-revit")));

            r.Add(New("pkg-action", "打开本地目录", pkg.EffectiveLocalPath ?? pkg.LocalPath ?? string.Empty, "dakaibendimulu|dkbml|open", key: K("open-folder")));
            r.Add(New("pkg-action", "打开参数目录 (config)", pkg.ProductName, "dakaicanshumulu|dkcsml|config", key: K("open-config")));
            r.Add(New("pkg-action", "打开图片目录 (Image)", pkg.ProductName, "dakaitupianmulu|dktpml|image", key: K("open-image")));
            r.Add(New("pkg-action", "切换调试模式" + (pkg.IsDebugMode ? "（当前：调试）" : "（当前：正式）"), pkg.ProductName, "qiehongdiaoshimoshi|qhms|debug|tiaoshi", key: K("toggle-debug")));

            if (!pkg.IsSignatureEncryptionRunning)
                r.Add(New("pkg-action", "签名 / 加密校验", pkg.ProductName, "qianmijiamijiaoyan|qmjmjy|sign", key: K("signature")));

            if (!string.IsNullOrEmpty(pkg.FinalizeFtpServerPath))
                r.Add(New("pkg-action", "定版（上传到定版服务器）", pkg.ProductName, "dingban|finalize", key: K("finalize")));

            return r;
        }

        public List<PaletteItem> BuildPackageVersions(string productName, string action)
        {
            var r = new List<PaletteItem>();
            var mw = ServiceLocator.Resolve<MainWindow>();
            var pkg = mw?.Packages?.FirstOrDefault(p => string.Equals(p.ProductName, productName, StringComparison.OrdinalIgnoreCase));
            if (pkg?.AvailableVersions == null) return r;
            foreach (var v in pkg.AvailableVersions)
            {
                if (string.IsNullOrEmpty(v)) continue;
                r.Add(New("pkg-action", v, pkg.ProductName, string.Empty, key: productName + "\x1f" + action + "\x1f" + v));
            }
            return r;
        }

        public async Task<List<PaletteItem>> SelectVersionForUnlockAsync(string productName, string version)
        {
            var mw = ServiceLocator.Resolve<MainWindow>();
            if (mw == null) return new List<PaletteItem>();
            await mw.Dispatcher.InvokeAsync(() =>
            {
                var pkg = mw.Packages?.FirstOrDefault(p => string.Equals(p.ProductName, productName, StringComparison.OrdinalIgnoreCase));
                if (pkg != null && pkg.Version != version) pkg.Version = version;
            });
            // 等待该版本的 AvailablePackages 加载（OnPackageVersionChanged 触发的异步加载）
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(100);
                bool ready = false;
                await mw.Dispatcher.InvokeAsync(() =>
                {
                    var pkg = mw.Packages?.FirstOrDefault(p => string.Equals(p.ProductName, productName, StringComparison.OrdinalIgnoreCase));
                    ready = pkg?.AvailablePackages != null && pkg.AvailablePackages.Count > 0;
                });
                if (ready) break;
            }
            return BuildPackageNames(productName, "unlock-final");
        }

        public List<PaletteItem> BuildPackageNames(string productName, string action)
        {
            var r = new List<PaletteItem>();
            var mw = ServiceLocator.Resolve<MainWindow>();
            var pkg = mw?.Packages?.FirstOrDefault(p => string.Equals(p.ProductName, productName, StringComparison.OrdinalIgnoreCase));
            if (pkg?.AvailablePackages == null) return r;
            foreach (var name in pkg.AvailablePackages)
            {
                if (string.IsNullOrEmpty(name)) continue;
                r.Add(New("pkg-action", name, pkg.ProductName, string.Empty, key: productName + "\x1f" + action + "\x1f" + name));
            }
            return r;
        }

        public async Task ExecutePackageActionAsync(string executeKey)
        {
            var parts = (executeKey ?? string.Empty).Split('\x1f');
            if (parts.Length < 2) return;
            var productName = parts[0];
            var action = parts[1];
            var selectedVersion = parts.Length > 2 ? parts[2] : null;
            var mw = ServiceLocator.Resolve<MainWindow>();
            if (mw == null) return;

            bool viewProgress = false;
            await mw.Dispatcher.InvokeAsync(() =>
            {
                var pkg = mw.Packages?.FirstOrDefault(p => string.Equals(p.ProductName, productName, StringComparison.OrdinalIgnoreCase));
                if (pkg == null) return;
                mw.LatestActivePackage = pkg;
                switch (action)
                {
                    case "update":
                        if (!pkg.IsUpdatingRunning && !string.IsNullOrEmpty(pkg.LatestServerVersion))
                        { pkg.Version = pkg.LatestServerVersion; pkg.UnlockAndDownloadCommand?.Execute(null); viewProgress = true; }
                        break;
                    case "unlock-final":
                        if (!pkg.IsUpdatingRunning && !string.IsNullOrEmpty(selectedVersion))
                        { pkg.UploadPackageName = selectedVersion; pkg.UnlockAndDownloadCommand?.Execute(null); viewProgress = true; }
                        break;
                    case "cancel-update":
                        pkg.UpdateToggleCommand?.Execute(null); viewProgress = true;
                        break;
                    case "open-revit":
                        pkg.OpenPathCommand?.Execute(null);
                        break;
                    case "open-folder":
                        TryOpenFolder(pkg.EffectiveLocalPath ?? pkg.LocalPath);
                        break;
                    case "open-config":
                        pkg.OpenParameterConfigCommand?.Execute(null);
                        break;
                    case "open-image":
                        pkg.OpenImageConfigCommand?.Execute(null);
                        break;
                    case "toggle-debug":
                        pkg.ChangeModeToDebugCommand?.Execute(null);
                        break;
                    case "signature":
                        if (!pkg.IsSignatureEncryptionRunning) { pkg.RunEmbeddedToolCommand?.Execute(null); viewProgress = true; }
                        break;
                }
            });

            if (action == "finalize")
            {
                await mw.FinalizeSelectedPackageAsync();
                viewProgress = true;
            }

            if (viewProgress)
                ServiceLocator.Resolve<NavigationService>()?.NavigateTo("packages-home");

            ToastService.ShowToast("命令面板", ActionLabel(action, productName), viewProgress ? "Success" : "Info");
        }

        private static string ActionLabel(string action, string productName)
        {
            switch (action)
            {
                case "update": return "正在更新 " + productName;
                case "unlock-update": return "正在解锁更新 " + productName;
                case "cancel-update": return "已取消 " + productName + " 的更新";
                case "signature": return "正在校验 " + productName;
                case "finalize": return "正在定版 " + productName;
                default: return productName + "：" + action;
            }
        }

        private static void TryOpenFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (System.IO.Directory.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { LoggingService.LogError(ex, "打开目录失败：" + path); }
        }

        private static void OpenFile(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "打开文件失败：" + path);
                ToastService.ShowToast("命令面板", "无法打开：" + path, "Warning");
            }
        }
    }
}
