using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PackageManager.Features.CodeWorkspace.Models
{
    public class MergeRepositoryItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private string _statusText = "等待检查";
        private string _detailText;
        private bool _canMerge;
        private bool _isProcessing;
        private bool _hasError;
        private bool _hasConflict;
        private bool _isCompleted;
        private bool _isPushed;

        public event PropertyChangedEventHandler PropertyChanged;

        public string DisplayName { get; set; }

        public string RepositoryPath { get; set; }

        public string RelativePath { get; set; }

        public string SourceBranch { get; set; }

        public string TargetBranch { get; set; } = "master";

        public bool IsRoot { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool CanMerge
        {
            get => _canMerge;
            set => SetProperty(ref _canMerge, value);
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set => SetProperty(ref _isProcessing, value);
        }

        public bool HasError
        {
            get => _hasError;
            set => SetProperty(ref _hasError, value);
        }

        public bool HasConflict
        {
            get => _hasConflict;
            set => SetProperty(ref _hasConflict, value);
        }

        public bool IsCompleted
        {
            get => _isCompleted;
            set => SetProperty(ref _isCompleted, value);
        }

        public bool IsPushed
        {
            get => _isPushed;
            set => SetProperty(ref _isPushed, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public string DetailText
        {
            get => _detailText;
            set => SetProperty(ref _detailText, value);
        }

        public string TypeText => IsRoot ? "根仓库" : "Git 子仓库";

        public string PathText => string.IsNullOrWhiteSpace(RelativePath) ? RepositoryPath : RelativePath;

        private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(storage, value))
            {
                return false;
            }

            storage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}
