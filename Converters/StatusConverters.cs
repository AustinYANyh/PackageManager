using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PackageManager.Models;
using System.Windows.Media;

namespace PackageManager.Converters
{
    /// <summary>
    /// 状态到可见性转换器 - 用于进度条显示
    /// </summary>
    public class StatusToVisibilityConverter : IValueConverter
    {
        /// <summary>
        /// 将 <see cref="PackageStatus"/> 转换为可见性，下载/解压/校验状态时显示。
        /// </summary>
        /// <param name="value"><see cref="PackageStatus"/> 枚举值。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>进度中返回 <see cref="Visibility.Visible"/>，否则返回 <see cref="Visibility.Collapsed"/>。</returns>
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

        /// <summary>
        /// 不支持反向转换。
        /// </summary>
        /// <exception cref="NotImplementedException">始终抛出。</exception>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 根据工作项类型判断是否为缺陷，返回对应的可见性。
    /// </summary>
    public class TypeIsDefectToVisibilityConverter : IValueConverter
    {
        /// <summary>
        /// 判断类型字符串是否包含缺陷关键词，返回可见性。
        /// </summary>
        /// <param name="value">工作项类型字符串。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>包含缺陷关键词时返回 <see cref="Visibility.Visible"/>，否则返回 <see cref="Visibility.Collapsed"/>。</returns>
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
        /// <summary>
        /// 不支持反向转换。
        /// </summary>
        /// <exception cref="NotImplementedException">始终抛出。</exception>
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
        /// <summary>
        /// 将 <see cref="PackageStatus"/> 转换为按钮可见性，就绪/完成/错误状态时显示。
        /// </summary>
        /// <param name="value"><see cref="PackageStatus"/> 枚举值。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>可操作状态返回 <see cref="Visibility.Visible"/>，否则返回 <see cref="Visibility.Collapsed"/>。</returns>
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

        /// <summary>
        /// 不支持反向转换。
        /// </summary>
        /// <exception cref="NotImplementedException">始终抛出。</exception>
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
        /// <summary>
        /// 获取转换器的单例实例。
        /// </summary>
        public static readonly BooleanToInverseBooleanConverter Instance = new BooleanToInverseBooleanConverter();

        /// <summary>
        /// 将布尔值取反。
        /// </summary>
        /// <param name="value">要取反的布尔值。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>取反后的布尔值。</returns>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return true; // 默认启用
        }

        /// <summary>
        /// 将布尔值取反（双向转换逻辑相同）。
        /// </summary>
        /// <param name="value">要取反的布尔值。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>取反后的布尔值。</returns>
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
        /// <summary>
        /// 在 URL 字符串的分隔符后插入零宽空格以允许换行。
        /// </summary>
        /// <param name="value">URL 字符串。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>包含零宽空格的字符串。</returns>
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

        /// <summary>
        /// 移除零宽空格，还原原始字符串。
        /// </summary>
        /// <param name="value">包含零宽空格的字符串。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>去除零宽空格后的字符串。</returns>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string ?? string.Empty;
            // 移除零宽空格，避免污染数据
            return s.Replace("\u200B", string.Empty);
        }
    }

    /// <summary>
    /// 严重性标识到中文文本的转换器。
    /// </summary>
    public class SeverityTextConverter : IValueConverter
    {
        /// <summary>
        /// 将严重性标识转换为中文描述文本。
        /// </summary>
        /// <param name="value">严重性标识字符串。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>严重性中文描述：致命、严重、一般、建议，或默认的"-"。</returns>
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
        /// <summary>
        /// 直接返回原始字符串值。
        /// </summary>
        /// <param name="value">源值。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>原始字符串值。</returns>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.ToString();
        }
    }

    /// <summary>
    /// 严重性标识到颜色画刷的转换器。
    /// </summary>
    public class SeverityColorConverter : IValueConverter
    {
        /// <summary>
        /// 从十六进制颜色字符串创建画刷。
        /// </summary>
        /// <param name="hex">十六进制颜色字符串。</param>
        /// <returns>对应的 <see cref="SolidColorBrush"/>。</returns>
        private static Brush FromHex(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush(c);
        }

        /// <summary>
        /// 将严重性标识转换为对应的颜色画刷。
        /// </summary>
        /// <param name="value">严重性标识字符串。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>对应严重性的颜色画刷。</returns>
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
        /// <summary>
        /// 不支持反向转换。
        /// </summary>
        /// <exception cref="NotImplementedException">始终抛出。</exception>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 字符串空值转短横线转换器，空或空白字符串显示为"-"。
    /// </summary>
    public class StringDashConverter : IValueConverter
    {
        /// <summary>
        /// 将字符串转换为显示文本，空白值显示为"-"。
        /// </summary>
        /// <param name="value">原始字符串。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>非空字符串或"-"。</returns>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value?.ToString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(s) ? "-" : s;
        }
        /// <summary>
        /// 直接返回原始字符串值。
        /// </summary>
        /// <param name="value">源值。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>原始字符串值。</returns>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.ToString();
        }
    }

    /// <summary>
    /// 双精度浮点数转换器，零值或空值显示为"-"。
    /// </summary>
    public class DoubleDashConverter : IValueConverter
    {
        /// <summary>
        /// 将双精度浮点数转换为显示文本，零值或无效值显示为"-"。
        /// </summary>
        /// <param name="value">双精度数值。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">数字格式字符串。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>格式化后的数值字符串或"-"。</returns>
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
        /// <summary>
        /// 将字符串解析为双精度浮点数。
        /// </summary>
        /// <param name="value">字符串值。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>解析成功的数值，失败返回 0。</returns>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double d;
            return double.TryParse(value?.ToString() ?? "", out d) ? d : 0;
        }
    }

    /// <summary>
    /// 日期时间转换器，空值或默认值显示为"-"。
    /// </summary>
    public class DateDashConverter : IValueConverter
    {
        /// <summary>
        /// 将日期时间转换为格式化字符串，空值或默认值显示为"-"。
        /// </summary>
        /// <param name="value"><see cref="DateTime"/> 或 <see cref="Nullable{DateTime}"/> 值。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">日期格式字符串，默认为"yyyy-MM-dd"。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>格式化后的日期字符串或"-"。</returns>
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
        /// <summary>
        /// 将字符串解析为可空日期时间。
        /// </summary>
        /// <param name="value">日期字符串。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>解析成功的可空日期时间，失败返回 <c>null</c>。</returns>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            DateTime dt;
            return DateTime.TryParse(value?.ToString() ?? "", out dt) ? (DateTime?)dt : null;
        }
    }
}
