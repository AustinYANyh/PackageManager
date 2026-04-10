using System.Windows;

namespace PackageManager.Function.StartupTool;

public partial class StartupItemEditWindow : Window
{
    public StartupItemVm Result { get; private set; }

    public StartupItemEditWindow(StartupItemVm vm)
    {
        InitializeComponent();
        NameBox.Text = vm.Name ?? "";
        PathBox.Text = vm.FullPath ?? "";
        ArgsBox.Text = vm.Arguments ?? "";
        NoteBox.Text = vm.Note ?? "";
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择程序或脚本",
            Filter = "可执行文件|*.exe;*.bat;*.cmd;*.ps1;*.lnk|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            PathBox.Text = dlg.FileName;
            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show("请输入名称。", "编辑启动项", MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(PathBox.Text))
        {
            MessageBox.Show("请输入或选择路径。", "编辑启动项", MessageBoxButton.OK, MessageBoxImage.Warning);
            PathBox.Focus();
            return;
        }

        Result = new StartupItemVm
        {
            Name = NameBox.Text.Trim(),
            FullPath = PathBox.Text.Trim(),
            Arguments = ArgsBox.Text.Trim(),
            Note = NoteBox.Text.Trim()
        };
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
