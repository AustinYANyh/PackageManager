using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using CustomControlLibrary.CustomControl.Attribute.TreeView;
using CustomControlLibrary.CustomControl.Controls.TreeView;

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

        public event PropertyChangedEventHandler PropertyChanged;

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

        [DataGridButton(4,
                        DisplayName = "选择路径",
                        Width = "120",
                        ControlType = "Button",
                        ButtonText = "浏览...",
                        ButtonWidth = 90,
                        ButtonHeight = 26,
                        ButtonCommandProperty = "BrowseCommand")]
        public string Browse { get; set; }

        public ICommand BrowseCommand
        {
            get => browseCommand ?? (browseCommand = new RelayCommand(ExecuteBrowse));

            set => SetProperty(ref browseCommand, value);
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
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
    }

    public class LocalPathInfoVersion : MixedTreeNodeBase
    {
        private ICommand browseCommand;

        [TreeViewNode(Order = 2, DisplayName = "产品版本" ,Width = 100 ,Height = 32)]
        public string Version { get; set; }

        private string path;

        [TreeViewNode(Order = 3, ControlType = TreeViewControlType.TextBox, DisplayName = "本地包路径",Width = 400 , Height = 32)]
        public string Path
        {
            get => path;

            set
            {
                path = value;
                OnPropertyChanged();
            }
        }
        
        [TreeViewNode(Order = 4, ControlType = TreeViewControlType.Button, DisplayName = "浏览..." ,Width = 70,Height = 32)]
        public ICommand BrowseCommand
        {
            get => browseCommand ?? (browseCommand = new RelayCommand(ExecuteBrowse));

            set => OnPropertyChanged();
        }

        public override string NodeType => "产品版本";

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
                Path = System.IO.Path.GetDirectoryName(dialog.FileName);
            }
        }
    }

    public class LocalPathProduct : MixedTreeNodeBase
    {
        [TreeViewNode(Order = 1, DisplayName = "产品名称")]
        public new string Name { get; set; }

        public override string NodeType => "产品名称";
    }
}
