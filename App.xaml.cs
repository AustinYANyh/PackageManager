using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Windows;
using System.Threading.Tasks;
using PackageManager.Services;
using PackageManager.Features.Notifications.Services;
using PackageManager.Features.CodeWorkspace.Services;
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

            var dataPersistence = new DataPersistenceService();
            ServiceLocator.Register(dataPersistence);
            ServiceLocator.Register(new CredentialStore(dataPersistence));
            ServiceLocator.Register(new FtpService(dataPersistence));
            ServiceLocator.Register(new PackageUpdateService());
            ServiceLocator.Register(new ApplicationFinderService());
            ServiceLocator.Register(new LanTransferService(dataPersistence));
            ServiceLocator.Register(new NotificationService(dataPersistence));
            var vcsStatusService = new VcsStatusService();
            var codeWorkspaceVcsCache = new CodeWorkspaceVcsCacheService(dataPersistence, vcsStatusService);
            ServiceLocator.Register(vcsStatusService);
            ServiceLocator.Register(codeWorkspaceVcsCache);

            var ftpService = ServiceLocator.Resolve<FtpService>();
            var monitor = new PackageVersionMonitorService(ftpService);
            ServiceLocator.Register(monitor);

            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            if (TryHandleAdminTask(e.Args))
            {
                return;
            }

            InitializeCommonStartupHotkey();
            codeWorkspaceVcsCache.StartWarmup();
            StartAiCommitEnvironmentWarmup();
        }

        private static void StartAiCommitEnvironmentWarmup()
        {
            Task.Run(() =>
            {
                try
                {
                    AiGlobalInstructionService.EnsureCodeGraphInstructions();
                    new AiCommitSkillService().EnsureSkillAvailable(null);
                    AiMemoryService.EnsureMemoryAvailable();
                }
                catch (Exception ex)
                {
                    LoggingService.LogError(ex, "启动时预热 AI 提交环境失败");
                }
            });
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

                if (args != null && args.Length >= 2 && string.Equals(args[0], "--pm-admin-register-index-host-task", StringComparison.OrdinalIgnoreCase))
                {
                    var exitCode = IndexHostTaskService.RunAdminRegister(args[1]);
                    Environment.Exit(exitCode);
                    return true;
                }

                if (args != null && args.Length >= 1 && string.Equals(args[0], "--pm-admin-ensure-index-host", StringComparison.OrdinalIgnoreCase))
                {
                    var exitCode = IndexHostTaskService.RunAdminEnsureHost();
                    Environment.Exit(exitCode);
                    return true;
                }

                // if (args != null && args.Length >= 1 && string.Equals(args[0], "--install-index-service", StringComparison.OrdinalIgnoreCase))
                // {
                //     var exitCode = MftIndexServiceManager.RunAdminInstallOrUpdate();
                //     Environment.Exit(exitCode);
                //     return true;
                // }
                //
                // if (args != null && args.Length >= 1 && string.Equals(args[0], "--uninstall-index-service", StringComparison.OrdinalIgnoreCase))
                // {
                //     var exitCode = MftIndexServiceManager.RunAdminUninstall();
                //     Environment.Exit(exitCode);
                //     return true;
                // }
                //
                // if (args != null && args.Length >= 1 && string.Equals(args[0], "--service-status", StringComparison.OrdinalIgnoreCase))
                // {
                //     Console.WriteLine(MftIndexServiceManager.GetStatusJson());
                //     Environment.Exit(0);
                //     return true;
                // }
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
                ServiceLocator.Resolve<CodeWorkspaceVcsCacheService>()?.Cancel();
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
                if (!IndexHostTaskService.EnsureRegisteredAndRunningOnStartup())
                {
                    const string message = "后台索引宿主未能就绪，PackageManager 将退出。\n\n"
                                           + "可能原因：旧版 MftScanner/CommonStartupTool 仍占用索引宿主文件、管理员授权被取消，或系统策略/安全软件阻止创建计划任务。\n"
                                           + "请关闭残留工具进程后重试；详细原因已写入日志。";
                    LoggingService.LogWarning("后台索引宿主未能就绪，程序将退出。");
                    MessageBox.Show(message, "PackageManager 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(-1);
                    return;
                }

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
            _fileSearchWindowManager?.ShowOrActivate();
        }
    }
}

