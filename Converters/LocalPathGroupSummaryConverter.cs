using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Documents;
using PackageManager.Models;

namespace PackageManager.Converters
{
    public class LocalPathGroupSummaryConverter : IValueConverter
    {
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

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
