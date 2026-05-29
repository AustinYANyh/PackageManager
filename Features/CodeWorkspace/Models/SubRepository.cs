using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;

namespace PackageManager.Features.CodeWorkspace.Models
{
    public class SubRepository : INotifyPropertyChanged
    {
        private string _relativePath;
        private VcsType _vcsType;
        private string _branch;
        private int _revision;
        private VcsStatus _status;
        private int _changedFileCount;
        private int _gitAheadCount;
        private int _gitBehindCount;
        private int _stagedCount;
        private string _statusSummary;
        private ObservableCollection<VcsChangedFile> _changedFiles = new ObservableCollection<VcsChangedFile>();
        private static readonly Brush CleanBrush = CreateBrush(0x2E, 0xA0, 0x43);
        private static readonly Brush ModifiedBrush = CreateBrush(0xD9, 0x7A, 0x00);
        private static readonly Brush ErrorBrush = CreateBrush(0xD1, 0x24, 0x2F);
        private static readonly Brush UnknownBrush = CreateBrush(0x8A, 0x94, 0xA3);
        private static readonly Brush GitBrush = CreateBrush(0x34, 0x6D, 0xDB);
        private static readonly Brush SvnBrush = CreateBrush(0x7A, 0x52, 0xC7);

        public event PropertyChangedEventHandler PropertyChanged;

        public string RelativePath
        {
            get => _relativePath;
            set
            {
                if (SetProperty(ref _relativePath, value))
                {
                    OnDisplayTextChanged();
                }
            }
        }

        public VcsType VcsType
        {
            get => _vcsType;
            set
            {
                if (SetProperty(ref _vcsType, value))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VcsTypeText)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VcsTypeBrush)));
                    OnDisplayTextChanged();
                }
            }
        }

        public string Branch
        {
            get => _branch;
            set
            {
                if (SetProperty(ref _branch, value))
                {
                    OnDisplayTextChanged();
                }
            }
        }

        public int Revision
        {
            get => _revision;
            set
            {
                if (SetProperty(ref _revision, value))
                {
                    OnDisplayTextChanged();
                }
            }
        }

        public VcsStatus Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
                    OnDisplayTextChanged();
                }
            }
        }

        public int ChangedFileCount
        {
            get => _changedFileCount;
            set
            {
                if (SetProperty(ref _changedFileCount, value))
                {
                    OnChangeStateChanged();
                }
            }
        }

        public int GitAheadCount
        {
            get => _gitAheadCount;
            set
            {
                if (SetProperty(ref _gitAheadCount, value))
                {
                    OnChangeStateChanged();
                }
            }
        }

        public int GitBehindCount
        {
            get => _gitBehindCount;
            set
            {
                if (SetProperty(ref _gitBehindCount, value))
                {
                    OnChangeStateChanged();
                }
            }
        }

        public int StagedCount
        {
            get => _stagedCount;
            set
            {
                if (SetProperty(ref _stagedCount, value))
                {
                    OnChangeStateChanged();
                }
            }
        }

        public string StatusSummary
        {
            get => _statusSummary;
            set
            {
                if (SetProperty(ref _statusSummary, value))
                {
                    OnDisplayTextChanged();
                }
            }
        }

        public ObservableCollection<VcsChangedFile> ChangedFiles
        {
            get => _changedFiles;
            set
            {
                if (SetProperty(ref _changedFiles, value ?? new ObservableCollection<VcsChangedFile>()))
                {
                    OnChangeStateChanged();
                }
            }
        }

        public Brush StatusBrush
        {
            get
            {
                switch (Status)
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
        }

        public bool HasChanges => ChangedFileCount > 0 || ChangedFiles?.Count > 0;

        public string DiffHint => HasChanges ? "双击查看差异" : null;

        public Cursor DetailCursor => HasChanges ? Cursors.Hand : Cursors.Arrow;

        public string VcsTypeText => VcsType == VcsType.Git ? "Git" : VcsType == VcsType.Svn ? "SVN" : "-";

        public Brush VcsTypeBrush => VcsType == VcsType.Git ? GitBrush : VcsType == VcsType.Svn ? SvnBrush : UnknownBrush;

        public string DisplayName => string.IsNullOrWhiteSpace(RelativePath) ? "-" : RelativePath;

        public string SecondaryLine
        {
            get
            {
                if (VcsType == VcsType.Git)
                {
                    return string.IsNullOrWhiteSpace(Branch) ? "-" : Branch;
                }

                return Revision > 0 ? $"r{Revision}" : "-";
            }
        }

        public string RemoteSummary => $"↑{GitAheadCount} ↓{GitBehindCount}";

        public string MetaLine
        {
            get
            {
                var statusText = string.IsNullOrWhiteSpace(StatusSummary) ? FormatStatus(Status) : StatusSummary;
                if (VcsType != VcsType.Git)
                {
                    return statusText;
                }

                var stagedText = StagedCount > 0 ? $" · staged {StagedCount}" : string.Empty;
                var changeText = ChangedFileCount > 0 ? $" · 变更 {ChangedFileCount}" : string.Empty;
                return $"{statusText}{stagedText}{changeText} · {RemoteSummary}";
            }
        }

        public string CompactLine
        {
            get
            {
                var secondary = string.IsNullOrWhiteSpace(SecondaryLine) ? "-" : SecondaryLine;
                var meta = string.IsNullOrWhiteSpace(MetaLine) ? "-" : MetaLine;
                return $"{secondary} · {meta}";
            }
        }

        public string FullTooltip => $"{VcsTypeText} 子仓库: {DisplayName}\n{SecondaryLine}\n{MetaLine}";

        public string DetailLine
        {
            get
            {
                if (VcsType == VcsType.Git)
                {
                    var branch = string.IsNullOrWhiteSpace(Branch) ? "-" : Branch;
                    return $"{branch}  {StatusSummary}  staged {StagedCount}  ahead {GitAheadCount} / behind {GitBehindCount}";
                }

                return $"r{Revision}  {StatusSummary}";
            }
        }

        public SubRepository Clone()
        {
            return new SubRepository
            {
                RelativePath = RelativePath,
                VcsType = VcsType,
                Branch = Branch,
                Revision = Revision,
                Status = Status,
                ChangedFileCount = ChangedFileCount,
                GitAheadCount = GitAheadCount,
                GitBehindCount = GitBehindCount,
                StagedCount = StagedCount,
                StatusSummary = StatusSummary,
                ChangedFiles = ChangedFiles == null
                    ? new ObservableCollection<VcsChangedFile>()
                    : new ObservableCollection<VcsChangedFile>(ChangedFiles.Select(file => file.Clone())),
            };
        }

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        private void OnChangeStateChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasChanges)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DiffHint)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailCursor)));
            OnDisplayTextChanged();
        }

        private void OnDisplayTextChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SecondaryLine)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemoteSummary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MetaLine)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompactLine)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullTooltip)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailLine)));
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
                    return "未检测";
            }
        }

        private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
