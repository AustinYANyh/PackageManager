using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;
using PackageManager.Function.CsvTool;
using PackageManager.Function.SlnTool;
using PackageManager.Function.UnlockTool;
using PackageManager.Services;

namespace PackageManager.Features.DevTools
{
    public static class DevToolLauncher
    {
        public static void OpenCsvCrypto(Window owner)
        {
            try
            {
                var win = new CsvCryptoWindow();
                win.Owner = owner;
                win.Show();
            }
            catch
            {
                MessageBox.Show("打开CSV加解密窗口失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void OpenRevitFileCleanup()
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
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "打开 Revit 文件清理窗口失败");
                MessageBox.Show($"打开清理窗口失败：{ex.Message}", "清理RVT", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void OpenUnlockFiles(Window owner)
        {
            try
            {
                var win = new UnlockFilesWindow { Owner = owner };
                win.Show();
            }
            catch
            {
                MessageBox.Show("打开解除占用窗口失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void OpenSlnUpdate(Window owner)
        {
            try
            {
                var win = new SlnUpdateWindow { Owner = owner };
                win.Show();
            }
            catch
            {
                MessageBox.Show("打开编译顺序窗口失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static async Task ToggleGitProxy(Action<string> statusCallback)
        {
            try
            {
                statusCallback?.Invoke("正在切换 Git 代理...");
                var result = await GitProxyService.ToggleAsync();
                if (statusCallback != null)
                    statusCallback(result.Message);
                else
                    MessageBox.Show(result.Message, "Git代理", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "切换 Git 代理失败");
                statusCallback?.Invoke($"Git代理切换失败：{ex.Message}");
                MessageBox.Show($"切换 Git 代理失败：{ex.Message}", "Git代理", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void OpenVcsMapping()
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

                var existingDirs = new System.Collections.Generic.HashSet<string>(
                    component.Elements("mapping")
                             .Select(m => (string)m.Attribute("directory"))
                             .Where(d => !string.IsNullOrEmpty(d)),
                    StringComparer.OrdinalIgnoreCase);

                int updated = 0;
                int added = 0;

                foreach (var mapping in component.Elements("mapping").ToList())
                {
                    var dir = (string)mapping.Attribute("directory");
                    if (string.IsNullOrEmpty(dir) || (dir == "$PROJECT_DIR$"))
                        continue;

                    var absPath = dir.Replace("$PROJECT_DIR$", rootDir).Replace('/', Path.DirectorySeparatorChar);
                    if (!Directory.Exists(absPath))
                        continue;

                    string detectedVcs = DetectVcs(absPath);
                    if (detectedVcs == null)
                        continue;

                    var current = (string)mapping.Attribute("vcs");
                    if (!string.Equals(current, detectedVcs, StringComparison.OrdinalIgnoreCase))
                    {
                        mapping.SetAttributeValue("vcs", detectedVcs);
                        updated++;
                    }
                }

                foreach (var subDir in Directory.GetDirectories(rootDir))
                {
                    var detectedVcs = DetectVcs(subDir);
                    if (detectedVcs == null)
                        continue;

                    var folderName = Path.GetFileName(subDir);
                    var relKey = $"$PROJECT_DIR$/{folderName}";

                    if (existingDirs.Contains(relKey))
                        continue;

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

        private static string DetectVcs(string directory)
        {
            if (Directory.Exists(Path.Combine(directory, ".git")))
                return "Git";
            if (Directory.Exists(Path.Combine(directory, ".svn")))
                return "svn";
            return null;
        }

        public static void OpenRevitActivation()
        {
            try
            {
                var asm = typeof(DevToolLauncher).Assembly;
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
                try { Directory.CreateDirectory(Path.Combine(targetDir, "Logs")); } catch { }

                foreach (var suffix in suffixes)
                {
                    var name = names.FirstOrDefault(n =>
                        n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                        n.Contains("Assets.Tools.Autodesk.Universal.Activation.Tools"));
                    if (string.IsNullOrEmpty(name))
                        continue;

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
    }
}
