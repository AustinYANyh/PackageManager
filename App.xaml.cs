using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Windows;
using System.Threading.Tasks;
using PackageManager.Services;

namespace PackageManager
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            LoggingService.Initialize();

            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            if (TryHandleAdminTask(e.Args))
            {
                return;
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
                        // DownloadUrl = cfg.DownloadUrl,
                        LocalPath = cfg.LocalPath,
                        Status = Models.PackageStatus.Downloading,
                        StatusText = "正在下载..."
                    };
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
    }
}
