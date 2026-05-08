using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MftScanner
{
    public enum UsnChangeKind
    {
        Create,
        Delete,
        Rename
    }

    public sealed class UsnChangeEntry
    {
        public UsnChangeEntry(
            UsnChangeKind kind,
            ulong frn,
            string lowerName,
            string originalName,
            ulong parentFrn,
            char driveLetter,
            bool isDirectory,
            string oldLowerName = null,
            ulong oldParentFrn = 0)
        {
            Kind = kind;
            Frn = frn;
            LowerName = lowerName;
            OriginalName = originalName;
            ParentFrn = parentFrn;
            DriveLetter = driveLetter;
            IsDirectory = isDirectory;
            OldLowerName = oldLowerName;
            OldParentFrn = oldParentFrn;
        }

        public UsnChangeKind Kind { get; }
        public ulong Frn { get; }
        public string LowerName { get; }
        public string OriginalName { get; }
        public ulong ParentFrn { get; }
        public char DriveLetter { get; }
        public bool IsDirectory { get; }
        public string OldLowerName { get; }
        public ulong OldParentFrn { get; }

        public FileRecord ToRecord()
        {
            return new FileRecord(LowerName, OriginalName, ParentFrn, DriveLetter, IsDirectory, Frn);
        }
    }

    // ── 事件参数 ────────────────────────────────────────────────────────────────

    /// <summary>文件创建事件参数。需求 6.2</summary>
    public sealed class UsnFileCreatedEventArgs : EventArgs
    {
        public UsnFileCreatedEventArgs(ulong frn, string fileName, ulong parentFrn, char driveLetter, bool isDirectory)
        {
            Frn         = frn;
            FileName    = fileName;
            ParentFrn   = parentFrn;
            DriveLetter = driveLetter;
            IsDirectory = isDirectory;
        }
        public ulong  Frn         { get; }
        public string FileName    { get; }
        public ulong  ParentFrn   { get; }
        public char   DriveLetter { get; }
        public bool   IsDirectory { get; }
    }

    /// <summary>文件删除事件参数。需求 6.3</summary>
    public sealed class UsnFileDeletedEventArgs : EventArgs
    {
        public UsnFileDeletedEventArgs(ulong frn, string lowerName, ulong parentFrn, char driveLetter)
        {
            Frn         = frn;
            LowerName   = lowerName;
            ParentFrn   = parentFrn;
            DriveLetter = driveLetter;
        }
        public ulong  Frn         { get; }
        public string LowerName   { get; }
        public ulong  ParentFrn   { get; }
        public char   DriveLetter { get; }
    }

    public sealed class UsnChangesCollectedEventArgs : EventArgs
    {
        public UsnChangesCollectedEventArgs(char driveLetter, List<UsnChangeEntry> changes)
        {
            DriveLetter = driveLetter;
            Changes = changes ?? new List<UsnChangeEntry>();
        }

        public char DriveLetter { get; }
        public List<UsnChangeEntry> Changes { get; }
    }

    /// <summary>文件重命名事件参数。需求 6.4</summary>
    public sealed class UsnFileRenamedEventArgs : EventArgs
    {
        public UsnFileRenamedEventArgs(string oldLowerName, ulong oldParentFrn, ulong newFrn, char driveLetter, FileRecord newRecord)
        {
            OldLowerName = oldLowerName;
            OldParentFrn = oldParentFrn;
            NewFrn       = newFrn;
            DriveLetter  = driveLetter;
            NewRecord    = newRecord;
        }
        public string     OldLowerName { get; }
        public ulong      OldParentFrn { get; }
        public ulong      NewFrn       { get; }
        public char       DriveLetter  { get; }
        public FileRecord NewRecord    { get; }
    }

    // ── UsnWatcher ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 通过 NTFS USN 日志（FSCTL_READ_USN_JOURNAL）监听文件系统增量变更，
    /// 并通过事件通知 <see cref="IndexService"/> 执行 <see cref="MemoryIndex"/> 增量更新。
    /// 需求：6.1、6.2、6.3、6.4、6.5、8.3
    /// </summary>
    public sealed class UsnWatcher
    {
        private static readonly bool LogUsnRecords = false;
        private static readonly TimeSpan WatcherPollLogInterval = TimeSpan.FromSeconds(30);
        private const int ReadUsnBufferSize = 4 * 1024 * 1024;
        private const int MaxWatcherReadRecordsPerSlice = 20000;
        private const int MaxWatcherReadSliceMilliseconds = 200;

        // ── Win32 常量 ──────────────────────────────────────────────────────────
        private const uint GENERIC_READ              = 0x80000000;
        private const uint FILE_SHARE_READ           = 1;
        private const uint FILE_SHARE_WRITE          = 2;
        private const uint FILE_SHARE_DELETE         = 4;
        private const uint OPEN_EXISTING             = 3;
        private const uint FSCTL_QUERY_USN_JOURNAL   = 0x000900F4;
        private const uint FSCTL_READ_USN_JOURNAL    = 0x000900BB;
        private const uint FILE_ATTRIBUTE_DIRECTORY  = 0x10;
        private const uint USN_REASON_FILE_CREATE    = 0x00000100;
        private const uint USN_REASON_FILE_DELETE    = 0x00000200;
        private const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;
        private const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;
        private const uint USN_REASON_CLOSE          = 0x80000000;
        private const int  ERROR_JOURNAL_ENTRY_DELETED = 1181;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        // ── P/Invoke 声明 ───────────────────────────────────────────────────────
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        private static extern bool DeviceIoControlQueryUsn(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            int nInBufferSize,
            out UsnJournalData lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        private static extern bool DeviceIoControlReadUsn(
            IntPtr hDevice,
            uint dwIoControlCode,
            ref ReadUsnJournalData lpInBuffer,
            int nInBufferSize,
            IntPtr lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // ── 内部结构 ────────────────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct UsnJournalData
        {
            public ulong UsnJournalID;
            public long  FirstUsn;
            public long  NextUsn;
            public long  LowestValidUsn;
            public long  MaxUsn;
            public ulong MaximumSize;
            public ulong AllocationDelta;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ReadUsnJournalData
        {
            public long  StartUsn;
            public uint  ReasonMask;
            public uint  ReturnOnlyOnClose;
            public ulong Timeout;
            public ulong BytesToWaitFor;
            public ulong UsnJournalID;
        }

        // ── 状态（每卷独立）────────────────────────────────────────────────────
        private sealed class VolumeWatchState
        {
            public char   DriveLetter;
            public long   NextUsn;
            public ulong  JournalId;
            public CancellationTokenSource Cts;
            public Task   WatchTask;
            public DateTime LastPollLogUtc = DateTime.MinValue;
            public long LastLoggedNextUsn = long.MinValue;
            public long LastLoggedJournalNextUsn = long.MinValue;
            public bool HasBacklog;
            public DateTime LastActivityUtc = DateTime.MinValue;
            public Dictionary<ulong, (string oldName, ulong oldParentFrn)> PendingRenameOldByFrn
                = new Dictionary<ulong, (string oldName, ulong oldParentFrn)>();
            public Dictionary<ulong, UsnChangeEntry> PendingProvisionalByFrn
                = new Dictionary<ulong, UsnChangeEntry>();
        }

        private readonly Dictionary<char, VolumeWatchState> _volumes
            = new Dictionary<char, VolumeWatchState>();
        private readonly object _volumesLock = new object();

        // ── 公开事件 ────────────────────────────────────────────────────────────

        /// <summary>文件或目录被创建时触发。需求 6.2</summary>
        public event EventHandler<UsnFileCreatedEventArgs> FileCreated;

        /// <summary>文件或目录被删除时触发。需求 6.3</summary>
        public event EventHandler<UsnFileDeletedEventArgs> FileDeleted;

        /// <summary>文件或目录被重命名时触发。需求 6.4</summary>
        public event EventHandler<UsnFileRenamedEventArgs> FileRenamed;

        public event EventHandler<UsnChangesCollectedEventArgs> ChangesCollected;

        /// <summary>USN 日志溢出或失效时触发，订阅者应触发全量重建。需求 6.5</summary>
        public event EventHandler JournalOverflow;

        // ── 公开 API ────────────────────────────────────────────────────────────

        /// <summary>
        /// 启动后台 USN 日志监听循环。
        /// 调用前应先完成 <see cref="IndexService.BuildIndexAsync"/>，
        /// 并通过 <paramref name="startUsn"/> 和 <paramref name="journalId"/> 传入初始游标。
        /// 需求 6.1
        /// </summary>
        /// <param name="driveLetter">要监听的盘符，如 'C'。</param>
        /// <param name="startUsn">初始 USN 游标（通常来自 MFT 全量扫描后查询到的 NextUsn）。</param>
        /// <param name="journalId">USN 日志 ID（用于检测日志重建）。</param>
        /// <param name="ct">外部取消令牌。</param>
        public void StartWatching(char driveLetter, long startUsn, ulong journalId, CancellationToken ct)
        {
            var dl = char.ToUpperInvariant(driveLetter);

            // 停止该卷已有的 watcher
            lock (_volumesLock)
            {
                if (_volumes.TryGetValue(dl, out var old))
                {
                    old.Cts?.Cancel();
                    _volumes.Remove(dl);
                }
            }

            var state = new VolumeWatchState
            {
                DriveLetter = dl,
                NextUsn     = startUsn,
                JournalId   = journalId,
                Cts         = CancellationTokenSource.CreateLinkedTokenSource(ct)
            };

            UsnDiagLog.Write($"[WATCHER START] drive={dl} startUsn={startUsn} journalId={journalId}");

            state.WatchTask = Task.Run(() => WatchLoop(state), state.Cts.Token);

            lock (_volumesLock)
                _volumes[dl] = state;
        }

        public void StopWatching()
        {
            List<VolumeWatchState> all;
            lock (_volumesLock)
            {
                all = new List<VolumeWatchState>(_volumes.Values);
                _volumes.Clear();
            }
            foreach (var s in all)
            {
                s.Cts?.Cancel();
                try { s.WatchTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
                s.Cts?.Dispose();
            }
        }

        public (char driveLetter, long nextUsn, ulong journalId)[] GetVolumeCheckpoints()
        {
            lock (_volumesLock)
            {
                var checkpoints = new (char driveLetter, long nextUsn, ulong journalId)[_volumes.Count];
                var index = 0;
                foreach (var item in _volumes.Values)
                {
                    checkpoints[index++] = (item.DriveLetter, item.NextUsn, item.JournalId);
                }

                return checkpoints;
            }
        }

        public bool TryCatchUp(char driveLetter, long startUsn, ulong journalId, CancellationToken ct,
            out long nextUsn, out ulong latestJournalId)
        {
            var dl = char.ToUpperInvariant(driveLetter);
            nextUsn = startUsn;
            latestJournalId = journalId;

            var volumePath = @"\\.\" + dl + ":";
            var handle = CreateFile(volumePath, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle == INVALID_HANDLE_VALUE)
                return false;

            const int bufferSize = ReadUsnBufferSize;
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (!DeviceIoControlQueryUsn(handle, FSCTL_QUERY_USN_JOURNAL,
                    IntPtr.Zero, 0, out var journalData,
                    Marshal.SizeOf(typeof(UsnJournalData)), out _, IntPtr.Zero))
                    return false;

                latestJournalId = journalData.UsnJournalID;

                if (journalData.UsnJournalID != journalId || startUsn < journalData.LowestValidUsn)
                    return false;

                if (startUsn >= journalData.NextUsn)
                {
                    nextUsn = journalData.NextUsn;
                    return true;
                }

                var state = new VolumeWatchState
                {
                    DriveLetter = dl,
                    NextUsn = startUsn,
                    JournalId = journalId
                };

                if (!ReadUsnBatch(state, handle, ref journalData, buffer, bufferSize, ct,
                        raiseOverflowEvent: false, collectedChanges: null))
                    return false;
                nextUsn = state.NextUsn;
                latestJournalId = state.JournalId;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                CloseHandle(handle);
            }
        }

        public bool TryCollectCatchUpChanges(char driveLetter, long startUsn, ulong journalId, CancellationToken ct,
            out List<UsnChangeEntry> changes, out long nextUsn, out ulong latestJournalId)
        {
            changes = new List<UsnChangeEntry>(4096);

            var dl = char.ToUpperInvariant(driveLetter);
            nextUsn = startUsn;
            latestJournalId = journalId;

            var volumePath = @"\\.\" + dl + ":";
            var handle = CreateFile(volumePath, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle == INVALID_HANDLE_VALUE)
                return false;

            const int bufferSize = ReadUsnBufferSize;
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (!DeviceIoControlQueryUsn(handle, FSCTL_QUERY_USN_JOURNAL,
                    IntPtr.Zero, 0, out var journalData,
                    Marshal.SizeOf(typeof(UsnJournalData)), out _, IntPtr.Zero))
                    return false;

                latestJournalId = journalData.UsnJournalID;

                if (journalData.UsnJournalID != journalId || startUsn < journalData.LowestValidUsn)
                    return false;

                if (startUsn >= journalData.NextUsn)
                {
                    nextUsn = journalData.NextUsn;
                    return true;
                }

                var state = new VolumeWatchState
                {
                    DriveLetter = dl,
                    NextUsn = startUsn,
                    JournalId = journalId
                };

                if (!ReadUsnBatch(state, handle, ref journalData, buffer, bufferSize, ct,
                        raiseOverflowEvent: false, collectedChanges: changes))
                    return false;
                nextUsn = state.NextUsn;
                latestJournalId = state.JournalId;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                CloseHandle(handle);
            }
        }

        // ── 私有实现 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 后台轮询循环：每隔 1 秒读取一批 USN 记录，解析后触发对应事件。
        /// 若检测到日志溢出（ERROR_JOURNAL_ENTRY_DELETED）或日志 ID 变更，触发 JournalOverflow。
        /// </summary>
        private void WatchLoop(VolumeWatchState state)
        {
            var ct = state.Cts.Token;
            const int bufferSize = ReadUsnBufferSize;
            var buffer = Marshal.AllocHGlobal(bufferSize);
            UsnDiagLog.Write($"[WATCHER LOOP START] drive={state.DriveLetter}");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var recentlyActive = state.LastActivityUtc != DateTime.MinValue
                                         && (DateTime.UtcNow - state.LastActivityUtc).TotalSeconds <= 2;
                    var delayMilliseconds = state.HasBacklog ? 10 : (recentlyActive ? 50 : 500);
                    try { Task.Delay(delayMilliseconds, ct).Wait(ct); }
                    catch (OperationCanceledException) { break; }

                    if (ct.IsCancellationRequested) break;

                    var volumePath = @"\\.\" + state.DriveLetter + ":";
                    var handle = CreateFile(volumePath, GENERIC_READ,
                        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                        IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                    if (handle == INVALID_HANDLE_VALUE)
                    {
                        UsnDiagLog.Write($"[WATCHER] drive={state.DriveLetter} cannot open volume err={Marshal.GetLastWin32Error()}");
                        continue;
                    }

                    try
                    {
                        if (!DeviceIoControlQueryUsn(handle, FSCTL_QUERY_USN_JOURNAL,
                            IntPtr.Zero, 0, out var journalData,
                            Marshal.SizeOf(typeof(UsnJournalData)), out _, IntPtr.Zero))
                        {
                            UsnDiagLog.Write($"[WATCHER] drive={state.DriveLetter} QueryUsn failed err={Marshal.GetLastWin32Error()}");
                            continue;
                        }

                        if (journalData.UsnJournalID != state.JournalId ||
                            state.NextUsn < journalData.LowestValidUsn)
                        {
                            UsnDiagLog.Write($"[WATCHER OVERFLOW] drive={state.DriveLetter} storedId={state.JournalId} actualId={journalData.UsnJournalID} nextUsn={state.NextUsn} lowestValid={journalData.LowestValidUsn}");
                            JournalOverflow?.Invoke(this, EventArgs.Empty);
                            state.NextUsn   = journalData.NextUsn;
                            state.JournalId = journalData.UsnJournalID;
                            continue;
                        }

                        if (state.NextUsn >= journalData.NextUsn)
                            continue;

                        LogWatcherPollIfNeeded(state, journalData.NextUsn);
                        var changes = new List<UsnChangeEntry>(256);
                        ReadUsnBatch(state, handle, ref journalData, buffer, bufferSize, ct,
                            raiseOverflowEvent: true, collectedChanges: changes);
                        if (changes.Count > 0)
                        {
                            state.LastActivityUtc = DateTime.UtcNow;
                            UsnDiagLog.Write(
                                $"[WATCHER BATCH] drive={state.DriveLetter} changes={changes.Count} nextUsn={state.NextUsn}");
                            ChangesCollected?.Invoke(this, new UsnChangesCollectedEventArgs(state.DriveLetter, changes));
                        }
                        state.HasBacklog = state.NextUsn < journalData.NextUsn;
                    }
                    finally { CloseHandle(handle); }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                UsnDiagLog.Write($"[WATCHER LOOP END] drive={state.DriveLetter}");
            }
        }

        private static void LogWatcherPollIfNeeded(VolumeWatchState state, long journalNextUsn)
        {
            if (state == null)
            {
                return;
            }

            var utcNow = DateTime.UtcNow;
            var previousBacklog = state.LastLoggedJournalNextUsn > state.LastLoggedNextUsn
                ? state.LastLoggedJournalNextUsn - state.LastLoggedNextUsn
                : 0;
            var currentBacklog = journalNextUsn > state.NextUsn
                ? journalNextUsn - state.NextUsn
                : 0;
            var backlogStateChanged = (previousBacklog == 0) != (currentBacklog == 0);
            var shouldLog = state.LastPollLogUtc == DateTime.MinValue
                || utcNow - state.LastPollLogUtc >= WatcherPollLogInterval
                || backlogStateChanged;

            if (!shouldLog)
            {
                return;
            }

            state.LastPollLogUtc = utcNow;
            state.LastLoggedNextUsn = state.NextUsn;
            state.LastLoggedJournalNextUsn = journalNextUsn;
            UsnDiagLog.Write($"[WATCHER POLL] drive={state.DriveLetter} nextUsn={state.NextUsn} journalNextUsn={journalNextUsn}");
        }

        private bool ReadUsnBatch(VolumeWatchState state, IntPtr handle,
            ref UsnJournalData journalData, IntPtr buffer, int bufferSize, CancellationToken ct,
            bool raiseOverflowEvent, List<UsnChangeEntry> collectedChanges)
        {
            var readData = new ReadUsnJournalData
            {
                StartUsn          = state.NextUsn,
                ReasonMask        = USN_REASON_FILE_CREATE | USN_REASON_FILE_DELETE
                                  | USN_REASON_RENAME_OLD_NAME | USN_REASON_RENAME_NEW_NAME
                                  | USN_REASON_CLOSE,
                ReturnOnlyOnClose = 0,
                Timeout           = 0,
                BytesToWaitFor    = 0,
                UsnJournalID      = state.JournalId,
            };

            var sliceStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var recordsRead = 0;
            var provisionalCreated = 0;
            var provisionalCommitted = 0;
            var provisionalDropped = 0;
            while (!ct.IsCancellationRequested)
            {
                if (!DeviceIoControlReadUsn(handle, FSCTL_READ_USN_JOURNAL,
                    ref readData, Marshal.SizeOf(typeof(ReadUsnJournalData)),
                    buffer, bufferSize, out var bytesReturned, IntPtr.Zero))
                {
                    var err = Marshal.GetLastWin32Error();
                    UsnDiagLog.Write($"[WATCHER READ FAIL] drive={state.DriveLetter} err={err}");
                    if (err == ERROR_JOURNAL_ENTRY_DELETED)
                    {
                        if (raiseOverflowEvent)
                            JournalOverflow?.Invoke(this, EventArgs.Empty);
                        state.NextUsn   = journalData.NextUsn;
                        state.JournalId = journalData.UsnJournalID;
                    }
                    return false;
                }

                if (bytesReturned <= 8) break;

                readData.StartUsn = Marshal.ReadInt64(buffer, 0);

                var offset = 8;
                while (offset + 60 < bytesReturned)
                {
                    var recordLength = Marshal.ReadInt32(buffer, offset);
                    if (recordLength <= 0) break;

                    var frn            = (ulong)Marshal.ReadInt64(buffer, offset + 8)  & 0x0000FFFFFFFFFFFF;
                    var parentFrn      = (ulong)Marshal.ReadInt64(buffer, offset + 16) & 0x0000FFFFFFFFFFFF;
                    var reason         = (uint)Marshal.ReadInt32(buffer, offset + 40);
                    var fileAttributes = (uint)Marshal.ReadInt32(buffer, offset + 52);
                    var fileNameLength = (ushort)Marshal.ReadInt16(buffer, offset + 56);
                    var fileNameOffset = (ushort)Marshal.ReadInt16(buffer, offset + 58);

                    if (fileNameLength > 0 && offset + fileNameOffset + fileNameLength <= bytesReturned)
                    {
                        recordsRead++;
                        var fileName = Marshal.PtrToStringUni(
                            IntPtr.Add(buffer, offset + fileNameOffset), fileNameLength / 2);

                        var isDir     = (fileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                        var isOldName = (reason & USN_REASON_RENAME_OLD_NAME) != 0;
                        var isNewName = (reason & USN_REASON_RENAME_NEW_NAME) != 0;
                        var isDelete  = (reason & USN_REASON_FILE_DELETE) != 0;
                        var isCreate  = (reason & USN_REASON_FILE_CREATE) != 0;
                        var isClose   = (reason & USN_REASON_CLOSE) != 0;

                        if (LogUsnRecords)
                        {
                            UsnDiagLog.Write($"[USN RECORD] drive={state.DriveLetter} file={fileName} reason=0x{reason:X} del={isDelete} create={isCreate} oldName={isOldName} newName={isNewName}");
                        }

                        if (isOldName)
                        {
                            DropProvisional(state, frn, ref provisionalDropped);
                            state.PendingRenameOldByFrn[frn] = (fileName, parentFrn);
                        }
                        else if (isNewName
                                 && state.PendingRenameOldByFrn.TryGetValue(frn, out var pendingRenameOld))
                        {
                            DropProvisional(state, frn, ref provisionalDropped);
                            var lowerName = fileName.ToLowerInvariant();
                            var oldLowerName = pendingRenameOld.oldName.ToLowerInvariant();
                            if (collectedChanges != null)
                            {
                                AppendCollectedChange(collectedChanges, new UsnChangeEntry(
                                    kind: UsnChangeKind.Rename,
                                    frn: frn,
                                    lowerName: lowerName,
                                    originalName: fileName,
                                    parentFrn: parentFrn,
                                    driveLetter: state.DriveLetter,
                                    isDirectory: isDir,
                                    oldLowerName: oldLowerName,
                                    oldParentFrn: pendingRenameOld.oldParentFrn));
                            }
                            else
                            {
                                var newRecord = new FileRecord(
                                    lowerName: lowerName,
                                    originalName: fileName,
                                    parentFrn: parentFrn,
                                    driveLetter: state.DriveLetter,
                                    isDirectory: isDir,
                                    frn: frn);

                                FileRenamed?.Invoke(this, new UsnFileRenamedEventArgs(
                                    oldLowerName: oldLowerName,
                                    oldParentFrn: pendingRenameOld.oldParentFrn,
                                    newFrn: frn,
                                    driveLetter: state.DriveLetter,
                                    newRecord: newRecord));
                            }

                            state.PendingRenameOldByFrn.Remove(frn);
                        }
                        else if (isNewName)
                        {
                            var lowerName = fileName.ToLowerInvariant();
                            UsnDiagLog.Write(
                                $"[USN RENAME_NEW UPSERT] drive={state.DriveLetter} frn={frn} parentFrn={parentFrn} lowerName={lowerName}");
                            var change = new UsnChangeEntry(
                                kind: UsnChangeKind.Create,
                                frn: frn,
                                lowerName: lowerName,
                                originalName: fileName,
                                parentFrn: parentFrn,
                                driveLetter: state.DriveLetter,
                                isDirectory: isDir);
                            if (ShouldHoldProvisional(lowerName))
                            {
                                state.PendingProvisionalByFrn[frn] = change;
                                provisionalCreated++;
                                if (isClose)
                                {
                                    CommitProvisional(state, frn, collectedChanges, ref provisionalCommitted);
                                }
                            }
                            else
                            {
                                EmitCreate(change, collectedChanges);
                            }
                        }
                        else if (isDelete)
                        {
                            state.PendingRenameOldByFrn.Remove(frn);
                            DropProvisional(state, frn, ref provisionalDropped);
                            var lowerName = fileName.ToLowerInvariant();
                            if (collectedChanges != null)
                            {
                                AppendCollectedChange(collectedChanges, new UsnChangeEntry(
                                    kind: UsnChangeKind.Delete,
                                    frn: frn,
                                    lowerName: lowerName,
                                    originalName: fileName,
                                    parentFrn: parentFrn,
                                    driveLetter: state.DriveLetter,
                                    isDirectory: isDir));
                            }
                            else
                            {
                                FileDeleted?.Invoke(this, new UsnFileDeletedEventArgs(
                                    frn: frn,
                                    lowerName: lowerName,
                                    parentFrn: parentFrn,
                                    driveLetter: state.DriveLetter));
                            }
                        }
                        else if (isCreate)
                        {
                            var lowerName = fileName.ToLowerInvariant();
                            var change = new UsnChangeEntry(
                                kind: UsnChangeKind.Create,
                                frn: frn,
                                lowerName: lowerName,
                                originalName: fileName,
                                parentFrn: parentFrn,
                                driveLetter: state.DriveLetter,
                                isDirectory: isDir);
                            if (ShouldHoldProvisional(lowerName))
                            {
                                state.PendingProvisionalByFrn[frn] = change;
                                provisionalCreated++;
                                if (isClose)
                                {
                                    CommitProvisional(state, frn, collectedChanges, ref provisionalCommitted);
                                }
                            }
                            else
                            {
                                EmitCreate(change, collectedChanges);
                            }
                        }
                        else if (isClose)
                        {
                            CommitProvisional(state, frn, collectedChanges, ref provisionalCommitted);
                        }
                    }

                    offset += recordLength;
                }

                if (recordsRead >= MaxWatcherReadRecordsPerSlice
                    || sliceStopwatch.ElapsedMilliseconds >= MaxWatcherReadSliceMilliseconds)
                {
                    state.NextUsn = readData.StartUsn;
                    UsnDiagLog.Write(
                        $"[WATCHER READ SLICE] drive={state.DriveLetter} records={recordsRead} elapsedMs={sliceStopwatch.ElapsedMilliseconds} " +
                        $"nextUsn={state.NextUsn} journalNextUsn={journalData.NextUsn} yielded=true");
                    return true;
                }

                if (readData.StartUsn >= journalData.NextUsn) break;
            }

            state.NextUsn = readData.StartUsn;
            LogProvisionalStats(state, provisionalCreated, provisionalCommitted, provisionalDropped);
            if (recordsRead > 0 && sliceStopwatch.ElapsedMilliseconds >= 50)
            {
                UsnDiagLog.Write(
                    $"[WATCHER READ SLICE] drive={state.DriveLetter} records={recordsRead} elapsedMs={sliceStopwatch.ElapsedMilliseconds} " +
                    $"nextUsn={state.NextUsn} journalNextUsn={journalData.NextUsn} yielded=false");
            }
            return true;
        }

        private void EmitCreate(UsnChangeEntry change, List<UsnChangeEntry> collectedChanges)
        {
            if (change == null)
            {
                return;
            }

            if (collectedChanges != null)
            {
                AppendCollectedChange(collectedChanges, change);
                return;
            }

            FileCreated?.Invoke(this, new UsnFileCreatedEventArgs(
                frn: change.Frn,
                fileName: change.OriginalName,
                parentFrn: change.ParentFrn,
                driveLetter: change.DriveLetter,
                isDirectory: change.IsDirectory));
        }

        private void CommitProvisional(
            VolumeWatchState state,
            ulong frn,
            List<UsnChangeEntry> collectedChanges,
            ref int committed)
        {
            if (state == null || !state.PendingProvisionalByFrn.TryGetValue(frn, out var change))
            {
                return;
            }

            state.PendingProvisionalByFrn.Remove(frn);
            EmitCreate(change, collectedChanges);
            committed++;
        }

        private static void DropProvisional(VolumeWatchState state, ulong frn, ref int dropped)
        {
            if (state != null && state.PendingProvisionalByFrn.Remove(frn))
            {
                dropped++;
            }
        }

        private static bool ShouldHoldProvisional(string lowerName)
        {
            if (string.IsNullOrEmpty(lowerName))
            {
                return false;
            }

            return lowerName.IndexOf("~rf", StringComparison.Ordinal) >= 0
                   || lowerName.EndsWith(".tmp", StringComparison.Ordinal)
                   || lowerName.EndsWith(".tme", StringComparison.Ordinal)
                   || lowerName.EndsWith(".temp", StringComparison.Ordinal);
        }

        private static void LogProvisionalStats(
            VolumeWatchState state,
            int created,
            int committed,
            int dropped)
        {
            if (created == 0 && committed == 0 && dropped == 0)
            {
                return;
            }

            UsnDiagLog.Write(
                $"[USN PROVISIONAL] drive={state.DriveLetter} created={created} committed={committed} dropped={dropped} pending={state.PendingProvisionalByFrn.Count}");
        }

        private static void AppendCollectedChange(List<UsnChangeEntry> changes, UsnChangeEntry change)
        {
            if (changes.Count > 0)
            {
                var last = changes[changes.Count - 1];
                if (last.Kind == change.Kind
                    && last.Frn == change.Frn
                    && last.ParentFrn == change.ParentFrn
                    && last.DriveLetter == change.DriveLetter
                    && string.Equals(last.LowerName, change.LowerName, StringComparison.Ordinal)
                    && string.Equals(last.OldLowerName, change.OldLowerName, StringComparison.Ordinal)
                    && last.OldParentFrn == change.OldParentFrn)
                    return;
            }

            changes.Add(change);
        }
    }
}

