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

            TryElevateIfNotAdmin(e.Args);
        }

        private static bool IsRunningAsAdministrator()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private void TryElevateIfNotAdmin(string[] args)
        {
            if (IsRunningAsAdministrator())
            {
                return;
            }

            try
            {
                var exePath = Process.GetCurrentProcess().MainModule.FileName;
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = string.Join(" ", args.Select(QuoteIfNeeded))
                };
                Process.Start(psi);
                Shutdown(); // 退出当前非管理员实例
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "管理员提权失败，继续以普通权限运行");
                MessageBox.Show("未能获取管理员权限，部分操作可能失败。已记录错误日志。", "权限警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string QuoteIfNeeded(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            return arg.Contains(" ") ? $"\"{arg}\"" : arg;
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