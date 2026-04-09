using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using PackageManager.Function.CsvTool;
using PackageManager.Function.DnsTool;
using PackageManager.Function.SlnTool;
using PackageManager.Function.UnlockTool;
using PackageManager.Services;

namespace PackageManager.Views;

/// <summary>
/// 包列表主页：承载包列表与右侧快捷操作面板
/// 继承 MainWindow 的 DataContext，不在此处重置。
/// </summary>
public partial class PackagesHomePage : Page
{
    /// <summary>
    /// 初始化 <see cref="PackagesHomePage"/> 的新实例。
    /// </summary>
    public PackagesHomePage()
    {
        InitializeComponent();
        if (!Environment.UserName.Equals("AustinYanyh", StringComparison.OrdinalIgnoreCase))
            KiroProxyButton.IsEnabled = false;
    }

    /// <summary>
    /// 获取内部包数据网格控件，供主窗口进行筛选交互。
    /// </summary>
    public CustomControlLibrary.CustomControl.Controls.DataGrid.CDataGrid PackageGrid => PackageDataGrid;

    private static string DetectVcs(string path)
    {
        if (Directory.Exists(Path.Combine(path, ".git")))
        {
            return "Git";
        }

        if (Directory.Exists(Path.Combine(path, ".svn")))
        {
            return "svn";
        }

        return null;
    }

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

    private async void TriggerJenkinsBuildButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var main = Window.GetWindow(this) as MainWindow;
            if (main == null)
            {
                MessageBox.Show("未找到主窗口，无法触发 Jenkins 编译", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var package = main.LatestActivePackage;
            if (package == null)
            {
                MessageBox.Show("请先选择需要编译的产品包", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            package.StatusText = $"正在触发 {package.ProductName} 的 Jenkins 编译...";

            var data = new DataPersistenceService();
            var settings = data.LoadSettings();
            var service = new JenkinsBuildService(settings);
            var result = await service.TriggerBuildAsync(package,
                                                         message => { Dispatcher.Invoke(() => package.StatusText = message); });

            package.StatusText = result.Message;

            if (!result.IsSuccess)
            {
                MessageBox.Show(result.Message, "Jenkins 编译", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"触发 Jenkins 编译失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
            if ((pkg != null) && !string.IsNullOrWhiteSpace(pkg.SelectedExecutableVersion))
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
                icp.RequestExit += () =>
                    main.GetType().GetMethod("NavigateHome", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        ?.Invoke(main, null);
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
                "禁用日志跟踪DisableLogTrack.cmd",
            };
            var targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                         "PackageManager",
                                         "AutodeskActivation");
            Directory.CreateDirectory(targetDir);
            try
            {
                Directory.CreateDirectory(Path.Combine(targetDir, "Logs"));
            }
            catch
            {
            }

            foreach (var suffix in suffixes)
            {
                var name = names.FirstOrDefault(n =>
                                                    n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                                                    n.Contains("Assets.Tools.Autodesk.Universal.Activation.Tools"));
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var path = Path.Combine(targetDir, suffix);
                if (!File.Exists(path))
                {
                    using (var s = asm.GetManifestResourceStream(name))
                    using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        s.CopyTo(fs);
                    }
                }
            }

            var exePath = Path.Combine(targetDir, "AdskUAT.exe");
            if (!File.Exists(exePath))
            {
                MessageBox.Show("未找到资源：AdskUAT.exe", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WorkingDirectory = targetDir,
            };
            Process.Start(psi);
        }
        catch
        {
            MessageBox.Show("运行Revit破解工具失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenKiroProxyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var asm = typeof(PackagesHomePage).Assembly;
            var names = asm.GetManifestResourceNames();
            var files = new[] { "a.cmd", "config.json", "credentials.json", "kiro-rs.exe", "kiro_stats.json" };
            var targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackageManager", "kiro-rs");
            Directory.CreateDirectory(targetDir);
            foreach (var file in files)
            {
                var resName = names.FirstOrDefault(n =>
                                                       n.EndsWith(file, StringComparison.OrdinalIgnoreCase) &&
                                                       n.Contains("kiro"));
                if (string.IsNullOrEmpty(resName))
                {
                    continue;
                }

                var dest = Path.Combine(targetDir, file);
                if (!File.Exists(dest))
                {
                    using (var s = asm.GetManifestResourceStream(resName))
                    using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        s.CopyTo(fs);
                    }
                }
            }

            var cmdPath = Path.Combine(targetDir, "a.cmd");
            if (!File.Exists(cmdPath))
            {
                MessageBox.Show("未找到资源：a.cmd", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = cmdPath,
                UseShellExecute = true,
                WorkingDirectory = targetDir,
            });
        }
        catch
        {
            MessageBox.Show("运行Kiro反代失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenRevitFileCleanupWindowButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = AdminElevationService.ExtractEmbeddedTool("MftScanner.exe", "MftScanner.exe");
            if (string.IsNullOrEmpty(exePath))
            {
                MessageBox.Show("未找到 MftScanner.exe 工具，请检查安装。", "清理RVT", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户取消了 UAC 提权
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "打开 Revit 文件清理窗口失败");
            MessageBox.Show($"打开清理窗口失败：{ex.Message}", "清理RVT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenVcsMappingButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rootDir = FolderPickerService.PickFolder("选择根目录（包含各子项目文件夹）");
            if (rootDir == null)
                return;

            var vcsXmlFiles = Directory.GetFiles(rootDir, "vcs.xml", SearchOption.AllDirectories);
            var vcsXmlPath = vcsXmlFiles.FirstOrDefault(f =>
            {
                try
                {
                    var doc2 = XDocument.Load(f);
                    return doc2.Descendants("component")
                               .Any(c => (string)c.Attribute("name") == "VcsDirectoryMappings");
                }
                catch
                {
                    return false;
                }
            });

            if (vcsXmlPath == null)
            {
                MessageBox.Show("未在根目录下找到包含 VcsDirectoryMappings 的 vcs.xml", "VCS映射", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var doc = XDocument.Load(vcsXmlPath);
            var component = doc.Descendants("component")
                               .First(c => (string)c.Attribute("name") == "VcsDirectoryMappings");

            // 收集已有映射的目录（相对路径形式）
            var existingDirs = new System.Collections.Generic.HashSet<string>(component.Elements("mapping")
                                                                                       .Select(m => (string)m.Attribute("directory"))
                                                                                       .Where(d => !string.IsNullOrEmpty(d)),
                                                                              StringComparer.OrdinalIgnoreCase);

            int updated = 0;
            int added = 0;

            // 1. 更新已有映射
            foreach (var mapping in component.Elements("mapping").ToList())
            {
                var dir = (string)mapping.Attribute("directory");
                if (string.IsNullOrEmpty(dir) || (dir == "$PROJECT_DIR$"))
                {
                    continue;
                }

                var absPath = dir.Replace("$PROJECT_DIR$", rootDir).Replace('/', Path.DirectorySeparatorChar);
                if (!Directory.Exists(absPath))
                {
                    continue;
                }

                string detectedVcs = DetectVcs(absPath);
                if (detectedVcs == null)
                {
                    continue;
                }

                var current = (string)mapping.Attribute("vcs");
                if (!string.Equals(current, detectedVcs, StringComparison.OrdinalIgnoreCase))
                {
                    mapping.SetAttributeValue("vcs", detectedVcs);
                    updated++;
                }
            }

            // 2. 扫描根目录下所有一级子文件夹，补充缺失的映射
            foreach (var subDir in Directory.GetDirectories(rootDir))
            {
                var detectedVcs = DetectVcs(subDir);
                if (detectedVcs == null)
                {
                    continue;
                }

                var folderName = Path.GetFileName(subDir);
                var relKey = $"$PROJECT_DIR$/{folderName}";

                if (existingDirs.Contains(relKey))
                {
                    continue;
                }

                component.Add(new XElement("mapping",
                                           new XAttribute("directory", relKey),
                                           new XAttribute("vcs", detectedVcs)));
                existingDirs.Add(relKey);
                added++;
            }

            doc.Save(vcsXmlPath);
            MessageBox.Show($"VCS映射已更新：修正 {updated} 项，新增 {added} 项。\n文件：{vcsXmlPath}", "VCS映射", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"VCS映射失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ToggleGitProxyButton_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        var main = Window.GetWindow(this) as MainWindow;
        var package = main?.LatestActivePackage;

        try
        {
            if (button != null)
            {
                button.IsEnabled = false;
            }

            if (package != null)
            {
                package.StatusText = "正在切换 Git 代理...";
            }

            var result = await GitProxyService.ToggleAsync();
            if (package != null)
            {
                package.StatusText = result.Message;
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "切换 Git 代理失败");
            if (package != null)
            {
                package.StatusText = $"Git代理切换失败：{ex.Message}";
            }

            MessageBox.Show($"切换 Git 代理失败：{ex.Message}", "Git代理", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (button != null)
            {
                button.IsEnabled = true;
            }
        }
    }
}