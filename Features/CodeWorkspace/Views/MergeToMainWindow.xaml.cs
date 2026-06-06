using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using PackageManager.Features.CodeWorkspace.Models;
using PackageManager.Features.CodeWorkspace.Services;
using PackageManager.Services;

namespace PackageManager.Features.CodeWorkspace.Views
{
    public partial class MergeToMainWindow : Window, INotifyPropertyChanged
    {
        private static readonly Brush PendingBrush = CreateBrush(0x8A, 0x94, 0xA3);
        private static readonly Brush ActiveBrush = CreateBrush(0x34, 0x6D, 0xDB);
        private static readonly Brush SuccessBrush = CreateBrush(0x2E, 0xA0, 0x43);
        private static readonly Brush ErrorBrush = CreateBrush(0xD1, 0x24, 0x2F);

        private readonly CodeRepository _repository;
        private readonly GitMergeService _mergeService = new GitMergeService();
        private string _targetBranch = "master";
        private string _statusText = "等待预检。";
        private string _aiStatusText = "AI 建议会显示在这里，确认后可应用到当前冲突文件。";
        private string _aiSuggestionText;
        private MergeRepositoryItem _selectedMergeItem;
        private MergeRepositoryItem _activeConflictItem;
        private MergeConflictFile _selectedConflictFile;
        private int _stepIndex;
        private bool _hasConflict;
        private bool _mergeCompleted;
        private bool _isBusy;
        private int _resumeIndex;

        public MergeToMainWindow(CodeRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            InitializeComponent();
            DataContext = this;
            BuildMergeItems();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<MergeRepositoryItem> MergeItems { get; } = new ObservableCollection<MergeRepositoryItem>();

        public ObservableCollection<MergeConflictFile> ConflictFiles { get; } = new ObservableCollection<MergeConflictFile>();

        public string TitleText => $"合并回主干 - {_repository.Name}";

        public string HeaderDetail => $"{_repository.Path}  |  默认目标分支: {TargetBranch}";

        public string RepositorySummary => $"{MergeItems.Count} 个 Git 仓库，已选 {MergeItems.Count(i => i.IsSelected)} 个";

        public string ConflictSummary => ConflictFiles.Count == 0 ? "无冲突" : $"{ConflictFiles.Count} 个冲突";

        public bool CanEditTargetBranch => !_isBusy && !_mergeCompleted && !_hasConflict;

        public string TargetBranch
        {
            get => _targetBranch;
            set
            {
                if (SetProperty(ref _targetBranch, string.IsNullOrWhiteSpace(value) ? "master" : value))
                {
                    foreach (var item in MergeItems)
                    {
                        item.TargetBranch = _targetBranch;
                        item.CanMerge = false;
                        item.StatusText = "等待检查";
                        item.DetailText = null;
                    }

                    OnPropertyChanged(nameof(HeaderDetail));
                }
            }
        }

        public MergeRepositoryItem SelectedMergeItem
        {
            get => _selectedMergeItem;
            set => SetProperty(ref _selectedMergeItem, value);
        }

        public MergeConflictFile SelectedConflictFile
        {
            get => _selectedConflictFile;
            set => SetProperty(ref _selectedConflictFile, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public string AiStatusText
        {
            get => _aiStatusText;
            set => SetProperty(ref _aiStatusText, value);
        }

        public string AiSuggestionText
        {
            get => _aiSuggestionText;
            set => SetProperty(ref _aiSuggestionText, value);
        }

        public string StepCheckText => BuildStepText(0, "检查工作区");

        public string StepFetchText => BuildStepText(1, "fetch/pull");

        public string StepMergeText => BuildStepText(2, "merge");

        public string StepResolveText => BuildStepText(3, "解决冲突");

        public string StepPushText => BuildStepText(4, "等待推送");

        public Brush StepCheckBrush => BuildStepBrush(0);

        public Brush StepFetchBrush => BuildStepBrush(1);

        public Brush StepMergeBrush => BuildStepBrush(2);

        public Brush StepResolveBrush => BuildStepBrush(3);

        public Brush StepPushBrush => BuildStepBrush(4);

        private void BuildMergeItems()
        {
            if (Directory.Exists(Path.Combine(_repository.Path, ".git")) || File.Exists(Path.Combine(_repository.Path, ".git")))
            {
                MergeItems.Add(new MergeRepositoryItem
                {
                    DisplayName = _repository.Name,
                    RepositoryPath = _repository.Path,
                    RelativePath = string.Empty,
                    SourceBranch = _repository.GitBranch,
                    TargetBranch = TargetBranch,
                    IsRoot = true,
                });
            }

            foreach (var subRepository in (_repository.SubRepositories ?? new ObservableCollection<SubRepository>())
                         .Where(s => s.VcsType == VcsType.Git)
                         .OrderBy(s => s.RelativePath))
            {
                MergeItems.Add(new MergeRepositoryItem
                {
                    DisplayName = System.IO.Path.GetFileName(subRepository.RelativePath?.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)) ?? subRepository.RelativePath,
                    RepositoryPath = System.IO.Path.Combine(_repository.Path, subRepository.RelativePath ?? string.Empty),
                    RelativePath = subRepository.RelativePath,
                    SourceBranch = subRepository.Branch,
                    TargetBranch = TargetBranch,
                    IsRoot = false,
                });
            }

            SelectedMergeItem = MergeItems.FirstOrDefault();
            if (MergeItems.Count == 0)
            {
                StatusText = "未检测到可合并的 Git 仓库。";
            }
        }

        private async void PrecheckButton_Click(object sender, RoutedEventArgs e)
        {
            await RunPrecheckAsync();
        }

        private async void MergeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await RunPrecheckAsync())
            {
                return;
            }

            await RunMergeAsync();
        }

        private async void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeConflictItem == null)
            {
                StatusText = "当前没有待继续的冲突合并。";
                return;
            }

            await RunBusyAsync(async () =>
            {
                StatusText = $"正在继续合并: {_activeConflictItem.DisplayName}";
                var result = await _mergeService.ContinueMergeAsync(_activeConflictItem);
                if (result.HasConflict)
                {
                    SetConflictState(_activeConflictItem, result);
                    StatusText = result.Message;
                    return;
                }

                if (!result.Success)
                {
                    _activeConflictItem.HasError = true;
                    _activeConflictItem.StatusText = "继续失败";
                    _activeConflictItem.DetailText = result.Message;
                    StatusText = result.Message;
                    return;
                }

                _activeConflictItem.HasConflict = false;
                _activeConflictItem.IsCompleted = true;
                _activeConflictItem.StatusText = "合并完成";
                _activeConflictItem.DetailText = result.Message;
                _activeConflictItem = null;
                _hasConflict = false;
                ConflictFiles.Clear();
                AiSuggestionText = string.Empty;
                AiStatusText = "冲突已解决。";
                SetStep(4);
                await ContinueRemainingAfterConflictAsync();
            });
        }

        private async void AbortButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeConflictItem == null)
            {
                StatusText = "当前没有可终止的合并。";
                return;
            }

            await RunBusyAsync(async () =>
            {
                var result = await _mergeService.AbortMergeAsync(_activeConflictItem);
                _activeConflictItem.StatusText = result.Success ? "已终止" : "终止失败";
                _activeConflictItem.DetailText = result.Message;
                _activeConflictItem.HasConflict = false;
                _activeConflictItem.HasError = !result.Success;
                _activeConflictItem = null;
                _hasConflict = false;
                ConflictFiles.Clear();
                StatusText = result.Message;
                SetStep(0);
            });
        }

        private async void PushButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_mergeCompleted)
            {
                StatusText = "合并尚未全部完成，不能推送。";
                return;
            }

            var confirm = MessageBox.Show("确认推送所有已合并仓库的 master 到 origin？", "推送 master", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            await RunBusyAsync(async () =>
            {
                foreach (var item in MergeItems.Where(i => i.IsSelected && i.IsCompleted && !i.IsPushed))
                {
                    item.IsProcessing = true;
                    item.StatusText = "正在推送";
                    var result = await _mergeService.PushTargetAsync(item);
                    item.IsProcessing = false;
                    item.IsPushed = result.Success;
                    item.HasError = !result.Success;
                    item.StatusText = result.Success ? "已推送" : "推送失败";
                    item.DetailText = result.Message;
                    if (!result.Success)
                    {
                        StatusText = $"{item.DisplayName}: {result.Message}";
                        return;
                    }
                }

                StatusText = "所有已合并仓库已推送。";
            });
        }

        private async void AnalyzeConflictButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeConflictItem == null || SelectedConflictFile == null)
            {
                AiStatusText = "请选择一个冲突文件。";
                return;
            }

            await RunBusyAsync(async () =>
            {
                var promptPath = await _mergeService.CreateAiConflictPromptAsync(_activeConflictItem, SelectedConflictFile);
                var promptArgument = AiCliLaunchService.BuildPromptFileInstruction(promptPath);
                AiStatusText = $"已生成 AI 冲突分析提示: {promptPath}";
                AiSuggestionText = $"提示文件已生成：{promptPath}{Environment.NewLine}AI 输出建议后，将完整合并结果粘贴到这里，再点击“应用建议到文件”。";
                TerminalHelper.LaunchTerminalWithCommand(
                    _activeConflictItem.RepositoryPath,
                    $"Set-Location -LiteralPath {PsQuote(_activeConflictItem.RepositoryPath)}\ncodex --sandbox danger-full-access --ask-for-approval never {PsQuote(promptArgument)}",
                    $"AI 冲突分析 - {SelectedConflictFile.RelativePath}");
            });
        }

        private void ApplySuggestionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _mergeService.ApplyConflictSuggestion(SelectedConflictFile, AiSuggestionText);
                AiStatusText = "已应用建议到文件，请点击“继续合并”检查冲突状态。";
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "应用 AI 冲突建议失败");
                MessageBox.Show($"应用建议失败: {ex.Message}", "AI 冲突辅助", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenConflictFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedConflictFile == null || !File.Exists(SelectedConflictFile.FullPath))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = SelectedConflictFile.FullPath,
                UseShellExecute = true,
            });
        }

        private void OpenConflictFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedConflictFile == null)
            {
                return;
            }

            var directory = Path.GetDirectoryName(SelectedConflictFile.FullPath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = directory,
                    UseShellExecute = true,
                });
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async Task<bool> RunPrecheckAsync()
        {
            var selectedItems = MergeItems.Where(i => i.IsSelected).ToList();
            if (selectedItems.Count == 0)
            {
                StatusText = "请至少选择一个 Git 仓库。";
                return false;
            }

            var allPassed = true;
            await RunBusyAsync(async () =>
            {
                SetStep(0);
                foreach (var item in selectedItems)
                {
                    item.TargetBranch = TargetBranch;
                    item.IsProcessing = true;
                    item.HasError = false;
                    item.HasConflict = false;
                    item.StatusText = "检查中";
                    item.DetailText = null;
                    var result = await _mergeService.PrecheckAsync(item);
                    item.IsProcessing = false;
                    item.CanMerge = result.Success;
                    item.HasError = !result.Success;
                    item.StatusText = result.Success ? "检查通过" : "检查失败";
                    item.DetailText = result.Success ? result.RemoteStatus : result.Message;
                    if (!result.Success)
                    {
                        allPassed = false;
                    }
                }

                StatusText = allPassed ? "预检通过，可以开始合并。" : "预检失败，请处理标红项。";
            });

            return allPassed;
        }

        private async Task RunMergeAsync()
        {
            await RunBusyAsync(async () =>
            {
                _mergeCompleted = false;
                SetStep(1);
                var selectedItems = MergeItems.Where(i => i.IsSelected && i.CanMerge).ToList();
                for (var index = 0; index < selectedItems.Count; index++)
                {
                    var item = selectedItems[index];
                    var result = await MergeOneAsync(item);
                    if (result.HasConflict)
                    {
                        _resumeIndex = index + 1;
                        SetConflictState(item, result);
                        return;
                    }

                    if (!result.Success)
                    {
                        item.HasError = true;
                        item.StatusText = "合并失败";
                        item.DetailText = result.Message;
                        StatusText = $"{item.DisplayName}: {result.Message}";
                        return;
                    }
                }

                MarkMergeCompleted();
            });
        }

        private async Task ContinueRemainingAfterConflictAsync()
        {
            var selectedItems = MergeItems.Where(i => i.IsSelected && i.CanMerge).ToList();
            for (var index = _resumeIndex; index < selectedItems.Count; index++)
            {
                var item = selectedItems[index];
                if (item.IsCompleted)
                {
                    continue;
                }

                var result = await MergeOneAsync(item);
                if (result.HasConflict)
                {
                    _resumeIndex = index + 1;
                    SetConflictState(item, result);
                    return;
                }

                if (!result.Success)
                {
                    item.HasError = true;
                    item.StatusText = "合并失败";
                    item.DetailText = result.Message;
                    StatusText = $"{item.DisplayName}: {result.Message}";
                    return;
                }
            }

            MarkMergeCompleted();
        }

        private async Task<MergeExecutionResult> MergeOneAsync(MergeRepositoryItem item)
        {
            item.IsProcessing = true;
            item.StatusText = "合并中";
            item.DetailText = null;
            SetStep(2);
            var result = await _mergeService.MergeAsync(item);
            item.IsProcessing = false;
            if (result.Success)
            {
                item.IsCompleted = true;
                item.StatusText = "合并完成";
                item.DetailText = result.Message;
            }

            return result;
        }

        private void SetConflictState(MergeRepositoryItem item, MergeExecutionResult result)
        {
            _activeConflictItem = item;
            _hasConflict = true;
            item.HasConflict = true;
            item.StatusText = "存在冲突";
            item.DetailText = result.Message;
            ConflictFiles.Clear();
            foreach (var conflict in result.ConflictFiles)
            {
                ConflictFiles.Add(conflict);
            }

            SelectedConflictFile = ConflictFiles.FirstOrDefault();
            StatusText = $"{item.DisplayName}: {result.Message}";
            AiStatusText = "可选择冲突文件进行 AI 分析。";
            SetStep(3);
            OnPropertyChanged(nameof(ConflictSummary));
        }

        private void MarkMergeCompleted()
        {
            _mergeCompleted = true;
            SetStep(4);
            StatusText = "合并完成，确认后可推送 master。";
        }

        private async Task RunBusyAsync(Func<Task> action)
        {
            if (_isBusy)
            {
                StatusText = "当前操作尚未完成，请稍后。";
                return;
            }

            try
            {
                _isBusy = true;
                OnPropertyChanged(nameof(CanEditTargetBranch));
                await action();
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "合并回主干操作失败");
                StatusText = $"操作失败: {ex.Message}";
                MessageBox.Show($"操作失败: {ex.Message}", "合并回主干", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                OnPropertyChanged(nameof(CanEditTargetBranch));
                OnPropertyChanged(nameof(RepositorySummary));
                OnPropertyChanged(nameof(ConflictSummary));
            }
        }

        private void SetStep(int stepIndex)
        {
            _stepIndex = stepIndex;
            OnPropertyChanged(nameof(StepCheckText));
            OnPropertyChanged(nameof(StepFetchText));
            OnPropertyChanged(nameof(StepMergeText));
            OnPropertyChanged(nameof(StepResolveText));
            OnPropertyChanged(nameof(StepPushText));
            OnPropertyChanged(nameof(StepCheckBrush));
            OnPropertyChanged(nameof(StepFetchBrush));
            OnPropertyChanged(nameof(StepMergeBrush));
            OnPropertyChanged(nameof(StepResolveBrush));
            OnPropertyChanged(nameof(StepPushBrush));
        }

        private string BuildStepText(int index, string text)
        {
            if (_stepIndex > index)
            {
                return "✓ " + text;
            }

            return _stepIndex == index ? "● " + text : "○ " + text;
        }

        private Brush BuildStepBrush(int index)
        {
            if (_hasConflict && index == 3)
            {
                return ErrorBrush;
            }

            if (_stepIndex > index)
            {
                return SuccessBrush;
            }

            return _stepIndex == index ? ActiveBrush : PendingBrush;
        }

        private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static Brush CreateBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static string PsQuote(string value)
        {
            return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        }
    }
}
