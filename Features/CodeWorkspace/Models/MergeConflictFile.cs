using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PackageManager.Features.CodeWorkspace.Models
{
    public class MergeConflictFile : INotifyPropertyChanged
    {
        private string _statusText;

        public event PropertyChangedEventHandler PropertyChanged;

        public string RelativePath { get; set; }

        public string FullPath { get; set; }

        public string StatusText
        {
            get => _statusText;
            set
            {
                if (_statusText == value)
                {
                    return;
                }

                _statusText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            }
        }
    }
}
