using System;
using System.Globalization;
using System.Text;
using System.Windows.Data;
using PackageManager.Models;

namespace PackageManager.Converters
{
    public sealed class PresetToIniConverter : IValueConverter
    {
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

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
