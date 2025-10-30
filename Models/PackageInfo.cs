using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;

namespace PackageManager.Models
{
    /// <summary>
    /// 产品包信息数据模型
    /// </summary>
    public class PackageInfo : INotifyPropertyChanged
    {
        private string _productName;
        private string _version;
        private string _ftpServerPath;
        private string _localPath;
        private PackageStatus _status;
        private double _progress;
        private string _statusText;
        private string _uploadTime;
        private ObservableCollection<string> _availableVersions;

        /// <summary>
        /// 产品名称
        /// </summary>
        [DataGridColumn(1, DisplayName = "产品名称", Width = "180",IsReadOnly = true)]
        public string ProductName
        {
            get => _productName;
            set => SetProperty(ref _productName, value);
        }

        /// <summary>
        /// 当前版本
        /// </summary>
        [DataGridComboBox(2, "版本", "AvailableVersions",Width = "120")]
        public string Version
        {
            get => _version;
            set => SetProperty(ref _version, value);
        }

        /// <summary>
        /// FTP服务器路径
        /// </summary>
        [DataGridColumn(4, DisplayName = "FTP服务器路径", Width = "350",IsReadOnly = true)]
        public string FtpServerPath
        {
            get => _ftpServerPath;
            set => SetProperty(ref _ftpServerPath, value);
        }

        /// <summary>
        /// 本地包路径
        /// </summary>
        [DataGridColumn(5, DisplayName = "本地包路径", Width = "280")]
        public string LocalPath
        {
            get => _localPath;
            set => SetProperty(ref _localPath, value);
        }

        /// <summary>
        /// 包状态
        /// </summary>
        [DataGridColumn(6, DisplayName = "状态", Width = "100",IsReadOnly = true)]
        public PackageStatus Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }
        
        [DataGridColumn(7, DisplayName = "操作" ,Width = "150", ControlType = "Button", ButtonText = "操作", ButtonWidth = 100, ButtonHeight = 26 )]
        public string DoWork { get; set; }
        

        /// <summary>
        /// 下载进度 (0-100)
        /// </summary>
        [DataGridColumn(8, DisplayName = "进度", Width = "120")]
        public double Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, value);
        }

        /// <summary>
        /// 状态文本
        /// </summary>
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        /// <summary>
        /// 包上传时间
        /// </summary>
        [DataGridColumn(3, DisplayName = "上传时间", Width = "150")]
        public string UploadTime
        {
            get => _uploadTime;
            set => SetProperty(ref _uploadTime, value);
        }

        /// <summary>
        /// 可用版本列表
        /// </summary>
        public ObservableCollection<string> AvailableVersions
        {
            get => _availableVersions ?? (_availableVersions = new ObservableCollection<string>());
            set => SetProperty(ref _availableVersions, value);
        }

        /// <summary>
        /// 更新可用版本列表
        /// </summary>
        /// <param name="versions">版本列表</param>
        public void UpdateAvailableVersions(IEnumerable<string> versions)
        {
            AvailableVersions.Clear();
            foreach (var version in versions)
            {
                AvailableVersions.Add(version);
            }

            // 如果有版本且当前版本为空，则选择最后一个版本
            if (AvailableVersions.Count > 0 && string.IsNullOrEmpty(Version))
            {
                Version = AvailableVersions.Last();
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

    /// <summary>
    /// 包状态枚举
    /// </summary>
    public enum PackageStatus
    {
        /// <summary>
        /// 就绪
        /// </summary>
        Ready,
        /// <summary>
        /// 下载中
        /// </summary>
        Downloading,
        /// <summary>
        /// 解压中
        /// </summary>
        Extracting,
        /// <summary>
        /// 完成
        /// </summary>
        Completed,
        /// <summary>
        /// 错误
        /// </summary>
        Error
    }
}