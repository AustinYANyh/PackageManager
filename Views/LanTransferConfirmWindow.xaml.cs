using System.Windows;
using PackageManager.Services;

namespace PackageManager.Views;

/// <summary>
/// 文件传输接收确认窗口。
/// </summary>
public partial class LanTransferConfirmWindow : Window
{
    public LanTransferConfirmWindow(LanTransferRequest request)
    {
        InitializeComponent();
        Request = request;
        DataContext = this;
    }

    public LanTransferRequest Request { get; }

    public string SenderLabel => Request?.SenderLabel ?? "未知发送者";

    public string SaveDirectory => $"保存到：{Request?.SaveDirectory ?? "-"}";

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
