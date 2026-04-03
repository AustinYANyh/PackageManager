using System;
using System.Globalization;
using System.Text;
using System.Windows.Data;
using PackageManager.Models;

namespace PackageManager.Converters
{
    /// <summary>
    /// 将 <see cref="ConfigPreset"/> 对象转换为 INI 格式字符串的转换器。
    /// </summary>
    public sealed class PresetToIniConverter : IValueConverter
    {
        /// <summary>
        /// 将配置预设对象转换为 INI 格式字符串。
        /// </summary>
        /// <param name="value">要转换的 <see cref="ConfigPreset"/> 对象。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>INI 格式的字符串；若输入无效则返回空字符串。</returns>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ConfigPreset p)
            {
                if (!string.IsNullOrWhiteSpace(p.RawIniContent))
                {
                    return p.RawIniContent;
                }

                var sb = new StringBuilder();
                sb.AppendLine("[ServerInfo]");
                sb.AppendLine($"ServerDomain=\"{p.ServerDomain ?? string.Empty}\"");
                sb.AppendLine($"CommonServerDomain=\"{p.CommonServerDomain ?? string.Empty}\"");
                sb.AppendLine($"IEProxyAvailable=\"{(string.IsNullOrEmpty(p.IEProxyAvailable) ? "yes" : p.IEProxyAvailable)}\"");
                sb.AppendLine("[LoginSetting]");
                sb.AppendLine($"requestTimeout={p.requestTimeout}");
                sb.AppendLine($"responseTimeout={p.responseTimeout}");
                sb.AppendLine($"requestRetryTimes={p.requestRetryTimes}");
                return sb.ToString();
            }
            return string.Empty;
        }

        /// <summary>
        /// 反向转换不做处理，返回 <see cref="Binding.DoNothing"/>。
        /// </summary>
        /// <param name="value">源值。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns><see cref="Binding.DoNothing"/></returns>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
