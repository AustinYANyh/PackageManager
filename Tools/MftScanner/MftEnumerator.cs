using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace MftScanner
{
    /// <summary>
    /// 通过 NTFS MFT（FSCTL_ENUM_USN_DATA）枚举卷上所有文件和目录的原始记录。
    /// 枚举完成后保留 FRN→(name, parentFrn) 字典，供搜索时按需解析完整路径。
    /// </summary>
    public sealed class MftEnumerator
    {
        // ── Win32 常量 ──────────────────────────────────────────────────────────
        private const uint GENERIC_READ = 0x80000000;

        private const uint FILE_SHARE_READ = 1;

        private const uint FILE_SHARE_WRITE = 2;

        private const uint FILE_SHARE_DELETE = 4;

        private const uint OPEN_EXISTING = 3;

        private const uint FSCTL_ENUM_USN_DATA = 0x000900B3;

        private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;

        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        // ── P/Invoke 声明 ───────────────────────────────────────────────────────
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string lpFileName,
                                                uint dwDesiredAccess,
                                                uint dwShareMode,
                                                IntPtr lpSecurityAttributes,
                                                uint dwCreationDisposition,
                                                uint dwFlagsAndAttributes,
                                                IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr hDevice,
                                                   uint dwIoControlCode,
                                                   ref MftEnumDataV0 lpInBuffer,
                                                   int nInBufferSize,
                                                   IntPtr lpOutBuffer,
                                                   int nOutBufferSize,
                                                   out int lpBytesReturned,
                                                   IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        private static extern bool DeviceIoControlQueryUsn(IntPtr hDevice,
                                                           uint dwIoControlCode,
                                                           IntPtr lpInBuffer,
                                                           int nInBufferSize,
                                                           out UsnJournalData lpOutBuffer,
                                                           int nOutBufferSize,
                                                           out int lpBytesReturned,
                                                           IntPtr lpOverlapped);

        // ── 内部结构 ────────────────────────────────────────────────────────────
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

        // ── FRN 字典（按卷存储，供路径解析使用）──────────────────────────────────
        // key = driveLetter(upper), value = FRN → (name, parentFrn)
        private readonly Dictionary<char, Dictionary<ulong, (string name, ulong parentFrn, bool isDirectory)>> _frnMaps
            = new Dictionary<char, Dictionary<ulong, (string name, ulong parentFrn, bool isDirectory)>>();

        private readonly Dictionary<char, Dictionary<ulong, string>> _pathCaches
            = new Dictionary<char, Dictionary<ulong, string>>();
        private readonly object _mapsLock = new object();

        // ── 公开 API ────────────────────────────────────────────────────────────

        /// <summary>
        /// 枚举卷上所有 MFT 记录，直接构建 FileRecord 写入 output（省去中间 entries 层），
        /// 同时保留 FRN 字典供路径解析，返回 USN 游标。
        /// </summary>
        public (int count, long nextUsn, ulong journalId) EnumerateVolumeIntoRecords(
            char driveLetter, List<FileRecord> output, CancellationToken ct)
        {
            var dl = char.ToUpperInvariant(driveLetter);
            var volumePath = @"\\.\" + dl + ":";
            var handle = CreateFile(volumePath,
                                    GENERIC_READ,
                                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                    IntPtr.Zero,
                                    OPEN_EXISTING,
                                    0,
                                    IntPtr.Zero);

            if (handle == INVALID_HANDLE_VALUE)
                throw new InvalidOperationException($"无法打开卷 {volumePath}，错误码={Marshal.GetLastWin32Error()}");

            const int bufferSize = 1024 * 1024; // 1MB，进一步减少 DeviceIoControl 调用次数
            var buffer  = Marshal.AllocHGlobal(bufferSize);
            var frnMap  = new Dictionary<ulong, (string name, ulong parentFrn, bool isDirectory)>(300_000);
            long nextUsn  = 0;
            ulong journalId = 0;
            var startCount = output.Count;

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
                    ct.ThrowIfCancellationRequested();

                    if (!DeviceIoControl(handle,
                                         FSCTL_ENUM_USN_DATA,
                                         ref enumData,
                                         Marshal.SizeOf(typeof(MftEnumDataV0)),
                                         buffer,
                                         bufferSize,
                                         out var bytesReturned,
                                         IntPtr.Zero))
                        break;
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
                            var fileName = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + fileNameOffset), fileNameLength / 2);
                            var isDir = (fileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;

                            // 同一 FRN 可能有多条记录（短文件名 8.3 等），保留最长文件名（长文件名）
                            if (!frnMap.TryGetValue(frn, out var existing) || fileName.Length > existing.name.Length)
                            {
                                frnMap[frn] = (fileName, parentFrn, isDir);
                            }
                        }

                        offset += recordLength;
                    }
                }

                if (DeviceIoControlQueryUsn(handle,
                                            FSCTL_QUERY_USN_JOURNAL,
                                            IntPtr.Zero,
                                            0,
                                            out var journalData,
                                            Marshal.SizeOf(typeof(UsnJournalData)),
                                            out _,
                                            IntPtr.Zero))
                {
                    nextUsn = journalData.NextUsn;
                    journalId = journalData.UsnJournalID;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                CloseHandle(handle);
            }

            // 第二遍：从去重后的 frnMap 构建 FileRecord
            foreach (var kv in frnMap)
            {
                var (name, parentFrn, isDir) = kv.Value;
                output.Add(new FileRecord(
                    lowerName:    name.ToLowerInvariant(),
                    originalName: name,
                    parentFrn:    parentFrn,
                    driveLetter:  dl,
                    isDirectory:  isDir));
            }

            lock (_mapsLock)
            {
                _frnMaps[dl] = frnMap;
                _pathCaches[dl] = new Dictionary<ulong, string>();
            }

            return (output.Count - startCount, nextUsn, journalId);
        }

        /// <summary>
        /// 将另一个 MftEnumerator 实例的 FRN 字典合并到本实例（用于多线程并行枚举后合并）。
        /// </summary>
        public void MergeFrnMap(char driveLetter, MftEnumerator source)
        {
            var dl = char.ToUpperInvariant(driveLetter);
            lock (source._mapsLock)
            {
                if (!source._frnMaps.TryGetValue(dl, out var srcMap))
                    return;

                lock (_mapsLock)
                {
                    _frnMaps[dl] = srcMap;
                    _pathCaches[dl] = new Dictionary<ulong, string>();
                }
            }
        }

        public VolumeSnapshot[] CreateVolumeSnapshots((char driveLetter, long nextUsn, ulong journalId)[] checkpoints)
        {
            if (checkpoints == null || checkpoints.Length == 0)
                return Array.Empty<VolumeSnapshot>();

            var snapshots = new VolumeSnapshot[checkpoints.Length];
            lock (_mapsLock)
            {
                for (var i = 0; i < checkpoints.Length; i++)
                {
                    var checkpoint = checkpoints[i];
                    var dl = char.ToUpperInvariant(checkpoint.driveLetter);
                    if (!_frnMaps.TryGetValue(dl, out var frnMap))
                    {
                        snapshots[i] = new VolumeSnapshot(dl, checkpoint.nextUsn, checkpoint.journalId, Array.Empty<FrnSnapshotEntry>());
                        continue;
                    }

                    var entries = new FrnSnapshotEntry[frnMap.Count];
                    var index = 0;
                    foreach (var kv in frnMap)
                    {
                        entries[index++] = new FrnSnapshotEntry(
                            kv.Key,
                            kv.Value.name,
                            kv.Value.parentFrn,
                            kv.Value.isDirectory);
                    }

                    snapshots[i] = new VolumeSnapshot(dl, checkpoint.nextUsn, checkpoint.journalId, entries);
                }
            }

            return snapshots;
        }

        public void LoadVolumeSnapshots(IReadOnlyList<VolumeSnapshot> snapshots)
        {
            lock (_mapsLock)
            {
                _frnMaps.Clear();
                _pathCaches.Clear();

                if (snapshots == null)
                    return;

                for (var i = 0; i < snapshots.Count; i++)
                {
                    var snapshot = snapshots[i];
                    var dl = char.ToUpperInvariant(snapshot.DriveLetter);
                    var map = new Dictionary<ulong, (string name, ulong parentFrn, bool isDirectory)>(
                        Math.Max(snapshot.FrnEntries?.Length ?? 0, 0));

                    if (snapshot.FrnEntries != null)
                    {
                        for (var j = 0; j < snapshot.FrnEntries.Length; j++)
                        {
                            var entry = snapshot.FrnEntries[j];
                            map[entry.Frn] = (entry.Name, entry.ParentFrn, entry.IsDirectory);
                        }
                    }

                    _frnMaps[dl] = map;
                    _pathCaches[dl] = new Dictionary<ulong, string>();
                }
            }
        }

        /// <summary>
        /// USN 事件新增文件时调用，将 frn→(name, parentFrn) 写入字典，
        /// 确保后续 ResolveFullPath 能正确解析该文件及其子文件的路径。
        /// </summary>
        public void RegisterFrn(char driveLetter, ulong frn, string fileName, ulong parentFrn, bool isDirectory)
        {
            var dl = char.ToUpperInvariant(driveLetter);
            lock (_mapsLock)
            {
                if (!_frnMaps.TryGetValue(dl, out var frnMap))
                    return; // 该卷尚未枚举，忽略
                frnMap[frn] = (fileName, parentFrn, isDirectory);
                if (_pathCaches.TryGetValue(dl, out var pathCache))
                    pathCache.Remove(frn);
            }
        }

        /// <summary>
        /// USN 事件删除文件时调用，从字典中移除对应 FRN。
        /// </summary>
        public void UnregisterFrn(char driveLetter, ulong frn)
        {
            var dl = char.ToUpperInvariant(driveLetter);
            lock (_mapsLock)
            {
                if (_frnMaps.TryGetValue(dl, out var frnMap))
                    frnMap.Remove(frn);
                if (_pathCaches.TryGetValue(dl, out var pathCache))
                    pathCache.Remove(frn);
            }
        }

        public void ApplyUsnChanges(IReadOnlyList<UsnChangeEntry> changes)
        {
            if (changes == null || changes.Count == 0)
                return;

            lock (_mapsLock)
            {
                for (var i = 0; i < changes.Count; i++)
                {
                    var change = changes[i];
                    var dl = char.ToUpperInvariant(change.DriveLetter);
                    if (!_frnMaps.TryGetValue(dl, out var frnMap))
                        continue;

                    if (!_pathCaches.TryGetValue(dl, out var pathCache))
                        pathCache = _pathCaches[dl] = new Dictionary<ulong, string>();

                    switch (change.Kind)
                    {
                        case UsnChangeKind.Create:
                        case UsnChangeKind.Rename:
                            frnMap[change.Frn] = (change.OriginalName, change.ParentFrn, change.IsDirectory);
                            pathCache.Remove(change.Frn);
                            break;
                        case UsnChangeKind.Delete:
                            frnMap.Remove(change.Frn);
                            pathCache.Remove(change.Frn);
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// 按需解析完整路径（带缓存）。搜索结果展示时调用，不在构建阶段调用。
        /// </summary>
        public string ResolveFullPath(char driveLetter, ulong parentFrn, string fileName)
        {
            var dl = char.ToUpperInvariant(driveLetter);
            lock (_mapsLock)
            {
                if (!_frnMaps.TryGetValue(dl, out var frnMap))
                    return dl + ":\\" + fileName;

                if (!_pathCaches.TryGetValue(dl, out var pathCache))
                    pathCache = _pathCaches[dl] = new Dictionary<ulong, string>();

                var dirPath = ResolveDir(parentFrn, dl, frnMap, pathCache);
                return dirPath + "\\" + fileName;
            }
        }

        private static string ResolveDir(ulong dirFrn,
                                         char dl,
                                         Dictionary<ulong, (string name, ulong parentFrn, bool isDirectory)> frnMap,
                                         Dictionary<ulong, string> pathCache)
        {
            if (pathCache.TryGetValue(dirFrn, out var hit)) return hit;

            var chain = new List<ulong>(8) { dirFrn };
            var current = dirFrn;

            while (true)
            {
                if (chain.Count > 64) return dl + ":";

                if (!frnMap.TryGetValue(current, out var entry))
                {
                    var root = dl + ":";
                    BackfillPathCache(chain, root, frnMap, pathCache);
                    return pathCache[dirFrn];
                }

                if (entry.parentFrn == current)
                {
                    var root = dl + ":";
                    pathCache[current] = root;
                    if (chain.Count > 1) BackfillPathCache(chain, root, frnMap, pathCache);
                    return pathCache[dirFrn];
                }

                var parentFrn = entry.parentFrn;
                if (pathCache.TryGetValue(parentFrn, out var cached))
                {
                    BackfillPathCache(chain, cached, frnMap, pathCache);
                    return pathCache[dirFrn];
                }

                chain.Add(parentFrn);
                current = parentFrn;
            }
        }

        private static void BackfillPathCache(List<ulong> chain,
                                              string anchorPath,
                                              Dictionary<ulong, (string name, ulong parentFrn, bool isDirectory)> frnMap,
                                              Dictionary<ulong, string> pathCache)
        {
            var path = anchorPath;
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var frn = chain[i];
                if (frnMap.TryGetValue(frn, out var e))
                    path = path + "\\" + e.name;
                pathCache[frn] = path;
            }
        }

        /// <summary>
        /// MFT 枚举返回的原始条目，包含文件引用号、父目录引用号、文件名和属性。
        /// </summary>
        public struct RawMftEntry
        {
            /// <summary>文件引用号（File Reference Number）。</summary>
            public ulong Frn;

            /// <summary>父目录的文件引用号。</summary>
            public ulong ParentFrn;

            /// <summary>文件或目录名称（原始大小写）。</summary>
            public string FileName;

            /// <summary>Win32 文件属性标志（FILE_ATTRIBUTE_*）。</summary>
            public uint FileAttributes;

            /// <summary>是否为目录。</summary>
            public bool IsDirectory;
        }
    }
}
