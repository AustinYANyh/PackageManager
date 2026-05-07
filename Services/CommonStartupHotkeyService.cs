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
        private const int VkE = 0x45;
        private const int VkQ = 0x51;
        private const int VkControl = 0x11;
        private const int VkMenu = 0x12;
        private const int VkShift = 0x10;
        private const int VkLWin = 0x5B;
        private const int VkRWin = 0x5C;

        private readonly CommonStartupWindowManager _windowManager;
        private readonly FileSearchWindowManager _fileSearchWindowManager;
        private readonly LowLevelKeyboardProc _keyboardProc;
        private IntPtr _hookHandle = IntPtr.Zero;
        private bool _hotkeyConsumed;
        private bool _hotkeyPendingActivation;
        private int _pendingHotkeyVkCode;

        /// <summary>
        /// 初始化 <see cref="CommonStartupHotkeyService"/> 的新实例。
        /// </summary>
        /// <param name="windowManager">常用启动项窗口管理器。</param>
        /// <param name="fileSearchWindowManager">文件搜索窗口管理器。</param>
        public CommonStartupHotkeyService(CommonStartupWindowManager windowManager, FileSearchWindowManager fileSearchWindowManager)
        {
            _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
            _fileSearchWindowManager = fileSearchWindowManager ?? throw new ArgumentNullException(nameof(fileSearchWindowManager));
            _keyboardProc = KeyboardHookCallback;
        }

        /// <summary>
        /// 获取常用启动项热键的默认显示文本。
        /// </summary>
        public static string DefaultHotkeyDisplayText => "Ctrl+Q";

        /// <summary>
        /// 获取文件搜索热键的默认显示文本。
        /// </summary>
        public static string DefaultFileSearchHotkeyDisplayText => "Ctrl+E";

        /// <summary>
        /// 启动全局键盘钩子，开始监听热键。
        /// </summary>
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
                LoggingService.LogInfo($"全局热键已启用：{DefaultHotkeyDisplayText} 打开常用启动项，{DefaultFileSearchHotkeyDisplayText} 打开文件搜索");
            }
        }

        /// <summary>
        /// 停止全局键盘钩子。
        /// </summary>
        public void Stop()
        {
            if (_hookHandle == IntPtr.Zero)
                return;

            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            _hotkeyConsumed = false;
            _hotkeyPendingActivation = false;
            _pendingHotkeyVkCode = 0;
        }

        /// <summary>
        /// 释放资源，停止键盘钩子。
        /// </summary>
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
                    var isSupportedHotkey = keyInfo.VkCode == VkQ || keyInfo.VkCode == VkE;
                    if (isSupportedHotkey && IsOnlyControlPressed() && !_hotkeyConsumed)
                    {
                        _hotkeyConsumed = true;
                        _hotkeyPendingActivation = true;
                        _pendingHotkeyVkCode = keyInfo.VkCode;
                        // 注入假的 Ctrl KeyUp，打断 VS 等通过 RegisterHotKey 注册的热键序列。
                        // RegisterHotKey 优先级高于 WH_KEYBOARD_LL，仅靠返回非零值无法阻止。
                        InjectCtrlKeyUp();
                        return (IntPtr)1;
                    }
                }
                else if (message == WmKeyUp || message == WmSysKeyUp)
                {
                    if (keyInfo.VkCode == _pendingHotkeyVkCode && _pendingHotkeyVkCode != 0)
                    {
                        var shouldActivate = _hotkeyPendingActivation;
                        var hotkeyVkCode = _pendingHotkeyVkCode;
                        _hotkeyPendingActivation = false;
                        _hotkeyConsumed = false;
                        _pendingHotkeyVkCode = 0;

                        if (shouldActivate)
                        {
                            if (hotkeyVkCode == VkQ)
                            {
                                LoggingService.LogInfo($"触发常用启动项全局热键：{DefaultHotkeyDisplayText}");
                                ThreadPool.QueueUserWorkItem(_ => _windowManager.ShowOrActivate());
                            }
                            else if (hotkeyVkCode == VkE)
                            {
                                LoggingService.LogInfo($"触发文件搜索全局热键：{DefaultFileSearchHotkeyDisplayText}");
                                ThreadPool.QueueUserWorkItem(_ => _fileSearchWindowManager.ShowOrActivate());
                            }

                            return (IntPtr)1;
                        }
                    }
                    else if (keyInfo.VkCode == VkControl)
                    {
                        _hotkeyPendingActivation = false;
                        _hotkeyConsumed = false;
                        _pendingHotkeyVkCode = 0;
                    }
                }
            }

            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        /// <summary>
        /// 注入一个合成的 Ctrl KeyUp 事件，用于打断 VS 等通过 RegisterHotKey 注册的热键序列。
        /// KEYEVENTF_KEYUP | KEYEVENTF_SCANCODE，标记为合成输入（dwExtraInfo=0）。
        /// </summary>
        private static void InjectCtrlKeyUp()
        {
            var input = new Input
            {
                Type = 1, // INPUT_KEYBOARD
                Ki = new KeyboardInput
                {
                    Vk = VkControl,
                    Scan = 0x1D, // Left Ctrl scan code
                    Flags = 0x0002, // KEYEVENTF_KEYUP
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            };
            SendInput(1, ref input, Marshal.SizeOf<Input>());
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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, ref Input pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort Vk;
            public ushort Scan;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public KeyboardInput Ki;
        }
    }
}
