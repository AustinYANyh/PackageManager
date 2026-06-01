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
    /// <summary>
    /// 包编辑窗口，用于编辑或新增包配置项。
    /// </summary>
    public partial class PackageEditWindow : Window
    {
        /// <summary>
        /// 获取正在编辑的包配置项。
        /// </summary>
        public PackageItem Item { get; }

        /// <summary>
        /// 获取编辑项集合。
        /// </summary>
        public ObservableCollection<EditItem> EditItems { get; }

        private readonly bool isNew;

        /// <summary>
        /// 初始化 <see cref="PackageEditWindow"/> 的新实例。
        /// </summary>
        /// <param name="item">要编辑的包配置项。</param>
        /// <param name="isNew">是否为新增模式。</param>
        public PackageEditWindow(PackageItem item, bool isNew)
        {
            InitializeComponent();
            this.isNew = isNew;
            Item = item;
            EditItems = new ObservableCollection<EditItem> { new EditItem
            {
                ProductName = item.ProductName,
                FtpServerPath = item.FtpServerPath,
                LocalPath = item.LocalPath,
                FinalizeFtpServerPath = item.FinalizeFtpServerPath,
                IsBuiltIn = item.IsBuiltIn,
                IsNew = isNew,
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
                // 仅在新增时允许写入定版地址
                if (isNew)
                {
                    Item.FinalizeFtpServerPath = edited.FinalizeFtpServerPath;
                }
            }
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// 编辑项数据模型，用于在包编辑窗口中绑定编辑字段。
        /// </summary>
        public class EditItem : INotifyPropertyChanged
        {
            private string productName;
            private string ftpServerPath;
            private string localPath;
            private string finalizeFtpServerPath;

            /// <summary>
            /// 获取或设置是否为新增模式。
            /// </summary>
            public bool IsNew { get; set; }

            private ICommand browseCommand;

            /// <summary>
            /// 获取或设置产品名称。
            /// </summary>
            [DataGridColumn(1, DisplayName = "产品名称", Width = "180", IsReadOnlyProperty = nameof(IsBuiltIn))]
            public string ProductName
            {
                get => productName;
                set => SetProperty(ref productName, value);
            }

            /// <summary>
            /// 获取或设置 FTP 服务器路径。
            /// </summary>
            [DataGridColumn(2, DisplayName = "FTP服务器路径", Width = "350", IsReadOnlyProperty = nameof(IsBuiltIn))]
            public string FtpServerPath
            {
                get => ftpServerPath;
                set => SetProperty(ref ftpServerPath, value);
            }

            /// <summary>
            /// 获取或设置本地路径。
            /// </summary>
            [DataGridColumn(4, DisplayName = "本地路径", Width = "300", IsReadOnlyProperty = nameof(IsBuiltIn))]
            public string LocalPath
            {
                get => localPath;
                set => SetProperty(ref localPath, value);
            }

            /// <summary>
            /// 获取或设置定版 FTP 路径。
            /// </summary>
            [DataGridColumn(3, DisplayName = "定版FTP路径", Width = "350", IsReadOnlyProperty = nameof(IsFinalizeReadonly))]
            public string FinalizeFtpServerPath
            {
                get => finalizeFtpServerPath;
                set => SetProperty(ref finalizeFtpServerPath, value);
            }

            /// <summary>
            /// 获取定版 FTP 路径是否只读（内置项或非新增模式）。
            /// </summary>
            public bool IsFinalizeReadonly => IsBuiltIn || !IsNew;

            /// <summary>
            /// 浏览按钮列的占位属性。
            /// </summary>
            [DataGridButton(5, DisplayName = "选择路径", Width = "120", ControlType = "Button", ButtonText = "浏览...", ButtonWidth = 90, ButtonHeight = 26, ButtonCommandProperty = nameof(BrowseCommand), IsReadOnlyProperty = nameof(IsBuiltIn))]
            public string Browse { get; set; }

            /// <summary>
            /// 获取或设置浏览命令。
            /// </summary>
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

            /// <summary>
            /// 获取或设置是否为内置包。
            /// </summary>
            public bool IsBuiltIn { get; set; }

            /// <summary>
            /// 属性值变更时触发。
            /// </summary>
            public event PropertyChangedEventHandler PropertyChanged;

            /// <summary>
            /// 触发 <see cref="PropertyChanged"/> 事件。
            /// </summary>
            /// <param name="propertyName">发生变更的属性名称。</param>
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
