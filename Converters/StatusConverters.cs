using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PackageManager.Models;

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
}