using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
                ApplyCurrentViewRows();
                SetDiffViewerVisible(true);
                await ScrollToFirstChangeAsync(_firstChangedRowIndex);
                await MeasureRenderAsync();
                UpdateTimingText();
                SetMessage(null);
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

        private async Task ScrollToFirstChangeAsync(int fullRowIndex)
        {
            if (fullRowIndex < 0 || DiffRows.Count == 0)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
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

            var stopwatch = Stopwatch.StartNew();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            stopwatch.Stop();
            _currentTiming.RenderBindMs = stopwatch.ElapsedMilliseconds;
        }

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

        private void UpdateHorizontalScrollMetrics()
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

        private static double MeasureRunWidth(IEnumerable<DiffTextRun> runs, Typeface typeface)
        {
            var text = string.Concat((runs ?? Enumerable.Empty<DiffTextRun>()).Select(run => run?.Text ?? string.Empty));
            if (text.Length == 0)
            {
                return 0;
            }

            var formattedText = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                13,
                Brushes.Black,
                1.0);
            return formattedText.WidthIncludingTrailingWhitespace;
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
