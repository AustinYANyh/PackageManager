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
using System.Threading;
using System.Windows;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using PackageManager.Function.ConfigPreset;
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
        private const string DisabledAddinFolderName = "RevitAddinDisabled";

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
        private ICommand openDebugOptionsCommand;

        private bool isDebugMode;

        private ObservableCollection<ApplicationVersion> availableExecutableVersions;

        private string selectedExecutableVersion;

        private string executablePath;

        private ICommand openPathCommand;

        private bool isReadOnly;

        private bool isDownloadOnlyRunning;

        private ICommand downloadOnlyCommand;

        private string time;

        private ICommand runEmbeddedToolCommand;

        private System.Collections.Generic.Dictionary<string, string> versionLocalPaths;

        private string finalizeFtpServerPath;
        private bool isFinalizing;
        private string downloadUrlOverride;

        /// <summary>
        /// 更新请求事件
        /// </summary>
        public event Action<PackageInfo> UpdateRequested;

        /// <summary>
        /// 版本切换事件
        /// </summary>
        public event Action<PackageInfo, string> VersionChanged;

        /// <summary>
        /// 下载请求事件
        /// </summary>
        public event Action<PackageInfo> DownloadRequested;

        /// <summary>
        /// 调试模式切换事件
        /// </summary>
        public event Action<PackageInfo, bool> DebugModeChanged;

        /// <summary>
        /// 解锁并下载请求事件
        /// </summary>
        public event Action<PackageInfo> UnlockAndDownloadRequested;

        /// <summary>
        /// 仅下载ZIP包请求事件
        /// </summary>
        public event Action<PackageInfo> DownloadZipOnlyRequested;

        /// <summary>
        /// 属性值变更时触发。
        /// </summary>
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

        /// <summary>
        /// 包的上传时间（从包名解析或手动设置）
        /// </summary>
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

        /// <summary>
        /// 获取或设置各版本对应的本地路径映射。
        /// </summary>
        public System.Collections.Generic.Dictionary<string, string> VersionLocalPaths
        {
            get => versionLocalPaths ?? (versionLocalPaths = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase));
            set => SetProperty(ref versionLocalPaths, value);
        }

        private CancellationTokenSource updateCancellationSource;
        private bool isUpdatingRunning;
        private EmbeddedToolRunnerService embeddedToolRunner;
        private ICommand cancelUpdateCommand;
        private ICommand cancelEmbeddedToolCommand;

        /// <summary>
        /// 获取指定版本对应的本地路径，若未配置则返回默认 <see cref="LocalPath"/>。
        /// </summary>
        /// <param name="version">版本号。</param>
        /// <returns>对应版本的本地路径。</returns>
        public string GetLocalPathForVersion(string version)
        {
            if (!string.IsNullOrWhiteSpace(version) && VersionLocalPaths != null && VersionLocalPaths.TryGetValue(version, out var p) && !string.IsNullOrWhiteSpace(p))
            {
                return p;
            }
            return LocalPath;
        }

        /// <summary>
        /// 获取当前版本的有效本地路径。
        /// </summary>
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

        /// <summary>
        /// 操作按钮列的占位属性。
        /// </summary>
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
                             TextFormat = "{0:F1}%",
                             IsVisible = false)]
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

        /// <summary>
        /// 获取是否启用（非只读状态）。
        /// </summary>
        public bool IsEnabled => !IsReadOnly;

        /// <summary>
        /// 获取更新/取消按钮的启用状态。
        /// </summary>
        public bool UpdateCancelEnabled => IsUpdatingRunning || IsEnabled;

        /// <summary>
        /// 获取签名加密按钮的启用状态。
        /// </summary>
        public bool SignatureCancelEnabled => IsSignatureEncryptionRunning || IsEnabled;

        /// <summary>
        /// 获取当前产品是否为 TeamworkMaster(Develop)。
        /// </summary>
        public bool IsTeamworkMasterDevelop => string.Equals(ProductName, "TeamworkMaster(Develop)", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 获取或设置是否支持配置操作的覆盖值。
        /// </summary>
        public bool? SupportsConfigOpsOverride { get; set; }

        /// <summary>
        /// 获取当前产品是否支持配置操作。
        /// </summary>
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

        /// <summary>
        /// 获取配置操作按钮的启用状态。
        /// </summary>
        public bool ConfigOpsEnabled => SupportsConfigOps && IsEnabled;

        /// <summary>
        /// 签名加密操作按钮的占位属性。
        /// </summary>
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
                Text = "调试选项", Width = 80, Height = 26, CommandProperty = nameof(OpenDebugOptionsCommand), ToolTip = "查看并编辑 DebugSetting.json",
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
        /// 获取或设置打开调试选项的命令。
        /// </summary>
        public ICommand OpenDebugOptionsCommand
        {
            get => openDebugOptionsCommand ?? (openDebugOptionsCommand = new RelayCommand(ExecuteOpenDebugOptions, () => SupportsConfigOps));

            set => SetProperty(ref openDebugOptionsCommand, value);
        }

        /// <summary>
        /// 切换配置预设命令
        /// </summary>
        private ICommand changeConfigPresetCommand;

        /// <summary>
        /// 获取或设置切换配置预设的命令。
        /// </summary>
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

        /// <summary>
        /// 获取或设置解锁并下载的命令。
        /// </summary>
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
                OnPropertyChanged(nameof(UpdateCancelEnabled));
                OnPropertyChanged(nameof(SignatureCancelEnabled));
                OnPropertyChanged(nameof(DownloadOnlyEnabled));
            }
        }

        /// <summary>
        /// 仅下载流程是否运行中（用于禁用“仅下载”按钮）
        /// </summary>
        public bool IsDownloadOnlyRunning
        {
            get => isDownloadOnlyRunning;
            set
            {
                if (SetProperty(ref isDownloadOnlyRunning, value))
                {
                    OnPropertyChanged(nameof(DownloadOnlyEnabled));
                }
            }
        }

        /// <summary>
        /// “仅下载”按钮可用性：仅当未处于仅下载运行中且整体可用时启用
        /// </summary>
        public bool DownloadOnlyEnabled => !IsDownloadOnlyRunning && IsEnabled;

        /// <summary>
        /// 用于定版上传的目标FTP根路径
        /// </summary>
        [DataGridColumn(5, DisplayName = "定版FTP路径", Width = "350", IsReadOnly = true, IsVisible = false)]
        public string FinalizeFtpServerPath
        {
            get => finalizeFtpServerPath;
            set
            {
                if (SetProperty(ref finalizeFtpServerPath, value))
                {
                    OnPropertyChanged(nameof(FinalizeButtonEnabled));
                }
            }
        }

        /// <summary>
        /// 定版流程是否运行中（用于禁用按钮）
        /// </summary>
        public bool IsFinalizing
        {
            get => isFinalizing;
            set
            {
                if (SetProperty(ref isFinalizing, value))
                {
                    OnPropertyChanged(nameof(FinalizeButtonEnabled));
                }
            }
        }

        /// <summary>
        /// 定版按钮可用性：需已配置定版路径且当前不在定版中
        /// </summary>
        public bool FinalizeButtonEnabled => !IsFinalizing && !string.IsNullOrWhiteSpace(FinalizeFtpServerPath);

        /// <summary>
        /// 更新流程是否运行中（用于切换按钮文案与命令）
        /// </summary>
        public bool IsUpdatingRunning
        {
            get => isUpdatingRunning;
            set
            {
                if (SetProperty(ref isUpdatingRunning, value))
                {
                    OnPropertyChanged(nameof(UpdateButtonText));
                    OnPropertyChanged(nameof(UnlockUpdateButtonText));
                    OnPropertyChanged(nameof(UpdateToggleCommand));
                    OnPropertyChanged(nameof(UnlockUpdateToggleCommand));
                    OnPropertyChanged(nameof(UpdateCancelEnabled));
                }
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

        /// <summary>
        /// 获取或设置签名加密流程是否运行中。
        /// </summary>
        public bool IsSignatureEncryptionRunning
        {
            get => isSignatureEncryptionRunning;
            set
            {
                if (SetProperty(ref isSignatureEncryptionRunning, value))
                {
                    OnPropertyChanged(nameof(CanRunSignatureEncryption));
                    OnPropertyChanged(nameof(SignatureToggleText));
                    OnPropertyChanged(nameof(SignatureToggleCommand));
                    OnPropertyChanged(nameof(SignatureCancelEnabled));
                }
            }
        }

        /// <summary>
        /// 获取是否可以运行签名加密（非运行中且启用状态）。
        /// </summary>
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
        /// 更新/取消切换命令（运行中时为取消，否则为更新）
        /// </summary>
        public ICommand UpdateToggleCommand => IsUpdatingRunning ? CancelUpdateCommand : UpdateCommand;

        /// <summary>
        /// 解锁更新/取消切换命令（运行中时为取消，否则为解锁更新）
        /// </summary>
        public ICommand UnlockUpdateToggleCommand => IsUpdatingRunning ? CancelUpdateCommand : UnlockAndDownloadCommand;

        /// <summary>
        /// 签名加密校验/取消切换命令（运行中时为取消，否则为校验）
        /// </summary>
        public ICommand SignatureToggleCommand => IsSignatureEncryptionRunning ? CancelEmbeddedToolCommand : RunEmbeddedToolCommand;

        /// <summary>
        /// 仅下载命令
        /// </summary>
        public ICommand DownloadOnlyCommand
        {
            get => downloadOnlyCommand ?? (downloadOnlyCommand = new RelayCommand(ExecuteDownloadOnly, () => DownloadOnlyEnabled));
            set => SetProperty(ref downloadOnlyCommand, value);
        }

        /// <summary>
        /// 更新按钮文案（运行中显示“取消”）
        /// </summary>
        public string UpdateButtonText => IsUpdatingRunning ? "取消" : "更新";

        /// <summary>
        /// 解锁更新按钮文案（运行中显示“取消”）
        /// </summary>
        public string UnlockUpdateButtonText => IsUpdatingRunning ? "取消" : "解锁更新";

        /// <summary>
        /// 签名加密按钮文案（运行中显示“取消”）
        /// </summary>
        public string SignatureToggleText => IsSignatureEncryptionRunning ? "取消" : "签名加密";

        /// <summary>
        /// 取消更新命令
        /// </summary>
        public ICommand CancelUpdateCommand
        {
            get => cancelUpdateCommand ?? (cancelUpdateCommand = new RelayCommand(ExecuteCancelUpdate));
            set => SetProperty(ref cancelUpdateCommand, value);
        }

        /// <summary>
        /// 取消签名加密校验命令
        /// </summary>
        public ICommand CancelEmbeddedToolCommand
        {
            get => cancelEmbeddedToolCommand ?? (cancelEmbeddedToolCommand = new RelayCommand(ExecuteCancelEmbeddedTool));
            set => SetProperty(ref cancelEmbeddedToolCommand, value);
        }

        /// <summary>
        /// 更新流程的取消令牌源（由界面启动时创建）
        /// </summary>
        public CancellationTokenSource UpdateCancellationSource
        {
            get => updateCancellationSource;
            set => SetProperty(ref updateCancellationSource, value);
        }

        /// <summary>
        /// 嵌入工具运行器引用（用于取消）
        /// </summary>
        public EmbeddedToolRunnerService EmbeddedToolRunner
        {
            get => embeddedToolRunner;
            set => SetProperty(ref embeddedToolRunner, value);
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
        /// 设置下载地址覆盖值。
        /// </summary>
        /// <param name="url">覆盖的下载地址。</param>
        public void SetDownloadUrlOverride(string url)
        {
            if (downloadUrlOverride == url) return;
            downloadUrlOverride = url;
            OnPropertyChanged(nameof(DownloadUrl));
        }

        /// <summary>
        /// 完整的下载地址（FTP路径 + 版本 + 上传时间）
        /// </summary>
        public string DownloadUrl
        {
            get
            {
                if (!string.IsNullOrEmpty(downloadUrlOverride))
                {
                    return downloadUrlOverride;
                }
                if (string.IsNullOrEmpty(FtpServerPath) ||
                    string.IsNullOrEmpty(Version) ||
                    string.IsNullOrEmpty(UploadPackageName))
                {
                    return string.Empty;
                }

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

            // 刷新时默认选择：Dazzle 强制首项，其他包仅在未选择时选末项
            if (AvailableVersions.Count > 0)
            {
                if (string.Equals(ProductName, "BuildMaster(Dazzle)", StringComparison.OrdinalIgnoreCase))
                {
                    Version = AvailableVersions.First();
                }
                else if (string.IsNullOrEmpty(Version))
                {
                    Version = AvailableVersions.Last();
                }
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

        /// <summary>
        /// 触发 <see cref="PropertyChanged"/> 事件。
        /// </summary>
        /// <param name="propertyName">发生变更的属性名称。</param>
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// 设置属性值并在值变更时触发 <see cref="PropertyChanged"/> 事件。
        /// </summary>
        /// <typeparam name="T">属性类型。</typeparam>
        /// <param name="field">属性 backing 字段的引用。</param>
        /// <param name="value">新值。</param>
        /// <param name="propertyName">属性名称。</param>
        /// <returns>值是否发生变更。</returns>
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
                if (!string.IsNullOrWhiteSpace(settings?.AddinPath))
                {
                    return settings.AddinPath.Trim();
                }
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
                return settings?.ProgramEntryWithG ?? true;
            }

            return true;
        }

        /// <summary>
        /// 将当前产品对应的 Revit Addin 同步到目标版本目录。
        /// </summary>
        /// <param name="packageRootPath">包根目录；为空时使用当前有效本地路径。</param>
        public void DeploySelectedRevitAddin(string packageRootPath = null)
        {
            var applicationVersion = ResolveSelectedApplicationVersion();
            if (applicationVersion == null)
            {
                LoggingService.LogWarning($"同步 Revit Addin 跳过：{ProductName} 未找到已选择的 Revit 版本。");
                return;
            }

            var resolvedPackageRoot = string.IsNullOrWhiteSpace(packageRootPath) ? EffectiveLocalPath : packageRootPath;
            if (string.IsNullOrWhiteSpace(resolvedPackageRoot) || !Directory.Exists(resolvedPackageRoot))
            {
                LoggingService.LogWarning($"同步 Revit Addin 跳过：{ProductName} 的包目录无效，Path={resolvedPackageRoot}");
                return;
            }

            var addinTemplateFile = Directory.GetFiles(resolvedPackageRoot, "*.addin", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(addinTemplateFile))
            {
                LoggingService.LogInfo($"同步 Revit Addin 跳过：{ProductName} 在 {resolvedPackageRoot} 未找到 addin 模板。");
                return;
            }

            var targetAssembly = ResolveRevitEntryAssemblyPath(resolvedPackageRoot, applicationVersion.Version);
            if (string.IsNullOrWhiteSpace(targetAssembly))
            {
                LoggingService.LogWarning($"同步 Revit Addin 跳过：{ProductName} 未找到适用于 Revit {applicationVersion.Version} 的入口 DLL。");
                return;
            }

            var addinContent = File.ReadAllText(addinTemplateFile, Encoding.UTF8);
            const string assemblyPattern = @"<Assembly>.*?</Assembly>";
            if (!Regex.IsMatch(addinContent, assemblyPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                throw new InvalidOperationException($"Addin 模板缺少 Assembly 节点：{addinTemplateFile}");
            }

            addinContent = Regex.Replace(addinContent,
                                         assemblyPattern,
                                         $"<Assembly>{targetAssembly}</Assembly>",
                                         RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var addinDir = Path.Combine(GetAddinPath(), applicationVersion.Version);
            Directory.CreateDirectory(addinDir);

            var targetAddinFileName = Path.GetFileName(addinTemplateFile);
            DeleteDisabledRevitAddin(applicationVersion.Version, targetAddinFileName);

            var targetAddinFile = Path.Combine(addinDir, targetAddinFileName);
            File.WriteAllText(targetAddinFile, addinContent, new UTF8Encoding(false));

            LoggingService.LogInfo(
                $"已同步 Revit Addin：Product={ProductName}, RevitVersion={applicationVersion.Version}, Target={targetAddinFile}, Assembly={targetAssembly}");
        }

        private static void DeleteDisabledRevitAddin(string revitVersion, string addinFileName)
        {
            if (DataPersistenceService == null ||
                string.IsNullOrWhiteSpace(revitVersion) ||
                string.IsNullOrWhiteSpace(addinFileName))
            {
                return;
            }

            var disabledAddinFile = Path.Combine(
                DataPersistenceService.GetDataFolderPath(),
                DisabledAddinFolderName,
                revitVersion,
                addinFileName);
            if (!File.Exists(disabledAddinFile))
            {
                return;
            }

            File.Delete(disabledAddinFile);
            LoggingService.LogInfo($"已删除禁用目录中的旧 Revit Addin：{disabledAddinFile}");
        }

        private ApplicationVersion ResolveSelectedApplicationVersion()
        {
            var applicationVersion = AvailableExecutableVersions?.FirstOrDefault(x => x.DisPlayName == SelectedExecutableVersion);
            return applicationVersion ?? AvailableExecutableVersions?.FirstOrDefault();
        }

        /// <summary>
        /// 获取当前选中的应用程序版本信息。
        /// </summary>
        /// <returns>选中的 <see cref="ApplicationVersion"/>，若无选中则返回列表首项。</returns>
        public ApplicationVersion GetSelectedApplicationVersion()
        {
            return ResolveSelectedApplicationVersion();
        }

        private string ResolveRevitEntryAssemblyPath(string packageRootPath, string revitVersion)
        {
            var binDir = Path.Combine(packageRootPath, "bin");
            if (!Directory.Exists(binDir) || string.IsNullOrWhiteSpace(revitVersion))
            {
                return null;
            }

            var targetFile = Directory.GetFiles(binDir, "*.dll", SearchOption.TopDirectoryOnly)
                                      .Where(file =>
                                      {
                                          var fileName = Path.GetFileName(file);
                                          return fileName.StartsWith("G", StringComparison.OrdinalIgnoreCase) &&
                                                 fileName.Contains(revitVersion);
                                      })
                                      .OrderBy(Path.GetFileName)
                                      .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(targetFile))
            {
                return null;
            }

            if (!GetProgramEntryWithG())
            {
                var fileName = Path.GetFileName(targetFile);
                if (!string.IsNullOrWhiteSpace(fileName) && fileName.StartsWith("G", StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = Path.Combine(Path.GetDirectoryName(targetFile) ?? string.Empty, fileName.Substring(1));
                    if (File.Exists(candidate))
                    {
                        targetFile = candidate;
                    }
                    else
                    {
                        LoggingService.LogWarning($"未找到去除前缀 G 后的入口 DLL，继续使用原文件：{candidate}");
                    }
                }
            }

            return targetFile;
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

        /// <summary>
        /// 执行仅下载（不解压）操作
        /// </summary>
        private void ExecuteDownloadOnly()
        {
            try
            {
                LoggingService.LogInfo($"开始仅下载：Product={ProductName}, Url={DownloadUrl}");
                if (string.IsNullOrWhiteSpace(DownloadUrl))
                {
                    LoggingService.LogWarning("下载地址为空，下载可能失败。请检查 FtpServerPath/Version/UploadPackageName。");
                }

                DownloadZipOnlyRequested?.Invoke(this);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "触发仅下载事件时发生异常");
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    StatusText = $"仅下载触发失败：{ex.Message}";
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
            EmbeddedToolRunner = new EmbeddedToolRunnerService(this);
            EmbeddedToolRunner.RunAsync();
        }

        private void ExecuteUnlockAndDownload()
        {
            UnlockAndDownloadRequested?.Invoke(this);
        }

        private void ExecuteCancelUpdate()
        {
            try
            {
                UpdateCancellationSource?.Cancel();
                StatusText = $"{ProductName} 取消中...";
            }
            catch { }
        }

        private void ExecuteCancelEmbeddedTool()
        {
            try
            {
                EmbeddedToolRunner?.Cancel();
                StatusText = $"{ProductName} 校验取消中...";
            }
            catch { }
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

        private void ExecuteOpenDebugOptions()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(EffectiveLocalPath))
                {
                    MessageBox.Show("本地包路径无效，请先在路径设置中配置。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!Directory.Exists(EffectiveLocalPath))
                {
                    MessageBox.Show("本地包不存在，请先进行更新。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var window = new PackageManager.Function.Setting.DebugOptionsWindow(EffectiveLocalPath)
                {
                    Owner = Application.Current?.MainWindow,
                };
                var ok = window.ShowDialog();
                if (ok == true)
                {
                    try
                    {
                        bool isDebug = DebugSettingsService.ReadIsDebugMode(EffectiveLocalPath, IsDebugMode);
                        IsDebugMode = isDebug;
                        StatusText = "调试配置已保存";
                        Status = PackageStatus.Completed;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "打开调试选项失败");
            }
        }

        /// <summary>
        /// 执行打开路径操作
        /// </summary>
        private void ExecuteOpenPath()
        {
            ApplicationVersion applicationVersion = ResolveSelectedApplicationVersion();
            if (applicationVersion == null)
            {
                return;
            }

            ExecutablePath = applicationVersion.ExecutablePath;
            if (!string.IsNullOrEmpty(ExecutablePath) && File.Exists(ExecutablePath))
            {
                try
                {
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

        /// <summary>
        /// 初始化 <see cref="RelayCommand"/> 的新实例。
        /// </summary>
        /// <param name="execute">执行的操作。</param>
        /// <param name="canExecute">判断是否可执行的函数，为 null 时始终可执行。</param>
        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
            this.canExecute = canExecute;
        }

        /// <summary>
        /// 当影响命令是否应执行的条件发生更改时触发。
        /// </summary>
        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;

            remove => CommandManager.RequerySuggested -= value;
        }

        /// <summary>
        /// 判断当前命令是否可以执行。
        /// </summary>
        /// <param name="parameter">命令参数（未使用）。</param>
        /// <returns>如果可以执行返回 true，否则返回 false。</returns>
        public bool CanExecute(object parameter)
        {
            return canExecute?.Invoke() ?? true;
        }

        /// <summary>
        /// 执行命令。
        /// </summary>
        /// <param name="parameter">命令参数（未使用）。</param>
        public void Execute(object parameter)
        {
            execute();
        }
    }
}
