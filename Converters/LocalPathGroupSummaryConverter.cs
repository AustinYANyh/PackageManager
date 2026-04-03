using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Documents;
using PackageManager.Models;

namespace PackageManager.Converters
{
    /// <summary>
    /// 将本地路径集合转换为汇总描述文本的转换器。
    /// </summary>
    public class LocalPathGroupSummaryConverter : IValueConverter
    {
        /// <summary>
        /// 将本地路径集合转换为汇总描述字符串。
        /// </summary>
        /// <param name="value">包含 <see cref="LocalPathInfo"/> 对象的集合。</param>
        /// <param name="targetType">目标类型。</param>
        /// <param name="parameter">转换参数（未使用）。</param>
        /// <param name="culture">区域信息。</param>
        /// <returns>路径汇总描述文本。</returns>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var items = value as IEnumerable;
            if (items == null)
            {
                return "所有版本均未设置路径";
            }

            var localPaths = items.Cast<object>()
                                  .OfType<LocalPathInfo>()
                                  .Select(item => (item.LocalPath ?? string.Empty).Trim())
                                  .Where(path => !string.IsNullOrWhiteSpace(path))
                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                  .ToList();

            if (localPaths.Count == 0)
            {
                return "所有版本均未设置路径";
            }

            if (localPaths.Count == 1)
            {
                return "当前已统一为同一路径";
            }

            return $"当前存在 {localPaths.Count} 个不同路径";
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
