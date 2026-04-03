using System.Windows;
using PackageManager.Models;

namespace PackageManager.Function.Log
{
    /// <summary>
    /// 日志详情窗口，用于查看单条日志的详细信息。
    /// </summary>
    public partial class LogDetailsWindow : Window
    {
        private readonly LogEntry _entry;

        /// <summary>
        /// 初始化 <see cref="LogDetailsWindow"/> 的新实例。
        /// </summary>
        /// <param name="entry">要显示的日志条目。</param>
        public LogDetailsWindow(LogEntry entry)
        {
            InitializeComponent();
            _entry = entry;
            HeaderText.Text = $"{_entry.Timestamp} [{_entry.Level}] {_entry.Message}";
            DetailsText.Text = string.IsNullOrEmpty(_entry.Details) ? "(无详情)" : _entry.Details;
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = DetailsText.Text ?? string.Empty;
                Clipboard.SetText(text);
            }
            catch
            {
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
