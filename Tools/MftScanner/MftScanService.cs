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
    public sealed class MftScanService
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
            // key = FRN，存储全部文件（不按扩展名过滤），查询时再筛选
            public Dictionary<ulong, MftEntry> AllFiles { get; } = new Dictionary<ulong, MftEntry>();
            public Dictionary<ulong, string> DirectoryPathCache { get; } = new Dictionary<ulong, string>();
            public long NextUsn { get; set; }
            public ulong UsnJournalId { get; set; }
        }

        private sealed class SearchCandidate
        {
            public char DriveLetter { get; set; }
            public ulong FileFrn { get; set; }
            public ulong ParentFrn { get; set; }
            public string FileName { get; set; }
            public string FullPath { get; set; }
            public string RootPath { get; set; }
            public string RootDisplayName { get; set; }
            public bool IsDirectory { get; set; }
        }

        private sealed class SearchCandidateComparer : IComparer<SearchCandidate>
        {
            public static SearchCandidateComparer Instance { get; } = new SearchCandidateComparer();

            public int Compare(SearchCandidate x, SearchCandidate y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x == null) return -1;
                if (y == null) return 1;

                var result = StringComparer.OrdinalIgnoreCase.Compare(x.FileName, y.FileName);
                if (result != 0) return result;

                result = x.DriveLetter.CompareTo(y.DriveLetter);
                if (result != 0) return result;

                return x.FileFrn.CompareTo(y.FileFrn);
            }
        }

        // 魔数 + 版本，格式变更时递增版本号使旧缓存自动失效
        private const uint CacheMagic = 0x4D465443; // "MFTC"
        private const ushort CacheVersion = 3; // v3: 扩展名无关，存储全部文件

        private static readonly string CacheDir = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PackageManager");

        private readonly Dictionary<char, VolumeCache> _volumeCaches = new Dictionary<char, VolumeCache>();
        private static readonly char[] PathSearchSeparators = { '\\', '/', ':' };

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
                        cache.AllFiles[frn] = new MftEntry { Name = name, ParentFrn = parentFrn, NameLength = name.Length };
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

                    bw.Write(cache.AllFiles.Count);
                    foreach (var kv in cache.AllFiles)
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
                if (extensions != null)
                {
                    foreach (var ext in extensions)
                    {
                        var e = ext.Trim();
                        if (!e.StartsWith(".", StringComparison.Ordinal)) e = "." + e;
                        extSet.Add(e.ToLowerInvariant());
                    }
                }

                var volumeGroups = GroupRootsByVolume(roots);

                var results = new List<ScannedFileInfo>();

                foreach (var group in volumeGroups)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var driveLetter = group.Key;
                    var volumeRoots = group.Value;
                    var cache = GetOrRefreshVolumeCache(driveLetter, progress, cancellationToken);

                    progress?.Report($"正在解析路径... 共 {cache.AllFiles.Count} 个文件");

                    foreach (var kv in cache.AllFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var file = kv.Value;

                        // 查询时按扩展名过滤（extSet 为空则不过滤）
                        if (extSet.Count > 0)
                        {
                            var fileExt = GetExtension(file.Name);
                            if (fileExt == null || !extSet.Contains(fileExt)) continue;
                        }
                        var dirPath = ReconstructPath(file.ParentFrn, cache.Directories, driveLetter, cache.DirectoryPathCache);
                        if (dirPath == null) continue;
                        var fullPath = NormalizePath(Path.Combine(dirPath, file.Name));
                        if (fullPath == null) continue;

                        var matchedRoot = FindMatchedRoot(fullPath, volumeRoots);
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

        public Task<int> PrepareSearchIndexAsync(IReadOnlyList<ScanRoot> roots, IProgress<string> progress, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                var volumeGroups = GroupRootsByVolume(roots);
                var totalCount = 0;

                foreach (var group in volumeGroups)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var driveLetter = group.Key;
                    var volumeRoots = group.Value;
                    var cache = GetOrRefreshVolumeCache(driveLetter, progress, cancellationToken);
                    totalCount += CountEntriesUnderRoots(cache, driveLetter, volumeRoots, cancellationToken);
                }

                return totalCount;
            }, cancellationToken);
        }

        public Task<SearchQueryResult> SearchByKeywordAsync(IReadOnlyList<ScanRoot> roots, string keyword,
            int maxResults, IProgress<string> progress, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                var volumeGroups = GroupRootsByVolume(roots);
                var trimmedKeyword = (keyword ?? string.Empty).Trim();
                var kwLower = trimmedKeyword.ToLowerInvariant();
                var isPathQuery = trimmedKeyword.IndexOfAny(PathSearchSeparators) >= 0;

                var exactMatches = new SortedSet<SearchCandidate>(SearchCandidateComparer.Instance);
                var prefixMatches = new SortedSet<SearchCandidate>(SearchCandidateComparer.Instance);
                var containsMatches = new SortedSet<SearchCandidate>(SearchCandidateComparer.Instance);
                var pathMatches = new SortedSet<SearchCandidate>(SearchCandidateComparer.Instance);

                var totalIndexedCount = 0;
                var totalMatchedCount = 0;

                foreach (var group in volumeGroups)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var driveLetter = group.Key;
                    var volumeRoots = group.Value;
                    var wholeVolumeRoots = AreWholeVolumeRoots(volumeRoots, driveLetter);
                    var cache = GetOrRefreshVolumeCache(driveLetter, progress, cancellationToken);

                    totalIndexedCount += CountEntriesUnderRoots(cache, driveLetter, volumeRoots, cancellationToken);
                    progress?.Report($"正在搜索卷 {driveLetter}:...");

                    CollectMatchesForEntries(cache.AllFiles, isDirectory: false, driveLetter, volumeRoots, wholeVolumeRoots, cache,
                        trimmedKeyword, kwLower, isPathQuery, maxResults, cancellationToken,
                        exactMatches, prefixMatches, containsMatches, pathMatches, ref totalMatchedCount);

                    CollectMatchesForEntries(cache.Directories, isDirectory: true, driveLetter, volumeRoots, wholeVolumeRoots, cache,
                        trimmedKeyword, kwLower, isPathQuery, maxResults, cancellationToken,
                        exactMatches, prefixMatches, containsMatches, pathMatches, ref totalMatchedCount);
                }

                var candidates = exactMatches
                    .Concat(prefixMatches)
                    .Concat(containsMatches)
                    .Concat(pathMatches)
                    .Take(maxResults)
                    .ToList();

                var results = new List<ScannedFileInfo>(candidates.Count);
                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_volumeCaches.TryGetValue(candidate.DriveLetter, out var cache)) continue;

                    var info = CreateScannedFileInfo(candidate, cache);
                    if (info != null)
                        results.Add(info);
                }

                return new SearchQueryResult
                {
                    TotalIndexedCount = totalIndexedCount,
                    TotalMatchedCount = totalMatchedCount,
                    IsTruncated = totalMatchedCount > results.Count,
                    Results = results
                };
            }, cancellationToken);
        }

        private static Dictionary<char, List<ScanRoot>> GroupRootsByVolume(IReadOnlyList<ScanRoot> roots)
        {
            var normalizedRoots = (roots ?? Array.Empty<ScanRoot>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Path))
                .Select(r => new ScanRoot
                {
                    Path = NormalizePath(r.Path),
                    DisplayName = r.DisplayName
                })
                .Where(r => !string.IsNullOrWhiteSpace(r.Path) && r.Path.Length >= 2 && r.Path[1] == ':')
                .GroupBy(r => char.ToUpperInvariant(r.Path[0]))
                .ToDictionary(g => g.Key, g => g.ToList());

            return normalizedRoots;
        }

        private VolumeCache GetOrRefreshVolumeCache(char driveLetter, IProgress<string> progress, CancellationToken cancellationToken)
        {
            if (!_volumeCaches.ContainsKey(driveLetter))
                TryLoadCache(driveLetter);

            if (_volumeCaches.TryGetValue(driveLetter, out var existingCache))
            {
                progress?.Report($"正在通过 USN 日志增量更新卷 {driveLetter}:...");
                if (TryApplyUsnDelta(driveLetter, existingCache, cancellationToken))
                    return existingCache;

                progress?.Report($"USN 日志已失效，正在重新全量扫描卷 {driveLetter}:...");
            }
            else
            {
                progress?.Report($"正在通过 MFT 全量扫描卷 {driveLetter}:...");
            }

            var rebuiltCache = FullScanVolume(driveLetter, cancellationToken);
            _volumeCaches[driveLetter] = rebuiltCache;
            TrySaveCache(driveLetter, rebuiltCache);
            return rebuiltCache;
        }

        private static bool AreWholeVolumeRoots(IReadOnlyList<ScanRoot> roots, char driveLetter)
        {
            if (roots == null || roots.Count == 0) return false;
            var expectedRoot = $"{char.ToUpperInvariant(driveLetter)}:\\";
            return roots.All(r => string.Equals(r.Path, expectedRoot, StringComparison.OrdinalIgnoreCase));
        }

        private static void CollectMatchesForEntries(
            IEnumerable<KeyValuePair<ulong, MftEntry>> entries,
            bool isDirectory,
            char driveLetter,
            IReadOnlyList<ScanRoot> volumeRoots,
            bool wholeVolumeRoots,
            VolumeCache cache,
            string trimmedKeyword,
            string kwLower,
            bool isPathQuery,
            int maxResults,
            CancellationToken cancellationToken,
            SortedSet<SearchCandidate> exactMatches,
            SortedSet<SearchCandidate> prefixMatches,
            SortedSet<SearchCandidate> containsMatches,
            SortedSet<SearchCandidate> pathMatches,
            ref int totalMatchedCount)
        {
            foreach (var kv in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = kv.Value;
                var fileName = entry.Name ?? string.Empty;

                SearchCandidate candidate = null;
                var nameMatched = fileName.IndexOf(trimmedKeyword, StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isPathQuery)
                {
                    if (!nameMatched) continue;
                    candidate = CreateSearchCandidate(driveLetter, kv.Key, entry, isDirectory, cache, volumeRoots, requireFullPath: !wholeVolumeRoots);
                    if (candidate == null) continue;
                }
                else
                {
                    candidate = CreateSearchCandidate(driveLetter, kv.Key, entry, isDirectory, cache, volumeRoots, requireFullPath: true);
                    if (candidate == null) continue;

                    var pathMatched = candidate.FullPath.IndexOf(trimmedKeyword, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!nameMatched && !pathMatched) continue;
                }

                totalMatchedCount++;

                if (nameMatched)
                {
                    var normalizedName = fileName.ToLowerInvariant();
                    if (normalizedName == kwLower)
                    {
                        AddBoundedCandidate(exactMatches, candidate, maxResults);
                    }
                    else if (normalizedName.StartsWith(kwLower, StringComparison.Ordinal))
                    {
                        AddBoundedCandidate(prefixMatches, candidate, maxResults);
                    }
                    else
                    {
                        AddBoundedCandidate(containsMatches, candidate, maxResults);
                    }
                }
                else
                {
                    AddBoundedCandidate(pathMatches, candidate, maxResults);
                }
            }
        }

        private static int CountEntriesUnderRoots(VolumeCache cache, char driveLetter, IReadOnlyList<ScanRoot> volumeRoots, CancellationToken cancellationToken)
        {
            if (AreWholeVolumeRoots(volumeRoots, driveLetter))
                return cache.AllFiles.Count + cache.Directories.Count;

            var count = 0;
            foreach (var kv in cache.AllFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dirPath = ReconstructPath(kv.Value.ParentFrn, cache.Directories, driveLetter, cache.DirectoryPathCache);
                if (dirPath == null) continue;

                var fullPath = NormalizePath(Path.Combine(dirPath, kv.Value.Name));
                if (fullPath == null) continue;

                if (FindMatchedRoot(fullPath, volumeRoots) != null)
                    count++;
            }

            foreach (var kv in cache.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dirPath = ReconstructPath(kv.Value.ParentFrn, cache.Directories, driveLetter, cache.DirectoryPathCache);
                if (dirPath == null) continue;

                var fullPath = NormalizePath(Path.Combine(dirPath, kv.Value.Name));
                if (fullPath == null) continue;

                if (FindMatchedRoot(fullPath, volumeRoots) != null)
                    count++;
            }

            return count;
        }

        private static ScanRoot FindMatchedRoot(string fullPath, IReadOnlyList<ScanRoot> volumeRoots)
        {
            foreach (var root in volumeRoots)
            {
                if (IsPathUnderRoot(fullPath, root.Path))
                    return root;
            }

            return null;
        }

        private static void AddBoundedCandidate(SortedSet<SearchCandidate> bucket, SearchCandidate candidate, int maxResults)
        {
            bucket.Add(candidate);
            if (bucket.Count > maxResults)
                bucket.Remove(bucket.Max);
        }

        private static SearchCandidate CreateSearchCandidate(char driveLetter, ulong fileFrn, MftEntry file,
            bool isDirectory, VolumeCache cache, IReadOnlyList<ScanRoot> volumeRoots, bool requireFullPath)
        {
            if (volumeRoots == null || volumeRoots.Count == 0) return null;

            var wholeVolumeRoots = AreWholeVolumeRoots(volumeRoots, driveLetter);
            if (!requireFullPath && wholeVolumeRoots)
            {
                var root = volumeRoots[0];
                return new SearchCandidate
                {
                    DriveLetter = driveLetter,
                    FileFrn = fileFrn,
                    ParentFrn = file.ParentFrn,
                    FileName = file.Name,
                    RootPath = root.Path,
                    RootDisplayName = root.DisplayName,
                    IsDirectory = isDirectory
                };
            }

            var dirPath = ReconstructPath(file.ParentFrn, cache.Directories, driveLetter, cache.DirectoryPathCache);
            if (dirPath == null) return null;

            var fullPath = NormalizePath(Path.Combine(dirPath, file.Name));
            if (fullPath == null) return null;

            var matchedRoot = wholeVolumeRoots ? volumeRoots[0] : FindMatchedRoot(fullPath, volumeRoots);
            if (matchedRoot == null) return null;

            return new SearchCandidate
            {
                DriveLetter = driveLetter,
                FileFrn = fileFrn,
                ParentFrn = file.ParentFrn,
                FileName = file.Name,
                FullPath = fullPath,
                RootPath = matchedRoot.Path,
                RootDisplayName = matchedRoot.DisplayName,
                IsDirectory = isDirectory
            };
        }

        private static ScannedFileInfo CreateScannedFileInfo(SearchCandidate candidate, VolumeCache cache)
        {
            var fullPath = candidate.FullPath;
            if (fullPath == null)
            {
                var dirPath = ReconstructPath(candidate.ParentFrn, cache.Directories, candidate.DriveLetter, cache.DirectoryPathCache);
                if (dirPath == null) return null;
                fullPath = NormalizePath(Path.Combine(dirPath, candidate.FileName));
                if (fullPath == null) return null;
            }

            try
            {
                if (candidate.IsDirectory)
                {
                    var di = new DirectoryInfo(fullPath);
                    if (!di.Exists) return null;

                    return new ScannedFileInfo
                    {
                        FullPath = fullPath,
                        FileName = di.Name,
                        SizeBytes = 0,
                        ModifiedTimeUtc = di.LastWriteTimeUtc,
                        RootPath = candidate.RootPath,
                        RootDisplayName = candidate.RootDisplayName,
                        IsDirectory = true
                    };
                }

                var fi = new FileInfo(fullPath);
                if (!fi.Exists) return null;

                return new ScannedFileInfo
                {
                    FullPath = fullPath,
                    FileName = fi.Name,
                    SizeBytes = fi.Length,
                    ModifiedTimeUtc = fi.LastWriteTimeUtc,
                    RootPath = candidate.RootPath,
                    RootDisplayName = candidate.RootDisplayName,
                    IsDirectory = false
                };
            }
            catch
            {
                return null;
            }
        }

        private VolumeCache FullScanVolume(char driveLetter, CancellationToken ct)
        {
            var cache = new VolumeCache();
            EnumerateMft(driveLetter, cache.Directories, cache.AllFiles, ct);

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

        private bool TryApplyUsnDelta(char driveLetter, VolumeCache cache, CancellationToken ct)
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
                                        cache.AllFiles.Remove(frn);
                                    else if (isCreate)
                                    {
                                        if (!cache.AllFiles.TryGetValue(frn, out var existingFile) || fileName.Length > existingFile.NameLength)
                                            cache.AllFiles[frn] = new MftEntry { Name = fileName, ParentFrn = parentFrn, NameLength = fileName.Length };
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

        private static void EnumerateMft(char driveLetter,
            Dictionary<ulong, MftEntry> directories, Dictionary<ulong, MftEntry> allFiles,
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
                                if (!allFiles.TryGetValue(frn, out var existingFile) || fileName.Length > existingFile.NameLength)
                                    allFiles[frn] = new MftEntry { Name = fileName, ParentFrn = parentFrn, NameLength = fileName.Length };
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
        public bool IsDirectory { get; set; }
    }

    public sealed class SearchQueryResult
    {
        public int TotalIndexedCount { get; set; }
        public int TotalMatchedCount { get; set; }
        public bool IsTruncated { get; set; }
        public List<ScannedFileInfo> Results { get; set; } = new List<ScannedFileInfo>();
    }

    public sealed class ScanRoot
    {
        public string Path { get; set; }
        public string DisplayName { get; set; }
    }
}
