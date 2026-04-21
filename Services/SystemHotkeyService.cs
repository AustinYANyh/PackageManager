using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace PackageManager.Services
{
    /// <summary>
    /// 基于 RegisterHotKey 的系统级热键服务。
    /// RegisterHotKey 注册的热键优先级高于一切用户态程序（包括 VS、Rider、JVM 等），
    /// 系统会在任何前台程序之前将 WM_HOTKEY 投递到注册窗口，无法被其他程序拦截。
    /// </summary>
    internal sealed class SystemHotkeyService : IDisposable
    {
        private const int WmHotkey = 0x0312;
        private const int HotkeyIdCtrlQ = 0x4001;
        private const int HotkeyIdCtrlE = 0x4002;
        private const uint ModControl = 0x0002;
        private const uint ModNoRepeat = 0x4000;
        private const int VkQ = 0x51;
        private const int VkE = 0x45;

        private readonly CommonStartupWindowManager _windowManager;
        private readonly FileSearchWindowManager _fileSearchWindowManager;
        private HwndSource _hwndSource;
        private bool _ctrlQRegistered;
        private bool _ctrlERegistered;

        public SystemHotkeyService(CommonStartupWindowManager windowManager, FileSearchWindowManager fileSearchWindowManager)
        {
            _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
            _fileSearchWindowManager = fileSearchWindowManager ?? throw new ArgumentNullException(nameof(fileSearchWindowManager));
        }

        public void Start()
        {
            if (_hwndSource != null) return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (dispatcher.CheckAccess())
                CreateAndRegister();
            else
                dispatcher.Invoke(new Action(CreateAndRegister));
        }

        public void Stop()
        {
            if (_hwndSource == null) return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (dispatcher.CheckAccess())
                UnregisterAndDestroy();
            else
                dispatcher.Invoke(new Action(UnregisterAndDestroy));
        }

        public void Dispose() => Stop();

        private void CreateAndRegister()
        {
            var param = new HwndSourceParameters("SystemHotkeyService")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0x08000000, // WS_EX_NOACTIVATE
                ParentWindow = IntPtr.Zero,
            };

            _hwndSource = new HwndSource(param);
            _hwndSource.AddHook(WndProc);

            // MOD_CONTROL | MOD_NOREPEAT，NOREPEAT 防止长按时重复触发
            _ctrlQRegistered = RegisterHotKey(_hwndSource.Handle, HotkeyIdCtrlQ, ModControl | ModNoRepeat, (uint)VkQ);
            if (!_ctrlQRegistered)
            {
                var err = Marshal.GetLastWin32Error();
                LoggingService.LogWarning($"RegisterHotKey Ctrl+Q 失败，Win32Error={err}（可能被其他程序占用）");
            }
            else
            {
                LoggingService.LogInfo("系统热键 Ctrl+Q 注册成功");
            }

            if (UserFeatureAccessService.CanUseAustinOnlyFeatures)
            {
                _ctrlERegistered = RegisterHotKey(_hwndSource.Handle, HotkeyIdCtrlE, ModControl | ModNoRepeat, (uint)VkE);
                if (!_ctrlERegistered)
                {
                    var err = Marshal.GetLastWin32Error();
                    LoggingService.LogWarning($"RegisterHotKey Ctrl+E 失败，Win32Error={err}（可能被其他程序占用）");
                }
                else
                {
                    LoggingService.LogInfo("系统热键 Ctrl+E 注册成功");
                }
            }
        }

        private void UnregisterAndDestroy()
        {
            if (_hwndSource == null) return;

            if (_ctrlQRegistered)
            {
                UnregisterHotKey(_hwndSource.Handle, HotkeyIdCtrlQ);
                _ctrlQRegistered = false;
            }
            if (_ctrlERegistered)
            {
                UnregisterHotKey(_hwndSource.Handle, HotkeyIdCtrlE);
                _ctrlERegistered = false;
            }

            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey)
            {
                var id = wParam.ToInt32();
                if (id == HotkeyIdCtrlQ)
                {
                    LoggingService.LogInfo("系统热键触发：Ctrl+Q");
                    ThreadPool.QueueUserWorkItem(_ => _windowManager.ShowOrActivate());
                    handled = true;
                }
                else if (id == HotkeyIdCtrlE && UserFeatureAccessService.CanUseAustinOnlyFeatures)
                {
                    LoggingService.LogInfo("系统热键触发：Ctrl+E");
                    ThreadPool.QueueUserWorkItem(_ => _fileSearchWindowManager.ShowOrActivate());
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
