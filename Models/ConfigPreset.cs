using System;
using System.ComponentModel;
using Newtonsoft.Json;

namespace PackageManager.Models
{
    public class ConfigPreset : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string ServerDomain { get; set; }
        public string CommonServerDomain { get; set; }
        public string IEProxyAvailable { get; set; } = "yes";

        // 可选：用于存储用户粘贴的完整 INI 文本。当存在时，优先使用该文本进行应用。
        public string RawIniContent { get; set; }

        public int requestTimeout { get; set; }
        public int responseTimeout { get; set; }
        public int requestRetryTimes { get; set; }

        [JsonIgnore]
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        [JsonIgnore]
        public bool IsBuiltIn { get; set; }

        private bool _isSelected;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
