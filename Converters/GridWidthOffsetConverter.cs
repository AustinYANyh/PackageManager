using System;
using System.Globalization;
using System.Windows.Data;

namespace PackageManager.Converters
{
    /// <summary>
    /// 将源宽度减去固定偏移量的转换器，用于分组头部 Border 的宽度绑定。
    /// 取代原先代码后置遍历视觉树设置宽度的做法，对虚拟化按需生成的组头同样生效。
    /// </summary>
    public class GridWidthOffsetConverter : IValueConverter
    {
        /// <summary>
        /// 将源宽度减去参数指定的偏移量（默认 72，与原代码后置逻辑保持一致）。
        /// </summary>
        /// <param name="value">源宽度（double）。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">偏移量字符串，默认 72。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>减去偏移量后的宽度，不小于 0。</returns>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var offset = 72.0;
            if (parameter != null &&
                double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                offset = parsed;
            }

            var width = value is double d ? d : 0;
            return Math.Max(0, width - offset);
        }

        /// <summary>
        /// 不支持反向转换。
        /// </summary>
        /// <param name="value">源值。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <exception cref="NotImplementedException">始终抛出。</exception>
        /// <returns>此方法不会返回。</returns>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
