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

            // 包操作命令（扁平，参数按需收集：缺包选包、缺版本选版本、缺包文件选包文件）
            list.Add(New("pkg-cmd", "解锁更新", "回车用最新版/包更新，Tab 选版本/包", "jiesuogengxin|jsgx|gengxin|gx|update|jiesuo", key: "unlock"));
            list.Add(New("pkg-cmd", "打开 Revit", "启动选中包的 Revit", "dakairevit|dkrevit|revit|dakai|dk", key: "open-revit"));
            list.Add(New("pkg-cmd", "打开本地目录", "资源管理器打开包目录", "dakaibendimulu|dkbml|open|wenjianjia", key: "open-folder"));
            list.Add(New("pkg-cmd", "打开参数目录", "打开 config 目录", "dakaicanshumulu|dkcsml|config", key: "open-config"));
            list.Add(New("pkg-cmd", "打开图片目录", "打开 Image 目录", "dakaitupianmulu|dktpml|image|tupian", key: "open-image"));
            list.Add(New("pkg-cmd", "切换调试模式", "翻转调试/正式", "qiehongdiaoshimoshi|qhms|debug|tiaoshi|qiehuan", key: "toggle-debug"));
            list.Add(New("pkg-cmd", "签名加密校验", "校验包签名/加密", "qianmijiamijiaoyan|qmjmjy|sign|qianming", key: "signature"));
            list.Add(New("pkg-cmd", "定版", "上传到定版服务器", "dingban|finalize", key: "finalize"));
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
                    case "pkg": NavigateToPackage(item.ExecuteKey); break;
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

        /// <summary>
        /// 包操作参数收集向导：按操作所需的参数（包/版本/包文件）逐级收集，缺什么返回什么子列表，齐全则执行。
        /// executeKey 编码：op[\x1fpkg:名][\x1fver:版本][\x1fpkgfile:包文件]
        /// </summary>
        private static readonly (string Op, string Name, string Py)[] PackageOps = new (string, string, string)[]
        {
            ("unlock", "解锁更新", "jiesuogengxin|jsgx|gengxin|gx|update|jiesuo"),
            ("open-revit", "打开 Revit", "dakairevit|dkrevit|dakai|dk|revit"),
            ("open-folder", "打开本地目录", "dakaibendimulu|dkbml|mulu|folder|wenjianjia"),
            ("open-config", "打开参数目录", "dakaicanshumulu|dkcsml|config|canshu"),
            ("open-image", "打开图片目录", "dakaitupianmulu|dktpml|tupian|image"),
            ("toggle-debug", "切换调试模式", "qiehongdiaoshimoshi|qhms|debug|tiaoshi|qiehuan"),
            ("signature", "签名加密校验", "qianmijiamijiaoyan|qmjmjy|sign|qianming|qm"),
            ("finalize", "定版", "dingban|finalize|db"),
        };

        /// <summary>多 token 组合命令：空格分词，匹配操作 token + 包 token，生成"操作·包"组合项。</summary>
        public List<PaletteItem> BuildComposedCandidates(string query)
        {
            var r = new List<PaletteItem>();
            var tokens = (query ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) return r;
            var mw = ServiceLocator.Resolve<MainWindow>();
            if (mw?.Packages == null) return r;

            var opCands = new List<(string Token, string Op, string OpName, int Score)>();
            var pkgCands = new List<(string Token, string Pkg, int Score)>();
            var execCands = new List<(string Token, string Ver, int Score)>();
            foreach (var t in tokens)
            {
                foreach (var o in PackageOps)
                {
                    int s = FuzzyScore(o.Py + "|" + o.Name, t);
                    if (s > 0) opCands.Add((t, o.Op, o.Name, s));
                }
                foreach (var p in mw.Packages)
                {
                    if (p == null || string.IsNullOrEmpty(p.ProductName) || !mw.IsProductVisible(p.ProductName)) continue;
                    int s = FuzzyScore(p.ProductName, t);
                    if (s > 0) pkgCands.Add((t, p.ProductName, s));
                }
                // Revit 版本候选（系统已安装的 Revit，与包无关）
                foreach (var v in GetCachedRevitVersions())
                {
                    string vs = v.Version ?? v.DisPlayName ?? string.Empty;
                    if (string.IsNullOrEmpty(vs)) continue;
                    int s = FuzzyScore(vs, t);
                    if (s > 0) execCands.Add((t, vs, s));
                }
            }

            var combos = new List<(string Title, string ExecuteKey, int Score)>();
            // 操作 + 包
            foreach (var opc in opCands)
                foreach (var pkgc in pkgCands)
                {
                    if (opc.Token == pkgc.Token) continue;
                    combos.Add((opc.OpName + " · " + pkgc.Pkg, opc.Op + "\x1fpkg:" + pkgc.Pkg, opc.Score + pkgc.Score));
                }
            // 打开 Revit + Revit 版本（系统独立软件，与包无关）
            foreach (var opc in opCands)
            {
                if (opc.Op != "open-revit") continue;
                foreach (var ec in execCands)
                {
                    if (opc.Token == ec.Token) continue;
                    combos.Add(("打开 Revit " + ec.Ver, "open-revit" + '\x1f' + "execver:" + ec.Ver, opc.Score + ec.Score));
                }
            }

            var top = combos
                .GroupBy(c => c.ExecuteKey)
                .Select(g => g.OrderByDescending(c => c.Score).First())
                .OrderByDescending(c => c.Score)
                .Take(6);
            foreach (var c in top)
                r.Add(New("pkg-compose", c.Title, "回车执行，Tab 选参数", string.Empty, key: c.ExecuteKey));
            return r;
        }

        private static List<ApplicationVersion> GetCachedRevitVersions()
        {
            // 直接读包缓存里的 AvailableExecutableVersions（系统级 Revit 版本，已由 settings 持久化），不重新扫描
            var mw = ServiceLocator.Resolve<MainWindow>();
            var pkg = mw?.Packages?.FirstOrDefault(p => p?.AvailableExecutableVersions != null && p.AvailableExecutableVersions.Count > 0);
            return pkg?.AvailableExecutableVersions?.ToList() ?? new List<ApplicationVersion>();
        }

        private static int FuzzyScore(string text, string q)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(q)) return 0;
            text = text.ToLowerInvariant(); q = q.ToLowerInvariant();
            int ti = 0, score = 0, prev = -1; bool first = true;
            for (int qi = 0; qi < q.Length; qi++)
            {
                char c = q[qi];
                int found = -1;
                for (; ti < text.Length; ti++) { if (text[ti] == c) { found = ti; break; } }
                if (found < 0) return 0;
                int s = 1;
                if (found == prev + 1) s += 5;
                if (first && found == 0) s += 8;
                score += s; prev = found; ti = found + 1; first = false;
            }
            return score;
        }
        public async Task<CollectResult> CollectParameterAsync(string executeKey)
        {
            var parts = (executeKey ?? string.Empty).Split('\x1f');
            string op = parts.Length > 0 ? parts[0] : string.Empty;
            string pkg = null, ver = null, pkgfile = null;
            foreach (var p in parts)
            {
                if (p.StartsWith("pkg:")) pkg = p.Substring(4);
                else if (p.StartsWith("ver:")) ver = p.Substring(4);
                else if (p.StartsWith("pkgfile:")) pkgfile = p.Substring(8);
            }

            // 缺包 → 选包
            if (string.IsNullOrEmpty(pkg))
                return ShowCollect(BuildAllPackages(executeKey), "选择产品包");

            switch (op)
            {
                case "unlock":
                    if (string.IsNullOrEmpty(ver))
                        return ShowCollect(BuildVersionsForCollect(pkg, executeKey), "选择版本 · " + pkg);
                    if (string.IsNullOrEmpty(pkgfile))
                        return ShowCollect(await LoadNamesForCollect(pkg, ver, executeKey), "选择包 · " + pkg + " " + ver);
                    await ExecuteUnlockAsync(pkg, ver, pkgfile);
                    return CollectResult.Done;
                case "open-revit":
                case "open-folder":
                case "open-config":
                case "open-image":
                case "toggle-debug":
                case "signature":
                case "finalize":
                    await ExecuteSimpleAsync(op, pkg);
                    return CollectResult.Done;
            }
            return CollectResult.Done;
        }

        /// <summary>参数项 Enter（默认补全执行）：unlock 补最新版+最新包，其他操作直接执行。</summary>
        public async Task<CollectResult> CollectParameterDefaultAsync(string executeKey)
        {
            LoggingService.LogInfo("命令面板 CollectParameterDefault: key=" + executeKey);
            var parts = (executeKey ?? string.Empty).Split('\x1f');
            string op = parts.Length > 0 ? parts[0] : string.Empty;
            string pkg = null, ver = null, pkgfile = null;
            foreach (var p in parts)
            {
                if (p.StartsWith("pkg:")) pkg = p.Substring(4);
                else if (p.StartsWith("ver:")) ver = p.Substring(4);
                else if (p.StartsWith("pkgfile:")) pkgfile = p.Substring(8);
            }
            // 打开 Revit + Revit 版本（系统独立软件，不依赖包，需在 pkg 检查前处理）
            if (op == "open-revit")
            {
                string execver = null;
                foreach (var p in parts) if (p.StartsWith("execver:")) execver = p.Substring(8);
                if (!string.IsNullOrEmpty(execver))
                {
                    await ExecuteOpenRevitVersionAsync(execver);
                    return CollectResult.Done;
                }
            }

            if (string.IsNullOrEmpty(pkg)) return CollectResult.Done;

            if (op == "unlock")
            {
                var mw = ServiceLocator.Resolve<MainWindow>();
                if (mw == null) return CollectResult.Done;
                string useVer = ver;
                await mw.Dispatcher.InvokeAsync(() =>
                {
                    var p = mw.Packages?.FirstOrDefault(x => string.Equals(x.ProductName, pkg, StringComparison.OrdinalIgnoreCase));
                    if (string.IsNullOrEmpty(useVer) && !string.IsNullOrEmpty(p?.LatestServerVersion))
                        useVer = p.LatestServerVersion;
                });
                if (string.IsNullOrEmpty(pkgfile) && !string.IsNullOrEmpty(useVer))
                {
                    await mw.Dispatcher.InvokeAsync(() =>
                    {
                        var p = mw.Packages?.FirstOrDefault(x => string.Equals(x.ProductName, pkg, StringComparison.OrdinalIgnoreCase));
                        if (p != null && p.Version != useVer) p.Version = useVer;
                    });
                    for (int i = 0; i < 30; i++)
                    {
                        await Task.Delay(100);
                        bool ready = false;
                        await mw.Dispatcher.InvokeAsync(() =>
                        {
                            var p = mw.Packages?.FirstOrDefault(x => string.Equals(x.ProductName, pkg, StringComparison.OrdinalIgnoreCase));
                            ready = p?.AvailablePackages != null && p.AvailablePackages.Count > 0;
                        });
                        if (ready) break;
                    }
                    await mw.Dispatcher.InvokeAsync(() =>
                    {
                        var p = mw.Packages?.FirstOrDefault(x => string.Equals(x.ProductName, pkg, StringComparison.OrdinalIgnoreCase));
                        if (p?.AvailablePackages != null && p.AvailablePackages.Count > 0)
                            pkgfile = p.AvailablePackages[p.AvailablePackages.Count - 1];
                    });
                }
                await ExecuteUnlockAsync(pkg, useVer, pkgfile);
                return CollectResult.Done;
            }

            await ExecuteSimpleAsync(op, pkg);
            return CollectResult.Done;
        }

        private static CollectResult ShowCollect(List<PaletteItem> items, string title)
        {
            return new CollectResult { Show = true, Items = items, Title = title };
        }

        private List<PaletteItem> BuildAllPackages(string executeKey)
        {
            var r = new List<PaletteItem>();
            var mw = ServiceLocator.Resolve<MainWindow>();
            if (mw?.Packages == null) return r;
            foreach (var p in mw.Packages)
            {
                if (string.IsNullOrEmpty(p?.ProductName)) continue;
                r.Add(New("pkg-param", p.ProductName, p.Version ?? string.Empty, string.Empty, key: executeKey + "\x1fpkg:" + p.ProductName));
            }
            return r;
        }

        private List<PaletteItem> BuildVersionsForCollect(string productName, string executeKey)
        {
            var r = new List<PaletteItem>();
            var mw = ServiceLocator.Resolve<MainWindow>();
            var pkg = mw?.Packages?.FirstOrDefault(p => string.Equals(p.ProductName, productName, StringComparison.OrdinalIgnoreCase));
            if (pkg?.AvailableVersions == null) return r;
            foreach (var v in pkg.AvailableVersions)
            {
                if (string.IsNullOrEmpty(v)) continue;
                r.Add(New("pkg-param", v, productName, string.Empty, key: executeKey + "\x1fver:" + v));
            }
            return r;
        }

        private async Task<List<PaletteItem>> LoadNamesForCollect(string productName, string version, string executeKey)
        {
            var mw = ServiceLocator.Resolve<MainWindow>();
            if (mw == null) return new List<PaletteItem>();
            await mw.Dispatcher.InvokeAsync(() =>
            {
                var pkg = mw.Packages?.FirstOrDefault(p => string.Equals(p.ProductName, productName, StringComparison.OrdinalIgnoreCase));
                if (pkg != null && pkg.Version != version) pkg.Version = version;
            });
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
            var r = new List<PaletteItem>();
            await mw.Dispatcher.InvokeAsync(() =>
            {
                var pkg = mw.Packages?.FirstOrDefault(p => string.Equals(p.ProductName, productName, StringComparison.OrdinalIgnoreCase));
                if (pkg?.AvailablePackages == null) return;
                foreach (var name in pkg.AvailablePackages)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    r.Add(New("pkg-param", name, productName + " " + version, string.Empty, key: executeKey + "\x1fpkgfile:" + name));
                }
            });
            return r;
        }

        private async Task ExecuteUnlockAsync(string productName, string version, string pkgfile)
        {
            var mw = ServiceLocator.Resolve<MainWindow>();
            if (mw == null) return;
            await mw.Dispatcher.InvokeAsync(() =>
            {
                var pkg = mw.Packages?.FirstOrDefault(p => string.Equals(p.ProductName, productName, StringComparison.OrdinalIgnoreCase));
                if (pkg == null || pkg.IsUpdatingRunning) return;
                mw.LatestActivePackage = pkg;
                pkg.Version = version;
                pkg.UploadPackageName = pkgfile;
                pkg.UnlockAndDownloadCommand?.Execute(null);
            });
            ServiceLocator.Resolve<NavigationService>()?.NavigateTo("packages-home");
            ToastService.ShowToast("命令面板", "正在解锁更新 " + productName + " " + version, "Success");
        }

        private async Task ExecuteOpenRevitVersionAsync(string execver)
        {
            var versions = GetCachedRevitVersions();
            var ver = versions.FirstOrDefault(v => (v.Version ?? string.Empty).Contains(execver) || (v.DisPlayName ?? string.Empty).Contains(execver));
            if (ver == null || string.IsNullOrEmpty(ver.ExecutablePath) || !System.IO.File.Exists(ver.ExecutablePath))
            {
                ToastService.ShowToast("命令面板", $"未找到 Revit {execver}，缓存版本数={versions.Count}", "Warning");
                await Task.CompletedTask;
                return;
            }
            try
            {
                ToastService.ShowToast("命令面板", "启动 Revit：" + ver.ExecutablePath, "Info");
                Process.Start(new ProcessStartInfo(ver.ExecutablePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "启动 Revit 失败：" + ver.ExecutablePath);
                ToastService.ShowToast("命令面板", "启动失败：" + ex.Message, "Warning");
            }
            await Task.CompletedTask;
        }

        private async Task ExecuteSimpleAsync(string op, string productName)
        {
            var mw = ServiceLocator.Resolve<MainWindow>();
            if (mw == null) return;
            bool viewProgress = false;
            await mw.Dispatcher.InvokeAsync(() =>
            {
                var pkg = mw.Packages?.FirstOrDefault(p => string.Equals(p.ProductName, productName, StringComparison.OrdinalIgnoreCase));
                if (pkg == null) return;
                mw.LatestActivePackage = pkg;
                switch (op)
                {
                    case "open-revit": pkg.OpenPathCommand?.Execute(null); break;
                    case "open-folder": TryOpenFolder(pkg.EffectiveLocalPath ?? pkg.LocalPath); break;
                    case "open-config": pkg.OpenParameterConfigCommand?.Execute(null); break;
                    case "open-image": pkg.OpenImageConfigCommand?.Execute(null); break;
                    case "toggle-debug": pkg.ChangeModeToDebugCommand?.Execute(null); break;
                    case "signature": if (!pkg.IsSignatureEncryptionRunning) { pkg.RunEmbeddedToolCommand?.Execute(null); viewProgress = true; } break;
                    case "finalize": viewProgress = true; break;
                }
            });
            if (op == "finalize") { await mw.FinalizeSelectedPackageAsync(); viewProgress = true; }
            if (viewProgress) ServiceLocator.Resolve<NavigationService>()?.NavigateTo("packages-home");
        }

        private void NavigateToPackage(string productName)
        {
            var mw = ServiceLocator.Resolve<MainWindow>();
            if (mw == null) return;
            mw.Dispatcher.InvokeAsync(() =>
            {
                var pkg = mw.Packages?.FirstOrDefault(p => string.Equals(p.ProductName, productName, StringComparison.OrdinalIgnoreCase));
                if (pkg == null) return;
                mw.LatestActivePackage = pkg;
                ServiceLocator.Resolve<NavigationService>()?.NavigateTo("packages-home");
            });
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
