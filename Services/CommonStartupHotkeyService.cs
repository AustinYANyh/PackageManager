using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PackageManager.Services
{
    internal sealed class CommonStartupHotkeyService : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmSysKeyDown = 0x0104;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyUp = 0x0105;
        private const int VkQ = 0x51;
        private const int VkControl = 0x11;
        private const int VkMenu = 0x12;
        private const int VkShift = 0x10;
        private const int VkLWin = 0x5B;
        private const int VkRWin = 0x5C;

        private readonly CommonStartupWindowManager _windowManager;
        private readonly LowLevelKeyboardProc _keyboardProc;
        private IntPtr _hookHandle = IntPtr.Zero;
        private bool _hotkeyConsumed;
        private bool _hotkeyPendingActivation;

        public CommonStartupHotkeyService(CommonStartupWindowManager windowManager)
        {
            _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
            _keyboardProc = KeyboardHookCallback;
        }

        public static string DefaultHotkeyDisplayText => "Ctrl+Q";

        public void Start()
        {
            if (_hookHandle != IntPtr.Zero)
                return;

            _hookHandle = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, GetCurrentModuleHandle(), 0);
            if (_hookHandle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                LoggingService.LogWarning($"注册常用启动项全局热键钩子失败，Win32Error={error}");
            }
            else
            {
                LoggingService.LogInfo($"常用启动项全局热键已启用：{DefaultHotkeyDisplayText}");
            }
        }

        public void Stop()
        {
            if (_hookHandle == IntPtr.Zero)
                return;

            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            _hotkeyConsumed = false;
            _hotkeyPendingActivation = false;
        }

        public void Dispose()
        {
            Stop();
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var message = wParam.ToInt32();
                var keyInfo = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);

                if (message == WmKeyDown || message == WmSysKeyDown)
                {
                    if (keyInfo.VkCode == VkQ && IsOnlyControlPressed() && !_hotkeyConsumed)
                    {
                        _hotkeyConsumed = true;
                        _hotkeyPendingActivation = true;
                        return (IntPtr)1;
                    }
                }
                else if (message == WmKeyUp || message == WmSysKeyUp)
                {
                    if (keyInfo.VkCode == VkQ)
                    {
                        var shouldActivate = _hotkeyPendingActivation;
                        _hotkeyPendingActivation = false;
                        _hotkeyConsumed = false;

                        if (shouldActivate)
                        {
                            LoggingService.LogInfo($"触发常用启动项全局热键：{DefaultHotkeyDisplayText}");
                            ThreadPool.QueueUserWorkItem(_ => _windowManager.ShowOrActivate());
                            return (IntPtr)1;
                        }
                    }
                    else if (keyInfo.VkCode == VkControl)
                    {
                        _hotkeyPendingActivation = false;
                        _hotkeyConsumed = false;
                    }
                }
            }

            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        private static bool IsOnlyControlPressed()
        {
            return IsKeyPressed(VkControl)
                   && !IsKeyPressed(VkMenu)
                   && !IsKeyPressed(VkShift)
                   && !IsKeyPressed(VkLWin)
                   && !IsKeyPressed(VkRWin);
        }

        private static bool IsKeyPressed(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        private static IntPtr GetCurrentModuleHandle()
        {
            return GetModuleHandle(null);
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            public int VkCode;
            public int ScanCode;
            public int Flags;
            public int Time;
            public IntPtr DwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
    }
}
