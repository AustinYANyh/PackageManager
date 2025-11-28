using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms.VisualStyles;
using PackageManager.Models;

namespace PackageManager.Services
{
    /// <summary>
    /// 负责提取并运行嵌入的校验工具（签名/加密校验），并回传结果到PackageInfo
    /// </summary>
    public class EmbeddedToolRunnerService
    {
        public EmbeddedToolRunnerService(PackageInfo package)
        {
            this.Package = package;
        }

        public PackageInfo Package { get; private set; }

        private readonly Dictionary<string, double> stageProgress = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> stageOrder = new List<string>();
        private string currentStage;
        private CancellationTokenSource logTailCts;
        private Process runningProcess;
        private volatile bool isCancelled;
        
        /// <summary>
        /// 运行嵌入外部工具（异步，不阻塞UI）
        /// </summary>
        public void RunAsync()
        {
            if (Package == null) throw new ArgumentNullException(nameof(Package));

            Task.Run(() =>
            {
                Package.IsSignatureEncryptionRunning = true;

                try
                {
                    const string resourceSuffix = "签名加密校验20251112.exe";
                    const string outputFileName = "签名加密校验20251112.exe";

                    string exePath = EnsureEmbeddedToolExtracted(resourceSuffix, outputFileName);
                    if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                    {
                        LoggingService.LogWarning("未找到嵌入的工具资源或文件不存在：" + (exePath ?? "<null>"));
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            Package.StatusText = "未找到嵌入的工具资源";
                        }));
                        Package.IsSignatureEncryptionRunning = false;
                        return;
                    }

                    string logPath = GetEmbeddedToolLogPath(Package.ProductName);
                    EnsureLogFileExists(logPath);
                    logTailCts = new CancellationTokenSource();
                    StartRealtimeLogTail(logPath, logTailCts.Token, Package);
                    
                    string resultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Package.UploadPackageName + "_result.log");

                    var psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = BuildEmbeddedToolArguments(Package.DownloadUrl, logPath, resultPath),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
                    };

                    LoggingService.LogInfo($"启动外部工具：Exe={exePath}, Args={psi.Arguments}");
                    var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    runningProcess = process;
                    process.Exited += (s, e) =>
                    {
                        try
                        {
                            var exitCode = process.ExitCode;
                            if (!isCancelled && exitCode != 0)
                            {
                                LoggingService.LogError(new Exception($"ExitCode={exitCode}"), "外部工具运行失败");
                            }
                            else
                            {
                                LoggingService.LogInfo("外部工具运行完成（ExitCode=0）");
                            }

                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (isCancelled)
                                {
                                    Package.StatusText = "校验已取消";
                                    Package.Status = Models.PackageStatus.Ready;
                                    Package.Progress = 0;
                                    PackageUpdateService.NotifyVerificationCompleted(Package, false);
                                }
                                else
                                {
                                    Package.StatusText = exitCode == 0 ? "外部工具运行完成" : $"外部工具运行失败（{exitCode}）";
                                    Package.Progress = exitCode == 0 ? 100 : Package.Progress;
                                    Package.Status = exitCode == 0 ? Models.PackageStatus.Completed : Models.PackageStatus.Error;
                                    PackageUpdateService.NotifyVerificationCompleted(Package, exitCode == 0);
                                }
                            }));
                        }
                        finally
                        {
                            try { logTailCts?.Cancel(); } catch { }
                            process.Dispose();
                            Package.IsSignatureEncryptionRunning = false;
                        }

                        if (File.Exists(resultPath))
                        {
                            try
                            {
                                Process.Start(resultPath);
                            }
                            catch (Exception openEx)
                            {
                                LoggingService.LogError(openEx, "打开结果文件失败：" + resultPath);
                            }
                        }
                    };

                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Package.StatusText = $"正在运行外部工具：{Package.ProductName}";
                        Package.Status = Models.PackageStatus.Downloading;
                        Package.Progress = 0;
                        InitializeDefaultStages();
                    }));

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                }
                catch (Exception ex)
                {
                    LoggingService.LogError(ex, "运行外部工具失败（启动阶段）");
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Package.StatusText = $"运行外部工具失败：{ex.Message}";
                        Package.Status = Models.PackageStatus.Error;
                    }));
                    Package.IsSignatureEncryptionRunning = false;
                }
            });
        }

        /// <summary>
        /// 取消正在运行的嵌入式工具
        /// </summary>
        public void Cancel()
        {
            try
            {
                isCancelled = true;
                try { logTailCts?.Cancel(); } catch { }
                if (runningProcess != null)
                {
                    try
                    {
                        if (!runningProcess.HasExited)
                        {
                            runningProcess.Kill();
                        }
                    }
                    catch { }
                }

                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Package.StatusText = "校验取消中...";
                }));
            }
            catch { }
        }

        private string BuildEmbeddedToolArguments(string downloadUrl, string logPath, string resultPath)
        {
            if (string.IsNullOrEmpty(downloadUrl)) downloadUrl = string.Empty;
            var args = $"-u \"{downloadUrl}\"";
            args += " --no-wait-close";
            args += $" -o \"{resultPath}\"";
            args += $" -p \"{logPath}\"";
            return args;
        }

        private string GetEmbeddedToolLogPath(string productName)
        {
            var baseName = string.IsNullOrWhiteSpace(productName) ? "Package" : productName;
            var safeName = SanitizeFileName(baseName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackageManager", "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, safeName + ".log");
        }

        private string SanitizeFileName(string input)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
            {
                sb.Append(invalid.Contains(ch) ? '_' : ch);
            }
            return sb.ToString();
        }

        private void EnsureLogFileExists(string logPath)
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
        
        private void StartRealtimeLogTail(string logPath, CancellationToken token, PackageInfo Package)
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
                                        Package.StatusText = captured;
                                        ParseToolOutputLine(captured,Package);
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
        private void ParseToolOutputLine(string line, PackageInfo Package)
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

                    var oldStatus = Package.Status;
                    var mappedStatus = MapStatusForStage(stage);
                    if (mappedStatus != oldStatus)
                    {
                        Package.Status = mappedStatus;
                        PackageManager.Services.LoggingService.LogInfo($"状态切换：{oldStatus} -> {mappedStatus}（阶段={stage} 标记={statusWord}）");
                    }

                    // 若阶段标记为结束/完成，则将该阶段设置为100%
                    if (IsStageCompleteStatus(statusWord))
                    {
                        stageProgress[stage] = 100;
                        PackageManager.Services.LoggingService.LogInfo($"阶段完成：{stage} -> 100%");
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
                    PackageManager.Services.LoggingService.LogInfo($"阶段进度：{stage} -> {pct}%");

                    // 状态映射
                    var oldStatus = Package.Status;
                    var mappedStatus = MapStatusForStage(stage);
                    if (mappedStatus != oldStatus)
                    {
                        Package.Status = mappedStatus;
                        PackageManager.Services.LoggingService.LogInfo($"状态切换：{oldStatus} -> {mappedStatus}（阶段={stage}）");
                    }

                    UpdateOverallAggregatedProgress();
                }
            }
            catch (Exception ex)
            {
                PackageManager.Services.LoggingService.LogError(ex, "解析工具输出行失败：" + line);
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
            Package.Progress = Math.Max(Package.Progress, overall);

            // 若所有阶段均完成则标记为完成
            if (stageOrder.Count > 0 && stageOrder.All(st => stageProgress.TryGetValue(st, out var v) && v >= 100))
            {
                Package.Status = PackageStatus.Completed;
                Package.Progress = Math.Max(Package.Progress, 100);
            }
        }

        private void InitializeDefaultStages()
        {
            // 预置常见阶段，避免仅有“下载”阶段时把总进度跑满
            var defaults = new[] { "下载", "解压", "签名", "加密" };
            foreach (var st in defaults)
            {
                if (!stageOrder.Contains(st))
                {
                    stageOrder.Add(st);
                }
                if (!stageProgress.ContainsKey(st))
                {
                    stageProgress[st] = 0;
                }
            }
        }

        private PackageStatus MapStatusForStage(string stage)
        {
            if (string.IsNullOrWhiteSpace(stage)) return Package.Status;

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
            return Package.Status;
        }

        private bool IsStageCompleteStatus(string statusWord)
        {
            if (string.IsNullOrWhiteSpace(statusWord)) return false;
            var s = statusWord.Trim().ToLowerInvariant();
            return s == "end" || s == "done" || s == "finish" || s == "finished" || s == "success" || s == "completed" || s == "完成" || s == "结束" || s == "完毕";
        }

        /// <summary>
        /// 从嵌入资源提取exe到本地工具目录
        /// </summary>
        private string EnsureEmbeddedToolExtracted(string resourceSuffix, string outputFileName)
        {
            try
            {
                var asm = typeof(PackageInfo).Assembly;
                var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(name))
                {
                    LoggingService.LogWarning("嵌入资源未找到：" + resourceSuffix);
                    return null;
                }

                var targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackageManager", "tools");
                Directory.CreateDirectory(targetDir);
                var targetPath = Path.Combine(targetDir, outputFileName);

                if (File.Exists(targetPath))
                {
                    return targetPath;
                }

                using (var stream = asm.GetManifestResourceStream(name))
                {
                    if (File.Exists(targetPath))
                    {
                        return targetPath;
                    }
                    using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        stream.CopyTo(fs);
                    }
                }

                LoggingService.LogInfo("已提取嵌入工具到：" + targetPath);
                return targetPath;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "提取嵌入工具失败");
                return null;
            }
        }
    }
}
