using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace PackageManager.Services
{
    /// <summary>
    /// 基于 Raw Input 的热键监听源。
    /// Raw Input 直接从 HID 设备读取，绕过 JVM 等用户态程序的按键拦截，
    /// 配合 RIDEV_INPUTSINK 在后台也能收到所有键盘输入。
    /// 作为 WH_KEYBOARD_LL 的补充，解决 Rider/IntelliJ 等 JVM 程序无法触发钩子的问题。
    /// </summary>
    internal sealed class RawInputHotkeySource : IDisposable
    {
        private const int WmInput = 0x00FF;
        private const int RimTypeKeyboard = 1;
        private const uint RidevInputsink = 0x00000100;
        private const uint WmKeydown = 0x0100;
        private const uint WmSyskeydown = 0x0104;
        private const uint WmKeyup = 0x0101;
        private const uint WmSyskeyup = 0x0105;
        private const int VkQ = 0x51;
        private const int VkE = 0x45;
        private const int VkControl = 0x11;
        private const int VkLControl = 0xA2;
        private const int VkRControl = 0xA3;
        private const int VkMenu = 0x12;
        private const int VkShift = 0x10;
        private const int VkLWin = 0x5B;
        private const int VkRWin = 0x5C;

        private readonly CommonStartupWindowManager _windowManager;
        private readonly FileSearchWindowManager _fileSearchWindowManager;
        private HwndSource _hwndSource;

        // 用 Raw Input 自己跟踪修饰键状态，不依赖 GetAsyncKeyState
        private bool _ctrlDown;
        private bool _altDown;
        private bool _shiftDown;
        private bool _winDown;
        private bool _hotkeyArmed; // Ctrl+Q/E KeyDown 已记录，等 KeyUp 触发
        private int _armedVkCode;

        public RawInputHotkeySource(CommonStartupWindowManager windowManager, FileSearchWindowManager fileSearchWindowManager)
        {
            _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
            _fileSearchWindowManager = fileSearchWindowManager ?? throw new ArgumentNullException(nameof(fileSearchWindowManager));
        }

        public void Start()
        {
            if (_hwndSource != null)
                return;

            // 必须在 UI 线程创建 HwndSource
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (dispatcher.CheckAccess())
                CreateHwndSource();
            else
                dispatcher.Invoke(new Action(CreateHwndSource));
        }

        public void Stop()
        {
            if (_hwndSource == null) return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (dispatcher.CheckAccess())
                DestroyHwndSource();
            else
                dispatcher.Invoke(new Action(DestroyHwndSource));
        }

        public void Dispose() => Stop();

        private void CreateHwndSource()
        {
            var param = new HwndSourceParameters("RawInputHotkeySource")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0, // WS_OVERLAPPED，不可见
                ExtendedWindowStyle = 0x08000000, // WS_EX_NOACTIVATE
                ParentWindow = IntPtr.Zero,
                UsesPerPixelOpacity = false
            };

            _hwndSource = new HwndSource(param);
            _hwndSource.AddHook(WndProc);

            RegisterRawInput(_hwndSource.Handle);
        }

        private void DestroyHwndSource()
        {
            if (_hwndSource == null) return;
            UnregisterRawInput(_hwndSource.Handle);
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }

        private void RegisterRawInput(IntPtr hwnd)
        {
            var rid = new RawInputDevice
            {
                UsUsagePage = 0x01, // Generic Desktop Controls
                UsUsage = 0x06,     // Keyboard
                DwFlags = RidevInputsink,
                HwndTarget = hwnd
            };

            if (!RegisterRawInputDevices(ref rid, 1, Marshal.SizeOf<RawInputDevice>()))
            {
                var error = Marshal.GetLastWin32Error();
                LoggingService.LogWarning($"注册 Raw Input 热键监听失败，Win32Error={error}");
            }
            else
            {
                LoggingService.LogInfo("Raw Input 热键监听已启用");
            }
        }

        private void UnregisterRawInput(IntPtr hwnd)
        {
            var rid = new RawInputDevice
            {
                UsUsagePage = 0x01,
                UsUsage = 0x06,
                DwFlags = 0x00000001, // RIDEV_REMOVE
                HwndTarget = IntPtr.Zero
            };
            RegisterRawInputDevices(ref rid, 1, Marshal.SizeOf<RawInputDevice>());
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmInput)
            {
                ProcessRawInput(lParam);
            }
            return IntPtr.Zero;
        }

        private void ProcessRawInput(IntPtr hRawInput)
        {
            // 先查询所需缓冲区大小
            uint size = 0;
            GetRawInputData(hRawInput, 0x10000003 /* RID_INPUT */, IntPtr.Zero, ref size, Marshal.SizeOf<RawInputHeader>());
            if (size == 0) return;

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint written = GetRawInputData(hRawInput, 0x10000003, buffer, ref size, Marshal.SizeOf<RawInputHeader>());
                if (written == uint.MaxValue) return;

                var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
                if (header.Type != RimTypeKeyboard) return;

                // RawInput keyboard data 紧跟在 header 之后
                var keyboardOffset = Marshal.SizeOf<RawInputHeader>();
                var keyboard = Marshal.PtrToStructure<RawInputKeyboard>(buffer + keyboardOffset);

                HandleRawKey(keyboard);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private void HandleRawKey(RawInputKeyboard keyboard)
        {
            var vk = keyboard.VKey;
            var isKeyUp = (keyboard.Flags & 0x01) != 0; // RI_KEY_BREAK = 0x01

            // 跟踪修饰键状态（完全不依赖 GetAsyncKeyState，避免 JVM 程序干扰）
            if (vk == VkControl || vk == VkLControl || vk == VkRControl)
            {
                _ctrlDown = !isKeyUp;
                if (isKeyUp) { _hotkeyArmed = false; _armedVkCode = 0; }
                return;
            }
            if (vk == VkMenu)
            {
                _altDown = !isKeyUp;
                if (isKeyUp && _altDown == false) { _hotkeyArmed = false; _armedVkCode = 0; }
                return;
            }
            if (vk == VkShift)
            {
                _shiftDown = !isKeyUp;
                return;
            }
            if (vk == VkLWin || vk == VkRWin)
            {
                _winDown = !isKeyUp;
                return;
            }

            if (isKeyUp)
            {
                // KeyUp：如果是已 armed 的键，触发唤起
                if (_hotkeyArmed && vk == _armedVkCode)
                {
                    var vkCode = _armedVkCode;
                    _hotkeyArmed = false;
                    _armedVkCode = 0;

                    if (vkCode == VkQ)
                    {
                        LoggingService.LogInfo($"Raw Input 触发常用启动项热键：Ctrl+Q");
                        ThreadPool.QueueUserWorkItem(_ => _windowManager.ShowOrActivate());
                    }
                    else if (vkCode == VkE)
                    {
                        LoggingService.LogInfo($"Raw Input 触发文件搜索热键：Ctrl+E");
                        ThreadPool.QueueUserWorkItem(_ => _fileSearchWindowManager.ShowOrActivate());
                    }
                }
                return;
            }

            // KeyDown：检测 Ctrl+Q / Ctrl+E，且没有其他修饰键
            if (!_ctrlDown || _hotkeyArmed) return;
            if (IsModifierKey(vk)) return;
            if (IsOtherModifierPressed()) return;

            if (vk == VkQ || vk == VkE)
            {
                _hotkeyArmed = true;
                _armedVkCode = vk;
            }
        }

        private static bool IsModifierKey(int vk)
        {
            return vk == VkControl || vk == VkLControl || vk == VkRControl
                || vk == VkMenu || vk == VkShift
                || vk == VkLWin || vk == VkRWin;
        }

        private bool IsOtherModifierPressed()
        {
            // 不用 GetAsyncKeyState，完全依赖 Raw Input 自己跟踪的状态。
            // JVM 程序（Rider）会导致 GetAsyncKeyState 返回不准确的修饰键状态，
            // 改为只检查 Alt/Shift/Win，这些键的 Raw Input 事件同样会经过这里。
            return _altDown || _shiftDown || _winDown;
        }

        #region P/Invoke structs & imports

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDevice
        {
            public ushort UsUsagePage;
            public ushort UsUsage;
            public uint DwFlags;
            public IntPtr HwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputHeader
        {
            public uint Type;
            public uint Size;
            public IntPtr Device;
            public IntPtr WParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputKeyboard
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public ulong ExtraInformation;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(ref RawInputDevice pRawInputDevices, uint uiNumDevices, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, int cbSizeHeader);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        #endregion
    }
}
