using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
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

        // 阶段进度聚合（按外部工具输出的 [STAGE]/[PROGRESS] 进行合并）
        private readonly Dictionary<string, double> stageProgress = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> stageOrder = new List<string>();
        private string currentStage;

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
                    TryLoadDebugModeFromConfig();
                }
            }
        }

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
                          ComboBoxSelectedValuePath = "DisPlayName")]
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
                        IsReadOnlyProperty = "IsReadOnly")]
        public string OpenPath { get; set; }

        /// <summary>
        /// 配置操作
        /// </summary>
        [DataGridMultiButton(nameof(ConfigOperationConfig), 11, 
                             DisplayName = "配置操作", Width = "200", ButtonSpacing = 15)]
        public string ConfigOperation { get; set; }

        public bool IsEnabled => !IsReadOnly;

        
        [DataGridButton(12,
                        DisplayName = "签名加密",
                        Width = "100",
                        ControlType = "Button",
                        ButtonText = "校验",
                        ButtonWidth = 80,
                        ButtonHeight = 26,
                        ButtonCommandProperty = "RunEmbeddedToolCommand",
                        IsReadOnlyProperty = "IsReadOnly",
                        ToolTip = "进行签名加密的校验，并输出结果")]
        public string SignatureEncryption { get; set; }
        

        /// <summary>
        /// 配置操作动态按钮配置列表
        /// </summary>
        public List<ButtonConfig> ConfigOperationConfig => new List<ButtonConfig>
        {
            new ButtonConfig
            {
                Text = "目录", Width = 60, Height = 26, CommandProperty = nameof(OpenParameterConfigCommand), ToolTip = "打开参数配置文件夹",
                IsEnabledProperty = $"{nameof(IsEnabled)}",
            },
            // new ButtonConfig
            // {
            //     Text = "图片", Width = 60, Height = 26, CommandProperty = nameof(OpenImageConfigCommand), ToolTip = "打开图片配置文件夹",
            //     IsEnabledProperty = $"{nameof(IsEnabled)}",
            // },
            new ButtonConfig
            {
                Text = "模式切换", Width = 60, Height = 26, CommandProperty = nameof(ChangeModeToDebugCommand), ToolTip = "切换调试模式与正常模式",
                IsEnabledProperty = $"{nameof(IsEnabled)}",
            },
        };

        /// <summary>
        /// 更新操作命令
        /// </summary>
        public ICommand OpenParameterConfigCommand
        {
            get => openParameterConfigCommand ?? (openParameterConfigCommand = new RelayCommand(ExecuteOpenParameterConfig));

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
            get => changeModeToDebugCommand ?? (changeModeToDebugCommand = new RelayCommand(ExecuteToggleDebugMode));

            set => SetProperty(ref changeModeToDebugCommand, value);
        }
        
        private ICommand runEmbeddedToolCommand;

        /// <summary>
        /// 运行嵌入外部工具命令
        /// </summary>
        public ICommand RunEmbeddedToolCommand
        {
            get => runEmbeddedToolCommand ?? (runEmbeddedToolCommand = new RelayCommand(ExecuteRunEmbeddedTool));

            set => SetProperty(ref runEmbeddedToolCommand, value);
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

        /// <summary>
        /// 从嵌入资源提取并静默运行外部工具
        /// </summary>
        private void ExecuteRunEmbeddedTool()
        {
            // 异步运行，支持并发，避免阻塞UI
            System.Threading.Tasks.Task.Run(() =>
            {
                IsReadOnly = true;
                
                try
                {
                    const string resourceSuffix = "签名加密校验20251112.exe";
                    const string outputFileName = "签名加密校验20251112.exe";

                    string exePath = EnsureEmbeddedToolExtracted(resourceSuffix, outputFileName);
                    if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            StatusText = "未找到嵌入的工具资源";
                        }));
                        IsReadOnly = false;
                        return;
                    }

                    // 为当前产品生成独立日志文件，并启动实时追踪
                    string logPath = GetEmbeddedToolLogPath();
                    var cts = new CancellationTokenSource();
                    EnsureLogFileExists(logPath);
                    StartRealtimeLogTail(logPath, cts.Token);
                    
                    string resultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), UploadPackageName + "_result.log");

                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = BuildEmbeddedToolArguments(logPath,resultPath),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
                    };
                    
                    var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

                    // process.OutputDataReceived += (s, e) =>
                    // {
                    //     if (!string.IsNullOrWhiteSpace(e.Data))
                    //     {
                    //         // 将输出写入日志文件，供状态栏实时追踪
                    //         try { File.AppendAllText(logPath, e.Data + Environment.NewLine, Encoding.UTF8); } catch { }
                    //         Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    //         {
                    //             StatusText = e.Data;
                    //             ParseToolOutputLine(e.Data);
                    //         }));
                    //     }
                    // };
                    // process.ErrorDataReceived += (s, e) =>
                    // {
                    //     if (!string.IsNullOrWhiteSpace(e.Data))
                    //     {
                    //         // 错误输出写入日志
                    //         try { File.AppendAllText(logPath, "[ERROR] " + e.Data + Environment.NewLine, Encoding.UTF8); } catch { }
                    //         Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    //         {
                    //             StatusText = $"工具错误：{e.Data}";
                    //             ParseToolOutputLine(e.Data);
                    //         }));
                    //     }
                    // };
                    process.Exited += (s, e) =>
                    {
                        try
                        {
                            var exitCode = process.ExitCode;
                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                StatusText = exitCode == 0 ? "外部工具运行完成" : $"外部工具运行失败（{exitCode}）";
                                Progress = exitCode == 0 ? 100 : Progress; // 成功则满进度
                                Status = exitCode == 0 ? PackageStatus.Completed : PackageStatus.Error;
                            }));
                        }
                        finally
                        {
                            process.Dispose();
                            IsReadOnly = false;
                            cts.Cancel();
                        }

                        if (File.Exists(resultPath))
                        {
                            //打开 文件
                            Process.Start(resultPath);
                        }
                    };

                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StatusText = $"正在运行外部工具：{ProductName}";
                        Status = PackageStatus.Downloading;
                        Progress = 0;
                        stageProgress.Clear();
                        stageOrder.Clear();
                        currentStage = null;
                    }));

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StatusText = $"运行外部工具失败：{ex.Message}";
                        Status = PackageStatus.Error;
                    }));
                    IsReadOnly = false;
                }
            });
        }
        
        /// <summary>
        /// 根据当前包信息构建外部工具的命令行参数
        /// </summary>
        private string BuildEmbeddedToolArguments(string logPath, string resultPath)
        {
            // 根据对方工具的约定调整参数格式
            string downloadUrl = DownloadUrl;
            if (string.IsNullOrEmpty(downloadUrl)) downloadUrl = string.Empty;
            var args = $"-u \"{downloadUrl}\""; 
            args += $" --no-wait-close";
            args += $" -o \"{resultPath}\"";
            args += $" -p \"{logPath}\"";
            return args;
        }

        private string GetEmbeddedToolLogPath()
        {
            var baseName = string.IsNullOrWhiteSpace(ProductName) ? "Package" : ProductName;
            var safeName = SanitizeFileName(baseName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackageManager", "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, safeName + ".log");
        }

        private static string SanitizeFileName(string input)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
            {
                sb.Append(invalid.Contains(ch) ? '_' : ch);
            }
            return sb.ToString();
        }

        private static void EnsureLogFileExists(string logPath)
        {
            try
            {
                if (!File.Exists(logPath))
                {
                    using (var fs = new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { }
                }
            }
            catch { }
        }

        private void StartRealtimeLogTail(string logPath, CancellationToken token)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                long position = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (!File.Exists(logPath))
                        {
                            Thread.Sleep(250);
                            continue;
                        }

                        using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new StreamReader(fs, Encoding.UTF8, true))
                        {
                            if (fs.Length < position)
                            {
                                position = 0; // 日志被截断，重置
                            }
                            fs.Seek(position, SeekOrigin.Begin);

                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                var captured = line;
                                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    if (!string.IsNullOrWhiteSpace(captured))
                                    {
                                        StatusText = captured;
                                        ParseToolOutputLine(captured);
                                    }
                                }));
                            }
                            position = fs.Position;
                        }
                    }
                    catch
                    {
                        // 忽略瞬时读写冲突
                    }

                    Thread.Sleep(250);
                }
            }, token);
        }

        /// <summary>
        /// 解析外部工具输出的阶段与进度：
        /// 支持规范化行：[STAGE] {stage} {status} 与 [PROGRESS] {stage} {pct}%。
        /// 将各阶段独立的1-100合并为总完成度显示到 order=8 的进度列。
        /// </summary>
        private void ParseToolOutputLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            try
            {
                // [STAGE] 阶段行
                var stageMatch = Regex.Match(line, "^\\s*\\[STAGE\\]\\s+(.+?)\\s+(.+?)\\s*$");
                if (stageMatch.Success)
                {
                    var stage = stageMatch.Groups[1].Value.Trim();
                    var statusWord = stageMatch.Groups[2].Value.Trim();

                    EnsureStage(stage);
                    currentStage = stage;

                    // 根据阶段名映射状态枚举（若包含签名/加密则进入相应验证状态）
                    var mappedStatus = MapStatusForStage(stage);
                    if (mappedStatus != Status)
                    {
                        Status = mappedStatus;
                    }

                    // 若阶段标记为结束/完成，则将该阶段设置为100%
                    if (IsStageCompleteStatus(statusWord))
                    {
                        stageProgress[stage] = 100;
                        UpdateOverallAggregatedProgress();
                    }

                    return;
                }

                // [PROGRESS] 进度行（按5%节流由外部工具产生，我们直接取最新值）
                var progressMatch = Regex.Match(line, "^\\s*\\[PROGRESS\\]\\s+(.+?)\\s+(\\d{1,3})%\\s*$");
                if (progressMatch.Success)
                {
                    var stage = progressMatch.Groups[1].Value.Trim();
                    var pctStr = progressMatch.Groups[2].Value.Trim();
                    if (!int.TryParse(pctStr, out var pct)) pct = 0;

                    EnsureStage(stage);
                    currentStage = stage;

                    // 更新阶段进度
                    stageProgress[stage] = Math.Max(0, Math.Min(100, pct));

                    // 状态映射
                    var mappedStatus = MapStatusForStage(stage);
                    if (mappedStatus != Status)
                    {
                        Status = mappedStatus;
                    }

                    UpdateOverallAggregatedProgress();
                }
            }
            catch
            {
                // 忽略解析异常
            }
        }

        private void EnsureStage(string stage)
        {
            if (string.IsNullOrWhiteSpace(stage)) return;
            if (!stageProgress.ContainsKey(stage))
            {
                stageProgress[stage] = 0;
            }
            if (!stageOrder.Contains(stage))
            {
                stageOrder.Add(stage);
            }
        }

        private void UpdateOverallAggregatedProgress()
        {
            var total = Math.Max(stageOrder.Count, 1);
            var completed = stageProgress.Values.Count(v => v >= 100);
            double current = 0;
            if (!string.IsNullOrEmpty(currentStage) && stageProgress.TryGetValue(currentStage, out var cp))
            {
                current = cp;
            }

            // 合并：完成阶段数 + 当前阶段百分比
            var overall = ((completed + current / 100.0) / total) * 100.0;

            // 保证单调不减
            Progress = Math.Max(Progress, overall);

            // 若所有阶段均完成则标记为完成
            if (stageOrder.Count > 0 && stageOrder.All(st => stageProgress.TryGetValue(st, out var v) && v >= 100))
            {
                Status = PackageStatus.Completed;
                Progress = Math.Max(Progress, 100);
            }
        }

        private PackageStatus MapStatusForStage(string stage)
        {
            if (string.IsNullOrWhiteSpace(stage)) return Status;

            // 关键词映射（中文优先）
            if (Regex.IsMatch(stage, "签名", RegexOptions.IgnoreCase))
            {
                return PackageStatus.VerifyingSignature;
            }
            if (Regex.IsMatch(stage, "加密", RegexOptions.IgnoreCase))
            {
                return PackageStatus.VerifyingEncryption;
            }
            if (Regex.IsMatch(stage, "下载", RegexOptions.IgnoreCase))
            {
                return PackageStatus.Downloading;
            }
            if (Regex.IsMatch(stage, "解压", RegexOptions.IgnoreCase))
            {
                return PackageStatus.Extracting;
            }

            // 默认保持当前状态，以免频繁跳变
            return Status;
        }

        private static bool IsStageCompleteStatus(string statusWord)
        {
            if (string.IsNullOrWhiteSpace(statusWord)) return false;
            var s = statusWord.Trim().ToLowerInvariant();
            return s == "end" || s == "done" || s == "finish" || s == "finished" || s == "success" || s == "completed" || s == "完成" || s == "结束" || s == "完毕";
        }

        /// <summary>
        /// 提取嵌入的exe到本地工具目录
        /// </summary>
        private static string EnsureEmbeddedToolExtracted(string resourceSuffix, string outputFileName)
        {
            try
            {
                var asm = typeof(PackageInfo).Assembly;
                var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }

                var targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackageManager", "tools");
                Directory.CreateDirectory(targetDir);
                var targetPath = Path.Combine(targetDir, outputFileName);

                // 并发安全：若文件已存在则直接复用；否则尝试写入
                if (File.Exists(targetPath))
                {
                    return targetPath;
                }

                using (var stream = asm.GetManifestResourceStream(name))
                {
                    // 双重检查，避免并发写入冲突
                    if (File.Exists(targetPath))
                    {
                        return targetPath;
                    }
                    using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        stream.CopyTo(fs);
                    }
                }

                return targetPath;
            }
            catch
            {
                return null;
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