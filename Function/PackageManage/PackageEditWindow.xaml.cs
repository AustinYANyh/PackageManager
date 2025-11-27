using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using Microsoft.Win32;
using PackageManager.Models;
using RelayCommand = CustomControlLibrary.CustomControl.Example.RelayCommand;

namespace PackageManager.Function.PackageManage
{
    public partial class PackageEditWindow : Window
    {
        public PackageItem Item { get; }
        public ObservableCollection<EditItem> EditItems { get; }

        public PackageEditWindow(PackageItem item)
        {
            InitializeComponent();
            Item = item;
            EditItems = new ObservableCollection<EditItem> { new EditItem
            {
                ProductName = item.ProductName,
                FtpServerPath = item.FtpServerPath,
                LocalPath = item.LocalPath,
                IsBuiltIn = item.IsBuiltIn,
            }};
            DataContext = this;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var edited = EditItems[0];
            if (!Item.IsBuiltIn)
            {
                Item.ProductName = edited.ProductName;
                Item.FtpServerPath = edited.FtpServerPath;
                Item.LocalPath = edited.LocalPath;
            }
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        public class EditItem : INotifyPropertyChanged
        {
            private string productName;
            private string ftpServerPath;
            private string localPath;
            private ICommand browseCommand;

            [DataGridColumn(1, DisplayName = "产品名称", Width = "180", IsReadOnlyProperty = nameof(IsBuiltIn))]
            public string ProductName
            {
                get => productName;
                set => SetProperty(ref productName, value);
            }

            [DataGridColumn(2, DisplayName = "FTP服务器路径", Width = "350", IsReadOnlyProperty = nameof(IsBuiltIn))]
            public string FtpServerPath
            {
                get => ftpServerPath;
                set => SetProperty(ref ftpServerPath, value);
            }

            [DataGridColumn(3, DisplayName = "本地路径", Width = "300", IsReadOnlyProperty = nameof(IsBuiltIn))]
            public string LocalPath
            {
                get => localPath;
                set => SetProperty(ref localPath, value);
            }

            [DataGridButton(4, DisplayName = "选择路径", Width = "120", ControlType = "Button", ButtonText = "浏览...", ButtonWidth = 90, ButtonHeight = 26, ButtonCommandProperty = nameof(BrowseCommand), IsReadOnlyProperty = nameof(IsBuiltIn))]
            public string Browse { get; set; }

            public ICommand BrowseCommand
            {
                get => browseCommand ?? (browseCommand = new RelayCommand(ExecuteBrowse));
                set => SetProperty(ref browseCommand, value);
            }

            private void ExecuteBrowse()
            {
                var dialog = new OpenFileDialog
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
                    var folder = System.IO.Path.GetDirectoryName(dialog.FileName);
                    if (!string.IsNullOrEmpty(folder))
                    {
                        LocalPath = folder;
                    }
                }
            }

            public bool IsBuiltIn { get; set; }

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
}
