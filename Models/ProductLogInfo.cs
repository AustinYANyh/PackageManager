using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;

namespace PackageManager.Models
{
    /// <summary>
    /// 产品日志文件信息（用于 CDataGrid 的标签配置）
    /// </summary>
    public class ProductLogInfo : INotifyPropertyChanged
    {
        private string fileName;
        private string directory;
        private string fullPath;
        private string sizeText;
        private string modifiedText;

        private ICommand openCommand;
        private ICommand openWithLogViewProCommand;
        private ICommand openWithVSCodeCommand;
        private ICommand openWithNotepadCommand;

        [DataGridColumn(1, DisplayName = "日志文件名", Width = "250", IsReadOnly = true)]
        public string FileName
        {
            get => fileName;
            set => SetProperty(ref fileName, value);
        }

        [DataGridColumn(2, DisplayName = "所在目录", Width = "370", IsReadOnly = true,IsVisible = false)]
        public string Directory
        {
            get => directory;
            set => SetProperty(ref directory, value);
        }

        [DataGridColumn(3, DisplayName = "大小", Width = "110", IsReadOnly = true)]
        public string SizeText
        {
            get => sizeText;
            set => SetProperty(ref sizeText, value);
        }

        [DataGridColumn(4, DisplayName = "修改时间", Width = "170", IsReadOnly = true)]
        public string ModifiedText
        {
            get => modifiedText;
            set => SetProperty(ref modifiedText, value);
        }


        [DataGridMultiButton(nameof(OpenButtons), 5, DisplayName = "操作", Width = "110", ButtonSpacing = 12)]
        public string Open { get; set; }

        public List<ButtonConfig> OpenButtons => new List<ButtonConfig>
        {
            new ButtonConfig
            {
                Text = "打开",
                Width = 100,
                Height = 26,
                CommandProperty = nameof(OpenCommand),
            },
        };
        
        public string FullPath
        {
            get => fullPath;
            set => SetProperty(ref fullPath, value);
        }

        public ICommand OpenCommand
        {
            get => openCommand;
            set => SetProperty(ref openCommand, value);
        }

        public ICommand OpenWithLogViewProCommand
        {
            get => openWithLogViewProCommand;
            set => SetProperty(ref openCommand, value);
        }
        public ICommand OpenWithVSCodeCommand
        {
            get => openWithVSCodeCommand;
            set => SetProperty(ref openCommand, value);
        }
        public ICommand OpenWithNotepadCommand
        {
            get => openWithNotepadCommand;
            set => SetProperty(ref openCommand, value);
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
