using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PackageManager.Function.StartupTool;

namespace PackageManager.Services
{
    internal sealed class CommonStartupWindowManager
    {
        private readonly DataPersistenceService _persistenceService = new DataPersistenceService();
        private CommonStartupWindow _window;

        public void ShowOrActivate()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(ShowOrActivate));
                return;
            }

            if (_window == null)
            {
                _window = new CommonStartupWindow(_persistenceService);
                _window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                _window.Closed += StartupWindow_Closed;
                _window.Show();
                BringToFront(_window);
                return;
            }

            BringToFront(_window);
        }

        private void StartupWindow_Closed(object sender, EventArgs e)
        {
            if (ReferenceEquals(_window, sender))
            {
                _window.Closed -= StartupWindow_Closed;
                _window = null;
            }
        }

        private static void BringToFront(Window window)
        {
            if (window == null)
            {
                return;
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            if (!window.IsVisible)
            {
                window.Show();
            }

            window.Topmost = true;
            window.Activate();
            window.Topmost = false;
            window.Focus();

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            ShowWindow(handle, SwShow);
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }

        private const int SwShow = 5;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
