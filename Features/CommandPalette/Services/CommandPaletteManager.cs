using System;
using System.Windows;

namespace PackageManager.Features.CommandPalette.Services
{
    /// <summary>
    /// 命令面板浮层窗口的管理者：懒创建、显示/激活、隐藏复用（避免重复初始化 WebView2）。
    /// 角色类似 CommonStartupWindowManager / FileSearchWindowManager。
    /// </summary>
    internal sealed class CommandPaletteManager
    {
        private Views.CommandPaletteWindow _window;

        /// <summary>热键触发：显示并聚焦命令面板。</summary>
        public void ShowOrActivate()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            if (dispatcher.CheckAccess())
                ShowCore();
            else
                dispatcher.Invoke(new Action(ShowCore));
        }

        public void Preload()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            if (dispatcher.CheckAccess()) PreloadCore();
            else dispatcher.Invoke(new Action(PreloadCore));
        }

        private void PreloadCore()
        {
            if (_window != null) return;
            _window = new Views.CommandPaletteWindow();
            _window.Preload();
        }

        private void ShowCore()
        {
            if (_window == null)
            {
                _window = new Views.CommandPaletteWindow();
            }
            _window.ShowPalette();
        }

        public void Shutdown()
        {
            try
            {
                _window?.ClosePalette();
            }
            catch
            {
            }
            _window = null;
        }
    }
}
