using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using IOPath = System.IO.Path;

namespace MftScanner
{
    /// <summary>
    /// 通过 NTFS MFT 枚举实现高速文件扫描服务。
    /// </summary>
    internal sealed class MftScanService
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 1;
        private const uint FILE_SHARE_WRITE = 2;
        private const uint FILE_SHARE_DELETE = 4;
        private const uint OPEN_EXISTING = 3;
        private const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
            ref MftEnumDataV0 lpInBuffer, int nInBufferSize,
            IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct MftEnumDataV0
        {
            public ulong StartFileReferenceNumber;
            public long LowUsn;
            public long HighUsn;
        }

        private struct MftEntry
        {
            public string Name;
            public ulong ParentFrn;
            public int NameLength; // 用于多记录时优先保留长文件名
        }

        /// <summary>
        /// 扫描指定根目录下匹配扩展名的文件。
        /// </summary>
        public Task<List<ScannedFileInfo>> ScanAsync(IReadOnlyList<ScanRoot> roots, IReadOnlyList<string> extensions,
            IProgress<string> progress, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                var extSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ext in extensions)
                {
                    var e = ext.Trim();
                    if (!e.StartsWith(".", StringComparison.Ordinal)) e = "." + e;
                    extSet.Add(e.ToLowerInvariant());
                }

                var volumeGroups = roots
                    .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Path) && r.Path.Length >= 2 && r.Path[1] == ':')
                    .GroupBy(r => char.ToUpperInvariant(r.Path[0]));

                var results = new List<ScannedFileInfo>();

                foreach (var group in volumeGroups)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var driveLetter = group.Key;
                    var volumeRoots = group
                        .Select(r => new ScanRoot { Path = NormalizePath(r.Path), DisplayName = r.DisplayName })
                        .Where(r => r.Path != null)
                        .ToList();

                    progress?.Report($"正在通过 MFT 扫描卷 {driveLetter}:...");

                    var directories = new Dictionary<ulong, MftEntry>();
                    var matchingFiles = new List<MftEntry>();
                    EnumerateMft(driveLetter, extSet, directories, matchingFiles, cancellationToken);

                    progress?.Report($"正在解析路径... 找到 {matchingFiles.Count} 个匹配文件");

                    var pathCache = new Dictionary<ulong, string>();
                    foreach (var file in matchingFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var dirPath = ReconstructPath(file.ParentFrn, directories, driveLetter, pathCache);
                        if (dirPath == null) continue;
                        var fullPath = NormalizePath(Path.Combine(dirPath, file.Name));
                        if (fullPath == null) continue;

                        ScanRoot matchedRoot = null;
                        foreach (var root in volumeRoots)
                        {
                            if (IsPathUnderRoot(fullPath, root.Path))
                            {
                                matchedRoot = root;
                                break;
                            }
                        }
                        if (matchedRoot == null) continue;

                        try
                        {
                            var fi = new FileInfo(fullPath);
                            if (!fi.Exists) continue;
                            results.Add(new ScannedFileInfo
                            {
                                FullPath = fullPath,
                                FileName = fi.Name,
                                SizeBytes = fi.Length,
                                ModifiedTimeUtc = fi.LastWriteTimeUtc,
                                RootPath = matchedRoot.Path,
                                RootDisplayName = matchedRoot.DisplayName
                            });
                        }
                        catch { }
                    }
                }

                return results.OrderByDescending(f => f.ModifiedTimeUtc).ToList();
            }, cancellationToken);
        }

        private static void EnumerateMft(char driveLetter, HashSet<string> extensions,
            Dictionary<ulong, MftEntry> directories, List<MftEntry> matchingFiles,
            CancellationToken cancellationToken)
        {
            var volumePath = @"\\.\" + driveLetter + ":";
            var handle = CreateFile(volumePath, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle == INVALID_HANDLE_VALUE)
            {
                var err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"无法打开卷 {volumePath}，错误码={err}");
            }

            var bufferSize = 128 * 1024;
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var enumData = new MftEnumDataV0
                {
                    StartFileReferenceNumber = 0,
                    LowUsn = 0,
                    HighUsn = long.MaxValue
                };

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var ok = DeviceIoControl(handle, FSCTL_ENUM_USN_DATA,
                        ref enumData, Marshal.SizeOf(typeof(MftEnumDataV0)),
                        buffer, bufferSize, out var bytesReturned, IntPtr.Zero);

                    if (!ok) break;
                    if (bytesReturned <= 8) break;

                    enumData.StartFileReferenceNumber = (ulong)Marshal.ReadInt64(buffer, 0);

                    var offset = 8;
                    while (offset + 60 < bytesReturned)
                    {
                        var recordLength = Marshal.ReadInt32(buffer, offset);
                        if (recordLength <= 0) break;

                        var frn = (ulong)Marshal.ReadInt64(buffer, offset + 8) & 0x0000FFFFFFFFFFFF;
                        var parentFrn = (ulong)Marshal.ReadInt64(buffer, offset + 16) & 0x0000FFFFFFFFFFFF;
                        var fileAttributes = (uint)Marshal.ReadInt32(buffer, offset + 52);
                        var fileNameLength = (ushort)Marshal.ReadInt16(buffer, offset + 56);
                        var fileNameOffset = (ushort)Marshal.ReadInt16(buffer, offset + 58);

                        if (fileNameLength > 0 && offset + fileNameOffset + fileNameLength <= bytesReturned)
                        {
                            var fileName = Marshal.PtrToStringUni(
                                IntPtr.Add(buffer, offset + fileNameOffset), fileNameLength / 2);

                            if ((fileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                            {
                                // 同一 FRN 可能有多条记录（DOS 8.3 名 + 长文件名，或多硬链接）
                                // 优先保留文件名最长的那条（即长文件名）
                                if (!directories.TryGetValue(frn, out var existing) || fileName.Length > existing.NameLength)
                                {
                                    directories[frn] = new MftEntry { Name = fileName, ParentFrn = parentFrn, NameLength = fileName.Length };
                                }
                            }
                            else
                            {
                                var ext = GetExtension(fileName);
                                if (ext != null && extensions.Contains(ext))
                                {
                                    matchingFiles.Add(new MftEntry { Name = fileName, ParentFrn = parentFrn });
                                }
                            }
                        }

                        offset += recordLength;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                CloseHandle(handle);
            }
        }

        private static string ReconstructPath(ulong frn, Dictionary<ulong, MftEntry> dirs,
            char driveLetter, Dictionary<ulong, string> cache)
        {
            if (cache.TryGetValue(frn, out var cached)) return cached;

            var parts = new List<string>();
            var current = frn;
            var visited = new HashSet<ulong>();

            while (dirs.TryGetValue(current, out var entry))
            {
                if (!visited.Add(current)) break; // 循环引用，链断掉
                parts.Add(entry.Name);
                current = entry.ParentFrn;
                if (cache.TryGetValue(current, out var parentPath))
                {
                    parts.Reverse();
                    var result = Path.Combine(parentPath, string.Join("\\", parts));
                    cache[frn] = result;
                    return result;
                }
            }

            // current 不在 dirs 里，说明已到达卷根（父 FRN 指向自身或根目录记录）
            // 只有当链能追溯到卷根时才认为路径有效
            // 卷根的父 FRN 通常等于自身 FRN，或者 dirs 里不存在该 FRN
            // 如果 parts 为空说明 frn 本身就是根，直接返回驱动器根路径
            if (parts.Count == 0)
            {
                var root = driveLetter + ":\\";
                cache[frn] = root;
                return root;
            }

            // 检查链是否因循环引用断掉（visited 里有 current 说明是循环，不是正常到根）
            // 正常到根：current 不在 dirs 里（dirs 不含卷根记录）
            // 异常断链：visited 里有 current（循环引用）
            if (visited.Contains(current))
            {
                // 路径重建失败，返回 null 让调用方跳过该文件
                cache[frn] = null;
                return null;
            }

            parts.Reverse();
            var path = driveLetter + ":\\" + string.Join("\\", parts);
            cache[frn] = path;
            return path;
        }

        private static string GetExtension(string fileName)
        {
            var dot = fileName.LastIndexOf('.');
            return dot >= 0 ? fileName.Substring(dot).ToLowerInvariant() : null;
        }

        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var trimmed = path.Trim();

            // 裸盘符 "E:" → "E:\"
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Za-z]:$"))
            {
                return trimmed + IOPath.DirectorySeparatorChar;
            }

            // "E:\" 或 "E:/" → "E:\"
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Za-z]:[\\/]$"))
            {
                return trimmed.Substring(0, 2) + IOPath.DirectorySeparatorChar;
            }

            try
            {
                var full = IOPath.GetFullPath(trimmed);
                var root = IOPath.GetPathRoot(full);
                if (!string.IsNullOrWhiteSpace(root) && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                {
                    return root;
                }

                return full.TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
            }
            catch
            {
                return null;
            }
        }

        internal static bool IsPathUnderRoot(string path, string root)
        {
            var normalizedRoot = NormalizePath(root);
            if (normalizedRoot == null) return false;
            var prefix = normalizedRoot.TrimEnd('\\', '/') + '\\';
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, normalizedRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class ScannedFileInfo
    {
        public string FullPath { get; set; }
        public string FileName { get; set; }
        public long SizeBytes { get; set; }
        public DateTime ModifiedTimeUtc { get; set; }
        public string RootPath { get; set; }
        public string RootDisplayName { get; set; }
    }

    public sealed class ScanRoot
    {
        public string Path { get; set; }
        public string DisplayName { get; set; }
    }
}
