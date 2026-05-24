using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using PackageManager.Function.PackageManage;
using PackageManager.Services;

namespace PackageManager.Models
{
    /// <summary>
    /// 包配置项，表示一个产品的 FTP、本地路径等配置信息。
    /// </summary>
    public class PackageItem: INotifyPropertyChanged
    {
        private readonly IPackageEditorHost owner;

        private string productName;

        private string ftpServerPath;

        private string localPath;

        private string finalizeFtpServerPath;

        private bool supportsConfigOps;

        private bool isBuiltIn;

        /// <summary>
        /// 初始化包配置项。
        /// </summary>
        /// <param name="owner">包编辑器宿主。</param>
        public PackageItem(IPackageEditorHost owner)
        {
            this.owner = owner;
            EditCommand = new RelayCommand(() => owner.EditItem(this, false), () => CanEditDelete);
            DeleteCommand = new RelayCommand(() => owner.RemoveItem(this), () => CanEditDelete);
        }

        /// <summary>
        /// 获取或设置产品名称。
        /// </summary>
        [DataGridColumn(1, DisplayName = "产品名称", Width = "180", IsReadOnly = true)]
        public string ProductName
        {
            get => productName;

            set
            {
                if (value == productName)
                {
                    return;
                }

                productName = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 获取或设置 FTP 服务器路径。
        /// </summary>
        [DataGridColumn(2, DisplayName = "FTP服务器路径", Width = "450", IsReadOnly = true)]
        public string FtpServerPath
        {
            get => ftpServerPath;

            set
            {
                if (value == ftpServerPath)
                {
                    return;
                }

                ftpServerPath = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 获取或设置本地路径。
        /// </summary>
        [DataGridColumn(3, DisplayName = "本地路径", Width = "300", IsReadOnly = true)]
        public string LocalPath
        {
            get => localPath;

            set
            {
                if (value == localPath)
                {
                    return;
                }

                localPath = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 获取或设置定版 FTP 路径。
        /// </summary>
        [DataGridColumn(5, DisplayName = "定版FTP路径", Width = "450", IsReadOnly = true)]
        public string FinalizeFtpServerPath
        {
            get => finalizeFtpServerPath;

            set
            {
                if (value == finalizeFtpServerPath)
                {
                    return;
                }

                finalizeFtpServerPath = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 获取或设置是否允许操作按钮。
        /// </summary>
        [DataGridCheckBox(4, DisplayName = "允许操作按钮", Width = "120", IsReadOnlyProperty = nameof(IsBuiltIn))]
        public bool SupportsConfigOps
        {
            get => supportsConfigOps;

            set
            {
                if (value == supportsConfigOps)
                {
                    return;
                }

                supportsConfigOps = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 获取或设置是否为内置包。
        /// </summary>
        public bool IsBuiltIn
        {
            get => isBuiltIn;

            set
            {
                if (value == isBuiltIn)
                {
                    return;
                }

                isBuiltIn = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanEditDelete));
            }
        }

        /// <summary>
        /// 获取是否可编辑或删除（非内置包）。
        /// </summary>
        public bool CanEditDelete => !IsBuiltIn;

        /// <summary>
        /// 操作按钮列的占位属性。
        /// </summary>
        [DataGridMultiButton(nameof(ActionButtonsConfig), 6, DisplayName = "操作", Width = "250", ButtonSpacing = 12)]
        public string Actions { get; set; }

        /// <summary>
        /// 获取编辑命令。
        /// </summary>
        public ICommand EditCommand { get; }

        /// <summary>
        /// 获取删除命令。
        /// </summary>
        public ICommand DeleteCommand { get; }

        /// <summary>
        /// 获取操作按钮的配置列表。
        /// </summary>
        public List<ButtonConfig> ActionButtonsConfig => new List<ButtonConfig>
        {
            new ButtonConfig { Text = "编辑", Width = 70, Height = 26, CommandProperty = nameof(EditCommand), IsEnabledProperty = nameof(CanEditDelete) },
            new ButtonConfig { Text = "删除", Width = 70, Height = 26, CommandProperty = nameof(DeleteCommand), IsEnabledProperty = nameof(CanEditDelete) },
        };

        /// <summary>
        /// 从配置项创建 <see cref="PackageItem"/> 实例。
        /// </summary>
        /// <param name="c">包配置项。</param>
        /// <param name="builtIn">是否为内置包。</param>
        /// <param name="owner">包编辑器宿主。</param>
        /// <returns>新创建的 <see cref="PackageItem"/> 实例。</returns>
        public static PackageItem From(DataPersistenceService.PackageConfigItem c, bool builtIn, IPackageEditorHost owner) => new PackageItem(owner)
        {
            ProductName = c.ProductName,
            FtpServerPath = c.FtpServerPath,
            LocalPath = c.LocalPath,
            FinalizeFtpServerPath = c.FinalizeFtpServerPath,
            SupportsConfigOps = c.SupportsConfigOps,
            IsBuiltIn = builtIn
        };

        /// <summary>
        /// 将 <see cref="PackageItem"/> 转换为配置项。
        /// </summary>
        /// <param name="p">包配置项实例。</param>
        /// <returns>对应的 <see cref="DataPersistenceService.PackageConfigItem"/>。</returns>
        public static DataPersistenceService.PackageConfigItem ToConfig(PackageItem p) => new DataPersistenceService.PackageConfigItem
        {
            ProductName = p.ProductName,
            FtpServerPath = p.FtpServerPath,
            LocalPath = p.LocalPath,
            FinalizeFtpServerPath = p.FinalizeFtpServerPath,
            SupportsConfigOps = p.SupportsConfigOps
        };

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
        /// 设置字段值并在值变更时触发 <see cref="PropertyChanged"/> 事件。
        /// </summary>
        /// <typeparam name="T">字段类型。</typeparam>
        /// <param name="field">字段引用。</param>
        /// <param name="value">新值。</param>
        /// <param name="propertyName">属性名称。</param>
        /// <returns>值是否发生变更。</returns>
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
