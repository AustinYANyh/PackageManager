using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Windows;
using System.Threading.Tasks;
using PackageManager.Services;
using System.Runtime.InteropServices;
using System.IO;

namespace PackageManager
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
        private CommonStartupWindowManager _commonStartupWindowManager;
        private FileSearchWindowManager _fileSearchWindowManager;
        private SystemHotkeyService _systemHotkeyService;

        internal CommonStartupWindowManager CommonStartupWindowManager => _commonStartupWindowManager;
        internal FileSearchWindowManager FileSearchWindowManager => _fileSearchWindowManager;

        /// <summary>
        /// 应用程序启动时执行初始化操作，包括 WebView2 加载器、日志服务和异常处理。
        /// </summary>
        /// <param name="e">启动事件参数，包含命令行参数。</param>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            TryEnsureWebView2Loader();
            LoggingService.Initialize();

            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            if (TryHandleAdminTask(e.Args))
            {
                return;
            }

            InitializeCommonStartupHotkey();
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);
        
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern System.IntPtr LoadLibrary(string lpFileName);
        
        private static void TryEnsureWebView2Loader()
        {
            try
            {
                var asm = typeof(App).Assembly;
                var names = asm.GetManifestResourceNames();
                var arch = Environment.Is64BitProcess ? "x64" : "x86";
                try
                {
                    var pa = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
                    if (pa == Architecture.Arm64) arch = "arm64";
                    else if (pa == Architecture.X64) arch = "x64";
                    else if (pa == Architecture.X86) arch = "x86";
                }
                catch { }
                var name = names.FirstOrDefault(n =>
                    n.IndexOf("webview2loader", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                    (n.IndexOf(arch, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     n.IndexOf($"win-{arch}", StringComparison.OrdinalIgnoreCase) >= 0));
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = names.FirstOrDefault(n => n.EndsWith("WebView2Loader.dll", StringComparison.OrdinalIgnoreCase));
                }
                if (string.IsNullOrWhiteSpace(name))
                {
                    try
                    {
                        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                        var candidate = Path.Combine(baseDir, "runtimes", $"win-{arch}", "native", "WebView2Loader.dll");
                        if (File.Exists(candidate))
                        {
                            SetDllDirectory(Path.GetDirectoryName(candidate));
                            LoadLibrary(candidate);
                            return;
                        }
                        var assetsCandidate = Path.Combine(baseDir, "Assets", "Tools", "runtimes", $"win-{arch}", "native", "WebView2Loader.dll");
                        if (File.Exists(assetsCandidate))
                        {
                            SetDllDirectory(Path.GetDirectoryName(assetsCandidate));
                            LoadLibrary(assetsCandidate);
                            return;
                        }
                    }
                    catch
                    {
                    }
                    return;
                }
                
                var targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackageManager", "bin");
                Directory.CreateDirectory(targetDir);
                var targetPath = Path.Combine(targetDir, "WebView2Loader.dll");
                using (var s = asm.GetManifestResourceStream(name))
                {
                    if (s != null)
                    {
                        using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                        {
                            s.CopyTo(fs);
                        }
                    }
                }
                try
                {
                    SetDllDirectory(targetDir);
                    LoadLibrary(targetPath);
                }
                catch
                {
                }
            }
            catch
            {
            }
        }

        private bool TryHandleAdminTask(string[] args)
        {
            try
            {
                if (args != null && args.Length >= 2 && string.Equals(args[0], "--pm-admin-update", StringComparison.OrdinalIgnoreCase))
                {
                    var jsonPath = args[1];
                    var text = System.IO.File.ReadAllText(jsonPath);
                    var cfg = Newtonsoft.Json.JsonConvert.DeserializeObject<Services.AdminElevationService.AdminUpdateConfig>(text);
                    var info = new Models.PackageInfo
                    {
                        ProductName = cfg.ProductName,
                        LocalPath = cfg.LocalPath,
                        Status = Models.PackageStatus.Downloading,
                        StatusText = "正在下载..."
                    };
                    info.SetDownloadUrlOverride(cfg.DownloadUrl);
                    var svc = new Services.PackageUpdateService();
                    var ok = svc.UpdatePackageAsync(info, null, cfg.ForceUnlock).GetAwaiter().GetResult();
                    Environment.Exit(ok ? 0 : 1);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Services.LoggingService.LogError(ex, "管理员任务执行失败");
                try { Environment.Exit(1); } catch { }
                return true;
            }
            return false;
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LoggingService.LogError(e.Exception, "Dispatcher 未处理异常");
            MessageBox.Show("发生未处理的错误，已写入日志。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true; // 如需保留崩溃行为，可改为 false
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception ?? new Exception("未知异常");
            LoggingService.LogError(ex, "AppDomain 未处理异常");
        }

        private void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            LoggingService.LogError(e.Exception, "Task 未观察到的异常");
            e.SetObserved();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _systemHotkeyService?.Dispose();
                _commonStartupWindowManager?.Shutdown();
                _fileSearchWindowManager?.Shutdown();
            }
            catch
            {
            }

            base.OnExit(e);
        }

        private void InitializeCommonStartupHotkey()
        {
            try
            {
                _commonStartupWindowManager = new CommonStartupWindowManager();
                _fileSearchWindowManager = new FileSearchWindowManager();
                _systemHotkeyService = new SystemHotkeyService(_commonStartupWindowManager, _fileSearchWindowManager);
                _systemHotkeyService.Start();
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "初始化全局热键失败");
            }
        }

        internal void ShowCommonStartupWindow()
        {
            _commonStartupWindowManager?.ShowOrActivate();
        }

        internal void ShowFileSearchWindow()
        {
            if (!UserFeatureAccessService.CanUseAustinOnlyFeatures)
            {
                return;
            }

            _fileSearchWindowManager?.ShowOrActivate();
        }
    }
}

