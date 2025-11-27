using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;

namespace PackageManager.Models
{
    /// <summary>
    /// 本地路径设置项（用于路径设置专用表格）
    /// </summary>
    public class LocalPathInfo : INotifyPropertyChanged
    {
        private string productName;
        private string localPath;
        private ICommand browseCommand;

        [DataGridColumn(1, DisplayName = "产品名称", Width = "220", IsReadOnly = true)]
        public string ProductName
        {
            get => productName;
            set => SetProperty(ref productName, value);
        }

        [DataGridColumn(2, DisplayName = "版本", Width = "80", IsReadOnly = true)]
        public string Version { get; set; }

        [DataGridColumn(3, DisplayName = "本地包路径", Width = "420")]
        public string LocalPath
        {
            get => localPath;
            set => SetProperty(ref localPath, value);
        }

        [DataGridButton(4, DisplayName = "选择路径", Width = "120", ControlType = "Button", ButtonText = "浏览...", ButtonWidth = 90, ButtonHeight = 26, ButtonCommandProperty = "BrowseCommand")]
        public string Browse { get; set; }

        public ICommand BrowseCommand
        {
            get => browseCommand ?? (browseCommand = new RelayCommand(ExecuteBrowse));
            set => SetProperty(ref browseCommand, value);
        }

        private void ExecuteBrowse()
        {
            // 使用WPF的Microsoft.Win32.OpenFileDialog选择文件夹
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择本地包所在的文件夹",
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "选择文件夹",
                Filter = "文件夹|*.none",
            };

            var result = dialog.ShowDialog();
            if (result == true)
            {
                // 获取选择的文件夹路径（去掉FileName部分）
                LocalPath = System.IO.Path.GetDirectoryName(dialog.FileName);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
