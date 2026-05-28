using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
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
        private string _oldText;
        private string _newText;
        private string _fullOldText;
        private string _fullNewText;
        private string _diffOnlyOldText;
        private string _diffOnlyNewText;
        private string _messageText;
        private string _timingText;
        private DiffTiming _currentTiming;
        private bool _ignoreUnchanged = true;
        private bool _isMessageVisible = true;
        private bool _isDiffViewerVisible;

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

        public string OldText
        {
            get => _oldText;
            set => SetProperty(ref _oldText, value);
        }

        public string NewText
        {
            get => _newText;
            set => SetProperty(ref _newText, value);
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
            IgnoreUnchanged = true;
            SetDiffViewerVisible(false);
            SetMessage("正在加载差异...");
            var result = await Task.Run(() => _diffService.LoadDiffContentAsync(file));
            if (SelectedFile != file)
            {
                return;
            }

            if (result.Success)
            {
                _fullOldText = result.OldText ?? string.Empty;
                _fullNewText = result.NewText ?? string.Empty;
                _diffOnlyOldText = result.DiffOnlyOldText ?? string.Empty;
                _diffOnlyNewText = result.DiffOnlyNewText ?? string.Empty;
                _currentTiming = result.Timing ?? new DiffTiming();
                ApplyCurrentViewText();
                SetDiffViewerVisible(true);
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

        private void FullDiffModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            IgnoreUnchanged = false;
            ApplyCurrentViewText();
        }

        private void ChangedOnlyDiffModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            IgnoreUnchanged = true;
            ApplyCurrentViewText();
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

        private void ApplyCurrentViewText()
        {
            if (!IsLoaded && string.IsNullOrEmpty(_fullOldText) && string.IsNullOrEmpty(_fullNewText))
            {
                return;
            }

            if (IgnoreUnchanged)
            {
                OldText = _diffOnlyOldText ?? string.Empty;
                NewText = _diffOnlyNewText ?? string.Empty;
            }
            else
            {
                OldText = _fullOldText ?? string.Empty;
                NewText = _fullNewText ?? string.Empty;
            }
        }

        private void ClearDiffText()
        {
            _fullOldText = string.Empty;
            _fullNewText = string.Empty;
            _diffOnlyOldText = string.Empty;
            _diffOnlyNewText = string.Empty;
            _currentTiming = null;
            OldText = string.Empty;
            NewText = string.Empty;
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
