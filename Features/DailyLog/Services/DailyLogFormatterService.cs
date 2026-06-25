using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PackageManager.Features.DailyLog.Services
{
    /// <summary>
    /// 对日报文本进行结构化整理，修正空白与编号。
    /// </summary>
    public sealed class DailyLogFormatterService
    {
        private static readonly Regex SectionTitleRegex = new Regex(@"^[一二三四五六七八九十]+、.+$", RegexOptions.Compiled);
        private static readonly Regex NumberedLineRegex = new Regex(@"^\s*\d+[\.、]\s*(.+?)\s*$", RegexOptions.Compiled);

        /// <summary>
        /// 格式化日报文本，清理空行并重排列表编号。
        /// </summary>
        /// <param name="text">原始日报文本。</param>
        /// <returns>格式化后的日报文本。</returns>
        public string Format(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var normalized = NormalizeLineEndings(text);
            var rawLines = normalized
                .Split(new[] { '\n' }, StringSplitOptions.None)
                .Select(line => (line ?? string.Empty).TrimEnd())
                .ToList();

            var preambleLines = new List<string>();
            var sections = new List<DailyLogSection>();
            DailyLogSection currentSection = null;

            foreach (var rawLine in rawLines)
            {
                var line = rawLine.Trim();
                if (IsSectionTitle(line))
                {
                    currentSection = new DailyLogSection { Title = line };
                    sections.Add(currentSection);
                    continue;
                }

                if (currentSection == null)
                {
                    preambleLines.Add(line);
                    continue;
                }

                currentSection.Lines.Add(line);
            }

            var sb = new StringBuilder();
            AppendPreamble(sb, preambleLines);

            for (int i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                sb.AppendLine(section.Title);

                foreach (var line in FormatSection(section))
                {
                    sb.AppendLine(line);
                }

                if (i < sections.Count - 1)
                {
                    sb.AppendLine();
                }
            }

            return TrimTrailingBlankLines(sb.ToString());
        }

        private static void AppendPreamble(StringBuilder sb, IEnumerable<string> preambleLines)
        {
            var lines = CollapseBlankLines(preambleLines).ToList();
            if (lines.Count == 0)
            {
                return;
            }

            foreach (var line in lines)
            {
                sb.AppendLine(line);
            }

            sb.AppendLine();
        }

        private static IEnumerable<string> FormatSection(DailyLogSection section)
        {
            if (section == null)
            {
                return Array.Empty<string>();
            }

            return IsNumberedSection(section.Title)
                ? FormatNumberedSection(section.Lines)
                : CollapseBlankLines(section.Lines);
        }

        private static IEnumerable<string> FormatNumberedSection(IEnumerable<string> lines)
        {
            var items = new List<string>();
            var hasExplicitNone = false;

            foreach (var rawLine in lines ?? Enumerable.Empty<string>())
            {
                var line = (rawLine ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (string.Equals(line, "无", StringComparison.OrdinalIgnoreCase))
                {
                    hasExplicitNone = true;
                    continue;
                }

                var match = NumberedLineRegex.Match(line);
                items.Add(match.Success ? match.Groups[1].Value.Trim() : line);
            }

            if (items.Count == 0)
            {
                return hasExplicitNone ? new[] { "无" } : Array.Empty<string>();
            }

            return items.Select((item, index) => $"{index + 1}. {item}");
        }

        private static IEnumerable<string> CollapseBlankLines(IEnumerable<string> lines)
        {
            var result = new List<string>();
            var previousBlank = true;

            foreach (var rawLine in lines ?? Enumerable.Empty<string>())
            {
                var line = (rawLine ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (previousBlank)
                    {
                        continue;
                    }

                    result.Add(string.Empty);
                    previousBlank = true;
                    continue;
                }

                result.Add(line);
                previousBlank = false;
            }

            while (result.Count > 0 && string.IsNullOrWhiteSpace(result[result.Count - 1]))
            {
                result.RemoveAt(result.Count - 1);
            }

            return result;
        }

        private static bool IsSectionTitle(string line)
        {
            return !string.IsNullOrWhiteSpace(line) && SectionTitleRegex.IsMatch(line);
        }

        private static bool IsNumberedSection(string title)
        {
            return !string.IsNullOrWhiteSpace(title) &&
                   (title.Contains("今日工作") || title.Contains("明日计划"));
        }

        private static string NormalizeLineEndings(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static string TrimTrailingBlankLines(string text)
        {
            return (text ?? string.Empty).TrimEnd('\r', '\n');
        }

        private sealed class DailyLogSection
        {
            public string Title { get; set; }

            public List<string> Lines { get; } = new List<string>();
        }
    }
}
