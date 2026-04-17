using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using CustomControlLibrary.CustomControl.Controls.Navigation;
using PackageManager.Function.CsvTool;
using PackageManager.Function.DnsTool;
using PackageManager.Function.SlnTool;
using PackageManager.Function.UnlockTool;
using PackageManager.Models;
using PackageManager.Services;

namespace PackageManager.Views;

/// <summary>
/// 包列表主页：承载包列表与右侧快捷操作面板
/// 继承 MainWindow 的 DataContext，不在此处重置。
/// </summary>
public partial class PackagesHomePage : Page
{
    private readonly RevitProcessService _revitProcessService = new RevitProcessService();
    private readonly PackageUpdateService _packageUpdateService = new PackageUpdateService();
    private ApplicationVersion _pendingRevitVersion;
    private IReadOnlyList<RevitProcessInfo> _pendingRevitProcesses = Array.Empty<RevitProcessInfo>();

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

    private MainWindow GetMainWindow()
    {
        return Window.GetWindow(this) as MainWindow;
    }

    private PackageInfo GetSelectedPackage()
    {
        return GetMainWindow()?.LatestActivePackage;
    }

    private bool TryGetSelectedRevitVersion(out PackageInfo package, out ApplicationVersion applicationVersion)
    {
        package = GetSelectedPackage();
        applicationVersion = package?.GetSelectedApplicationVersion();
        return package != null && applicationVersion != null;
    }

    private static string BuildRevitProcessStatusText(ApplicationVersion applicationVersion, int processCount, bool hasUnresponsive)
    {
        var displayName = string.IsNullOrWhiteSpace(applicationVersion?.DisPlayName) ? "Revit" : applicationVersion.DisPlayName;
        return hasUnresponsive
            ? $"{displayName} 已运行（{processCount} 个进程，疑似无响应）"
            : $"{displayName} 已运行（{processCount} 个进程）";
    }

    private void OpenRevitButton_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (!(sender is FrameworkElement element))
            return;

        if (!TryGetSelectedRevitVersion(out _, out var applicationVersion))
        {
            element.ToolTip = "未找到可用的 Revit 版本";
            return;
        }

        if (string.IsNullOrWhiteSpace(applicationVersion.ExecutablePath) || !File.Exists(applicationVersion.ExecutablePath))
        {
            element.ToolTip = "当前所选 Revit 可执行文件不存在";
            return;
        }

        var processes = _revitProcessService.FindProcessesForExecutable(applicationVersion.ExecutablePath);
        if (processes.Count == 0)
        {
            element.ToolTip = $"{applicationVersion.DisPlayName} 未运行，点击将直接打开";
            return;
        }

        var hasUnresponsive = processes.Any(info => !info.IsResponding);
        element.ToolTip = hasUnresponsive
            ? $"{BuildRevitProcessStatusText(applicationVersion, processes.Count, true)}，点击可切换或结束"
            : $"{BuildRevitProcessStatusText(applicationVersion, processes.Count, false)}，点击可切换或管理";
    }

    private void OpenRevitButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedRevitVersion(out _, out var applicationVersion))
            return;

        var executablePath = applicationVersion.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            MessageBox.Show("可执行文件路径无效或文件不存在", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var processes = _revitProcessService.FindProcessesForExecutable(executablePath);
        if (processes.Count == 0)
        {
            LaunchRevitExecutable(executablePath);
            return;
        }

        if (sender is FrameworkElement target)
            ShowOpenRevitContextMenu(target, applicationVersion, processes);
    }

    private void ShowOpenRevitContextMenu(FrameworkElement target, ApplicationVersion applicationVersion, IReadOnlyList<RevitProcessInfo> processes)
    {
        _pendingRevitVersion = applicationVersion;
        _pendingRevitProcesses = processes ?? Array.Empty<RevitProcessInfo>();

        var processCount = _pendingRevitProcesses.Count;
        var hasUnresponsive = _pendingRevitProcesses.Any(info => !info.IsResponding);
        var canActivate = _pendingRevitProcesses.Any(info => info.HasMainWindow);

        var menu = new CContextMenu
        {
            PlacementTarget = target,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        menu.Items.Add(new CMenuItem
        {
            Header = BuildRevitProcessStatusText(applicationVersion, processCount, hasUnresponsive),
            IsEnabled = false
        });
        menu.Items.Add(new Separator());

        var switchItem = new CMenuItem
        {
            Header = canActivate ? "切换到现有 Revit" : "切换到现有 Revit（无可用窗口）",
            IsEnabled = canActivate
        };
        switchItem.Click += SwitchToExistingRevitMenuItem_Click;

        var restartItem = new CMenuItem
        {
            Header = hasUnresponsive ? "结束无响应进程并重新打开" : "结束并重新打开"
        };
        restartItem.Click += RestartRevitMenuItem_Click;

        var killItem = new CMenuItem
        {
            Header = processCount > 1 ? $"仅结束 {processCount} 个进程" : "仅结束进程"
        };
        killItem.Click += KillRevitMenuItem_Click;

        if (hasUnresponsive)
        {
            menu.Items.Add(restartItem);
            menu.Items.Add(switchItem);
        }
        else
        {
            menu.Items.Add(switchItem);
            menu.Items.Add(restartItem);
        }

        menu.Items.Add(killItem);
        menu.Items.Add(new CMenuItem { Header = "取消" });

        target.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void SwitchToExistingRevitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CloseRevitContextMenu(sender);

        var targetProcess = _pendingRevitProcesses
            .Where(info => info.HasMainWindow)
            .OrderByDescending(info => info.StartTime ?? DateTime.MinValue)
            .FirstOrDefault();
        if (targetProcess == null)
        {
            MessageBox.Show("未找到可激活的 Revit 窗口。", "打开Revit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_revitProcessService.TryActivateProcess(targetProcess))
        {
            MessageBox.Show("切换到现有 Revit 失败。", "打开Revit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RestartRevitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CloseRevitContextMenu(sender);
        await KillSelectedRevitProcessesAsync(restartAfterKill: true);
    }

    private async void KillRevitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CloseRevitContextMenu(sender);
        await KillSelectedRevitProcessesAsync(restartAfterKill: false);
    }

    private async Task KillSelectedRevitProcessesAsync(bool restartAfterKill)
    {
        var applicationVersion = _pendingRevitVersion;
        var processes = _pendingRevitProcesses ?? Array.Empty<RevitProcessInfo>();
        if (applicationVersion == null || processes.Count == 0)
            return;

        var pids = processes
            .Select(info => info.ProcessId)
            .Where(pid => pid > 0)
            .Distinct()
            .ToList();
        if (pids.Count == 0)
            return;

        var actionName = restartAfterKill ? "结束并重新打开 Revit" : "结束 Revit 进程";
        try
        {
            var killIssued = await _packageUpdateService.KillProcessesAsync(pids).ConfigureAwait(true);
            if (!killIssued)
            {
                MessageBox.Show($"{actionName}失败，可能是用户取消了权限确认或结束命令未成功发起。",
                                "打开Revit",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                return;
            }

            var exited = await WaitForProcessesExitAsync(pids, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            if (!exited)
            {
                MessageBox.Show("Revit 进程在规定时间内未完全退出，已取消后续操作。",
                                "打开Revit",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                return;
            }

            if (restartAfterKill)
                LaunchRevitExecutable(applicationVersion.ExecutablePath);
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, $"{actionName}失败");
            MessageBox.Show($"{actionName}失败：{ex.Message}", "打开Revit", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static async Task<bool> WaitForProcessesExitAsync(IEnumerable<int> pids, TimeSpan timeout)
    {
        var pending = new HashSet<int>((pids ?? Array.Empty<int>()).Where(pid => pid > 0));
        if (pending.Count == 0)
            return true;

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var exited = new List<int>();
            foreach (var pid in pending)
            {
                try
                {
                    using (var process = Process.GetProcessById(pid))
                    {
                        if (process.HasExited)
                            exited.Add(pid);
                    }
                }
                catch (ArgumentException)
                {
                    exited.Add(pid);
                }
                catch
                {
                }
            }

            foreach (var pid in exited)
                pending.Remove(pid);

            if (pending.Count == 0)
                return true;

            await Task.Delay(300).ConfigureAwait(true);
        }

        return false;
    }

    private static void CloseRevitContextMenu(object sender)
    {
        var menuItem = sender as MenuItem;
        var contextMenu = menuItem?.Parent as ContextMenu;
        if (contextMenu != null)
            contextMenu.IsOpen = false;
    }

    private static void LaunchRevitExecutable(string executablePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开路径: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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
                Arguments = "--window cleanup",
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

    private void OpenCommonStartupWindowButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = AdminElevationService.ExtractEmbeddedTool("CommonStartupTool.exe", "CommonStartupTool.exe");
            if (string.IsNullOrEmpty(exePath))
            {
                MessageBox.Show("未找到 CommonStartupTool.exe 工具，请检查安装。", "常用启动项", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "打开常用启动项工具失败");
            MessageBox.Show($"打开常用启动项工具失败：{ex.Message}", "常用启动项", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenVcsMappingButton_Click(object sender, RoutedEventArgs e)    {
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


