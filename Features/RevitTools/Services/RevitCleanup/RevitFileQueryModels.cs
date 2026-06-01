using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using IOPath = System.IO.Path;

namespace PackageManager.Services.RevitCleanup
{
    internal enum RevitFileQuerySourceKind
    {
        EverythingIndex = 0,
        LocalIndex = 1,
        FirstBuild = 2,
        MftIndex = 3
    }

    internal sealed class RevitFileQueryRoot
    {
        /// <summary>
        /// 获取或设置根目录的显示名称。
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// 获取或设置根目录路径。
        /// </summary>
        public string RootPath { get; set; }
    }

    internal sealed class RevitFileQueryOptions
    {
        /// <summary>
        /// 获取或设置查询的根目录列表。
        /// </summary>
        public List<RevitFileQueryRoot> Roots { get; set; } = new List<RevitFileQueryRoot>();

        /// <summary>
        /// 获取或设置要搜索的文件扩展名数组。
        /// </summary>
        public string[] Extensions { get; set; } = { ".rvt", ".rfa" };

        /// <summary>
        /// 获取或设置是否强制重建本地索引。
        /// </summary>
        public bool ForceRebuildLocalIndex { get; set; }

        /// <summary>
        /// 对查询选项进行标准化：规范化路径、去重、补齐默认扩展名。
        /// </summary>
        /// <returns>标准化后的新 <see cref="RevitFileQueryOptions"/> 实例。</returns>
        public RevitFileQueryOptions Normalize()
        {
            var normalizedRoots = Roots.Where(root => root != null)
                                       .Select(root => new RevitFileQueryRoot
                                       {
                                           DisplayName = string.IsNullOrWhiteSpace(root.DisplayName) ? "自定义" : root.DisplayName,
                                           RootPath = RevitCleanupPathUtility.NormalizePath(root.RootPath)
                                       })
                                       .Where(root => !string.IsNullOrWhiteSpace(root.RootPath))
                                       .GroupBy(root => root.RootPath, StringComparer.OrdinalIgnoreCase)
                                       .Select(group => group.First())
                                       .ToList();

            var normalizedExtensions = (Extensions ?? Array.Empty<string>())
                .Select(RevitCleanupPathUtility.NormalizeExtension)
                .Where(extension => !string.IsNullOrWhiteSpace(extension))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new RevitFileQueryOptions
            {
                Roots = normalizedRoots,
                Extensions = normalizedExtensions.Length == 0 ? new[] { ".rvt", ".rfa" } : normalizedExtensions,
                ForceRebuildLocalIndex = ForceRebuildLocalIndex
            };
        }
    }

    internal sealed class RevitIndexedFileInfo
    {
        /// <summary>
        /// 获取或设置文件完整路径。
        /// </summary>
        public string FullPath { get; set; }

        /// <summary>
        /// 获取或设置文件名。
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// 获取或设置文件大小（字节）。
        /// </summary>
        public long SizeBytes { get; set; }

        /// <summary>
        /// 获取或设置文件最后修改时间（UTC）。
        /// </summary>
        public DateTime ModifiedTimeUtc { get; set; }

        /// <summary>
        /// 获取或设置所属根目录路径。
        /// </summary>
        public string RootPath { get; set; }

        /// <summary>
        /// 获取或设置所属根目录的显示名称。
        /// </summary>
        public string RootDisplayName { get; set; }

        /// <summary>
        /// 获取或设置文件来源的索引类型。
        /// </summary>
        public RevitFileQuerySourceKind SourceKind { get; set; }
    }

    internal sealed class RevitFileQueryProgress
    {
        /// <summary>
        /// 获取或设置进度消息文本。
        /// </summary>
        public string Message { get; set; }
    }

    internal sealed class RevitFileQueryResult
    {
        /// <summary>
        /// 获取或设置查询结果的索引来源类型。
        /// </summary>
        public RevitFileQuerySourceKind SourceKind { get; set; }

        /// <summary>
        /// 获取或设置索引提供方的显示名称。
        /// </summary>
        public string ProviderDisplayText { get; set; }

        /// <summary>
        /// 获取或设置查询到的文件列表。
        /// </summary>
        public IReadOnlyList<RevitIndexedFileInfo> Files { get; set; } = Array.Empty<RevitIndexedFileInfo>();

        /// <summary>
        /// 获取所有文件的总大小（字节）。
        /// </summary>
        public long TotalSizeBytes => Files?.Sum(item => item.SizeBytes) ?? 0L;

        /// <summary>
        /// 获取文件总数。
        /// </summary>
        public int TotalCount => Files?.Count ?? 0;
    }

    internal static class RevitCleanupPathUtility
    {
        /// <summary>
        /// 规范化路径：去除尾部斜杠、解析相对路径并统一分隔符。
        /// </summary>
        /// <param name="path">原始路径。</param>
        /// <returns>规范化后的路径；无效输入返回 null。</returns>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var trimmedInput = path.Trim();
            if (Regex.IsMatch(trimmedInput, @"^[A-Za-z]:$"))
            {
                return trimmedInput + IOPath.DirectorySeparatorChar;
            }

            if (Regex.IsMatch(trimmedInput, @"^[A-Za-z]:[\\/]{1}$"))
            {
                return trimmedInput.Substring(0, 2) + IOPath.DirectorySeparatorChar;
            }

            try
            {
                var fullPath = IOPath.GetFullPath(trimmedInput);
                var rootPath = IOPath.GetPathRoot(fullPath);
                if (!string.IsNullOrWhiteSpace(rootPath) && string.Equals(fullPath, rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    return rootPath;
                }

                return fullPath.TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
            }
            catch
            {
                var rootPath = IOPath.GetPathRoot(trimmedInput);
                if (!string.IsNullOrWhiteSpace(rootPath) && string.Equals(trimmedInput, rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    return rootPath;
                }

                return trimmedInput.TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
            }
        }

        /// <summary>
        /// 规范化文件扩展名：确保以点开头并转为小写。
        /// </summary>
        /// <param name="extension">原始扩展名。</param>
        /// <returns>规范化后的扩展名；无效输入返回 null。</returns>
        public static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return null;
            }

            extension = extension.Trim();
            return extension.StartsWith(".", StringComparison.Ordinal) ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        }

        /// <summary>
        /// 判断文件扩展名是否在给定的扩展名列表中。
        /// </summary>
        /// <param name="path">文件路径。</param>
        /// <param name="extensions">扩展名列表。</param>
        /// <returns>匹配返回 true，否则 false。</returns>
        public static bool IsIndexedExtension(string path, IEnumerable<string> extensions)
        {
            var extension = NormalizeExtension(IOPath.GetExtension(path));
            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            return (extensions ?? Array.Empty<string>()).Any(item => string.Equals(NormalizeExtension(item), extension, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 判断指定路径是否位于给定根目录下（含根目录本身）。
        /// </summary>
        /// <param name="path">待判断的路径。</param>
        /// <param name="rootPath">根目录路径。</param>
        /// <returns>是子路径或相等返回 true，否则 false。</returns>
        public static bool IsPathUnderRoot(string path, string rootPath)
        {
            var normalizedPath = NormalizePath(path);
            var normalizedRoot = NormalizePath(rootPath);
            if (string.IsNullOrWhiteSpace(normalizedPath) || string.IsNullOrWhiteSpace(normalizedRoot))
            {
                return false;
            }

            if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var rootWithoutTrailing = normalizedRoot.TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(rootWithoutTrailing))
            {
                return false;
            }

            return normalizedPath.StartsWith(rootWithoutTrailing + IOPath.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
