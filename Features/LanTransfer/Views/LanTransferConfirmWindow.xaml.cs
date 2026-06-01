using System.Windows;
using PackageManager.Services;

namespace PackageManager.Views;

/// <summary>
/// 文件传输接收确认窗口。
/// </summary>
public partial class LanTransferConfirmWindow : Window
{
    /// <summary>
    /// 初始化 <see cref="LanTransferConfirmWindow"/> 的新实例。
    /// </summary>
    /// <param name="request">文件传输请求信息。</param>
    public LanTransferConfirmWindow(LanTransferRequest request)
    {
        InitializeComponent();
        Request = request;
        DataContext = this;
    }

    /// <summary>
    /// 获取关联的文件传输请求。
    /// </summary>
    public LanTransferRequest Request { get; }

    /// <summary>
    /// 获取发送者显示标签。
    /// </summary>
    public string SenderLabel => Request?.SenderLabel ?? "未知发送者";

    /// <summary>
    /// 获取保存目录的显示文本。
    /// </summary>
    public string SaveDirectory => $"保存到：{Request?.SaveDirectory ?? "-"}";

    /// <summary>
    /// 获取传输摘要文本，包含项数、大小和接收时间。
    /// </summary>
    public string SummaryText => $"共 {Request?.ItemCount ?? 0} 项，大小 {LanTransferFormatting.FormatSize(Request?.TotalBytes ?? 0)}，接收时间 {Request?.ReceivedAtText}";

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void RejectButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
