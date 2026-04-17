using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PackageManager.Function.StartupTool;
using PackageManager.Services;

namespace CommonStartupTool;

public partial class App : Application
{
    private const string ShowRequestEventName = "PackageManager.CommonStartupTool.Show";
    private const string SingleInstanceMutexName = "PackageManager.CommonStartupTool.Singleton";
    private const int SwShow = 5;
    private const int SwRestore = 9;

    private DispatcherTimer _ownerMonitorTimer;
    private EventWaitHandle _showRequestEvent;
    private CancellationTokenSource _showRequestCts;
    private Task _showRequestListenerTask;
    private Mutex _singleInstanceMutex;
    private int? _ownerProcessId;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!TryAcquireSingleInstance())
        {
            Shutdown(0);
            return;
        }

        _ownerProcessId = ParseOwnerProcessId(e.Args);

        InitializeShowRequestListener();

        var window = new CommonStartupWindow(new DataPersistenceService());
        MainWindow = window;
        if (!_ownerProcessId.HasValue)
        {
            window.PrepareForProcessExit();
        }

        window.Show();
        StartOwnerMonitor();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _ownerMonitorTimer?.Stop();
            _showRequestCts?.Cancel();
            _showRequestEvent?.Dispose();
            if (MainWindow is CommonStartupWindow window)
            {
                window.PrepareForProcessExit();
            }

            if (_singleInstanceMutex != null)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch
                {
                }

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }
        }
        catch
        {
        }

        base.OnExit(e);
    }

    private bool TryAcquireSingleInstance()
    {
        bool createdNew;
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
        if (createdNew)
        {
            return true;
        }

        try
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        catch
        {
        }

        SignalExistingInstance();
        return false;
    }

    private static void SignalExistingInstance()
    {
        for (var i = 0; i < 20; i++)
        {
            try
            {
                using (var showEvent = EventWaitHandle.OpenExisting(ShowRequestEventName))
                {
                    showEvent.Set();
                    return;
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
            catch
            {
                return;
            }
        }
    }

    private void InitializeShowRequestListener()
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
            AccessControlType.Allow));

        bool createdNew;
        _showRequestEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestEventName, out createdNew, security);
        _showRequestCts = new CancellationTokenSource();
        _showRequestListenerTask = Task.Run(() => ListenForShowRequests(_showRequestCts.Token));
    }

    private void ListenForShowRequests(CancellationToken cancellationToken)
    {
        if (_showRequestEvent == null)
        {
            return;
        }

        var waitHandles = new WaitHandle[] { _showRequestEvent, cancellationToken.WaitHandle };
        while (true)
        {
            var index = WaitHandle.WaitAny(waitHandles);
            if (index != 0)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(ShowAndActivateMainWindow));
        }
    }

    private void ShowAndActivateMainWindow()
    {
        if (!(MainWindow is CommonStartupWindow window))
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
        window.Focus();
        window.FocusSearchBoxAndSelectAll();
        BringWindowToFront(window);

        var delayedBringTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        delayedBringTimer.Tick += (sender, args) =>
        {
            delayedBringTimer.Stop();
            BringWindowToFront(window);
            window.FocusSearchBoxAndSelectAll();
        };
        delayedBringTimer.Start();
    }

    private static void BringWindowToFront(Window window)
    {
        if (window == null)
        {
            return;
        }

        window.Topmost = true;
        window.Activate();
        window.Topmost = false;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (IsIconic(handle))
        {
            ShowWindow(handle, SwRestore);
        }
        else
        {
            ShowWindow(handle, SwShow);
        }

        BringWindowToTop(handle);
        SetForegroundWindow(handle);
    }

    private void StartOwnerMonitor()
    {
        if (!_ownerProcessId.HasValue)
        {
            return;
        }

        _ownerMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ownerMonitorTimer.Tick += OwnerMonitorTimer_Tick;
        _ownerMonitorTimer.Start();
    }

    private void OwnerMonitorTimer_Tick(object sender, EventArgs e)
    {
        if (!_ownerProcessId.HasValue || IsProcessAlive(_ownerProcessId.Value))
        {
            return;
        }

        _ownerMonitorTimer?.Stop();

        if (MainWindow is CommonStartupWindow window)
        {
            window.PrepareForProcessExit();
            window.Close();
            return;
        }

        Shutdown();
    }

    private static int? ParseOwnerProcessId(string[] args)
    {
        if (args == null || args.Length == 0)
        {
            return null;
        }

        var ownerPidIndex = Array.FindIndex(args, arg => string.Equals(arg, "--owner-pid", StringComparison.OrdinalIgnoreCase));
        if (ownerPidIndex < 0 || ownerPidIndex + 1 >= args.Length)
        {
            return null;
        }

        return int.TryParse(args[ownerPidIndex + 1], out var pid) && pid > 0 ? pid : (int?)null;
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using (var process = Process.GetProcessById(processId))
            {
                return !process.HasExited;
            }
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
