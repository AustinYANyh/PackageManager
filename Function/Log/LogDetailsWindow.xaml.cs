using System.Windows;
using PackageManager.Models;

namespace PackageManager.Function.Log
{
    public partial class LogDetailsWindow : Window
    {
        private readonly LogEntry _entry;

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
