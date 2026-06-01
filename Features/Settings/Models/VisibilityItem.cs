using System.ComponentModel;
using System.Runtime.CompilerServices;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;

namespace PackageManager.Models
{
    /// <summary>
    /// 产品可见性配置项，控制某产品是否在界面上显示。
    /// </summary>
    public class VisibilityItem : INotifyPropertyChanged
    {
        private string productName;
        private bool isVisible;

        /// <summary>
        /// 获取或设置产品名称。
        /// </summary>
        [DataGridColumn(1, DisplayName = "产品名称", Width = "500")]
        public string ProductName
        {
            get => productName;
            set => SetProperty(ref productName, value);
        }

        /// <summary>
        /// 获取或设置产品是否可见。
        /// </summary>
        [DataGridCheckBox(2, DisplayName = "可见性", Width = "120")]
        public bool IsVisible
        {
            get => isVisible;
            set => SetProperty(ref isVisible, value);
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
