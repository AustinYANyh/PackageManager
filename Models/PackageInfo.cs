using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using PackageManager.Services;

namespace PackageManager.Models
{
    /// <summary>
    /// 包状态枚举
    /// </summary>
    public enum PackageStatus
    {
        /// <summary>
        /// 就绪
        /// </summary>
        Ready,

        /// <summary>
        /// 下载中
        /// </summary>
        Downloading,

        /// <summary>
        /// 解压中
        /// </summary>
        Extracting,

        /// <summary>
        /// 完成
        /// </summary>
        Completed,

        /// <summary>
        /// 错误
        /// </summary>
        Error,
    }

    /// <summary>
    /// 产品包信息数据模型
    /// </summary>
    public class PackageInfo : INotifyPropertyChanged
    {
        private string _productName;

        private string _version;

        private string _ftpServerPath;

        private string _localPath;

        private PackageStatus _status;

        private double _progress;

        private string _statusText;

        private string uploadPackageName;

        private ObservableCollection<string> _availableVersions;

        private ObservableCollection<string> _availablePackages;

        private ICommand _updateCommand;
        
        private ICommand _openParameterConfigCommand;
        
        private ICommand _openImageConfigCommand;
        
        private ICommand _changeModeToDebugCommand;

        private bool _isDebugMode;

        private ObservableCollection<ApplicationVersion> _availableExecutableVersions;

        private string _selectedExecutableVersion;

        private string _executablePath;

        private ICommand _openPathCommand;

        private bool _isReadOnly;

        private string time;

        /// <summary>
        /// 更新请求事件
        /// </summary>
        public event Action<PackageInfo> UpdateRequested;

        public event Action<PackageInfo, string> VersionChanged;

        public event Action<PackageInfo> DownloadRequested;
        public event Action<PackageInfo, bool> DebugModeChanged;

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 静态数据持久化服务实例，用于获取设置
        /// </summary>
        public static DataPersistenceService DataPersistenceService { get; set; }

        /// <summary>
        /// 产品名称
        /// </summary>
        [DataGridColumn(1, DisplayName = "产品名称", Width = "180", IsReadOnly = true)]
        public string ProductName
        {
            get => _productName;

            set => SetProperty(ref _productName, value);
        }

        /// <summary>
        /// 当前版本
        /// </summary>
        [DataGridComboBox(2, "版本", "AvailableVersions", Width = "120", IsReadOnlyProperty = "IsReadOnly")]
        public string Version
        {
            get => _version;

            set
            {
                if (SetProperty(ref _version, value))
                {
                    OnPropertyChanged(nameof(DownloadUrl));
                    VersionChanged?.Invoke(this, value);
                }
            }
        }

        [DataGridColumn(4, DisplayName = "时间", Width = "150", IsReadOnly = true)]
        public string Time
        {
            get
            {
                // 从UploadPackageName中解析时间
                if (!string.IsNullOrEmpty(UploadPackageName))
                {
                    var parsedTime = FtpService.ParseTimeFromFileName(UploadPackageName);
                    if (parsedTime != DateTime.MinValue)
                    {
                        return parsedTime.ToString("yyyy-MM-dd HH:mm");
                    }
                }

                return time;
            }

            set
            {
                if (value == time)
                {
                    return;
                }

                time = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// FTP服务器路径
        /// </summary>

        // [DataGridColumn(4, DisplayName = "FTP服务器路径", Width = "350",IsReadOnly = true)]
        public string FtpServerPath
        {
            get => _ftpServerPath;

            set
            {
                if (SetProperty(ref _ftpServerPath, value))
                {
                    OnPropertyChanged(nameof(DownloadUrl));
                }
            }
        }

        /// <summary>
        /// 本地包路径（从主界面表格中移除显示，改由专用窗口设置）
        /// </summary>
        public string LocalPath
        {
            get => _localPath;

            set
            {
                if (SetProperty(ref _localPath, value))
                {
                    TryLoadDebugModeFromConfig();
                }
            }
        }

        /// <summary>
        /// 包状态
        /// </summary>
        [DataGridColumn(6, DisplayName = "状态", Width = "100", IsReadOnly = true)]
        public PackageStatus Status
        {
            get => _status;

            set => SetProperty(ref _status, value);
        }

        [DataGridButton(7,
                        DisplayName = "操作",
                        Width = "100",
                        ControlType = "Button",
                        ButtonText = "更新",
                        ButtonWidth = 80,
                        ButtonHeight = 26,
                        ButtonCommandProperty = "UpdateCommand",
                        IsReadOnlyProperty = "IsReadOnly")]
        public string DoWork { get; set; }

        /// <summary>
        /// 下载进度 (0-100)
        /// </summary>
        [DataGridProgressBar(8,
                             0,
                             100,
                             DisplayName = "进度",
                             Width = "120",
                             ProgressBarWidth = 100,
                             ProgressBarHeight = 20,
                             TextFormat = "{0:F1}%")]
        public double Progress
        {
            get => _progress;

            set => SetProperty(ref _progress, value);
        }

        /// <summary>
        /// 可执行文件版本
        /// </summary>
        [DataGridComboBox(9,
                          "可执行版本",
                          "AvailableExecutableVersions",
                          Width = "150",
                          IsReadOnlyProperty = "IsReadOnly",
                          ComboBoxDisplayMemberPath = "DisPlayName",
                          ComboBoxSelectedValuePath = "DisPlayName")]
        public string SelectedExecutableVersion
        {
            get => _selectedExecutableVersion;

            set => SetProperty(ref _selectedExecutableVersion, value);
        }

        /// <summary>
        /// 打开路径按钮
        /// </summary>
        [DataGridButton(10,
                        DisplayName = "打开路径",
                        Width = "100",
                        ControlType = "Button",
                        ButtonText = "打开",
                        ButtonWidth = 80,
                        ButtonHeight = 26,
                        ButtonCommandProperty = "OpenPathCommand",
                        IsReadOnlyProperty = "IsReadOnly")]
        public string OpenPath { get; set; }

        /// <summary>
        /// 配置操作
        /// </summary>
        [DataGridMultiButton(nameof(ConfigOperationConfig), 11, 
                             DisplayName = "配置操作", Width = "300", ButtonSpacing = 15)]
        public string ConfigOperation { get; set; }
        

        /// <summary>
        /// 配置操作动态按钮配置列表
        /// </summary>
        public List<ButtonConfig> ConfigOperationConfig
        {
            get
            {
                return new List<ButtonConfig>
                {
                    new ButtonConfig
                    {
                        Text = "参数",
                        Width = 60,
                        Height = 26,
                        CommandProperty = nameof(OpenParameterConfigCommand),
                        ToolTip = "打开参数配置文件夹",
                        IsEnabledProperty = $"!{nameof(IsReadOnly)}"
                    },
                    new ButtonConfig
                    {
                        Text = "图片",
                        Width = 60,
                        Height = 26,
                        CommandProperty = nameof(OpenImageConfigCommand),
                        ToolTip = "打开图片配置文件夹",
                        IsEnabledProperty = $"!{nameof(IsReadOnly)}"
                    },
                    new ButtonConfig
                    {
                        Text = "模式切换",
                        Width = 60,
                        Height = 26,
                        CommandProperty = nameof(ChangeModeToDebugCommand),
                        ToolTip = "切换调试模式与正常模式",
                    }
                };
            }
        }
        
        /// <summary>
        /// 更新操作命令
        /// </summary>
        public ICommand OpenParameterConfigCommand
        {
            get => _openParameterConfigCommand ?? (_openParameterConfigCommand = new RelayCommand(ExecuteOpenParameterConfig));

            set => SetProperty(ref _openParameterConfigCommand, value);
        }

       
        /// <summary>
        /// 更新操作命令
        /// </summary>
        public ICommand OpenImageConfigCommand
        {
            get => _openImageConfigCommand ?? (_openImageConfigCommand = new RelayCommand(ExecuteOpenImageConfig));

            set => SetProperty(ref _openImageConfigCommand, value);
        }

        /// <summary>
        /// 更新操作命令
        /// </summary>
        public ICommand ChangeModeToDebugCommand
        {
            get => _changeModeToDebugCommand ?? (_changeModeToDebugCommand = new RelayCommand(ExecuteToggleDebugMode));

            set => SetProperty(ref _changeModeToDebugCommand, value);
        }
        

        /// <summary>
        /// 状态文本
        /// </summary>
        public string StatusText
        {
            get => _statusText;

            set => SetProperty(ref _statusText, value);
        }

        /// <summary>
        /// 可执行文件路径
        /// </summary>
        public string ExecutablePath
        {
            get => _executablePath;

            set => SetProperty(ref _executablePath, value);
        }

        /// <summary>
        /// 是否为只读状态（更新时不可编辑）
        /// </summary>
        public bool IsReadOnly
        {
            get => _isReadOnly;

            set => SetProperty(ref _isReadOnly, value);
        }

        /// <summary>
        /// 是否为调试模式（影响按钮文案与配置写入），需持久化
        /// </summary>
        public bool IsDebugMode
        {
            get => _isDebugMode;

            set => SetProperty(ref _isDebugMode, value);
        }

        /// <summary>
        /// 包名
        /// </summary>
        [DataGridComboBox(3,
                          "包名",
                          "AvailablePackages",
                          Width = "320",
                          IsReadOnlyProperty = "IsReadOnly",
                          ContentAlign = HorizontalAlignment.Left)]
        public string UploadPackageName
        {
            get => uploadPackageName;

            set
            {
                if (SetProperty(ref uploadPackageName, value))
                {
                    OnPropertyChanged(nameof(DownloadUrl));
                    OnPropertyChanged(nameof(Time)); // 通知Time属性更新
                }
            }
        }

        /// <summary>
        /// 可用版本列表
        /// </summary>
        public ObservableCollection<string> AvailableVersions
        {
            get => _availableVersions ?? (_availableVersions = new ObservableCollection<string>());

            set => SetProperty(ref _availableVersions, value);
        }

        /// <summary>
        /// 可用包列表
        /// </summary>
        public ObservableCollection<string> AvailablePackages
        {
            get => _availablePackages ?? (_availablePackages = new ObservableCollection<string>());

            set => SetProperty(ref _availablePackages, value);
        }

        /// <summary>
        /// 可用可执行文件版本列表
        /// </summary>
        public ObservableCollection<ApplicationVersion> AvailableExecutableVersions
        {
            get => _availableExecutableVersions ?? (_availableExecutableVersions = new ObservableCollection<ApplicationVersion>());

            set => SetProperty(ref _availableExecutableVersions, value);
        }

        /// <summary>
        /// 更新操作命令
        /// </summary>
        public ICommand UpdateCommand
        {
            get => _updateCommand ?? (_updateCommand = new RelayCommand(ExecuteDownload));

            set => SetProperty(ref _updateCommand, value);
        }

        /// <summary>
        /// 打开路径命令
        /// </summary>
        public ICommand OpenPathCommand
        {
            get => _openPathCommand ?? (_openPathCommand = new RelayCommand(ExecuteOpenPath));

            set => SetProperty(ref _openPathCommand, value);
        }

        /// <summary>
        /// 完整的下载地址（FTP路径 + 版本 + 上传时间）
        /// </summary>
        public string DownloadUrl
        {
            get
            {
                if (string.IsNullOrEmpty(FtpServerPath) ||
                    string.IsNullOrEmpty(Version) ||
                    string.IsNullOrEmpty(UploadPackageName))
                {
                    return string.Empty;
                }

                // 组合完整的下载地址
                var basePath = FtpServerPath.TrimEnd('/');
                return $"{basePath}/{Version}/{UploadPackageName}";
            }
        }

        /// <summary>
        /// 更新可用版本列表
        /// </summary>
        /// <param name="versions">版本列表</param>
        public void UpdateAvailableVersions(IEnumerable<string> versions)
        {
            AvailableVersions.Clear();
            foreach (var version in versions)
            {
                AvailableVersions.Add(version);
            }

            // 如果有版本且当前版本为空，则选择最后一个版本
            if ((AvailableVersions.Count > 0) && string.IsNullOrEmpty(Version))
            {
                Version = AvailableVersions.Last();
            }
        }

        /// <summary>
        /// 更新可用包列表（上传时间）
        /// </summary>
        /// <param name="packages">包列表</param>
        public void UpdateAvailablePackages(IEnumerable<string> packages)
        {
            AvailablePackages.Clear();
            foreach (var package in packages)
            {
                AvailablePackages.Add(package);
            }

            // 如果有包且当前上传时间为空，则选择最后一个包
            if ((AvailablePackages.Count > 0) && string.IsNullOrEmpty(UploadPackageName))
            {
                UploadPackageName = AvailablePackages.Last();
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        /// <summary>
        /// 获取配置的Addin路径
        /// </summary>
        private static string GetAddinPath()
        {
            if (DataPersistenceService != null)
            {
                var settings = DataPersistenceService.LoadSettings();
                return settings.AddinPath;
            }

            return @"C:\ProgramData\Autodesk\Revit\Addins"; // 默认路径
        }

        /// <summary>
        /// 获取配置的Addin路径
        /// </summary>
        private static bool GetProgramEntryWithG()
        {
            if (DataPersistenceService != null)
            {
                var settings = DataPersistenceService.LoadSettings();
                return settings.ProgramEntryWithG;
            }

            return true;
        }

        /// <summary>
        /// 执行更新操作
        /// </summary>
        private void ExecuteUpdate()
        {
            // 触发更新事件，由MainWindow处理具体的更新逻辑
            UpdateRequested?.Invoke(this);
        }

        /// <summary>
        /// 执行下载替换
        /// </summary>
        private void ExecuteDownload()
        {
            // 触发更新事件，由MainWindow处理具体的更新逻辑
            DownloadRequested?.Invoke(this);
        }
        
        private void ExecuteOpenParameterConfig()
        {
            string path = Path.Combine(LocalPath, "config");
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                Process.Start(path);
            }
            else
            {
                MessageBox.Show("参数配置路径无效或文件夹不存在", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        
        private void ExecuteOpenImageConfig()
        {
            string path = Path.Combine(LocalPath, "Image");
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                //打开文件夹
                Process.Start(path);
            }
            else
            {
                MessageBox.Show("图片配置路径无效或文件夹不存在", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ExecuteToggleDebugMode()
        {
            // 切换调试模式，并写入配置文件
            bool target = !IsDebugMode;
            UpdateDebugModeInConfig(target);
            IsDebugMode = target;
            DebugModeChanged?.Invoke(this, target);
        }

        /// <summary>
        /// 从配置文件读取调试模式状态
        /// </summary>
        private void TryLoadDebugModeFromConfig()
        {
            try
            {
                string debugSettingPath = Path.Combine(LocalPath ?? string.Empty, "config", "DebugSetting.json");
                if (!string.IsNullOrEmpty(LocalPath) && File.Exists(debugSettingPath))
                {
                    string json = File.ReadAllText(debugSettingPath);
                    var match = Regex.Match(json, "\"IsDebugMode\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        IsDebugMode = string.Equals(match.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch
            {
                // 忽略解析异常，保持当前状态
            }
        }

        /// <summary>
        /// 写入配置文件中的调试模式
        /// </summary>
        private void UpdateDebugModeInConfig(bool enable)
        {
            try
            {
                if (string.IsNullOrEmpty(LocalPath))
                {
                    return;
                }

                string configDir = Path.Combine(LocalPath, "config");
                string debugSettingPath = Path.Combine(configDir, "DebugSetting.json");
                Directory.CreateDirectory(configDir);

                string newValue = enable ? "true" : "false";
                string content = File.Exists(debugSettingPath) ? File.ReadAllText(debugSettingPath) : "{\n  \"IsDebugMode\": false\n}";

                if (Regex.IsMatch(content, "\"IsDebugMode\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase))
                {
                    content = Regex.Replace(content, "(\"IsDebugMode\"\\s*:\\s*)(true|false)", $"$1{newValue}", RegexOptions.IgnoreCase);
                }
                else
                {
                    // 简单地插入键值
                    content = "{\n  \"IsDebugMode\": " + newValue + "\n}";
                }

                File.WriteAllText(debugSettingPath, content);
            }
            catch
            {
                // 忽略写入异常
            }
        }
        
        /// <summary>
        /// 执行打开路径操作
        /// </summary>
        private void ExecuteOpenPath()
        {
            ApplicationVersion applicationVersion = AvailableExecutableVersions.FirstOrDefault(x => x.DisPlayName == SelectedExecutableVersion);
            if (applicationVersion == null)
            {
                return;
            }

            ExecutablePath = applicationVersion.ExecutablePath;
            if (!string.IsNullOrEmpty(ExecutablePath) && File.Exists(ExecutablePath))
            {
                try
                {
                    //配置Addin文件
                    string defaultAddinDir = GetAddinPath();
                    if (!Directory.Exists(defaultAddinDir))
                    {
                        return;
                    }

                    string version = applicationVersion.Version;
                    string addinDir = Path.Combine(defaultAddinDir, version);

                    string binDir = Path.Combine(LocalPath, "bin");

                    // 查找bin目录下所有dll文件，找出以G开头、包含版本号、以.dll结尾的文件，并复制其完整路径
                    if (Directory.Exists(binDir))
                    {
                        var targetFiles = Directory.GetFiles(binDir, "*.dll")
                                                   .Where(file => Path.GetFileName(file).StartsWith("G") && Path.GetFileName(file).Contains(version))
                                                   .ToList().FirstOrDefault();

                        if (!string.IsNullOrEmpty(targetFiles))
                        {
                            bool programEntryWithG = GetProgramEntryWithG();
                            if (!programEntryWithG)
                            {
                                string replace = Path.GetFileName(targetFiles).Replace("G", string.Empty);
                                targetFiles = Path.Combine(Path.GetDirectoryName(targetFiles) ?? string.Empty, replace);
                            }

                            string addinFile = Directory.GetFiles(LocalPath, "*.addin").FirstOrDefault();
                            if (!string.IsNullOrEmpty(addinFile))
                            {
                                string addinStr = string.Empty;
                                using (StreamReader streamReader = new StreamReader(new FileStream(addinFile, FileMode.Open)))
                                {
                                    addinStr = streamReader.ReadToEnd();

                                    //找出<Assembly>C:\红瓦科技\MaxiBIM（PMEP）Develop\GHWPMEP4RevitEntry.dll</Assembly>匹配的内容，进行替换
                                    string assemblyPattern = @"<Assembly>(.*?)</Assembly>";
                                    Match match = Regex.Match(addinStr, assemblyPattern);
                                    if (match.Success)
                                    {
                                        string assemblyPath = match.Groups[1].Value;
                                        addinStr = addinStr.Replace(assemblyPath, targetFiles);
                                    }
                                }

                                File.WriteAllText(addinFile, addinStr);

                                string addinFilePath = Path.Combine(addinDir, Path.GetFileName(addinFile));
                                File.Copy(addinFile, addinFilePath, true);
                            }
                        }
                    }

                    // 打开文件所在的文件夹并选中该文件
                    System.Diagnostics.Process.Start(ExecutablePath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"无法打开路径: {ex.Message}",
                                    "错误",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("可执行文件路径无效或文件不存在",
                                "提示",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
            }
        }
    }

    /// <summary>
    /// 简单的RelayCommand实现
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;

        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;

            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter)
        {
            return _canExecute?.Invoke() ?? true;
        }

        public void Execute(object parameter)
        {
            _execute();
        }
    }
}