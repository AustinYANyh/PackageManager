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
        private string _statusSummary;
        private ObservableCollection<VcsChangedFile> _changedFiles = new ObservableCollection<VcsChangedFile>();
        private static readonly Brush CleanBrush = CreateBrush(0x2E, 0xA0, 0x43);
        private static readonly Brush ModifiedBrush = CreateBrush(0xD9, 0x7A, 0x00);
        private static readonly Brush ErrorBrush = CreateBrush(0xD1, 0x24, 0x2F);
        private static readonly Brush UnknownBrush = CreateBrush(0x8A, 0x94, 0xA3);

        public event PropertyChangedEventHandler PropertyChanged;

        public string RelativePath
        {
            get => _relativePath;
            set => SetProperty(ref _relativePath, value);
        }

        public VcsType VcsType
        {
            get => _vcsType;
            set => SetProperty(ref _vcsType, value);
        }

        public string Branch
        {
            get => _branch;
            set => SetProperty(ref _branch, value);
        }

        public int Revision
        {
            get => _revision;
            set => SetProperty(ref _revision, value);
        }

        public VcsStatus Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
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

        public string StatusSummary
        {
            get => _statusSummary;
            set => SetProperty(ref _statusSummary, value);
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
        }

        private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
