using System;
using System.ComponentModel;
using Newtonsoft.Json;

namespace PackageManager.Models
{
    /// <summary>
    /// 配置预设项，表示一组服务器和连接参数的预设配置。
    /// </summary>
    public class ConfigPreset : INotifyPropertyChanged
    {
        /// <summary>
        /// 获取或设置预设名称。
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 获取或设置服务器域名。
        /// </summary>
        public string ServerDomain { get; set; }

        /// <summary>
        /// 获取或设置公共服务器域名。
        /// </summary>
        public string CommonServerDomain { get; set; }

        /// <summary>
        /// 获取或设置 IE 代理是否可用，默认为 "yes"。
        /// </summary>
        public string IEProxyAvailable { get; set; } = "yes";

        /// <summary>
        /// 获取或设置用户粘贴的完整 INI 文本。当存在时，优先使用该文本进行应用。
        /// </summary>
        public string RawIniContent { get; set; }

        /// <summary>
        /// 获取或设置请求超时时间（毫秒）。
        /// </summary>
        public int requestTimeout { get; set; }

        /// <summary>
        /// 获取或设置响应超时时间（毫秒）。
        /// </summary>
        public int responseTimeout { get; set; }

        /// <summary>
        /// 获取或设置请求重试次数。
        /// </summary>
        public int requestRetryTimes { get; set; }

        /// <summary>
        /// 获取或设置该预设是否被选中。
        /// </summary>
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

        /// <summary>
        /// 获取或设置该预设是否为内置预设。
        /// </summary>
        [JsonIgnore]
        public bool IsBuiltIn { get; set; }

        private bool _isSelected;

        /// <summary>
        /// 属性值变更时触发。
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 触发 <see cref="PropertyChanged"/> 事件。
        /// </summary>
        /// <param name="propertyName">发生变更的属性名称。</param>
        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
