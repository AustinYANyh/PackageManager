using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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
        /// 校验签名中
        /// </summary>
        VerifyingSignature,

        /// <summary>
        /// 校验加密中
        /// </summary>
        VerifyingEncryption,

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
        private string productName;

        private string version;

        private string ftpServerPath;

        private string localPath;

        private PackageStatus status;

        private double progress;

        private string statusText;

        private string uploadPackageName;

        private ObservableCollection<string> availableVersions;

        private ObservableCollection<string> availablePackages;

        private ICommand updateCommand;

        private ICommand openParameterConfigCommand;

        private ICommand openImageConfigCommand;

        private ICommand changeModeToDebugCommand;

        private bool isDebugMode;

        private ObservableCollection<ApplicationVersion> availableExecutableVersions;

        private string selectedExecutableVersion;

        private string executablePath;

        private ICommand openPathCommand;

        private bool isReadOnly;

        private string time;

        private ICommand runEmbeddedToolCommand;

        private System.Collections.Generic.Dictionary<string, string> versionLocalPaths;

        /// <summary>
        /// 更新请求事件
        /// </summary>
        public event Action<PackageInfo> UpdateRequested;

        public event Action<PackageInfo, string> VersionChanged;

        public event Action<PackageInfo> DownloadRequested;

        public event Action<PackageInfo, bool> DebugModeChanged;

        public event Action<PackageInfo> UnlockAndDownloadRequested;

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
            get => productName;

            set => SetProperty(ref productName, value);
        }

        /// <summary>
        /// 当前版本
        /// </summary>
        [DataGridComboBox(2, "版本", "AvailableVersions", Width = "120", IsReadOnlyProperty = "IsReadOnly")]
        public string Version
        {
            get => version;

            set
            {
                if (SetProperty(ref version, value))
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
            get => ftpServerPath;

            set
            {
                if (SetProperty(ref ftpServerPath, value))
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
            get => localPath;

            set
            {
                if (SetProperty(ref localPath, value))
                {
                    try
                    {
                        bool isDebug = DebugSettingsService.ReadIsDebugMode(localPath, isDebugMode);
                        IsDebugMode = isDebug;
                    }
                    catch
                    {
                    }
                }
            }
        }

        public System.Collections.Generic.Dictionary<string, string> VersionLocalPaths
        {
            get => versionLocalPaths ?? (versionLocalPaths = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase));
            set => SetProperty(ref versionLocalPaths, value);
        }

        public string GetLocalPathForVersion(string version)
        {
            if (!string.IsNullOrWhiteSpace(version) && VersionLocalPaths != null && VersionLocalPaths.TryGetValue(version, out var p) && !string.IsNullOrWhiteSpace(p))
            {
                return p;
            }
            return LocalPath;
        }

        public string EffectiveLocalPath => GetLocalPathForVersion(Version);

        /// <summary>
        /// 包状态
        /// </summary>
        [DataGridColumn(6, DisplayName = "状态", Width = "130", IsReadOnly = true)]
        public PackageStatus Status
        {
            get => status;

            set => SetProperty(ref status, value);
        }

        [DataGridButton(7,
                        DisplayName = "操作",
                        Width = "100",
                        ControlType = "Button",
                        ButtonText = "更新",
                        ButtonWidth = 80,
                        ButtonHeight = 26,
                        ButtonCommandProperty = "UpdateCommand",
                        IsReadOnlyProperty = "IsReadOnly",
                        IsVisible = false)]
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
            get => progress;

            set => SetProperty(ref progress, value);
        }

        /// <summary>
        /// 可执行文件版本
        /// </summary>
        [DataGridComboBox(9,
                          "可执行版本",
                          "AvailableExecutableVersions",
                          Width = "135",
                          IsReadOnlyProperty = "IsReadOnly",
                          ComboBoxDisplayMemberPath = "DisPlayName",
                          ComboBoxSelectedValuePath = "DisPlayName",
                          IsVisible = false)]
        public string SelectedExecutableVersion
        {
            get => selectedExecutableVersion;

            set => SetProperty(ref selectedExecutableVersion, value);
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
                        IsReadOnlyProperty = "IsReadOnly",
                        IsVisible = false)]
        public string OpenPath { get; set; }

        /// <summary>
        /// 配置操作
        /// </summary>
        [DataGridMultiButton(nameof(ConfigOperationConfig),
                             11,
                             DisplayName = "配置操作",
                             Width = "300",
                             ButtonSpacing = 15,
                             IsVisible = false)]
        public string ConfigOperation { get; set; }

        public bool IsEnabled => !IsReadOnly;

        public bool IsTeamworkMasterDevelop => string.Equals(ProductName, "TeamworkMaster(Develop)", StringComparison.OrdinalIgnoreCase);

        public bool? SupportsConfigOpsOverride { get; set; }

        public bool SupportsConfigOps
        {
            get
            {
                if (SupportsConfigOpsOverride.HasValue) return SupportsConfigOpsOverride.Value;
                var name = ProductName ?? string.Empty;
                if (string.Equals(name, "BuildMaster(Dazzle)", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(name, "TeamworkMaster(Develop)", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
        }

        public bool ConfigOpsEnabled => SupportsConfigOps && IsEnabled;

        [DataGridButton(12,
                        DisplayName = "签名加密",
                        Width = "100",
                        ControlType = "Button",
                        ButtonText = "校验",
                        ButtonWidth = 80,
                        ButtonHeight = 26,
                        ButtonCommandProperty = "RunEmbeddedToolCommand",
                        IsReadOnlyProperty = "IsReadOnly",
                        ToolTip = "进行签名加密的校验，并输出结果",
                        IsVisible = false)]
        public string SignatureEncryption { get; set; }

        /// <summary>
        /// 配置操作动态按钮配置列表
        /// </summary>
        public List<ButtonConfig> ConfigOperationConfig => new List<ButtonConfig>
        {
            new ButtonConfig
            {
                Text = "目录", Width = 60, Height = 26, CommandProperty = nameof(OpenParameterConfigCommand), ToolTip = "打开参数配置文件夹",
                IsEnabledProperty = $"{nameof(ConfigOpsEnabled)}",
            },

            // new ButtonConfig
            // {
            //     Text = "图片", Width = 60, Height = 26, CommandProperty = nameof(OpenImageConfigCommand), ToolTip = "打开图片配置文件夹",
            //     IsEnabledProperty = $"{nameof(IsEnabled)}",
            // },
            new ButtonConfig
            {
                Text = "模式切换", Width = 60, Height = 26, CommandProperty = nameof(ChangeModeToDebugCommand), ToolTip = "切换调试模式与正常模式",
                IsEnabledProperty = $"{nameof(ConfigOpsEnabled)}",
            },

            new ButtonConfig
            {
                Text = "切换配置", Width = 80, Height = 26, CommandProperty = nameof(ChangeConfigPresetCommand), ToolTip = "选择并应用预设配置到 ServerInfo.ini",
                IsEnabledProperty = $"{nameof(ConfigOpsEnabled)}",
            },
        };

        /// <summary>
        /// 更新操作命令
        /// </summary>
        public ICommand OpenParameterConfigCommand
        {
            get => openParameterConfigCommand ?? (openParameterConfigCommand = new RelayCommand(ExecuteOpenParameterConfig, () => SupportsConfigOps));

            set => SetProperty(ref openParameterConfigCommand, value);
        }

        /// <summary>
        /// 更新操作命令
        /// </summary>
        public ICommand OpenImageConfigCommand
        {
            get => openImageConfigCommand ?? (openImageConfigCommand = new RelayCommand(ExecuteOpenImageConfig));

            set => SetProperty(ref openImageConfigCommand, value);
        }

        /// <summary>
        /// 更新操作命令
        /// </summary>
        public ICommand ChangeModeToDebugCommand
        {
            get => changeModeToDebugCommand ?? (changeModeToDebugCommand = new RelayCommand(ExecuteToggleDebugMode, () => SupportsConfigOps));

            set => SetProperty(ref changeModeToDebugCommand, value);
        }

        /// <summary>
        /// 切换配置预设命令
        /// </summary>
        private ICommand changeConfigPresetCommand;

        public ICommand ChangeConfigPresetCommand
        {
            get => changeConfigPresetCommand ?? (changeConfigPresetCommand = new RelayCommand(ExecuteChangeConfigPreset, () => SupportsConfigOps));

            set => SetProperty(ref changeConfigPresetCommand, value);
        }

        /// <summary>
        /// 运行嵌入外部工具命令
        /// </summary>
        public ICommand RunEmbeddedToolCommand
        {
            get => runEmbeddedToolCommand ?? (runEmbeddedToolCommand = new RelayCommand(ExecuteRunEmbeddedTool));

            set => SetProperty(ref runEmbeddedToolCommand, value);
        }

        private ICommand unlockAndDownloadCommand;

        public ICommand UnlockAndDownloadCommand
        {
            get => unlockAndDownloadCommand ?? (unlockAndDownloadCommand = new RelayCommand(ExecuteUnlockAndDownload));

            set => SetProperty(ref unlockAndDownloadCommand, value);
        }

        /// <summary>
        /// 状态文本
        /// </summary>
        public string StatusText
        {
            get => statusText;

            set => SetProperty(ref statusText, value);
        }

        /// <summary>
        /// 可执行文件路径
        /// </summary>
        public string ExecutablePath
        {
            get => executablePath;

            set => SetProperty(ref executablePath, value);
        }

        /// <summary>
        /// 是否为只读状态（更新时不可编辑）
        /// </summary>
        public bool IsReadOnly
        {
            get => isReadOnly;

            set
            {
                SetProperty(ref isReadOnly, value);
                OnPropertyChanged(nameof(IsEnabled));
                OnPropertyChanged(nameof(CanRunSignatureEncryption));
                OnPropertyChanged(nameof(ConfigOpsEnabled));
            }
        }

        /// <summary>
        /// 是否为调试模式（影响按钮文案与配置写入），需持久化
        /// </summary>
        public bool IsDebugMode
        {
            get => isDebugMode;

            set => SetProperty(ref isDebugMode, value);
        }

        private bool isSignatureEncryptionRunning;

        public bool IsSignatureEncryptionRunning
        {
            get => isSignatureEncryptionRunning;
            set
            {
                if (SetProperty(ref isSignatureEncryptionRunning, value))
                {
                    OnPropertyChanged(nameof(CanRunSignatureEncryption));
                }
            }
        }

        public bool CanRunSignatureEncryption => !IsSignatureEncryptionRunning && IsEnabled;

        /// <summary>
        /// 包名
        /// </summary>
        [DataGridComboBox(3,
                          "包名",
                          "AvailablePackages",
                          Width = "320",
                          IsReadOnlyProperty = "IsReadOnly",
                          ContentAlign = HorizontalAlignment.Left,
                          IsVisible = false)]
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
            get => availableVersions ?? (availableVersions = new ObservableCollection<string>());

            set => SetProperty(ref availableVersions, value);
        }

        /// <summary>
        /// 可用包列表
        /// </summary>
        public ObservableCollection<string> AvailablePackages
        {
            get => availablePackages ?? (availablePackages = new ObservableCollection<string>());

            set => SetProperty(ref availablePackages, value);
        }

        /// <summary>
        /// 可用可执行文件版本列表
        /// </summary>
        public ObservableCollection<ApplicationVersion> AvailableExecutableVersions
        {
            get => availableExecutableVersions ?? (availableExecutableVersions = new ObservableCollection<ApplicationVersion>());

            set => SetProperty(ref availableExecutableVersions, value);
        }

        /// <summary>
        /// 更新操作命令
        /// </summary>
        public ICommand UpdateCommand
        {
            get => updateCommand ?? (updateCommand = new RelayCommand(ExecuteDownload));

            set => SetProperty(ref updateCommand, value);
        }

        /// <summary>
        /// 打开路径命令
        /// </summary>
        public ICommand OpenPathCommand
        {
            get => openPathCommand ?? (openPathCommand = new RelayCommand(ExecuteOpenPath));

            set => SetProperty(ref openPathCommand, value);
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
            try
            {
                LoggingService.LogInfo($"开始下载：Product={ProductName}, Url={DownloadUrl}");
                if (string.IsNullOrWhiteSpace(DownloadUrl))
                {
                    LoggingService.LogWarning("下载地址为空，下载可能失败。请检查 FtpServerPath/Version/UploadPackageName。");
                }

                // 触发更新事件，由MainWindow处理具体的更新逻辑
                DownloadRequested?.Invoke(this);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "触发下载事件时发生异常");
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    StatusText = $"下载触发失败：{ex.Message}";
                    Status = PackageStatus.Error;
                }));
            }
        }

        private void ExecuteOpenParameterConfig()
        {
            string path = System.IO.Path.Combine(EffectiveLocalPath, "config");
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
            string path = System.IO.Path.Combine(EffectiveLocalPath, "Image");
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

        /// <summary>
        /// 从嵌入资源提取并静默运行外部工具
        /// </summary>
        private void ExecuteRunEmbeddedTool()
        {
            new EmbeddedToolRunnerService(this).RunAsync();
        }

        private void ExecuteUnlockAndDownload()
        {
            UnlockAndDownloadRequested?.Invoke(this);
        }
        
        /// <summary>
        /// 打开预设配置窗口并写入 ServerInfo.ini
        /// </summary>
        private void ExecuteChangeConfigPreset()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(EffectiveLocalPath))
                {
                    MessageBox.Show("本地包路径无效，请先在路径设置中配置。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                
                if (!System.IO.Directory.Exists(EffectiveLocalPath))
                {
                    MessageBox.Show("本地包不存在，请先进行更新。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 读取当前配置内容，以便在选择界面默认选中
                string currentIniContent = null;
                try
                {
                    var currentIniPath = System.IO.Path.Combine(EffectiveLocalPath, "config", "ServerInfo.ini");
                    if (System.IO.File.Exists(currentIniPath))
                    {
                        currentIniContent = System.IO.File.ReadAllText(currentIniPath, Encoding.UTF8);
                    }
                }
                catch { }

                var window = new ConfigPresetWindow(currentIniContent)
                {
                    Owner = Application.Current?.MainWindow,
                };

                var result = window.ShowDialog();
                if (result != true || string.IsNullOrWhiteSpace(window.SelectedPresetContent))
                {
                    return;
                }

                var configDir = System.IO.Path.Combine(EffectiveLocalPath, "config");
                if (!System.IO.Directory.Exists(configDir))
                {
                    System.IO.Directory.CreateDirectory(configDir);
                }

                var iniPath = System.IO.Path.Combine(configDir, "ServerInfo.ini");
                System.IO.File.WriteAllText(iniPath, window.SelectedPresetContent, new UTF8Encoding(false));

                LoggingService.LogInfo($"已应用预设配置到: {iniPath}");
                StatusText = "预设配置已应用，已写入 ServerInfo.ini";
                Status = PackageStatus.Completed;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "应用预设配置时发生异常");
                StatusText = $"应用配置失败：{ex.Message}";
                Status = PackageStatus.Error;
            }
        }

        private void ExecuteToggleDebugMode()
        {
            // 切换调试模式，并通过服务写入配置
            bool target = !IsDebugMode;
            try
            {
            DebugSettingsService.WriteIsDebugMode(EffectiveLocalPath, target);
            }
            catch
            {
            }

            IsDebugMode = target;
            DebugModeChanged?.Invoke(this, target);
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
                    if (!System.IO.Directory.Exists(defaultAddinDir))
                    {
                        return;
                    }

                    string version = applicationVersion.Version;
                    string addinDir = Path.Combine(defaultAddinDir, version);

                    string binDir = System.IO.Path.Combine(EffectiveLocalPath, "bin");

                    // 查找bin目录下所有dll文件，找出以G开头、包含版本号、以.dll结尾的文件，并复制其完整路径
                    if (Directory.Exists(binDir))
                    {
                        var targetFiles = System.IO.Directory.GetFiles(binDir, "*.dll")
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

                            string addinFile = System.IO.Directory.GetFiles(EffectiveLocalPath, "*.addin").FirstOrDefault();
                            if (!string.IsNullOrEmpty(addinFile))
                            {
                                string addinStr = string.Empty;
                                using (System.IO.StreamReader streamReader = new System.IO.StreamReader(new System.IO.FileStream(addinFile, System.IO.FileMode.Open)))
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

                                System.IO.File.WriteAllText(addinFile, addinStr);

                                string addinFilePath = System.IO.Path.Combine(addinDir, Path.GetFileName(addinFile));
                                System.IO.File.Copy(addinFile, addinFilePath, true);
                            }
                        }
                    }

                    // 打开文件所在的文件夹并选中该文件
                    Process.Start(ExecutablePath);
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
        private readonly Action execute;

        private readonly Func<bool> canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
            this.canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;

            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter)
        {
            return canExecute?.Invoke() ?? true;
        }

        public void Execute(object parameter)
        {
            execute();
        }
    }
}
