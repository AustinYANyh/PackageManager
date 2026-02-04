using System.ComponentModel;
using System.Runtime.CompilerServices;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;

namespace PackageManager.Models
{
    public class VisibilityItem : INotifyPropertyChanged
    {
        private string productName;
        private bool isVisible;

        [DataGridColumn(1, DisplayName = "产品名称", Width = "500")]
        public string ProductName
        {
            get => productName;
            set => SetProperty(ref productName, value);
        }

        [DataGridCheckBox(2, DisplayName = "可见性", Width = "120")]
        public bool IsVisible
        {
            get => isVisible;
            set => SetProperty(ref isVisible, value);
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
