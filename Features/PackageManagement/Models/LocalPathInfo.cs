using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using PackageManager.Services;

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

        /// <summary>
        /// 获取或设置产品名称。
        /// </summary>
        [DataGridColumn(1, DisplayName = "产品名称", Width = "220", IsReadOnly = true)]
        public string ProductName
        {
            get => productName;
            set => SetProperty(ref productName, value);
        }

        /// <summary>
        /// 获取或设置版本号。
        /// </summary>
        [DataGridColumn(2, DisplayName = "版本", Width = "150", IsReadOnly = true)]
        public string Version { get; set; }

        /// <summary>
        /// 获取或设置本地包路径。
        /// </summary>
        [DataGridColumn(3, DisplayName = "本地包路径", Width = "450")]
        public string LocalPath
        {
            get => localPath;
            set => SetProperty(ref localPath, value);
        }

        /// <summary>
        /// 浏览按钮列的占位属性。
        /// </summary>
        [DataGridButton(4, DisplayName = "选择路径", Width = "120", ControlType = "Button", ButtonText = "浏览...", ButtonWidth = 90, ButtonHeight = 26, ButtonCommandProperty = "BrowseCommand")]
        public string Browse { get; set; }

        /// <summary>
        /// 获取或设置浏览文件夹命令。
        /// </summary>
        public ICommand BrowseCommand
        {
            get => browseCommand ?? (browseCommand = new RelayCommand(ExecuteBrowse));
            set => SetProperty(ref browseCommand, value);
        }

        private void ExecuteBrowse()
        {
            var selectedPath = FolderPickerService.PickFolder("选择本地包所在的文件夹", LocalPath);
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                LocalPath = selectedPath;
            }
        }

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

        /// <summary>
        /// 设置属性值并在值变更时触发 <see cref="PropertyChanged"/> 事件。
        /// </summary>
        /// <typeparam name="T">属性类型。</typeparam>
        /// <param name="field">属性 backing 字段的引用。</param>
        /// <param name="value">新值。</param>
        /// <param name="propertyName">属性名称。</param>
        /// <returns>值是否发生变更。</returns>
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
