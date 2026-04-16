using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MftScanner
{
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
        public UsnFileDeletedEventArgs(string lowerName, ulong parentFrn, char driveLetter)
        {
            LowerName   = lowerName;
            ParentFrn   = parentFrn;
            DriveLetter = driveLetter;
        }
        public string LowerName   { get; }
        public ulong  ParentFrn   { get; }
        public char   DriveLetter { get; }
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

            const int bufferSize = 64 * 1024;
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

                if (!ReadUsnBatch(state, handle, ref journalData, buffer, bufferSize, ct, raiseOverflowEvent: false))
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
            const int bufferSize = 64 * 1024;
            var buffer = Marshal.AllocHGlobal(bufferSize);
            UsnDiagLog.Write($"[WATCHER LOOP START] drive={state.DriveLetter}");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try { Task.Delay(1000, ct).Wait(ct); }
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

                        UsnDiagLog.Write($"[WATCHER POLL] drive={state.DriveLetter} nextUsn={state.NextUsn} journalNextUsn={journalData.NextUsn}");
                        ReadUsnBatch(state, handle, ref journalData, buffer, bufferSize, ct, raiseOverflowEvent: true);
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

        private bool ReadUsnBatch(VolumeWatchState state, IntPtr handle,
            ref UsnJournalData journalData, IntPtr buffer, int bufferSize, CancellationToken ct, bool raiseOverflowEvent)
        {
            string pendingOldName   = null;
            ulong  pendingOldParent = 0;

            var readData = new ReadUsnJournalData
            {
                StartUsn          = state.NextUsn,
                ReasonMask        = USN_REASON_FILE_CREATE | USN_REASON_FILE_DELETE
                                  | USN_REASON_RENAME_OLD_NAME | USN_REASON_RENAME_NEW_NAME,
                ReturnOnlyOnClose = 0,
                Timeout           = 0,
                BytesToWaitFor    = 0,
                UsnJournalID      = state.JournalId,
            };

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
                        var fileName = Marshal.PtrToStringUni(
                            IntPtr.Add(buffer, offset + fileNameOffset), fileNameLength / 2);

                        var isDir     = (fileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                        var isOldName = (reason & USN_REASON_RENAME_OLD_NAME) != 0;
                        var isNewName = (reason & USN_REASON_RENAME_NEW_NAME) != 0;
                        var isDelete  = (reason & USN_REASON_FILE_DELETE) != 0;
                        var isCreate  = (reason & USN_REASON_FILE_CREATE) != 0;

                        UsnDiagLog.Write($"[USN RECORD] drive={state.DriveLetter} file={fileName} reason=0x{reason:X} del={isDelete} create={isCreate} oldName={isOldName} newName={isNewName}");

                        if (isOldName)
                        {
                            pendingOldName   = fileName;
                            pendingOldParent = parentFrn;
                        }
                        else if (isNewName && pendingOldName != null)
                        {
                            var newRecord = new FileRecord(
                                lowerName:    fileName.ToLowerInvariant(),
                                originalName: fileName,
                                parentFrn:    parentFrn,
                                driveLetter:  state.DriveLetter,
                                isDirectory:  isDir);

                            FileRenamed?.Invoke(this, new UsnFileRenamedEventArgs(
                                oldLowerName: pendingOldName.ToLowerInvariant(),
                                oldParentFrn: pendingOldParent,
                                newFrn:       frn,
                                driveLetter:  state.DriveLetter,
                                newRecord:    newRecord));

                            pendingOldName   = null;
                            pendingOldParent = 0;
                        }
                        else if (isDelete)
                        {
                            FileDeleted?.Invoke(this, new UsnFileDeletedEventArgs(
                                lowerName:   fileName.ToLowerInvariant(),
                                parentFrn:   parentFrn,
                                driveLetter: state.DriveLetter));
                        }
                        else if (isCreate)
                        {
                            FileCreated?.Invoke(this, new UsnFileCreatedEventArgs(
                                frn:         frn,
                                fileName:    fileName,
                                parentFrn:   parentFrn,
                                driveLetter: state.DriveLetter,
                                isDirectory: isDir));
                        }
                    }

                    offset += recordLength;
                }

                if (readData.StartUsn >= journalData.NextUsn) break;
            }

            state.NextUsn = readData.StartUsn;
            return true;
        }
    }
}
