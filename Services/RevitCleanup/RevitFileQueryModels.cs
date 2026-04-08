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
        FirstBuild = 2
    }

    internal sealed class RevitFileQueryRoot
    {
        public string DisplayName { get; set; }

        public string RootPath { get; set; }
    }

    internal sealed class RevitFileQueryOptions
    {
        public List<RevitFileQueryRoot> Roots { get; set; } = new List<RevitFileQueryRoot>();

        public string[] Extensions { get; set; } = { ".rvt", ".rfa" };

        public bool ForceRebuildLocalIndex { get; set; }

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
        public string FullPath { get; set; }

        public string FileName { get; set; }

        public long SizeBytes { get; set; }

        public DateTime ModifiedTimeUtc { get; set; }

        public string RootPath { get; set; }

        public string RootDisplayName { get; set; }

        public RevitFileQuerySourceKind SourceKind { get; set; }
    }

    internal sealed class RevitFileQueryProgress
    {
        public string Message { get; set; }
    }

    internal sealed class RevitFileQueryResult
    {
        public RevitFileQuerySourceKind SourceKind { get; set; }

        public string ProviderDisplayText { get; set; }

        public IReadOnlyList<RevitIndexedFileInfo> Files { get; set; } = Array.Empty<RevitIndexedFileInfo>();

        public long TotalSizeBytes => Files?.Sum(item => item.SizeBytes) ?? 0L;

        public int TotalCount => Files?.Count ?? 0;
    }

    internal static class RevitCleanupPathUtility
    {
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

        public static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return null;
            }

            extension = extension.Trim();
            return extension.StartsWith(".", StringComparison.Ordinal) ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        }

        public static bool IsIndexedExtension(string path, IEnumerable<string> extensions)
        {
            var extension = NormalizeExtension(IOPath.GetExtension(path));
            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            return (extensions ?? Array.Empty<string>()).Any(item => string.Equals(NormalizeExtension(item), extension, StringComparison.OrdinalIgnoreCase));
        }
    }
}
