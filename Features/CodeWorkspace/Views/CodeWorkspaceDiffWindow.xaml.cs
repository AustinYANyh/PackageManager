using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using PackageManager.Features.CodeWorkspace.Models;
using PackageManager.Features.CodeWorkspace.Services;
using PackageManager.Services;

namespace PackageManager.Features.CodeWorkspace.Views
{
    public partial class CodeWorkspaceDiffWindow : Window, INotifyPropertyChanged
    {
        private readonly CodeRepository _repository;
        private readonly VcsDiffService _diffService = new VcsDiffService();
        private VcsChangedFile _selectedFile;
        private string _fileFilter;
        private IReadOnlyList<DiffLineRow> _fullRows = new List<DiffLineRow>();
        private IReadOnlyList<DiffLineRow> _diffOnlyRows = new List<DiffLineRow>();
        private string _messageText;
        private string _timingText;
        private DiffTiming _currentTiming;
        private IReadOnlyList<DiffLineRow> _diffRows = new List<DiffLineRow>();
        private bool _ignoreUnchanged = true;
        private bool _isMessageVisible = true;
        private bool _isDiffViewerVisible;
        private double _horizontalScrollOffset;
        private double _horizontalScrollMaximum;
        private double _horizontalScrollViewport;
        private double _horizontalTextExtent;
        private Visibility _horizontalScrollVisibility = Visibility.Collapsed;
        private int _firstChangedRowIndex = -1;

        public CodeWorkspaceDiffWindow(CodeRepository repository, IEnumerable<VcsChangedFile> files, string scopeTitle)
        {
            InitializeComponent();
            _repository = repository;
            ScopeTitle = scopeTitle ?? "全部变更";
            foreach (var file in (files ?? Enumerable.Empty<VcsChangedFile>())
                         .Where(file => file != null)
                         .OrderBy(file => file.GroupName)
                         .ThenBy(file => file.DisplayPath))
            {
                ChangedFiles.Add(file);
            }

            ChangedFileView = CollectionViewSource.GetDefaultView(ChangedFiles);
            ChangedFileView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VcsChangedFile.GroupName)));
            ChangedFileView.Filter = FilterChangedFile;
            DataContext = this;
            MessageText = ChangedFiles.Count == 0 ? "当前范围没有可查看的变更文件。" : "请选择左侧文件查看差异。";
            // 窗口就绪后后台并行预取全部变更文件的旧文本（限流 4）：用户点击任一文件时
            // git show/svn cat 结果已在窗口级缓存就绪，读取段接近 0ms
            Loaded += (s, e) => { _ = _diffService.PrefetchOldTextsAsync(ChangedFiles); };
            SelectedFile = ChangedFiles.FirstOrDefault();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<VcsChangedFile> ChangedFiles { get; } = new ObservableCollection<VcsChangedFile>();

        public IReadOnlyList<DiffLineRow> DiffRows
        {
            get => _diffRows;
            private set => SetProperty(ref _diffRows, value ?? new List<DiffLineRow>());
        }

        public ICollectionView ChangedFileView { get; }

        public string ScopeTitle { get; }

        public string TitleText => $"{_repository?.Name ?? "代码仓库"} - {ScopeTitle}";

        public string SummaryText => $"{_repository?.VcsDetailTitle ?? "版本控制"}  |  {(_repository?.LastStatusRefresh == DateTime.MinValue ? "尚未刷新" : $"上次刷新: {_repository?.LastStatusRefresh:HH:mm:ss}")}";

        public string FileCountText => $"{ChangedFiles.Count} 个文件";

        public string FileFilter
        {
            get => _fileFilter;
            set
            {
                if (SetProperty(ref _fileFilter, value))
                {
                    ChangedFileView.Refresh();
                }
            }
        }

        public VcsChangedFile SelectedFile
        {
            get => _selectedFile;
            set
            {
                if (SetProperty(ref _selectedFile, value))
                {
                    _ = LoadSelectedDiffAsync(value);
                }
            }
        }

        public string MessageText
        {
            get => _messageText;
            set => SetProperty(ref _messageText, value);
        }

        public string TimingText
        {
            get => _timingText;
            set => SetProperty(ref _timingText, value);
        }

        public Visibility MessageVisibility => _isMessageVisible ? Visibility.Visible : Visibility.Collapsed;

        public Visibility DiffViewerVisibility => _isDiffViewerVisible ? Visibility.Visible : Visibility.Collapsed;

        public bool IgnoreUnchanged
        {
            get => _ignoreUnchanged;
            set => SetProperty(ref _ignoreUnchanged, value);
        }

        public double HorizontalScrollOffset
        {
            get => _horizontalScrollOffset;
            set
            {
                var offset = Math.Round(Math.Max(0, Math.Min(value, HorizontalScrollMaximum)));
                if (SetProperty(ref _horizontalScrollOffset, offset))
                {
                    RaisePropertyChanged(nameof(NegativeHorizontalScrollOffset));
                }
            }
        }

        public double NegativeHorizontalScrollOffset => -HorizontalScrollOffset;

        public double HorizontalScrollMaximum
        {
            get => _horizontalScrollMaximum;
            private set
            {
                if (SetProperty(ref _horizontalScrollMaximum, Math.Max(0, value)) &&
                    HorizontalScrollOffset > _horizontalScrollMaximum)
                {
                    HorizontalScrollOffset = _horizontalScrollMaximum;
                }
            }
        }

        public double HorizontalScrollViewport
        {
            get => _horizontalScrollViewport;
            private set => SetProperty(ref _horizontalScrollViewport, Math.Max(0, value));
        }

        public double HorizontalTextExtent
        {
            get => _horizontalTextExtent;
            private set => SetProperty(ref _horizontalTextExtent, Math.Max(0, value));
        }

        public Visibility HorizontalScrollVisibility
        {
            get => _horizontalScrollVisibility;
            private set => SetProperty(ref _horizontalScrollVisibility, value);
        }

        private bool FilterChangedFile(object value)
        {
            if (string.IsNullOrWhiteSpace(FileFilter))
            {
                return true;
            }

            if (value is VcsChangedFile file)
            {
                return (file.DisplayPath ?? string.Empty).IndexOf(FileFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       (file.GroupName ?? string.Empty).IndexOf(FileFilter, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return false;
        }

        private async Task LoadSelectedDiffAsync(VcsChangedFile file)
        {
            if (file == null)
            {
                ClearDiffText();
                SetMessage("请选择左侧文件查看差异。");
                return;
            }

            ClearDiffText();
            TimingText = string.Empty;
            SetDiffViewerVisible(false);
            SetMessage("正在加载差异...");
            var result = await Task.Run(() => _diffService.LoadDiffContentAsync(file));
            if (SelectedFile != file)
            {
                return;
            }

            if (result.Success)
            {
                _fullRows = result.FullRows ?? new List<DiffLineRow>();
                _diffOnlyRows = result.DiffOnlyRows ?? new List<DiffLineRow>();
                _firstChangedRowIndex = result.FirstChangedRowIndex;
                _currentTiming = result.Timing ?? new DiffTiming();
                var probe = Stopwatch.StartNew();
                ApplyCurrentViewRows();
                probe.Stop();
                var applyMs = probe.ElapsedMilliseconds;
                probe.Restart();
                SetDiffViewerVisible(true);
                // 遮罩先撤、内容先见：渲染计时改为等待真实渲染帧（MeasureRenderAsync），
                // 不再把"隐藏加载提示"押在一个会被常驻动画持续插队的空闲优先级上
                SetMessage(null);
                await ScrollToFirstChangeAsync(_firstChangedRowIndex);
                probe.Stop();
                var scrollMs = probe.ElapsedMilliseconds;
                var loadedWait = ScrollToFirstChangeLoadedWaitMs;
                probe.Restart();
                await MeasureRenderAsync();
                probe.Stop();
                LoggingService.LogDebug(
                    $"[Diff 渲染诊断] 行数={DiffRows.Count}(全量 {_fullRows.Count}) 应用={applyMs}ms 滚动={scrollMs}ms(其中Loaded等待={loadedWait}ms) 渲染帧等待={probe.ElapsedMilliseconds}ms 文件={file.DisplayPath}");
                UpdateTimingText();
            }
            else
            {
                SetDiffViewerVisible(false);
                TimingText = string.Empty;
                SetMessage(result.ErrorMessage);
            }
        }

        private async void OpenExternalButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _diffService.OpenExternalAsync(SelectedFile);
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "打开外部差异工具失败");
                MessageBox.Show(
                    $"打开外部差异工具失败: {ex.Message}{Environment.NewLine}已尝试常见目录、PATH 和文件索引。",
                    "变更差异",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void FullDiffModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            IgnoreUnchanged = false;
            ApplyCurrentViewRows();
            await ScrollToFirstChangeAsync(_firstChangedRowIndex);
        }

        private void ChangedOnlyDiffModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            IgnoreUnchanged = true;
            ApplyCurrentViewRows();
        }

        private void CopyPathButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedFile == null)
            {
                return;
            }

            Clipboard.SetText(SelectedFile.AbsolutePath ?? SelectedFile.DisplayPath ?? string.Empty);
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var path = SelectedFile?.AbsolutePath;
            var directory = File.Exists(path) ? Path.GetDirectoryName(path) : SelectedFile?.WorkingDirectory;
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }

        private void SetMessage(string message)
        {
            MessageText = message;
            _isMessageVisible = !string.IsNullOrWhiteSpace(message);
            RaisePropertyChanged(nameof(MessageVisibility));
        }

        private void SetDiffViewerVisible(bool visible)
        {
            _isDiffViewerVisible = visible;
            RaisePropertyChanged(nameof(DiffViewerVisibility));
        }

        private void ApplyCurrentViewRows()
        {
            if (!IsLoaded && (_fullRows == null || _fullRows.Count == 0))
            {
                return;
            }

            var source = IgnoreUnchanged ? _diffOnlyRows : _fullRows;
            DiffRows = source ?? new List<DiffLineRow>();
            HorizontalScrollOffset = 0;
            UpdateHorizontalScrollMetrics();
        }

        private void ClearDiffText()
        {
            _fullRows = new List<DiffLineRow>();
            _diffOnlyRows = new List<DiffLineRow>();
            _currentTiming = null;
            _firstChangedRowIndex = -1;
            DiffRows = new List<DiffLineRow>();
            HorizontalScrollOffset = 0;
            UpdateHorizontalScrollMetrics();
        }

        /// <summary>最近一次滚动定位中等待 Dispatcher Loaded 优先级的时长（渲染诊断用）。</summary>
        private int ScrollToFirstChangeLoadedWaitMs;

        private async Task ScrollToFirstChangeAsync(int fullRowIndex)
        {
            if (fullRowIndex < 0 || DiffRows.Count == 0)
            {
                return;
            }

            // 原实现先等 Dispatcher Loaded 优先级再滚动——同属"会被常驻动画插队"的等待
            //（实测 55~355ms 纯排队）；ScrollIntoView/ScrollToVerticalOffset 在 Item 滚动模式下
            // 自带定位能力，直接执行即可
            ScrollToFirstChangeLoadedWaitMs = 0;
            var target = IgnoreUnchanged
                ? DiffRows.FirstOrDefault(row => row.IsChanged && !row.IsSeparator)
                : fullRowIndex < DiffRows.Count ? DiffRows[fullRowIndex] : null;
            if (target == null)
            {
                return;
            }

            if (IgnoreUnchanged)
            {
                ScrollDiffOnlyFirstChangeIntoView(target);
                return;
            }

            CenterFullFileFirstChange(fullRowIndex, target);
        }

        private void ScrollDiffOnlyFirstChangeIntoView(DiffLineRow target)
        {
            DiffRowsList.ScrollIntoView(target);
        }

        private void CenterFullFileFirstChange(int fullRowIndex, DiffLineRow fallbackTarget)
        {
            DiffRowsList.UpdateLayout();
            var scrollViewer = FindVisualChild<ScrollViewer>(DiffRowsList);
            if (scrollViewer == null)
            {
                DiffRowsList.ScrollIntoView(fallbackTarget);
                return;
            }

            var visibleRows = scrollViewer.ViewportHeight;
            if (double.IsNaN(visibleRows) || double.IsInfinity(visibleRows) || visibleRows <= 0)
            {
                visibleRows = Math.Max(1, DiffRowsList.ActualHeight / 22);
            }

            var centeredOffset = Math.Max(0, fullRowIndex - Math.Floor(visibleRows / 2));
            scrollViewer.ScrollToVerticalOffset(centeredOffset);
        }

        private async Task MeasureRenderAsync()
        {
            if (_currentTiming == null)
            {
                return;
            }

            // 等待 2 个真实渲染帧作为"渲染完成"信号（500ms 超时兜底）。
            // 原实现等 ContextIdle（空闲优先级）——程序存在常驻动画/高频消息流时该优先级被
            // 持续插队（实测可拖到数百 ms~秒级），计时严重虚高且曾连带延迟加载遮罩的隐藏。
            // timing 取局部快照：await 让出期间用户切文件会触发 ClearDiffText 置空 _currentTiming
            var timing = _currentTiming;
            if (timing == null)
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var framesSeen = 0;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler onFrame = (s, e) =>
            {
                if (Interlocked.Increment(ref framesSeen) >= 2)
                {
                    tcs.TrySetResult(true);
                }
            };
            CompositionTarget.Rendering += onFrame;
            try
            {
                await Task.WhenAny(tcs.Task, Task.Delay(500));
            }
            finally
            {
                CompositionTarget.Rendering -= onFrame;
            }

            stopwatch.Stop();
            timing.RenderBindMs = stopwatch.ElapsedMilliseconds;
        }

        // ========================= 渲染性能诊断插桩（已破案，默认停用）=========================
        // 破案结论（2026-09-01）：diff 渲染本身健康（真实成本几十~300ms），秒级卡顿的元凶是
        //   FtpService.GetDirectoriesAsync/GetFilesAsync 的同步段在 UI 线程执行
        //   ResolveDefaultCredentials → LoadSettings → File.ReadAllText 读盘（版本监控周期任务），
        //   Dispatcher 队列被占导致 ContextIdle 等待虚高。已修：LoadSettings mtime 缓存 + 凭证解析入 Task.Run。
        // 若未来再出现"渲染段"莫名变慢：把本段整体解注释并在 MeasureRenderAsync 中改为调用本方法即可。
        // 诊断能力：①分级等待（定位堵在哪一级优先级）②GC 计数（证伪/证实 GC 假设）
        // ③UI 线程调用栈采样（Thread.Suspend + StackTrace，具名元凶——本次破案的终局手段）
        // ========================= 以下为诊断方法本体，停用 =========================
        /*
        private async Task MeasureRenderDiagnosticsAsync()
        {
            if (_currentTiming == null)
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var t0 = stopwatch.ElapsedMilliseconds;
            var uiThread = Dispatcher.Thread;
            var samples = new System.Collections.Concurrent.ConcurrentBag<string>();
            var gc0Before = GC.CollectionCount(0);
            var gc1Before = GC.CollectionCount(1);
            var gc2Before = GC.CollectionCount(2);
            var memBefore = GC.GetTotalMemory(false);
            var samplerCts = new System.Threading.CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!samplerCts.Token.IsCancellationRequested)
                {
                    try
                    {
#pragma warning disable CS0618 // Thread.Suspend 仅为诊断采样使用
                        uiThread.Suspend();
                        try
                        {
                            var trace = new StackTrace(uiThread, false);
                            var frames = trace.GetFrames();
                            if (frames != null)
                            {
                                samples.Add(string.Join(" <- ", frames.Take(14).Select(f => (f?.GetMethod()?.DeclaringType?.Name ?? "?") + "." + (f?.GetMethod()?.Name ?? "?"))));
                            }
                        }
                        finally
                        {
                            uiThread.Resume();
                        }
#pragma warning restore CS0618
                    }
                    catch (Exception ex)
                    {
                        samples.Add("ERR " + ex.GetType().Name + ": " + ex.Message);
                    }

                    try
                    {
                        await Task.Delay(60, samplerCts.Token);
                    }
                    catch
                    {
                        break;
                    }
                }
            });
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            samplerCts.Cancel();
            var tBackground = stopwatch.ElapsedMilliseconds - t0;
            if (tBackground > 300)
            {
                var gcInfo = $"GC0={GC.CollectionCount(0) - gc0Before} GC1={GC.CollectionCount(1) - gc1Before} GC2={GC.CollectionCount(2) - gc2Before} 内存增量={(GC.GetTotalMemory(false) - memBefore) / 1024}KB";
                var sampleText = samples.IsEmpty ? "(无样本)" : string.Join(" ||| ", samples.Take(5));
                LoggingService.LogDebug($"[Diff 渲染诊断3] Background等待={tBackground}ms {gcInfo} 样本={samples.Count} 栈样本: {sampleText}");
            }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            var tInput = stopwatch.ElapsedMilliseconds - t0;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            var tLoaded = stopwatch.ElapsedMilliseconds - t0;
            var frames = 0;
            EventHandler onFrame = (s, e) => frames++;
            CompositionTarget.Rendering += onFrame;
            try
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                var tRender = stopwatch.ElapsedMilliseconds - t0;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                var tDataBind = stopwatch.ElapsedMilliseconds - t0;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                stopwatch.Stop();
                _currentTiming.RenderBindMs = stopwatch.ElapsedMilliseconds;
                LoggingService.LogDebug(
                    $"[Diff 渲染诊断2] Background={tBackground}ms Input增量={tInput - tBackground}ms Loaded增量={tLoaded - tInput}ms Render增量={tRender - tLoaded}ms DataBind增量={tDataBind - tRender}ms ContextIdle增量={_currentTiming.RenderBindMs - tDataBind}ms 渲染帧数={frames}");
            }
            finally
            {
                CompositionTarget.Rendering -= onFrame;
            }
        }
        */
        // ========================= 诊断插桩结束 =========================
        private void UpdateTimingText()
        {
            if (_currentTiming == null)
            {
                TimingText = string.Empty;
                return;
            }

            var text = $"读取 {_currentTiming.ReadTotalMs}ms | 计算 {_currentTiming.DiffBuildMs}ms | 渲染 {_currentTiming.RenderBindMs}ms";
            if (_currentTiming.IsSlow)
            {
                text += " | 内嵌预览较慢，建议使用外部工具";
            }

            TimingText = text;
        }

        private void DiffRowsList_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateHorizontalScrollMetrics();
        }

        /// <summary>测宽去抖标志：加载序列（清空→应用→尺寸变化）会在同一轮触发多次全量测宽，合并为一次。</summary>
        private bool _metricsUpdateQueued;

        private void UpdateHorizontalScrollMetrics()
        {
            if (_metricsUpdateQueued)
            {
                return;
            }

            _metricsUpdateQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _metricsUpdateQueued = false;
                UpdateHorizontalScrollMetricsCore();
            }), DispatcherPriority.Background);
        }

        private void UpdateHorizontalScrollMetricsCore()
        {
            var sideTextViewport = Math.Max(0, (DiffRowsList.ActualWidth - 4 - 18) / 2 - 54 - 24 - 12);
            HorizontalScrollViewport = sideTextViewport;

            var maxTextWidth = EstimateMaxTextWidth(DiffRows);
            HorizontalTextExtent = Math.Max(sideTextViewport, maxTextWidth);
            HorizontalScrollMaximum = Math.Max(0, maxTextWidth - sideTextViewport);
            HorizontalScrollVisibility = HorizontalScrollMaximum > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static double EstimateMaxTextWidth(IEnumerable<DiffLineRow> rows)
        {
            if (rows == null)
            {
                return 0;
            }

            var maxCharacters = 0;
            foreach (var row in rows)
            {
                if (row == null || row.IsSeparator)
                {
                    continue;
                }

                maxCharacters = Math.Max(maxCharacters, GetDisplayLength(row.OldTextRuns));
                maxCharacters = Math.Max(maxCharacters, GetDisplayLength(row.NewTextRuns));
            }

            return MeasureTextWidth(rows, maxCharacters);
        }

        private static int GetDisplayLength(IEnumerable<DiffTextRun> runs)
        {
            return runs?.Sum(run => run?.Text?.Length ?? 0) ?? 0;
        }

        private static double MeasureTextWidth(IEnumerable<DiffLineRow> rows, int fallbackMaxCharacters)
        {
            var typeface = new Typeface(
                new FontFamily("Consolas"),
                FontStyles.Normal,
                FontWeights.Normal,
                FontStretches.Normal);
            var maxWidth = 0d;

            foreach (var row in rows)
            {
                if (row == null || row.IsSeparator)
                {
                    continue;
                }

                maxWidth = Math.Max(maxWidth, MeasureRunWidth(row.OldTextRuns, typeface));
                maxWidth = Math.Max(maxWidth, MeasureRunWidth(row.NewTextRuns, typeface));
            }

            var fallbackWidth = fallbackMaxCharacters * 8.2;
            return Math.Max(maxWidth, fallbackWidth) + 24;
        }

        // 等宽字体宽度解析计算：Consolas 的 ASCII 全同宽、CJK/全角统一按双宽处理，
        // 只需一次性测量两个基准字符。取代原"逐字符查缓存（未命中构建 FormattedText）"方案——
        // 大文件（数千行 × 双侧）全量测宽时旧方案在 UI 线程产生数百 ms 到秒级开销（渲染段耗时主体之一）
        private static readonly object MonospaceMeasureSync = new object();
        private static double _monospaceAsciiWidth = double.NaN;
        private static double _monospaceCjkWidth = double.NaN;

        private static void EnsureMonospaceMetrics(Typeface typeface)
        {
            if (!double.IsNaN(_monospaceAsciiWidth))
            {
                return;
            }

            lock (MonospaceMeasureSync)
            {
                if (!double.IsNaN(_monospaceAsciiWidth))
                {
                    return;
                }

                _monospaceAsciiWidth = MeasureSingleCharWidth('M', typeface);
                _monospaceCjkWidth = Math.Max(_monospaceAsciiWidth * 2, MeasureSingleCharWidth('中', typeface));
            }
        }

        private static double MeasureSingleCharWidth(char c, Typeface typeface)
        {
            var formattedText = new FormattedText(
                c.ToString(),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                13,
                Brushes.Black,
                1.0);
            return formattedText.WidthIncludingTrailingWhitespace;
        }

        private static double MeasureRunWidth(IEnumerable<DiffTextRun> runs, Typeface typeface)
        {
            EnsureMonospaceMetrics(typeface);
            double total = 0;
            foreach (var run in runs ?? Enumerable.Empty<DiffTextRun>())
            {
                var text = run?.Text;
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                foreach (var c in text)
                {
                    total += c > 127 ? _monospaceCjkWidth : _monospaceAsciiWidth;
                }
            }

            return total;
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                {
                    return match;
                }

                var descendant = FindVisualChild<T>(child);
                if (descendant != null)
                {
                    return descendant;
                }
            }

            return null;
        }

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            RaisePropertyChanged(propertyName);
            return true;
        }

        private void RaisePropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
