using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PackageManager.Models;
using PackageManager.Services;
using System.Text.RegularExpressions;
using CustomControlLibrary.CustomControl.Attribute.ComboBox;

namespace PackageManager.Views
{
    public partial class ProductLogsPage : Page, ICentralPage
    {
        private readonly string _baseDir;
        private readonly DataPersistenceService _dataPersistenceService;
        private readonly ApplicationFinderService _applicationFinderService;
        private AppSettings _settings;

        public event Action RequestExit;

        public ObservableCollection<ProductLogInfo> ProductLogs { get; } = new ObservableCollection<ProductLogInfo>();
        public ObservableCollection<ProductLogInfo> RevitLogs { get; } = new ObservableCollection<ProductLogInfo>();
        public ObservableCollection<ApplicationVersion> RevitExecutableVersions { get; } = new ObservableCollection<ApplicationVersion>();
        public ObservableCollection<string> LogLevels { get; } = new ObservableCollection<string>();

        public string SelectedRevitExecutableVersion { get; set; }

        [ComboBox(["ALL", "ERROR", "WARN", "INFO"])]
        public string SelectedLogLevel { get; set; } = "ERROR";
        

        public ProductLogsPage(string baseDir)
        {
            InitializeComponent();
            _baseDir = baseDir;
            _dataPersistenceService = new DataPersistenceService();
            _applicationFinderService = new ApplicationFinderService();
            _settings = _dataPersistenceService.LoadSettings();

            DataContext = this;
            BaseDirText.Text = $"日志目录: {_baseDir}";
            RefreshLogs();

            EnsureRevitDirFromCache();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshLogs();
        }
        
        private void OpenProductDirButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OpenDirectory(_baseDir);
            }
            catch
            {
            }
        }

        private void RefreshLogs()
        {
            try
            {
                ProductLogs.Clear();

                if (string.IsNullOrWhiteSpace(_baseDir) || !Directory.Exists(_baseDir))
                {
                    LoggingService.LogWarning($"日志目录 {_baseDir}不存在");
                    return;
                }

                var files = Directory.EnumerateFiles(_baseDir, "*.log", SearchOption.AllDirectories)
                                      .Select(f => new FileInfo(f))
                                      .OrderByDescending(fi => fi.LastWriteTime);
                foreach (var fi in files)
                {
                    var model = new ProductLogInfo
                    {
                        FileName = fi.Name,
                        Directory = fi.DirectoryName,
                        FullPath = fi.FullName,
                        SizeText = FormatSize(fi.Length),
                        ModifiedText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    };

                    model.OpenCommand = new RelayCommand(() => Open(model));

                    ProductLogs.Add(model);
                }

                ProductLogGrid.ItemsSource = ProductLogs;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "刷新产品日志失败");
                MessageBox.Show($"刷新产品日志失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LogLevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var level = SelectedLogLevel;
                if (string.IsNullOrWhiteSpace(level))
                {
                    LoggingService.LogWarning("日志等级为空，未进行更新");
                    return;
                }
                var main = Application.Current?.MainWindow as PackageManager.MainWindow;
                var packages = main?.Packages;
                if (packages == null)
                {
                    LoggingService.LogWarning("未获取包列表，无法更新日志等级");
                    return;
                }
                foreach (var pkg in packages)
                {
                    var local = pkg?.EffectiveLocalPath;
                    if (string.IsNullOrWhiteSpace(local))
                    {
                        LoggingService.LogWarning($"包 {pkg?.ProductName} 本地路径为空，跳过");
                        continue;
                    }
                    if (!Directory.Exists(local))
                    {
                        LoggingService.LogWarning($"包 {pkg?.ProductName} 本地路径不存在：{local}，跳过");
                        continue;
                    }
                    var configDir = Path.Combine(local, "config");
                    if (!Directory.Exists(configDir))
                    {
                        LoggingService.LogWarning($"包 {pkg?.ProductName} 缺少 config 目录：{configDir}，跳过");
                        continue;
                    }
                    var configPath = Path.Combine(configDir, "log4net.config");
                    if (!File.Exists(configPath))
                    {
                        LoggingService.LogWarning($"包 {pkg?.ProductName} 缺少 log4net.config 文件：{configPath}，跳过");
                        continue;
                    }
                    try
                    {
                        var text = File.ReadAllText(configPath);
                        var replaced = Regex.Replace(text, "<\\s*level\\s+value\\s*=\\s*\"[^\"]+\"\\s*/\\s*>", $"<level value=\"{level}\" />", RegexOptions.IgnoreCase);
                        if (!string.Equals(text, replaced, StringComparison.Ordinal))
                        {
                            File.WriteAllText(configPath, replaced);
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogError(ex, $"更新 {pkg?.ProductName} 的 log4net.config 失败");
                    }
                    
                    pkg.StatusText = $"已更新 {pkg.ProductName} 的日志等级为 {level}";
                }
                LoggingService.LogInfo($"已将所有包的日志等级设置为 {level}");
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "切换日志等级时发生异常");
            }
        }

        private string _revitDir;

        public void SetRevitJournalDir(string dir)
        {
            _revitDir = dir;
            RevitDirText.Text = string.IsNullOrWhiteSpace(dir) ? "Revit日志目录: 未选择" : $"Revit日志目录: {dir}";
            RefreshRevitLogs();
        }

        private void RefreshRevitLogs()
        {
            try
            {
                RevitLogs.Clear();

                if (string.IsNullOrWhiteSpace(_revitDir) || !Directory.Exists(_revitDir))
                {
                    RevitLogGrid.ItemsSource = null;
                    return;
                }

                var files = Directory.EnumerateFiles(_revitDir, "*.txt", SearchOption.TopDirectoryOnly)
                                      .Select(f => new FileInfo(f))
                                      .OrderByDescending(fi => fi.LastWriteTime);
                foreach (var fi in files)
                {
                    var model = new ProductLogInfo
                    {
                        FileName = fi.Name,
                        Directory = fi.DirectoryName,
                        FullPath = fi.FullName,
                        SizeText = FormatSize(fi.Length),
                        ModifiedText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    };

                    model.OpenCommand = new RelayCommand(() => Open(model));

                    RevitLogs.Add(model);
                }

                RevitLogGrid.ItemsSource = RevitLogs;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "刷新Revit日志失败");
            }
        }

        private void Open(ProductLogInfo model)
        {
            if (_settings.LogTxtReader == "LogViewPro")
            {
                OpenWithLogViewPro(model);
            }
            else if (_settings.LogTxtReader == "VSCode")
            {
                OpenWithVSCode(model);
            }
            else if (_settings.LogTxtReader == "Notepad")
            {
                OpenWith(model, "notepad.exe");
            }
            else if (_settings.LogTxtReader == "NotepadPlusPlus")
            {
                OpenWith(model, "notepad++.exe");
            }
            else
            {
                throw new NotImplementedException("未实现的软件");
            }
        }

        private void ReSelectButton_Click(object sender, RoutedEventArgs e)
        {
            RequestExit?.Invoke();
        }

        private void RevitVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var ver = (e.AddedItems.Count > 0 ? (e.AddedItems[0] as ApplicationVersion)?.Version : null) ?? string.Empty;
            var baseLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = string.IsNullOrWhiteSpace(ver) ? null : Path.Combine(baseLocal, "Autodesk", "Revit", $"Autodesk Revit {ver}", "Journals");
            SetRevitJournalDir(dir);
        }
        
        private void OpenRevitDirButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                OpenDirectory(_revitDir);
            }
            catch
            {
            }
        }

        private void EnsureRevitDirFromCache()
        {
            try
            {
                if (RevitExecutableVersions.Count == 0)
                {
                    var programName = "Revit";
                    var cached = _dataPersistenceService.GetCachedData(programName) ?? new System.Collections.Generic.List<ApplicationVersion>();
                    if (!cached.Any())
                    {
                        var found = _applicationFinderService.FindAllApplicationVersions(programName) ?? new System.Collections.Generic.List<ApplicationVersion>();
                        if (found.Any())
                        {
                            _dataPersistenceService.UpdateCachedData(programName, found);
                            cached = found;
                        }
                    }
                    foreach (var v in cached)
                    {
                        RevitExecutableVersions.Add(v);
                    }
                }

                var ver = RevitExecutableVersions.FirstOrDefault()?.Version;
                SelectedRevitExecutableVersion = RevitExecutableVersions.FirstOrDefault()?.DisPlayName;
                var baseLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var dir = string.IsNullOrWhiteSpace(ver) ? null : Path.Combine(baseLocal, "Autodesk", "Revit", $"Autodesk Revit {ver}", "Journals");
                SetRevitJournalDir(dir);
            }
            catch
            {
                SetRevitJournalDir(null);
            }
        }

        private string FormatSize(long size)
        {
            var units = new[] { "B", "KB", "MB", "GB" };
            double s = size;
            int idx = 0;
            while (s >= 1024 && idx < units.Length - 1)
            {
                s /= 1024;
                idx++;
            }
            return $"{s:0.##} {units[idx]}";
        }

        private void OpenWithLogViewPro(ProductLogInfo item)
        {
            try
            {
                var path = GetBundledLogViewProPath();
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    MessageBox.Show("未找到内置 LogViewPro，请检查打包资源。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                StartProcess(path, item.FullPath);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "使用LogViewPro打开失败");
            }
        }

        private void OpenWithVSCode(ProductLogInfo item)
        {
            try
            {
                var path = GetOrResolveAppPath(_settings.VsCodePath, "Code", p => _settings.VsCodePath = p);
                path = PreferVSCodeExe(path);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    MessageBox.Show("未找到 VsCode，请安装或手动配置路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                StartProcess(path, item.FullPath);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "使用VsCode打开失败");
            }
        }

        private void OpenWith(ProductLogInfo item, string exeName)
        {
            try
            {
                StartProcess(exeName, item.FullPath);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"使用{exeName}打开失败");
            }
        }

        private void StartProcess(string appPath, string filePath)
        {
            try
            {
                var ext = System.IO.Path.GetExtension(appPath)?.ToLowerInvariant();
                var isScript = ext == ".cmd" || ext == ".bat";
                var psi = new ProcessStartInfo
                {
                    FileName = appPath,
                    Arguments = $"\"{filePath}\"",
                    UseShellExecute = !isScript,
                    CreateNoWindow = isScript,
                    WindowStyle = isScript ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(appPath)
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"启动外部程序失败: {appPath}");
                MessageBox.Show($"启动外部程序失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private void OpenDirectory(string dir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    MessageBox.Show("目录不存在或未选择", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                
                if (_settings.LogTxtReader == "VSCode")
                {
                    var path = GetOrResolveAppPath(_settings.VsCodePath, "Code", p => _settings.VsCodePath = p);
                    path = PreferVSCodeExe(path);
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        MessageBox.Show("未找到 VsCode，请安装或手动配置路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    StartProcess(path, dir);
                }
                else if (_settings.LogTxtReader == "NotepadPlusPlus")
                {
                    StartProcess("notepad++.exe", dir);
                }
                else
                {
                    MessageBox.Show($"当前使用的软件{_settings.LogTxtReader}不支持打开目录", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "打开目录失败");
            }
        }

        private string GetOrResolveAppPath(string cachedPath, string programName, Action<string> updateCached)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(cachedPath) && File.Exists(cachedPath))
                {
                    return cachedPath;
                }

                var found = _applicationFinderService.FindApplicationPath(programName);
                if (!string.IsNullOrWhiteSpace(found) && File.Exists(found))
                {
                    updateCached?.Invoke(found);
                    _dataPersistenceService.SaveSettings(_settings);
                    return found;
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"查找程序路径失败: {programName}");
            }
            return cachedPath;
        }

        private string PreferVSCodeExe(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return null;
                var ext = System.IO.Path.GetExtension(path)?.ToLowerInvariant();
                if (ext == ".exe") return path;
                if (ext == ".cmd" || ext == ".bat")
                {
                    var dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        var parent = Directory.GetParent(dir);
                        var candidate = System.IO.Path.Combine(parent?.FullName ?? string.Empty, "Code.exe");
                        if (!string.IsNullOrEmpty(parent?.FullName) && File.Exists(candidate))
                        {
                            _settings.VsCodePath = candidate;
                            _dataPersistenceService.SaveSettings(_settings);
                            return candidate;
                        }
                    }

                    var candidates = _applicationFinderService.FindAllApplicationPaths("Code") ?? new System.Collections.Generic.List<string>();
                    var exe = candidates.FirstOrDefault(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(exe))
                    {
                        _settings.VsCodePath = exe;
                        _dataPersistenceService.SaveSettings(_settings);
                        return exe;
                    }
                }
                return path;
            }
            catch
            {
                return path;
            }
        }

        private string GetBundledLogViewProPath()
        {
            try
            {
                var exe = EnsureEmbeddedToolExtracted("LogViewPro.exe", "LogViewPro.exe");
                if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                {
                    return exe;
                }

                var fallback = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Tools", "LogViewPro.exe");
                return fallback;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "获取内置 LogViewPro 路径失败");
                return null;
            }
        }

        private string EnsureEmbeddedToolExtracted(string resourceSuffix, string outputFileName)
        {
            try
            {
                var asm = typeof(ProductLogsPage).Assembly;
                var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }

                var targetDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackageManager", "tools");
                Directory.CreateDirectory(targetDir);
                var targetPath = System.IO.Path.Combine(targetDir, outputFileName);

                if (File.Exists(targetPath))
                {
                    return targetPath;
                }

                using (var stream = asm.GetManifestResourceStream(name))
                {
                    if (stream == null) return null;
                    using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        stream.CopyTo(fs);
                    }
                }

                return targetPath;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "提取内置工具失败");
                return null;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            RequestExit?.Invoke();
        }
    }
}
