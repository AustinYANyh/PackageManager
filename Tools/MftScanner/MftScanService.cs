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
    /// 首次扫描走全量 MFT 枚举，后续扫描通过 USN 日志增量更新，速度接近即时。
    /// </summary>
    internal sealed class MftScanService
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 1;
        private const uint FILE_SHARE_WRITE = 2;
        private const uint FILE_SHARE_DELETE = 4;
        private const uint OPEN_EXISTING = 3;
        private const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
        private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
        private const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const uint USN_REASON_FILE_CREATE = 0x00000100;
        private const uint USN_REASON_FILE_DELETE = 0x00000200;
        private const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;
        private const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
            ref MftEnumDataV0 lpInBuffer, int nInBufferSize,
            IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        private static extern bool DeviceIoControlQueryUsn(IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, int nInBufferSize,
            out UsnJournalData lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        private static extern bool DeviceIoControlReadUsn(IntPtr hDevice, uint dwIoControlCode,
            ref ReadUsnJournalData lpInBuffer, int nInBufferSize,
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

        [StructLayout(LayoutKind.Sequential)]
        private struct UsnJournalData
        {
            public ulong UsnJournalID;
            public long FirstUsn;
            public long NextUsn;
            public long LowestValidUsn;
            public long MaxUsn;
            public ulong MaximumSize;
            public ulong AllocationDelta;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ReadUsnJournalData
        {
            public long StartUsn;
            public uint ReasonMask;
            public uint ReturnOnlyOnClose;
            public ulong Timeout;
            public ulong BytesToWaitFor;
            public ulong UsnJournalID;
        }

        private struct MftEntry
        {
            public string Name;
            public ulong ParentFrn;
            public int NameLength;
        }

        private sealed class VolumeCache
        {
            public Dictionary<ulong, MftEntry> Directories { get; } = new Dictionary<ulong, MftEntry>();
            // key = FRN，保证同一物理文件不重复计数
            public Dictionary<ulong, MftEntry> MatchedFiles { get; } = new Dictionary<ulong, MftEntry>();
            public long NextUsn { get; set; }
            public ulong UsnJournalId { get; set; }
        }

        // 魔数 + 版本，格式变更时递增版本号使旧缓存自动失效
        private const uint CacheMagic = 0x4D465443; // "MFTC"
        private const ushort CacheVersion = 1;

        private static readonly string CacheDir = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PackageManager");

        private readonly Dictionary<char, VolumeCache> _volumeCaches = new Dictionary<char, VolumeCache>();

        /// <summary>
        /// 清除指定卷（或全部卷）的内存缓存及磁盘缓存文件，下次扫描将重新全量枚举 MFT。
        /// </summary>
        public void InvalidateCache(char? driveLetter = null)
        {
            if (driveLetter.HasValue)
            {
                var key = char.ToUpperInvariant(driveLetter.Value);
                _volumeCaches.Remove(key);
                TryDeleteCacheFile(key);
            }
            else
            {
                foreach (var key in _volumeCaches.Keys.ToList())
                    TryDeleteCacheFile(key);
                _volumeCaches.Clear();
            }
        }

        /// <summary>
        /// 将当前所有内存缓存写入磁盘，供下次启动时加载。
        /// </summary>
        public void SaveAllCaches()
        {
            foreach (var kv in _volumeCaches)
                TrySaveCache(kv.Key, kv.Value);
        }

        /// <summary>
        /// 从磁盘加载指定卷的缓存（若存在且格式有效）。
        /// </summary>
        private void TryLoadCache(char driveLetter)
        {
            var path = GetCacheFilePath(driveLetter);
            if (!File.Exists(path)) return;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs))
                {
                    if (br.ReadUInt32() != CacheMagic) return;
                    if (br.ReadUInt16() != CacheVersion) return;

                    var cache = new VolumeCache
                    {
                        UsnJournalId = br.ReadUInt64(),
                        NextUsn = br.ReadInt64(),
                    };

                    var dirCount = br.ReadInt32();
                    for (var i = 0; i < dirCount; i++)
                    {
                        var frn = br.ReadUInt64();
                        var name = br.ReadString();
                        var parentFrn = br.ReadUInt64();
                        cache.Directories[frn] = new MftEntry { Name = name, ParentFrn = parentFrn, NameLength = name.Length };
                    }

                    var fileCount = br.ReadInt32();
                    for (var i = 0; i < fileCount; i++)
                    {
                        var frn = br.ReadUInt64();
                        var name = br.ReadString();
                        var parentFrn = br.ReadUInt64();
                        cache.MatchedFiles[frn] = new MftEntry { Name = name, ParentFrn = parentFrn, NameLength = name.Length };
                    }

                    _volumeCaches[driveLetter] = cache;
                }
            }
            catch
            {
                // 缓存损坏，忽略，下次全量扫描会重建
                TryDeleteCacheFile(driveLetter);
            }
        }

        private void TrySaveCache(char driveLetter, VolumeCache cache)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var path = GetCacheFilePath(driveLetter);
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(CacheMagic);
                    bw.Write(CacheVersion);
                    bw.Write(cache.UsnJournalId);
                    bw.Write(cache.NextUsn);

                    bw.Write(cache.Directories.Count);
                    foreach (var kv in cache.Directories)
                    {
                        bw.Write(kv.Key);
                        bw.Write(kv.Value.Name);
                        bw.Write(kv.Value.ParentFrn);
                    }

                    bw.Write(cache.MatchedFiles.Count);
                    foreach (var kv in cache.MatchedFiles)
                    {
                        bw.Write(kv.Key);
                        bw.Write(kv.Value.Name);
                        bw.Write(kv.Value.ParentFrn);
                    }
                }
            }
            catch { }
        }

        private static string GetCacheFilePath(char driveLetter)
            => IOPath.Combine(CacheDir, $"mft_cache_{char.ToUpperInvariant(driveLetter)}.bin");

        private static void TryDeleteCacheFile(char driveLetter)
        {
            try { File.Delete(GetCacheFilePath(driveLetter)); } catch { }
        }

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

                    VolumeCache cache;
                    if (!_volumeCaches.ContainsKey(driveLetter))
                        TryLoadCache(driveLetter); // 内存没有时尝试从磁盘加载

                    if (_volumeCaches.TryGetValue(driveLetter, out var existing))
                    {
                        progress?.Report($"正在通过 USN 日志增量更新卷 {driveLetter}:...");
                        if (TryApplyUsnDelta(driveLetter, existing, extSet, cancellationToken))
                        {
                            cache = existing;
                        }
                        else
                        {
                            progress?.Report($"USN 日志已失效，正在重新全量扫描卷 {driveLetter}:...");
                            cache = FullScanVolume(driveLetter, extSet, cancellationToken);
                            _volumeCaches[driveLetter] = cache;
                            TrySaveCache(driveLetter, cache);
                        }
                    }
                    else
                    {
                        progress?.Report($"正在通过 MFT 全量扫描卷 {driveLetter}:...");
                        cache = FullScanVolume(driveLetter, extSet, cancellationToken);
                        _volumeCaches[driveLetter] = cache;
                        TrySaveCache(driveLetter, cache);
                    }

                    progress?.Report($"正在解析路径... 找到 {cache.MatchedFiles.Count} 个匹配文件");

                    var pathCache = new Dictionary<ulong, string>();
                    foreach (var kv in cache.MatchedFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var file = kv.Value;
                        var dirPath = ReconstructPath(file.ParentFrn, cache.Directories, driveLetter, pathCache);
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

        private VolumeCache FullScanVolume(char driveLetter, HashSet<string> extSet, CancellationToken ct)
        {
            var cache = new VolumeCache();
            EnumerateMft(driveLetter, extSet, cache.Directories, cache.MatchedFiles, ct);

            var volumePath = @"\\.\" + driveLetter + ":";
            var handle = CreateFile(volumePath, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle != INVALID_HANDLE_VALUE)
            {
                try
                {
                    if (DeviceIoControlQueryUsn(handle, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0,
                        out var journalData, Marshal.SizeOf(typeof(UsnJournalData)), out _, IntPtr.Zero))
                    {
                        cache.NextUsn = journalData.NextUsn;
                        cache.UsnJournalId = journalData.UsnJournalID;
                    }
                }
                finally { CloseHandle(handle); }
            }

            return cache;
        }

        private bool TryApplyUsnDelta(char driveLetter, VolumeCache cache, HashSet<string> extSet, CancellationToken ct)
        {
            var volumePath = @"\\.\" + driveLetter + ":";
            var handle = CreateFile(volumePath, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle == INVALID_HANDLE_VALUE) return false;

            try
            {
                if (!DeviceIoControlQueryUsn(handle, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0,
                    out var journalData, Marshal.SizeOf(typeof(UsnJournalData)), out _, IntPtr.Zero))
                    return false;

                // 日志被重建或游标已过期
                if (journalData.UsnJournalID != cache.UsnJournalId) return false;
                if (cache.NextUsn < journalData.LowestValidUsn) return false;

                // 没有新变更，直接复用缓存
                if (cache.NextUsn >= journalData.NextUsn) return true;

                var readData = new ReadUsnJournalData
                {
                    StartUsn = cache.NextUsn,
                    ReasonMask = USN_REASON_FILE_CREATE | USN_REASON_FILE_DELETE
                               | USN_REASON_RENAME_OLD_NAME | USN_REASON_RENAME_NEW_NAME,
                    ReturnOnlyOnClose = 0,
                    Timeout = 0,
                    BytesToWaitFor = 0,
                    UsnJournalID = cache.UsnJournalId,
                };

                var bufferSize = 64 * 1024;
                var buffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (!DeviceIoControlReadUsn(handle, FSCTL_READ_USN_JOURNAL,
                            ref readData, Marshal.SizeOf(typeof(ReadUsnJournalData)),
                            buffer, bufferSize, out var bytesReturned, IntPtr.Zero))
                            break;

                        if (bytesReturned <= 8) break;

                        readData.StartUsn = Marshal.ReadInt64(buffer, 0);

                        var offset = 8;
                        while (offset + 60 < bytesReturned)
                        {
                            var recordLength = Marshal.ReadInt32(buffer, offset);
                            if (recordLength <= 0) break;

                            var frn = (ulong)Marshal.ReadInt64(buffer, offset + 8) & 0x0000FFFFFFFFFFFF;
                            var parentFrn = (ulong)Marshal.ReadInt64(buffer, offset + 16) & 0x0000FFFFFFFFFFFF;
                            var reason = (uint)Marshal.ReadInt32(buffer, offset + 40);
                            var fileAttributes = (uint)Marshal.ReadInt32(buffer, offset + 52);
                            var fileNameLength = (ushort)Marshal.ReadInt16(buffer, offset + 56);
                            var fileNameOffset = (ushort)Marshal.ReadInt16(buffer, offset + 58);

                            if (fileNameLength > 0 && offset + fileNameOffset + fileNameLength <= bytesReturned)
                            {
                                var fileName = Marshal.PtrToStringUni(
                                    IntPtr.Add(buffer, offset + fileNameOffset), fileNameLength / 2);

                                var isDir = (fileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                                var isDelete = (reason & (USN_REASON_FILE_DELETE | USN_REASON_RENAME_OLD_NAME)) != 0;
                                var isCreate = (reason & (USN_REASON_FILE_CREATE | USN_REASON_RENAME_NEW_NAME)) != 0;

                                if (isDir)
                                {
                                    if (isDelete)
                                        cache.Directories.Remove(frn);
                                    else if (isCreate)
                                    {
                                        if (!cache.Directories.TryGetValue(frn, out var existingDir) || fileName.Length > existingDir.NameLength)
                                            cache.Directories[frn] = new MftEntry { Name = fileName, ParentFrn = parentFrn, NameLength = fileName.Length };
                                    }
                                }
                                else
                                {
                                    if (isDelete)
                                    {
                                        cache.MatchedFiles.Remove(frn);
                                    }
                                    else if (isCreate)
                                    {
                                        var ext = GetExtension(fileName);
                                        if (ext != null && extSet.Contains(ext))
                                            cache.MatchedFiles[frn] = new MftEntry { Name = fileName, ParentFrn = parentFrn, NameLength = fileName.Length };
                                        else
                                            cache.MatchedFiles.Remove(frn); // 重命名为不匹配扩展名
                                    }
                                }
                            }

                            offset += recordLength;
                        }

                        if (readData.StartUsn >= journalData.NextUsn) break;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                cache.NextUsn = journalData.NextUsn;
                TrySaveCache(driveLetter, cache);
                return true;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static void EnumerateMft(char driveLetter, HashSet<string> extensions,
            Dictionary<ulong, MftEntry> directories, Dictionary<ulong, MftEntry> matchingFiles,
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
                                if (!directories.TryGetValue(frn, out var existing) || fileName.Length > existing.NameLength)
                                    directories[frn] = new MftEntry { Name = fileName, ParentFrn = parentFrn, NameLength = fileName.Length };
                            }
                            else
                            {
                                var ext = GetExtension(fileName);
                                if (ext != null && extensions.Contains(ext))
                                {
                                    if (!matchingFiles.TryGetValue(frn, out var existingFile) || fileName.Length > existingFile.NameLength)
                                        matchingFiles[frn] = new MftEntry { Name = fileName, ParentFrn = parentFrn, NameLength = fileName.Length };
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
                if (!visited.Add(current)) break;
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

            if (parts.Count == 0)
            {
                var root = driveLetter + ":\\";
                cache[frn] = root;
                return root;
            }

            if (visited.Contains(current))
            {
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
            if (string.IsNullOrWhiteSpace(path)) return null;

            var trimmed = path.Trim();

            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Za-z]:$"))
                return trimmed + IOPath.DirectorySeparatorChar;

            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Za-z]:[\\/]$"))
                return trimmed.Substring(0, 2) + IOPath.DirectorySeparatorChar;

            try
            {
                var full = IOPath.GetFullPath(trimmed);
                var root = IOPath.GetPathRoot(full);
                if (!string.IsNullOrWhiteSpace(root) && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                    return root;
                return full.TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
            }
            catch { return null; }
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
