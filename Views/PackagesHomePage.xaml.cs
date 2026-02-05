using System;
using System.Windows;
using System.Windows.Controls;
using PackageManager.Function.CsvTool;
using PackageManager.Function.DnsTool;
using PackageManager.Function.UnlockTool;
using PackageManager.Function.SlnTool;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace PackageManager.Views
{
    /// <summary>
    /// 包列表主页：承载包列表与右侧快捷操作面板
    /// 继承 MainWindow 的 DataContext，不在此处重置。
    /// </summary>
    public partial class PackagesHomePage : Page
    {
        public PackagesHomePage()
        {
            InitializeComponent();
        }

        // 公开内部网格以便主窗口进行筛选交互
        public CustomControlLibrary.CustomControl.Controls.DataGrid.CDataGrid PackageGrid => PackageDataGrid;

        private void OpenCsvCryptoWindowButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new CsvCryptoWindow();
                win.Owner = Window.GetWindow(this);
                win.Show();
            }
            catch
            {
                MessageBox.Show("打开CSV加解密窗口失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenDnsSettingsWindowButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new DnsSettingsWindow { Owner = Window.GetWindow(this) };
                win.ShowDialog();
            }
            catch
            {
                MessageBox.Show("打开DNS设置窗口失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void FinalizeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var main = Window.GetWindow(this) as MainWindow;
                if (main == null)
                {
                    MessageBox.Show("未找到主窗口，无法执行定版操作", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                await main.FinalizeSelectedPackageAsync();
            }
            catch
            {
                MessageBox.Show("定版执行失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenRevitLogsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var main = Window.GetWindow(this) as MainWindow;
                if (main == null)
                {
                    MessageBox.Show("未找到主窗口", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var pkg = main.LatestActivePackage;
                string revitDir = null;
                if (pkg != null && !string.IsNullOrWhiteSpace(pkg.SelectedExecutableVersion))
                {
                    var av = pkg.AvailableExecutableVersions?.FirstOrDefault(x => x.DisPlayName == pkg.SelectedExecutableVersion);
                    var ver = av?.Version;
                    if (string.IsNullOrWhiteSpace(ver))
                    {
                        var m = Regex.Match(pkg.SelectedExecutableVersion ?? string.Empty, "(\\d{4})");
                        ver = m.Success ? m.Groups[1].Value : null;
                    }
                    if (!string.IsNullOrWhiteSpace(ver))
                    {
                        var baseLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                        revitDir = Path.Combine(baseLocal, "Autodesk", "Revit", $"Autodesk Revit {ver}", "Journals");
                    }
                }

                var productLogDir = Path.Combine(Path.GetTempPath(), "HongWaSoftLog");

                var page = new ProductLogsPage(productLogDir);
                page.SetRevitJournalDir(revitDir);
                if (page is ICentralPage icp)
                {
                    icp.RequestExit += () => main.GetType().GetMethod("NavigateHome", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(main, null);
                }
                
                main.GetType().GetMethod("NavigateTo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(main,
                [
                    page,
                ]);

                main.UpdateLeftNavSelection("产品日志");
            }
            catch
            {
                MessageBox.Show("打开Revit日志失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenUnlockFilesWindowButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new UnlockFilesWindow { Owner = Window.GetWindow(this) };
                win.Show();
            }
            catch
            {
                MessageBox.Show("打开解除占用窗口失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenSlnUpdateWindowButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new SlnUpdateWindow { Owner = Window.GetWindow(this) };
                win.Show();
            }
            catch
            {
                MessageBox.Show("打开编译顺序窗口失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenRevitActivationToolButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var asm = typeof(PackagesHomePage).Assembly;
                var names = asm.GetManifestResourceNames();
                var suffixes = new[]
                {
                    "AdskUAT.exe",
                    "BDGroupCore.bpf",
                    "Uninstall.exe",
                    "启用日志跟踪EnableLogTrack.cmd",
                    "疑难解答Faqs.txt",
                    "禁用日志跟踪DisableLogTrack.cmd"
                };
                var targetDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackageManager", "AutodeskActivation");
                System.IO.Directory.CreateDirectory(targetDir);
                try { System.IO.Directory.CreateDirectory(System.IO.Path.Combine(targetDir, "Logs")); } catch { }
                foreach (var suffix in suffixes)
                {
                    var name = names.FirstOrDefault(n =>
                        n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                        n.Contains("Assets.Tools.Autodesk.Universal.Activation.Tools"));
                    if (string.IsNullOrEmpty(name)) continue;
                    var path = System.IO.Path.Combine(targetDir, suffix);
                    if (!System.IO.File.Exists(path))
                    {
                        using (var s = asm.GetManifestResourceStream(name))
                        using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read))
                        {
                            s.CopyTo(fs);
                        }
                    }
                }
                var exePath = System.IO.Path.Combine(targetDir, "AdskUAT.exe");
                if (!System.IO.File.Exists(exePath))
                {
                    MessageBox.Show("未找到资源：AdskUAT.exe", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    WorkingDirectory = targetDir
                };
                Process.Start(psi);
            }
            catch
            {
                MessageBox.Show("运行Revit破解工具失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
