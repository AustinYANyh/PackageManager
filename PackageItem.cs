using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using PackageManager.Models;
using PackageManager.Services;

namespace PackageManager
{
    public class PackageItem: INotifyPropertyChanged
    {
        private readonly PackageConfigWindow owner;

        private string productName;

        private string ftpServerPath;

        private string localPath;

        private bool supportsConfigOps;

        private bool isBuiltIn;

        public PackageItem(PackageConfigWindow owner)
        {
            this.owner = owner;
            EditCommand = new RelayCommand(() => owner.EditItem(this, false), () => CanEditDelete);
            DeleteCommand = new RelayCommand(() => owner.RemoveItem(this), () => CanEditDelete);
        }

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

        public bool CanEditDelete => !IsBuiltIn;

        [DataGridMultiButton(nameof(ActionButtonsConfig), 5, DisplayName = "操作", Width = "250", ButtonSpacing = 12)]
        public string Actions { get; set; }

        public ICommand EditCommand { get; }

        public ICommand DeleteCommand { get; }

        public List<ButtonConfig> ActionButtonsConfig => new List<ButtonConfig>
        {
            new ButtonConfig { Text = "编辑", Width = 70, Height = 26, CommandProperty = nameof(EditCommand), IsEnabledProperty = nameof(CanEditDelete) },
            new ButtonConfig { Text = "删除", Width = 70, Height = 26, CommandProperty = nameof(DeleteCommand), IsEnabledProperty = nameof(CanEditDelete) },
        };

        public static PackageItem From(DataPersistenceService.PackageConfigItem c, bool builtIn, PackageConfigWindow owner) => new PackageItem(owner)
        {
            ProductName = c.ProductName,
            FtpServerPath = c.FtpServerPath,
            LocalPath = c.LocalPath,
            SupportsConfigOps = c.SupportsConfigOps,
            IsBuiltIn = builtIn
        };

        public static DataPersistenceService.PackageConfigItem ToConfig(PackageItem p) => new DataPersistenceService.PackageConfigItem
        {
            ProductName = p.ProductName,
            FtpServerPath = p.FtpServerPath,
            LocalPath = p.LocalPath,
            SupportsConfigOps = p.SupportsConfigOps
        };

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
