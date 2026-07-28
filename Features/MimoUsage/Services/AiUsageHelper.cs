namespace PackageManager.Features.MimoUsage.Services
{
    /// <summary>
    /// AI 平台用量查询的公共工具方法。
    /// </summary>
    public static class AiUsageHelper
    {
        /// <summary>
        /// 将数字格式化为中文单位显示（万/亿），省略为零的段。
        /// 示例：11052000000 → "110亿5200万"，11000000000 → "110亿"
        /// </summary>
        public static string FormatChineseNumber(long value)
        {
            if (value < 0) return $"-{FormatChineseNumber(-value)}";

            if (value >= 100_000_000L)
            {
                var yi = value / 100_000_000L;
                var r = value % 100_000_000L;
                var wan = r / 10_000L;
                var ge = r % 10_000L;
                if (wan == 0 && ge == 0) return $"{yi}亿";
                if (wan == 0) return $"{yi}亿{ge}";
                if (ge == 0) return $"{yi}亿{wan}万";
                return $"{yi}亿{wan}万{ge}";
            }

            if (value >= 10_000L)
            {
                var wan = value / 10_000L;
                var ge = value % 10_000L;
                if (ge == 0) return $"{wan}万";
                return $"{wan}万{ge}";
            }

            return value.ToString("N0");
        }
    }
}
