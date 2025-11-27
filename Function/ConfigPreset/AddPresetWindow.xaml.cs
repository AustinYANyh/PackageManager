using System.Windows;

namespace PackageManager.Function.ConfigPreset
{
    public partial class AddPresetWindow : Window
    {
        public Models.ConfigPreset ResultPreset { get; private set; }

        public AddPresetWindow()
        {
            InitializeComponent();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var name = NameText?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("请填写名称", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var raw = RawIniText?.Text;
            if (string.IsNullOrWhiteSpace(raw))
            {
                MessageBox.Show("请粘贴完整的配置文本", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ResultPreset = new Models.ConfigPreset
            {
                Name = name,
                RawIniContent = raw,
                IsBuiltIn = false,
            };

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}