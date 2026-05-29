using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using Newtonsoft.Json;

namespace PackageManager.Features.CodeWorkspace.Models
{
    /// <summary>
    /// 表示一个代码仓库配置。
    /// </summary>
    public class CodeRepository : INotifyPropertyChanged
    {
        private string _name;
        private string _path;
        private DateTime _lastUsed;
        private int _usageCount;
        private string _note = "";
        private List<string> _projectFiles = new List<string>();
        private VcsType _vcsType = VcsType.None;
        private VcsStatus _vcsStatus = VcsStatus.Unknown;
        private string _gitBranch;
        private int _gitAheadCount;
        private int _gitBehindCount;
        private int _addedCount;
        private int _modifiedCount;
        private int _deletedCount;
        private int _stagedCount;
        private int _svnRevision;
        private ObservableCollection<SubRepository> _subRepositories = new ObservableCollection<SubRepository>();
        private ObservableCollection<VcsChangedFile> _gitChangedFiles = new ObservableCollection<VcsChangedFile>();
        private ObservableCollection<VcsChangedFile> _rootSvnChangedFiles = new ObservableCollection<VcsChangedFile>();
        private DateTime _lastStatusRefresh;
        private bool _isRefreshing;
        private bool _hasConflict;

        private static readonly Brush CleanBrush = CreateBrush(0x2E, 0xA0, 0x43);
        private static readonly Brush ModifiedBrush = CreateBrush(0xD9, 0x7A, 0x00);
        private static readonly Brush ErrorBrush = CreateBrush(0xD1, 0x24, 0x2F);
        private static readonly Brush UnknownBrush = CreateBrush(0x8A, 0x94, 0xA3);
        private static readonly Brush GitBrush = CreateBrush(0x34, 0x6D, 0xDB);
        private static readonly Brush SvnBrush = CreateBrush(0x7A, 0x52, 0xC7);
        private static readonly Brush NeutralTextBrush = CreateBrush(0x46, 0x52, 0x66);
        private static readonly Brush CardBorderBrush = CreateBrush(0xE3, 0xE8, 0xF0);

        public event PropertyChangedEventHandler PropertyChanged;

        [DataGridColumn(1, DisplayName = "仓库", Width = "220", IsReadOnly = true)]
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        [DataGridColumn(5, DisplayName = "路径", Width = "*", IsReadOnly = true)]
        public string Path
        {
            get => _path;
            set => SetProperty(ref _path, value);
        }

        public DateTime LastUsed
        {
            get => _lastUsed;
            set
            {
                if (SetProperty(ref _lastUsed, value))
                {
                    OnPropertyChanged(nameof(LastUsedText));
                }
            }
        }

        public int UsageCount
        {
            get => _usageCount;
            set => SetProperty(ref _usageCount, value);
        }

        [DataGridColumn(2, DisplayName = "状态", Width = "56", IsReadOnly = true)]
        [JsonIgnore]
        public string VcsIndicator
        {
            get
            {
                var prefix = VcsType == VcsType.Git ? "G" :
                    VcsType == VcsType.Svn ? "S" :
                    VcsType == VcsType.Mixed ? "G+S" : "-";
                return $"{prefix}{GetStatusSymbol()}";
            }
            set { }
        }

        [DataGridColumn(3, DisplayName = "分支/版本", Width = "130", IsReadOnly = true)]
        [JsonIgnore]
        public string BranchDisplay
        {
            get
            {
                switch (VcsType)
                {
                    case VcsType.Git:
                        return string.IsNullOrWhiteSpace(GitBranch) ? "-" : GitBranch;
                    case VcsType.Svn:
                        return SvnRevision > 0 ? $"r{SvnRevision}" : "-";
                    case VcsType.Mixed:
                        var svnCount = SubRepositories?.Count(s => s.VcsType == VcsType.Svn) ?? 0;
                        return $"{(string.IsNullOrWhiteSpace(GitBranch) ? "-" : GitBranch)} | {svnCount} SVN";
                    default:
                        return "-";
                }
            }
            set { }
        }

        [DataGridColumn(4, DisplayName = "变更", Width = "80", IsReadOnly = true)]
        [JsonIgnore]
        public string ChangesSummary
        {
            get
            {
                if (VcsStatus == VcsStatus.Unknown)
                {
                    return VcsType == VcsType.None ? "-" : "未检测";
                }

                if (VcsStatus == VcsStatus.Error)
                {
                    return "检测失败";
                }

                if (VcsStatus == VcsStatus.Conflict)
                {
                    return "冲突";
                }

                if (VcsStatus == VcsStatus.Clean && !HasSubRepoChanges)
                {
                    return "干净";
                }

                if (VcsType == VcsType.Mixed)
                {
                    var gitChanges = AddedCount + ModifiedCount + DeletedCount;
                    var svnChanges = SubRepositories?.Sum(s => s.ChangedFileCount) ?? 0;
                    var parts = new List<string>();
                    if (gitChanges > 0)
                    {
                        parts.Add($"G:{gitChanges}");
                    }

                    if (svnChanges > 0)
                    {
                        parts.Add($"S:{svnChanges}");
                    }

                    return parts.Count == 0 ? "干净" : string.Join(" ", parts);
                }

                var total = AddedCount + ModifiedCount + DeletedCount;
                if (total == 0)
                {
                    return "干净";
                }

                var result = new List<string>();
                if (AddedCount > 0)
                {
                    result.Add($"+{AddedCount}");
                }

                if (ModifiedCount > 0)
                {
                    result.Add($"~{ModifiedCount}");
                }

                if (DeletedCount > 0)
                {
                    result.Add($"-{DeletedCount}");
                }

                return string.Join(" ", result);
            }
            set { }
        }

        [DataGridColumn(6, DisplayName = "备注", Width = "180", IsReadOnly = true,IsVisible = false)]
        public string Note
        {
            get => _note;
            set => SetProperty(ref _note, value);
        }

        public List<string> ProjectFiles
        {
            get => _projectFiles;
            set
            {
                if (SetProperty(ref _projectFiles, value ?? new List<string>()))
                {
                    OnPropertyChanged(nameof(ProjectFileCount));
                }
            }
        }

        [DataGridColumn(7, DisplayName = "项目文件", Width = "80", IsReadOnly = true,IsVisible = false)]
        public int ProjectFileCount
        {
            get => ProjectFiles?.Count ?? 0;
            set { }
        }

        [DataGridColumn(8, DisplayName = "最后使用", Width = "140", IsReadOnly = true,IsVisible = false)]
        public string LastUsedText
        {
            get => LastUsed == DateTime.MinValue ? "从未使用" : LastUsed.ToString("yyyy-MM-dd HH:mm");
            set { }
        }

        [DataGridMultiButton(nameof(ActionButtons), 9, DisplayName = "操作", Width = "94", ButtonSpacing = 8)]
        public string Actions { get; set; }

        [JsonIgnore]
        public VcsType VcsType
        {
            get => _vcsType;
            set
            {
                if (SetProperty(ref _vcsType, value))
                {
                    OnVcsSummaryChanged();
                }
            }
        }

        [JsonIgnore]
        public VcsStatus VcsStatus
        {
            get => _vcsStatus;
            set
            {
                if (SetProperty(ref _vcsStatus, value))
                {
                    OnVcsSummaryChanged();
                }
            }
        }

        [JsonIgnore]
        public string GitBranch
        {
            get => _gitBranch;
            set
            {
                if (SetProperty(ref _gitBranch, value))
                {
                    OnPropertyChanged(nameof(BranchDisplay));
                    OnPropertyChanged(nameof(VcsTooltip));
                }
            }
        }

        [JsonIgnore]
        public int GitAheadCount
        {
            get => _gitAheadCount;
            set
            {
                if (SetProperty(ref _gitAheadCount, value))
                {
                    OnPropertyChanged(nameof(VcsTooltip));
                    OnPropertyChanged(nameof(RootStatusDetail));
                }
            }
        }

        [JsonIgnore]
        public int GitBehindCount
        {
            get => _gitBehindCount;
            set
            {
                if (SetProperty(ref _gitBehindCount, value))
                {
                    OnPropertyChanged(nameof(VcsTooltip));
                    OnPropertyChanged(nameof(RootStatusDetail));
                }
            }
        }

        [JsonIgnore]
        public int AddedCount
        {
            get => _addedCount;
            set
            {
                if (SetProperty(ref _addedCount, value))
                {
                    OnChangesChanged();
                }
            }
        }

        [JsonIgnore]
        public int ModifiedCount
        {
            get => _modifiedCount;
            set
            {
                if (SetProperty(ref _modifiedCount, value))
                {
                    OnChangesChanged();
                }
            }
        }

        [JsonIgnore]
        public int DeletedCount
        {
            get => _deletedCount;
            set
            {
                if (SetProperty(ref _deletedCount, value))
                {
                    OnChangesChanged();
                }
            }
        }

        [JsonIgnore]
        public int StagedCount
        {
            get => _stagedCount;
            set
            {
                if (SetProperty(ref _stagedCount, value))
                {
                    OnPropertyChanged(nameof(VcsTooltip));
                    OnPropertyChanged(nameof(RootStatusDetail));
                }
            }
        }

        [JsonIgnore]
        public int SvnRevision
        {
            get => _svnRevision;
            set
            {
                if (SetProperty(ref _svnRevision, value))
                {
                    OnPropertyChanged(nameof(BranchDisplay));
                    OnPropertyChanged(nameof(VcsTooltip));
                }
            }
        }

        [JsonIgnore]
        public ObservableCollection<SubRepository> SubRepositories
        {
            get => _subRepositories;
            set
            {
                if (SetProperty(ref _subRepositories, value ?? new ObservableCollection<SubRepository>()))
                {
                    OnVcsSummaryChanged();
                }
            }
        }

        [JsonIgnore]
        public ObservableCollection<VcsChangedFile> GitChangedFiles
        {
            get => _gitChangedFiles;
            set
            {
                if (SetProperty(ref _gitChangedFiles, value ?? new ObservableCollection<VcsChangedFile>()))
                {
                    OnVcsSummaryChanged();
                }
            }
        }

        [JsonIgnore]
        public ObservableCollection<VcsChangedFile> RootSvnChangedFiles
        {
            get => _rootSvnChangedFiles;
            set
            {
                if (SetProperty(ref _rootSvnChangedFiles, value ?? new ObservableCollection<VcsChangedFile>()))
                {
                    OnVcsSummaryChanged();
                }
            }
        }

        [JsonIgnore]
        public bool IsRefreshing
        {
            get => _isRefreshing;
            set
            {
                if (SetProperty(ref _isRefreshing, value))
                {
                    OnPropertyChanged(nameof(ChangesSummary));
                    OnPropertyChanged(nameof(VcsTooltip));
                    OnPropertyChanged(nameof(RootStatusDetail));
                }
            }
        }

        [JsonIgnore]
        public DateTime LastStatusRefresh
        {
            get => _lastStatusRefresh;
            set
            {
                if (SetProperty(ref _lastStatusRefresh, value))
                {
                    OnPropertyChanged(nameof(VcsTooltip));
                    OnPropertyChanged(nameof(LastStatusRefreshText));
                }
            }
        }

        [JsonIgnore]
        public bool HasConflict
        {
            get => _hasConflict;
            set => SetProperty(ref _hasConflict, value);
        }

        [JsonIgnore]
        public string VcsTooltip
        {
            get
            {
                var lines = new List<string>
                {
                    $"类型: {VcsType}",
                    $"状态: {VcsStatus}",
                    $"变更: {ChangesSummary}",
                };

                if (!string.IsNullOrWhiteSpace(GitBranch))
                {
                    lines.Add($"Git: {GitBranch}, staged {StagedCount}, ahead {GitAheadCount}, behind {GitBehindCount}");
                }

                if (SvnRevision > 0)
                {
                    lines.Add($"SVN: r{SvnRevision}");
                }

                if (SubRepositories?.Count > 0)
                {
                    lines.Add($"SVN子仓库: {SubRepositories.Count}");
                    lines.AddRange(SubRepositories.Take(5).Select(s => $"{s.RelativePath} r{s.Revision} {s.StatusSummary}"));
                }

                if (LastStatusRefresh != DateTime.MinValue)
                {
                    lines.Add($"刷新: {LastStatusRefresh:HH:mm:ss}");
                }

                return string.Join(Environment.NewLine, lines);
            }
            set { }
        }

        [JsonIgnore]
        public Brush VcsStatusBrush
        {
            get => GetBrushForStatus(VcsStatus);
            set { }
        }

        [JsonIgnore]
        public Brush VcsTypeBrush
        {
            get
            {
                switch (VcsType)
                {
                    case VcsType.Git:
                        return GitBrush;
                    case VcsType.Svn:
                        return SvnBrush;
                    case VcsType.Mixed:
                        return GitBrush;
                    default:
                        return UnknownBrush;
                }
            }
            set { }
        }

        [JsonIgnore]
        public Brush SubRepositoryStatusBrush
        {
            get
            {
                if (SubRepositories == null || SubRepositories.Count == 0)
                {
                    return VcsStatusBrush;
                }

                if (SubRepositories.Any(s => s.Status == VcsStatus.Conflict || s.Status == VcsStatus.Error))
                {
                    return ErrorBrush;
                }

                return SubRepositories.Any(s => s.ChangedFileCount > 0) ? ModifiedBrush : CleanBrush;
            }
            set { }
        }

        [JsonIgnore]
        public Brush ChangesBrush
        {
            get
            {
                if (VcsStatus == VcsStatus.Conflict || VcsStatus == VcsStatus.Error || DeletedCount > 0)
                {
                    return ErrorBrush;
                }

                if (VcsStatus == VcsStatus.Modified || ModifiedCount > 0 || HasSubRepoChanges)
                {
                    return ModifiedBrush;
                }

                if (VcsStatus == VcsStatus.Clean)
                {
                    return CleanBrush;
                }

                return NeutralTextBrush;
            }
            set { }
        }

        [JsonIgnore]
        public string VcsTypeDisplay
        {
            get
            {
                switch (VcsType)
                {
                    case VcsType.Git:
                        return "G";
                    case VcsType.Svn:
                        return "S";
                    case VcsType.Mixed:
                        return "G+S";
                    default:
                        return "-";
                }
            }
            set { }
        }

        [JsonIgnore]
        public string VcsDetailTitle
        {
            get
            {
                switch (VcsType)
                {
                    case VcsType.Git:
                        return "Git 根目录";
                    case VcsType.Svn:
                        return "SVN 根目录";
                    case VcsType.Mixed:
                        return "Git 根目录 + SVN 子仓库";
                    default:
                        return "未检测到版本控制";
                }
            }
            set { }
        }

        [JsonIgnore]
        public string GitDetailLine
        {
            get
            {
                if (VcsType != VcsType.Git && VcsType != VcsType.Mixed)
                {
                    return "Git: 未检测到根仓库";
                }

                return $"Git: {(string.IsNullOrWhiteSpace(GitBranch) ? "-" : GitBranch)}  |  {BuildRootChangeSummary()}  |  staged {StagedCount}  |  ahead {GitAheadCount} / behind {GitBehindCount}";
            }
            set { }
        }

        [JsonIgnore]
        public string SvnDetailLine
        {
            get
            {
                if (VcsType == VcsType.Svn)
                {
                    return $"SVN: r{SvnRevision}  |  {BuildRootChangeSummary()}";
                }

                if (SubRepositories == null || SubRepositories.Count == 0)
                {
                    return "SVN 子仓库: 无";
                }

                var changed = SubRepositories.Sum(s => s.ChangedFileCount);
                var changedRepos = SubRepositories.Count(s => s.ChangedFileCount > 0);
                var clean = SubRepositories.Count(s => s.Status == VcsStatus.Clean);
                return $"SVN 子仓库: {SubRepositories.Count} 个  |  变更仓库 {changedRepos} 个  |  变更 {changed} 项  |  干净 {clean} 个";
            }
            set { }
        }

        [JsonIgnore]
        public string RootStatusDetail
        {
            get
            {
                if (VcsStatus == VcsStatus.Unknown)
                {
                    return "状态: 未检测";
                }

                var parts = new List<string>
                {
                    $"状态: {FormatStatus(VcsStatus)}",
                    $"变更: {ChangesSummary}",
                };

                if (!string.IsNullOrWhiteSpace(GitBranch))
                {
                    parts.Add($"分支: {GitBranch}");
                    parts.Add($"暂存: {StagedCount}");
                    parts.Add($"远程: 领先 {GitAheadCount}, 落后 {GitBehindCount}");
                }

                if (SvnRevision > 0)
                {
                    parts.Add($"版本: r{SvnRevision}");
                }

                return string.Join("  |  ", parts);
            }
            set { }
        }

        [JsonIgnore]
        public string SubRepositoryDetail
        {
            get
            {
                if (SubRepositories == null || SubRepositories.Count == 0)
                {
                    return "无 SVN 子仓库";
                }

                return $"SVN 子仓库: {SubRepositories.Count} 个";
            }
            set { }
        }

        [JsonIgnore]
        public string SubRepositoryPanelTitle
        {
            get
            {
                var count = SubRepositories?.Count ?? 0;
                return count == 0 ? "SVN 子仓库" : $"SVN 子仓库 {count} 个";
            }
            set { }
        }

        [JsonIgnore]
        public string LastStatusRefreshText
        {
            get => LastStatusRefresh == DateTime.MinValue ? "尚未刷新" : $"上次刷新: {LastStatusRefresh:HH:mm:ss}";
            set { }
        }

        public ICommand ClaudeCommitCommand { get; set; }

        public ICommand CodexCommitCommand { get; set; }

        public ICommand PullCommand { get; set; }

        public ICommand OpenVSCommand { get; set; }

        public ICommand OpenRiderCommand { get; set; }

        public ICommand OpenCursorCommand { get; set; }

        public ICommand OpenClaudeCommand { get; set; }

        public ICommand OpenCodexCommand { get; set; }

        public ICommand OpenFolderCommand { get; set; }

        public List<ButtonConfig> ActionButtons => new List<ButtonConfig>
        {
            new ButtonConfig { Text = "Claude提交", Width = 90, Height = 26, CommandProperty = nameof(ClaudeCommitCommand) },
            new ButtonConfig { Text = "Codex提交", Width = 86, Height = 26, CommandProperty = nameof(CodexCommitCommand) },
            new ButtonConfig { Text = "拉取", Width = 54, Height = 26, CommandProperty = nameof(PullCommand) },
            new ButtonConfig { Text = "VS", Width = 42, Height = 26, CommandProperty = nameof(OpenVSCommand) },
            new ButtonConfig { Text = "Rider", Width = 50, Height = 26, CommandProperty = nameof(OpenRiderCommand) },
            new ButtonConfig { Text = "Cursor", Width = 54, Height = 26, CommandProperty = nameof(OpenCursorCommand) },
            new ButtonConfig { Text = "Claude", Width = 58, Height = 26, CommandProperty = nameof(OpenClaudeCommand) },
            new ButtonConfig { Text = "Codex", Width = 54, Height = 26, CommandProperty = nameof(OpenCodexCommand) },
            new ButtonConfig { Text = "文件夹", Width = 52, Height = 26, CommandProperty = nameof(OpenFolderCommand) },
        };

        public CodeRepository Clone()
        {
            return new CodeRepository
            {
                Name = Name,
                Path = Path,
                LastUsed = LastUsed,
                UsageCount = UsageCount,
                Note = Note ?? "",
                ProjectFiles = ProjectFiles == null ? new List<string>() : new List<string>(ProjectFiles),
            };
        }

        public void ApplyVcsStatusFrom(CodeRepository source)
        {
            if (source == null)
            {
                return;
            }

            VcsType = source.VcsType;
            VcsStatus = source.VcsStatus;
            GitBranch = source.GitBranch;
            GitAheadCount = source.GitAheadCount;
            GitBehindCount = source.GitBehindCount;
            AddedCount = source.AddedCount;
            ModifiedCount = source.ModifiedCount;
            DeletedCount = source.DeletedCount;
            StagedCount = source.StagedCount;
            SvnRevision = source.SvnRevision;
            SubRepositories = source.SubRepositories == null
                ? new ObservableCollection<SubRepository>()
                : new ObservableCollection<SubRepository>(source.SubRepositories.Select(s => s.Clone()));
            GitChangedFiles = source.GitChangedFiles == null
                ? new ObservableCollection<VcsChangedFile>()
                : new ObservableCollection<VcsChangedFile>(source.GitChangedFiles.Select(file => file.Clone()));
            RootSvnChangedFiles = source.RootSvnChangedFiles == null
                ? new ObservableCollection<VcsChangedFile>()
                : new ObservableCollection<VcsChangedFile>(source.RootSvnChangedFiles.Select(file => file.Clone()));
            LastStatusRefresh = source.LastStatusRefresh;
            IsRefreshing = source.IsRefreshing;
            HasConflict = source.HasConflict;
        }

        private bool HasSubRepoChanges =>
            SubRepositories?.Any(s => s.ChangedFileCount > 0) == true;

        [JsonIgnore]
        public bool HasGitChanges => GitChangedFiles?.Count > 0 || AddedCount + ModifiedCount + DeletedCount > 0;

        [JsonIgnore]
        public bool HasSvnChanges => RootSvnChangedFiles?.Count > 0 || SubRepositories?.Any(s => s.ChangedFileCount > 0) == true;

        [JsonIgnore]
        public bool HasAnyChanges => HasGitChanges || HasSvnChanges;

        [JsonIgnore]
        public string DiffHint => HasAnyChanges ? "双击查看差异" : null;

        [JsonIgnore]
        public string GitDiffHint => HasGitChanges ? "双击查看差异" : null;

        [JsonIgnore]
        public string SvnDiffHint => HasSvnChanges ? "双击查看差异" : null;

        [JsonIgnore]
        public Cursor DetailCursor => HasAnyChanges ? Cursors.Hand : Cursors.Arrow;

        [JsonIgnore]
        public Cursor GitCursor => HasGitChanges ? Cursors.Hand : Cursors.Arrow;

        [JsonIgnore]
        public Cursor SvnCursor => HasSvnChanges ? Cursors.Hand : Cursors.Arrow;

        [JsonIgnore]
        public Thickness GitAccentThickness => HasGitChanges ? new Thickness(3, 1, 1, 1) : new Thickness(1);

        [JsonIgnore]
        public Thickness SvnAccentThickness => HasSvnChanges ? new Thickness(3, 1, 1, 1) : new Thickness(1);

        [JsonIgnore]
        public Brush GitAccentBrush => HasGitChanges ? GitBrush : CardBorderBrush;

        [JsonIgnore]
        public Brush SvnAccentBrush => HasSvnChanges ? SvnBrush : CardBorderBrush;

        private string GetStatusSymbol()
        {
            switch (VcsStatus)
            {
                case VcsStatus.Clean:
                    return "●";
                case VcsStatus.Modified:
                    return "◆";
                case VcsStatus.Conflict:
                case VcsStatus.Error:
                    return "!";
                default:
                    return "○";
            }
        }

        private void OnChangesChanged()
        {
            OnPropertyChanged(nameof(ChangesSummary));
            OnPropertyChanged(nameof(VcsTooltip));
            OnPropertyChanged(nameof(RootStatusDetail));
            OnPropertyChanged(nameof(HasGitChanges));
            OnPropertyChanged(nameof(HasAnyChanges));
            OnPropertyChanged(nameof(DiffHint));
            OnPropertyChanged(nameof(GitDiffHint));
            OnPropertyChanged(nameof(DetailCursor));
            OnPropertyChanged(nameof(GitCursor));
            OnPropertyChanged(nameof(GitAccentThickness));
            OnPropertyChanged(nameof(GitAccentBrush));
        }

        private void OnVcsSummaryChanged()
        {
            OnPropertyChanged(nameof(VcsIndicator));
            OnPropertyChanged(nameof(BranchDisplay));
            OnPropertyChanged(nameof(ChangesSummary));
            OnPropertyChanged(nameof(VcsTooltip));
            OnPropertyChanged(nameof(VcsStatusBrush));
            OnPropertyChanged(nameof(VcsTypeBrush));
            OnPropertyChanged(nameof(SubRepositoryStatusBrush));
            OnPropertyChanged(nameof(ChangesBrush));
            OnPropertyChanged(nameof(VcsTypeDisplay));
            OnPropertyChanged(nameof(VcsDetailTitle));
            OnPropertyChanged(nameof(RootStatusDetail));
            OnPropertyChanged(nameof(SubRepositoryDetail));
            OnPropertyChanged(nameof(SubRepositoryPanelTitle));
            OnPropertyChanged(nameof(LastStatusRefreshText));
            OnPropertyChanged(nameof(GitDetailLine));
            OnPropertyChanged(nameof(SvnDetailLine));
            OnPropertyChanged(nameof(HasGitChanges));
            OnPropertyChanged(nameof(HasSvnChanges));
            OnPropertyChanged(nameof(HasAnyChanges));
            OnPropertyChanged(nameof(DiffHint));
            OnPropertyChanged(nameof(GitDiffHint));
            OnPropertyChanged(nameof(SvnDiffHint));
            OnPropertyChanged(nameof(DetailCursor));
            OnPropertyChanged(nameof(GitCursor));
            OnPropertyChanged(nameof(SvnCursor));
            OnPropertyChanged(nameof(GitAccentThickness));
            OnPropertyChanged(nameof(SvnAccentThickness));
            OnPropertyChanged(nameof(GitAccentBrush));
            OnPropertyChanged(nameof(SvnAccentBrush));
        }

        private static string FormatStatus(VcsStatus status)
        {
            switch (status)
            {
                case VcsStatus.Clean:
                    return "干净";
                case VcsStatus.Modified:
                    return "有变更";
                case VcsStatus.Conflict:
                    return "有冲突";
                case VcsStatus.Error:
                    return "检测失败";
                default:
                    return "未知";
            }
        }

        private string BuildRootChangeSummary()
        {
            var total = AddedCount + ModifiedCount + DeletedCount;
            return total == 0 ? "干净" : $"+{AddedCount} ~{ModifiedCount} -{DeletedCount}";
        }

        private static Brush GetBrushForStatus(VcsStatus status)
        {
            switch (status)
            {
                case VcsStatus.Clean:
                    return CleanBrush;
                case VcsStatus.Modified:
                    return ModifiedBrush;
                case VcsStatus.Conflict:
                case VcsStatus.Error:
                    return ErrorBrush;
                default:
                    return UnknownBrush;
            }
        }

        private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
