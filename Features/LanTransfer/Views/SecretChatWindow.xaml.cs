using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PackageManager.Services;

namespace PackageManager.Views;

/// <summary>
/// 密语（加密聊天）窗口，提供端到端加密的即时通讯界面。
/// 已读判定基于视口：消息气泡进入视口并停留足够时间才标记已读；失焦时已读消息立即焚毁。
/// </summary>
public partial class SecretChatWindow : Window
{
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const uint WdaMonitor = 0x00000001;
    private static readonly TimeSpan VisibilityDwell = TimeSpan.FromMilliseconds(1000);
    private readonly LanTransferService _service;
    private readonly SecretChatSession _session;
    private readonly Dictionary<string, DateTime> visibleSince = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer visibilityTimer;

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
        Activated += SecretChatWindow_Activated;
        Deactivated += SecretChatWindow_Deactivated;
        Closed += SecretChatWindow_Closed;
        _session.Messages.CollectionChanged += Messages_CollectionChanged;

        visibilityTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        visibilityTimer.Tick += VisibilityTimer_Tick;
    }

    /// <summary>
    /// 获取当前密语会话实例。
    /// </summary>
    public SecretChatSession Session => _session;

    private void SecretChatWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _session.IsProtected = TryEnableCaptureProtection();
        _service.SetSecretChatWindowState(_session, true, IsActive);
        if (IsActive)
        {
            visibilityTimer.Start();
        }

        ScrollToFirstUnreadOrEnd();
    }

    private void SecretChatWindow_Activated(object sender, EventArgs e)
    {
        PrivacyOverlay.Visibility = Visibility.Collapsed;
        _service.SetSecretChatWindowState(_session, true, true);
        visibilityTimer.Start();
    }

    private void SecretChatWindow_Deactivated(object sender, EventArgs e)
    {
        visibilityTimer.Stop();
        visibleSince.Clear();
        _service.SetSecretChatWindowState(_session, true, false);
        PrivacyOverlay.Visibility = Visibility.Visible;
    }

    private void SecretChatWindow_Closed(object sender, EventArgs e)
    {
        visibilityTimer.Stop();
        _session.Messages.CollectionChanged -= Messages_CollectionChanged;
        _service.SetSecretChatWindowState(_session, false, false);
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
        if (e.Action == NotifyCollectionChangedAction.Add && IsActive)
        {
            ScrollMessagesToEnd();
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

    /// <summary>
    /// 视口判定核心：窗口前台时轮询未读气泡与视口的交集，停留超过阈值后批量标记已读。
    /// </summary>
    private void VisibilityTimer_Tick(object sender, EventArgs e)
    {
        if (!IsActive || _session == null || MessagesItemsControl == null || MessagesScrollViewer == null)
        {
            return;
        }

        var viewport = new Rect(
            new Point(0, 0),
            new Size(MessagesScrollViewer.ViewportWidth, MessagesScrollViewer.ViewportHeight));
        if (viewport.Height <= 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var readyToRead = new List<SecretChatMessage>();
        foreach (var message in _session.Messages.Where(m => m.IsIncomingUnread).ToList())
        {
            var container = MessagesItemsControl.ItemContainerGenerator.ContainerFromItem(message) as ContentPresenter;
            if (container == null || container.ActualHeight <= 0)
            {
                visibleSince.Remove(message.MessageId);
                continue;
            }

            var bounds = container.TransformToAncestor(MessagesScrollViewer).TransformBounds(new Rect(container.RenderSize));
            var overlap = Rect.Intersect(viewport, bounds);
            var visible = !overlap.IsEmpty && overlap.Height >= Math.Max(20, bounds.Height * 0.5);
            if (!visible)
            {
                visibleSince.Remove(message.MessageId);
                continue;
            }

            if (!visibleSince.TryGetValue(message.MessageId, out var since))
            {
                visibleSince[message.MessageId] = now;
                continue;
            }

            if (now - since >= VisibilityDwell)
            {
                visibleSince.Remove(message.MessageId);
                readyToRead.Add(message);
            }
        }

        if (readyToRead.Count > 0)
        {
            _ = _service.MarkSecretMessagesReadAsync(_session, readyToRead);
        }
    }

    private void ScrollToFirstUnreadOrEnd()
    {
        var firstUnread = _session.Messages.FirstOrDefault(m => m.IsIncomingUnread);
        if (firstUnread != null)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var container = MessagesItemsControl.ItemContainerGenerator.ContainerFromItem(firstUnread) as FrameworkElement;
                container?.BringIntoView();
            }), DispatcherPriority.Loaded);
            return;
        }

        ScrollMessagesToEnd();
    }

    private void ScrollMessagesToEnd()
    {
        MessagesScrollViewer?.ScrollToEnd();
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
