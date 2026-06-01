using System;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PackageManager.Services;

namespace PackageManager.Views;

/// <summary>
/// 密语（加密聊天）窗口，提供端到端加密的即时通讯界面。
/// </summary>
public partial class SecretChatWindow : Window
{
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const uint WdaMonitor = 0x00000001;
    private readonly LanTransferService _service;
    private readonly SecretChatSession _session;

    /// <summary>
    /// 初始化 <see cref="SecretChatWindow"/> 的新实例。
    /// </summary>
    /// <param name="service">局域网传输服务实例。</param>
    /// <param name="session">密语会话实例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> 或 <paramref name="session"/> 为 null。</exception>
    public SecretChatWindow(LanTransferService service, SecretChatSession session)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        InitializeComponent();
        DataContext = _session;
        Loaded += SecretChatWindow_Loaded;
        Activated += (_, __) =>
        {
            PrivacyOverlay.Visibility = Visibility.Collapsed;
            _service.SetSecretChatWindowState(_session, true, true);
        };
        Deactivated += (_, __) =>
        {
            _service.SetSecretChatWindowState(_session, true, false);
            PrivacyOverlay.Visibility = Visibility.Visible;
        };
        Closed += (_, __) =>
        {
            _session.Messages.CollectionChanged -= Messages_CollectionChanged;
            _service.SetSecretChatWindowState(_session, false, false);
        };
        _session.Messages.CollectionChanged += Messages_CollectionChanged;
    }

    /// <summary>
    /// 获取当前密语会话实例。
    /// </summary>
    public SecretChatSession Session => _session;

    private void SecretChatWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _session.IsProtected = TryEnableCaptureProtection();
        _session.StatusText = _session.IsProtected
            ? "密语会话受截图保护"
            : "当前系统未启用截图保护，已禁止发送";
        _service.SetSecretChatWindowState(_session, true, IsActive);
        ScrollMessagesToEnd();
    }

    private bool TryEnableCaptureProtection()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        return SetWindowDisplayAffinity(handle, WdaExcludeFromCapture)
               || SetWindowDisplayAffinity(handle, WdaMonitor);
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        var text = MessageTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            MessageTextBox.Clear();
            await _service.SendSecretMessageAsync(_session, text);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"密语发送失败：{ex.Message}", "密语", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Messages_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        ScrollMessagesToEnd();
        if (_session.IsWindowActive)
        {
            _ = _service.MarkUnreadSecretMessagesReadAsync(_session);
        }
    }

    private void MessageTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            SendButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ScrollMessagesToEnd()
    {
        MessagesScrollViewer?.ScrollToEnd();
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
