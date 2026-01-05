using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PackageManager.Models;
using System.Windows;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Globalization;

namespace PackageManager.Converters
{
    /// <summary>
    /// 状态到可见性转换器 - 用于进度条显示
    /// </summary>
    public class StatusToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is PackageStatus status)
            {
                return status == PackageStatus.Downloading || status == PackageStatus.Extracting 
                    || status == PackageStatus.VerifyingSignature || status == PackageStatus.VerifyingEncryption
                    ? Visibility.Visible 
                    : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    public class TypeIsDefectToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = (value as string ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(s)) return Visibility.Collapsed;
            if (s.Contains("缺陷") || s.Contains("bug"))
            {
                return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 状态到按钮可见性转换器 - 用于更新按钮显示
    /// </summary>
    public class StatusToButtonVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is PackageStatus status)
            {
                return status == PackageStatus.Ready || status == PackageStatus.Completed || status == PackageStatus.Error
                    ? Visibility.Visible 
                    : Visibility.Collapsed;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 布尔值反转转换器 - 用于将IsReadOnly转换为IsEnabled
    /// </summary>
    public class BooleanToInverseBooleanConverter : IValueConverter
    {
        public static readonly BooleanToInverseBooleanConverter Instance = new BooleanToInverseBooleanConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return true; // 默认启用
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return false;
        }
    }

    /// <summary>
    /// URL/域名换行转换器：在常见分隔符后注入零宽空格以提供换行点
    /// </summary>
    public class UrlWrapConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string ?? string.Empty;
            // 在", :, /, ., -"等分隔符后插入零宽空格以允许换行
            s = s.Replace(":", ":\u200B")
                 .Replace("/", "/\u200B")
                 .Replace(".", ".\u200B")
                 .Replace("-", "-\u200B");
            return s;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string ?? string.Empty;
            // 移除零宽空格，避免污染数据
            return s.Replace("\u200B", string.Empty);
        }
    }
    
    public class SeverityTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = (value as string ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(s)) return "-";
            if (s == "5cb7e6e2fda1ce4ca0020004") return "致命";
            if (s == "5cb7e6e2fda1ce4ca0020003") return "严重";
            if (s == "5cb7e6e2fda1ce4ca0020002") return "一般";
            if (s == "5cb7e6e2fda1ce4ca0020001") return "建议";
            if (s.Contains("critical") || s.Contains("致命")) return "致命";
            if (s.Contains("严重") || s.Contains("major")) return "严重";
            if (s.Contains("一般") || s.Contains("normal")) return "一般";
            if (s.Contains("建议") || s.Contains("minor") || s.Contains("suggest")) return "建议";
            return "-";
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.ToString();
        }
    }
    
    public class SeverityColorConverter : IValueConverter
    {
        private static Brush FromHex(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush(c);
        }
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = (value as string ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(s)) return FromHex("#9CA3AF");
            if (s == "5cb7e6e2fda1ce4ca0020004" || s.Contains("critical") || s.Contains("致命")) return FromHex("#EF4444");
            if (s == "5cb7e6e2fda1ce4ca0020003" || s.Contains("严重") || s.Contains("major")) return FromHex("#F59E0B");
            if (s == "5cb7e6e2fda1ce4ca0020002" || s.Contains("一般") || s.Contains("normal")) return FromHex("#FBBF24");
            if (s == "5cb7e6e2fda1ce4ca0020001" || s.Contains("建议") || s.Contains("minor") || s.Contains("suggest")) return FromHex("#10B981");
            return FromHex("#9CA3AF");
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    public class StringDashConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value?.ToString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(s) ? "-" : s;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.ToString();
        }
    }
    
    public class DoubleDashConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "-";
            double d;
            if (value is double dd) d = dd;
            else if (!double.TryParse(value.ToString(), out d)) return "-";
            if (Math.Abs(d) < 0.000001) return "-";
            var f = parameter as string;
            if (!string.IsNullOrWhiteSpace(f)) return d.ToString(f);
            return d.ToString("0.##");
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double d;
            return double.TryParse(value?.ToString() ?? "", out d) ? d : 0;
        }
    }
    
    public class DateDashConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var fmt = parameter as string ?? "yyyy-MM-dd";
            if (value == null) return "-";
            if (value is DateTime dt)
            {
                return dt == default ? "-" : dt.ToString(fmt);
            }

            var ndt = value as DateTime?;
            if (ndt != null)
            {
                return ndt.HasValue ? ndt.Value.ToString(fmt) : "-";
            }
            return "-";
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            DateTime dt;
            return DateTime.TryParse(value?.ToString() ?? "", out dt) ? (DateTime?)dt : null;
        }
    }
}
