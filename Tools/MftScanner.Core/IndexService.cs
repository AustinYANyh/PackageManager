using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MftScanner
{
    /// <summary>
    /// 纯内存索引服务，负责通过 MFT 枚举构建和维护 <see cref="MemoryIndex"/>。
    /// 替代 <see cref="MftScanService"/> 的索引构建层。
    /// 需求：1.1、1.4、1.5
    /// </summary>
    internal sealed class ManagedIndexServiceBackend : IIndexServiceBackend
    {
        private MftEnumerator _enumerator = new MftEnumerator();
        private readonly IndexSnapshotStore _snapshotStore = new IndexSnapshotStore();
        private readonly UsnWatcher _usnWatcher = new UsnWatcher();
        private volatile MemoryIndex _index = new MemoryIndex();
        private readonly object _snapshotSaveLock = new object();
        private readonly object _snapshotWriteLock = new object();
        private readonly object _backgroundCatchUpLock = new object();
        private readonly object _containsWarmupLock = new object();
        private readonly object _containsQueryCacheLock = new object();
        private readonly object _pathCandidateCacheLock = new object();
        private bool _watcherInitialized;
        private CancellationTokenSource _snapshotSaveCts;
        private DateTime _pendingSnapshotSaveStartedUtc = DateTime.MinValue;
        private CancellationTokenSource _backgroundCatchUpCts;
        private Task _backgroundCatchUpTask;
        private CancellationTokenSource _containsWarmupCts;
        private Task _containsWarmupTask;
        private CancellationTokenSource _shortContainsWarmupCts;
        private Task _shortContainsWarmupTask;
        private CancellationTokenSource _liveDeltaCompactCts;
        private Task _liveDeltaCompactTask;
        private long _lastLiveDeltaCompactAttemptUtcTicks;
        private readonly HashSet<string> _shortContainsWarmups = new HashSet<string>(StringComparer.Ordinal);
        private volatile bool _isBackgroundCatchUpInProgress;
        private volatile bool _isSnapshotStale;
        private volatile string _currentStatusMessage = string.Empty;
        private readonly object _periodicSnapshotSaveLock = new object();
        private CancellationTokenSource _periodicSnapshotSaveCts;
        private Task _periodicSnapshotSaveTask;
        private volatile VolumeSnapshot[] _lastVolumeSnapshots = Array.Empty<VolumeSnapshot>();
        private ContainsQueryCacheEntry _containsQueryCache;
        private PathCandidateCacheEntry _pathCandidateCache;
        private int _activeSearchCount;
        private long _lastSearchCompletedUtcTicks;
        private ulong _currentIndexContentFingerprint;
        private long _indexGeneration;
        private int _lastStableSnapshotRecordCount;

        private const int SnapshotSaveDebounceMilliseconds = 5000;
        private const int SnapshotPeriodicSaveMilliseconds = 120000;
        private const int SnapshotSaveMaxDeferredMilliseconds = 30000;
        private const int SnapshotSearchIdleDelayMilliseconds = 5000;
        private const int CatchUpApplyIdleDelayMilliseconds = 1000;
        private const int CatchUpApplyMaxDeferMilliseconds = 30000;
        private const int CatchUpLiveDeltaBatchSize = 4096;
        private const int LiveDeltaCompactMinMutations = 65536;
        private const int LiveDeltaCompactDelayMilliseconds = 750;
        private const int LiveDeltaCompactMinIntervalMilliseconds = 60000;
        private const int MaxIncrementalContainsCacheRecords = 2000000;
        private const int MaxPathCandidateCacheRecords = 500000;
        private const int MaxPostingsFirstPathVerifyRecords = 500000;
        private const int MaxPreferPathFirstDirectories = 10000;
        private static readonly bool BuildShortContainsBuckets = true;
        private const double SuspiciousSnapshotShrinkRatio = 0.50;
        private const ulong TargetUsnJournalMaximumSize = 256UL * 1024UL * 1024UL;
        private const ulong TargetUsnJournalAllocationDelta = 64UL * 1024UL * 1024UL;
        private int _usnJournalMaintenanceStarted;

        // 保存 progress 引用，供 OnJournalOverflow 使用（需求 6.5）
        private IProgress<string> _progress;

        /// <summary>当前内存索引实例（供 SearchAsync 使用）。</summary>
        public bool PreferSynchronousHostSearch => false;

        public MemoryIndex Index => _index;
        public int IndexedCount => _index.TotalCount;
        public bool IsBackgroundCatchUpInProgress => _isBackgroundCatchUpInProgress;
        public bool IsSnapshotStale => _isSnapshotStale;
        public string CurrentStatusMessage => _currentStatusMessage;
        public ContainsBucketStatus ContainsBucketStatus => _index?.GetContainsBucketStatus() ?? ContainsBucketStatus.Empty;

        public void EnsureSearchHotStructuresReady(CancellationToken ct, string reason)
        {
            EnsureSearchHotStructuresReady(reason, ct);
        }

        public string InvokeNativeTestControl(string requestJson)
        {
            throw new NotSupportedException("Native test control is only available for the native backend.");
        }

        /// <summary>文件系统增量变更事件，携带变更类型和文件信息，供 UI 直接更新列表。</summary>
        public event EventHandler<IndexChangedEventArgs> IndexChanged;
        public event EventHandler<IndexStatusChangedEventArgs> IndexStatusChanged;

        /// <summary>
        /// 枚举所有固定磁盘卷，构建纯内存索引，完成后启动 UsnWatcher。
        /// 需求 1.1、1.4、1.5、6.1
        /// </summary>
        /// <param name="progress">进度回调，上报"已索引 N 个对象"。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>索引的文件和目录总数。</returns>
        public Task<int> BuildIndexAsync(IProgress<string> progress, CancellationToken ct)
        {
            _progress = progress;
            SetupUsnWatcher();
            StartPeriodicSnapshotSaveLoop();
            return Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                UsnDiagLog.Write("[BUILD INDEX ASYNC] start");
                try
                {
                    var result = BuildIndex(progress, ct);
                    EnsureSearchHotStructuresReady("build-index-ready", ct);
                    stopwatch.Stop();
                    UsnDiagLog.Write($"[BUILD INDEX ASYNC] success elapsedMs={stopwatch.ElapsedMilliseconds} indexedCount={result}");
                    return result;
                }
                catch (OperationCanceledException)
                {
                    stopwatch.Stop();
                    UsnDiagLog.Write($"[BUILD INDEX ASYNC] canceled elapsedMs={stopwatch.ElapsedMilliseconds}");
                    throw;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    UsnDiagLog.Write($"[BUILD INDEX ASYNC] fail elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
                    throw;
                }
            }, ct);
        }

        /// <summary>
        /// 强制全量重建索引（供重建索引按钮调用）。
        /// 构建新 MemoryIndex 实例后原子替换引用。
        /// 需求 6.5、8.5
        /// </summary>
        public Task<int> RebuildIndexAsync(IProgress<string> progress, CancellationToken ct)
        {
            if (progress != null)
                _progress = progress;
            StartPeriodicSnapshotSaveLoop();
            return Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                UsnDiagLog.Write("[REBUILD INDEX ASYNC] start");
                try
                {
                    CancelPendingSnapshotSave();
                    CancelBackgroundCatchUp();
                    CancelContainsWarmup();
                    var result = BuildIndexFromMft(_progress, ct);
                    stopwatch.Stop();
                    UsnDiagLog.Write($"[REBUILD INDEX ASYNC] success elapsedMs={stopwatch.ElapsedMilliseconds} indexedCount={result}");
                    return result;
                }
                catch (OperationCanceledException)
                {
                    stopwatch.Stop();
                    UsnDiagLog.Write($"[REBUILD INDEX ASYNC] canceled elapsedMs={stopwatch.ElapsedMilliseconds}");
                    throw;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    UsnDiagLog.Write($"[REBUILD INDEX ASYNC] fail elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
                    throw;
                }
            }, ct);
        }

        public void Shutdown()
        {
            StopPeriodicSnapshotSaveLoop();
            CancelPendingSnapshotSave();
            CancelBackgroundCatchUp();
            CancelContainsWarmup();
            SaveCurrentSnapshot("shutdown");
            _usnWatcher.StopWatching();
        }

        // ── UsnWatcher 集成 ──────────────────────────────────────────────────────

        /// <summary>
        /// 订阅 UsnWatcher 事件。需求 6.1–6.5
        /// </summary>
        private void SetupUsnWatcher()
        {
            if (_watcherInitialized)
                return;

            _usnWatcher.FileCreated    += OnFileCreated;
            _usnWatcher.FileDeleted    += OnFileDeleted;
            _usnWatcher.FileRenamed    += OnFileRenamed;
            _usnWatcher.ChangesCollected += OnUsnChangesCollected;
            _usnWatcher.JournalOverflow += OnJournalOverflow;
            _watcherInitialized = true;
        }

        private void OnUsnChangesCollected(object sender, UsnChangesCollectedEventArgs args)
        {
            var changes = args?.Changes;
            if (changes == null || changes.Count == 0)
                return;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var eventStopwatch = Stopwatch.StartNew();
                var indexChangedArgs = BuildBatchIndexChangedArgs(changes);
                eventStopwatch.Stop();

                var cacheStopwatch = Stopwatch.StartNew();
                InvalidatePathCandidateCacheIfAffected(changes);
                cacheStopwatch.Stop();

                var enumeratorStopwatch = Stopwatch.StartNew();
                _enumerator.ApplyUsnChanges(changes);
                enumeratorStopwatch.Stop();

                var overlayStopwatch = Stopwatch.StartNew();
                var liveDelta = _index.ApplyLiveDeltaBatch(changes);
                overlayStopwatch.Stop();
                if (liveDelta.CompactRequired)
                    QueueLiveDeltaCompact("watcher-batch");

                var publishStopwatch = Stopwatch.StartNew();
                PublishBatchIndexChanges(indexChangedArgs);
                publishStopwatch.Stop();

                ScheduleSnapshotSave();
                stopwatch.Stop();
                UsnDiagLog.Write(
                    $"[WATCHER BATCH APPLY] outcome=success strategy=live-delta drive={args.DriveLetter} changes={changes.Count} " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds} eventsMs={eventStopwatch.ElapsedMilliseconds} " +
                    $"cacheMs={cacheStopwatch.ElapsedMilliseconds} enumeratorMs={enumeratorStopwatch.ElapsedMilliseconds} " +
                    $"overlayMs={overlayStopwatch.ElapsedMilliseconds} publishMs={publishStopwatch.ElapsedMilliseconds} " +
                    $"inserted={liveDelta.Inserted} deleted={liveDelta.Deleted} restored={liveDelta.Restored} " +
                    $"alreadyVisible={liveDelta.AlreadyVisible} overlayAdds={liveDelta.OverlayAdds} " +
                    $"overlayDeletes={liveDelta.OverlayDeletes} compactRequired={liveDelta.CompactRequired}");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                UsnDiagLog.Write(
                    $"[WATCHER BATCH APPLY] outcome=fail drive={args.DriveLetter} changes={changes.Count} " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
            }
        }

        /// <summary>文件创建：插入新 FileRecord，同时更新 FRN 字典供路径解析。需求 6.2</summary>
        private void OnFileCreated(object sender, UsnFileCreatedEventArgs args)
        {
            _enumerator.RegisterFrn(args.DriveLetter, args.Frn, args.FileName, args.ParentFrn, args.IsDirectory);
            InvalidatePathCandidateCacheIfAffected(args.DriveLetter, args.ParentFrn, args.Frn);

            var lowerName = args.FileName.ToLowerInvariant();

            // 同一文件的 USN 事件可能触发多次（创建+写入+关闭），用 FRN 去重
            // 如果索引里已有可见的相同 (lowerName, parentFrn, driveLetter) 记录，跳过；
            // tombstone 记录不能算可见，否则删除后同名重建会一直被隐藏。
            var idx = _index;
            var existing = FindRecordByNameParentAndDrive(
                idx.SortedArray,
                lowerName,
                args.ParentFrn,
                args.DriveLetter,
                idx,
                out var existingDeleted);
            if (existing != null && !existingDeleted)
            {
                UsnDiagLog.Write(
                    $"[CREATE UPSERT] outcome=already-visible drive={args.DriveLetter} frn={args.Frn} " +
                    $"parentFrn={args.ParentFrn} lowerName={IndexPerfLog.FormatValue(lowerName)}");
                return;
            }

            if (existingDeleted
                && existing != null
                && existing.Frn == args.Frn
                && _index.TryRestoreDeleted(existing, "usn-create"))
            {
                var restoredFullPath = _enumerator.ResolveFullPath(args.DriveLetter, args.ParentFrn, args.FileName);
                ScheduleSnapshotSave();
                UsnDiagLog.Write(
                    $"[CREATE UPSERT] outcome=restored-tombstone drive={args.DriveLetter} frn={args.Frn} " +
                    $"parentFrn={args.ParentFrn} lowerName={IndexPerfLog.FormatValue(lowerName)}");
                IndexChanged?.Invoke(this, new IndexChangedEventArgs(
                    IndexChangeType.Created, lowerName, restoredFullPath, isDirectory: args.IsDirectory));
                return;
            }

            var record = new FileRecord(
                lowerName:    lowerName,
                originalName: args.FileName,
                parentFrn:    args.ParentFrn,
                driveLetter:  args.DriveLetter,
                isDirectory:  args.IsDirectory,
                frn:          args.Frn);
            var liveDelta = _index.ApplyLiveDeltaBatch(new[]
            {
                new UsnChangeEntry(
                    UsnChangeKind.Create,
                    args.Frn,
                    lowerName,
                    args.FileName,
                    args.ParentFrn,
                    args.DriveLetter,
                    args.IsDirectory)
            });
            if (liveDelta.CompactRequired)
                QueueLiveDeltaCompact("usn-create");
            var fullPath = _enumerator.ResolveFullPath(args.DriveLetter, args.ParentFrn, args.FileName);
            ScheduleSnapshotSave();
            UsnDiagLog.Write(
                $"[CREATE UPSERT] outcome={(existingDeleted ? "inserted-after-tombstone" : "inserted")} drive={args.DriveLetter} frn={args.Frn} " +
                $"parentFrn={args.ParentFrn} lowerName={IndexPerfLog.FormatValue(lowerName)} strategy=live-delta " +
                $"inserted={liveDelta.Inserted} restored={liveDelta.Restored} alreadyVisible={liveDelta.AlreadyVisible} " +
                $"overlayAdds={liveDelta.OverlayAdds} overlayDeletes={liveDelta.OverlayDeletes}");
            IndexChanged?.Invoke(this, new IndexChangedEventArgs(
                IndexChangeType.Created, lowerName, fullPath, isDirectory: args.IsDirectory));
        }

        /// <summary>文件删除：从索引中移除记录。需求 6.3</summary>
        private void OnFileDeleted(object sender, UsnFileDeletedEventArgs args)
        {
            InvalidatePathCandidateCacheIfAffected(args.DriveLetter, args.ParentFrn, args.Frn);
            // 删除前先解析路径（FRN 字典此时还未清除），供 UI 精确匹配
            var fullPath = _enumerator.ResolveFullPath(args.DriveLetter, args.ParentFrn, args.LowerName);
            var applied = _index.MarkDeleted(args.Frn, args.LowerName, args.ParentFrn, args.DriveLetter, false, "usn-delete");
            if (!applied)
            {
                UsnDiagLog.Write(
                    $"[DELETE OVERLAY APPLY] source=usn-delete outcome=duplicate drive={args.DriveLetter} " +
                    $"frn={args.Frn} parentFrn={args.ParentFrn} lowerName={IndexPerfLog.FormatValue(args.LowerName)}");
                return;
            }

            if (_index.LiveDeltaCount > LiveDeltaCompactMinMutations)
                QueueLiveDeltaCompact("usn-delete");
            ScheduleSnapshotSave();
            IndexChanged?.Invoke(this, new IndexChangedEventArgs(
                IndexChangeType.Deleted, args.LowerName, fullPath, isDirectory: false));
        }

        /// <summary>文件重命名：更新 FRN 字典，先移除旧记录再插入新记录。需求 6.4</summary>
        private void OnFileRenamed(object sender, UsnFileRenamedEventArgs args)
        {
            InvalidatePathCandidateCacheIfAffected(args.DriveLetter, args.OldParentFrn, args.NewFrn);
            InvalidatePathCandidateCacheIfAffected(args.DriveLetter, args.NewRecord.ParentFrn, args.NewFrn);
            var oldFullPath = _enumerator.ResolveFullPath(
                args.DriveLetter, args.OldParentFrn, args.OldLowerName);
            _enumerator.RegisterFrn(args.DriveLetter, args.NewFrn,
                args.NewRecord.OriginalName, args.NewRecord.ParentFrn, args.NewRecord.IsDirectory);
            var liveDelta = _index.ApplyLiveDeltaBatch(new[]
            {
                new UsnChangeEntry(
                    UsnChangeKind.Rename,
                    args.NewFrn,
                    args.NewRecord.LowerName,
                    args.NewRecord.OriginalName,
                    args.NewRecord.ParentFrn,
                    args.DriveLetter,
                    args.NewRecord.IsDirectory,
                    args.OldLowerName,
                    args.OldParentFrn)
            });
            if (liveDelta.CompactRequired)
                QueueLiveDeltaCompact("usn-rename");
            var newFullPath = _enumerator.ResolveFullPath(
                args.DriveLetter, args.NewRecord.ParentFrn, args.NewRecord.OriginalName);
            ScheduleSnapshotSave();
            UsnDiagLog.Write(
                $"[RENAME UPSERT] outcome=success strategy=live-delta drive={args.DriveLetter} frn={args.NewFrn} " +
                $"oldParentFrn={args.OldParentFrn} newParentFrn={args.NewRecord.ParentFrn} " +
                $"oldLowerName={IndexPerfLog.FormatValue(args.OldLowerName)} newLowerName={IndexPerfLog.FormatValue(args.NewRecord.LowerName)} " +
                $"inserted={liveDelta.Inserted} deleted={liveDelta.Deleted} restored={liveDelta.Restored} " +
                $"alreadyVisible={liveDelta.AlreadyVisible} overlayAdds={liveDelta.OverlayAdds} overlayDeletes={liveDelta.OverlayDeletes}");
            IndexChanged?.Invoke(this, new IndexChangedEventArgs(
                IndexChangeType.Renamed, args.OldLowerName, newFullPath,
                oldFullPath,
                args.NewRecord.OriginalName, args.NewRecord.LowerName, args.NewRecord.IsDirectory));
        }

        /// <summary>
        /// USN 日志溢出：触发全量重建并上报提示。需求 6.5
        /// </summary>
        private void OnJournalOverflow(object sender, EventArgs args)
        {
            _progress?.Report("USN 日志溢出，正在重建索引...");
            Task.Run(() => RebuildIndexAsync(null, CancellationToken.None));
        }

        // ── 匹配模式识别 ────────────────────────────────────────────────────────

        /// <summary>四种匹配模式。需求 7.1–7.4</summary>
        private enum MatchMode { Contains, Prefix, Suffix, Regex, Wildcard }

        /// <summary>
        /// 根据关键词格式识别匹配模式，并返回规范化后的查询字符串。
        /// <list type="bullet">
        ///   <item><c>^prefix</c> → Prefix，normalizedQuery 为小写前缀</item>
        ///   <item><c>suffix$</c> → Suffix，normalizedQuery 为小写后缀</item>
        ///   <item><c>/regex/</c> → Regex，normalizedQuery 保留原始大小写</item>
        ///   <item>其他 → Contains，normalizedQuery 为小写字符串</item>
        /// </list>
        /// 需求 7.1、7.2、7.3、7.4
        /// </summary>
        /// <summary>
        /// 解析路径范围限定查询，格式为 <c>&lt;路径前缀&gt; &lt;搜索词&gt;</c>。
        /// 路径前缀必须以盘符（如 <c>C:\</c>）或 <c>\</c> 开头才视为合法。
        /// 需求 10.1、10.6
        /// </summary>
        /// <returns>
        /// 合法时返回 <c>(pathPrefix, searchTerm)</c>；
        /// 不合法时返回 <c>(null, keyword)</c>，整体作为搜索词。
        /// </returns>
        private static (string pathPrefix, string searchTerm) ParsePathScope(string keyword)
        {
            var spaceIdx = keyword.IndexOf(' ');
            if (spaceIdx <= 0)
                return (null, keyword);

            var candidate = keyword.Substring(0, spaceIdx);
            var isValidPath = (candidate.Length >= 3
                               && char.IsLetter(candidate[0])
                               && candidate[1] == ':'
                               && candidate[2] == '\\')
                           || candidate.StartsWith("\\");

            if (!isValidPath)
                return (null, keyword);

            var term = keyword.Substring(spaceIdx).TrimStart();
            return (candidate, string.IsNullOrEmpty(term) ? string.Empty : term);
        }

        private static (MatchMode mode, string normalizedQuery) DetectMatchMode(string keyword)
        {
            if (keyword.StartsWith("^"))
                return (MatchMode.Prefix, keyword.Substring(1).ToLowerInvariant());

            if (keyword.EndsWith("$"))
                return (MatchMode.Suffix, keyword.Substring(0, keyword.Length - 1).ToLowerInvariant());

            if (keyword.Length >= 3 && keyword.StartsWith("/") && keyword.EndsWith("/"))
                return (MatchMode.Regex, keyword.Substring(1, keyword.Length - 2));

            // 需求 9.1：含 * 或 ? 则识别为通配符模式
            if (keyword.IndexOfAny(new[] { '*', '?' }) >= 0)
                return (MatchMode.Wildcard, keyword.ToLowerInvariant());

            return (MatchMode.Contains, keyword.ToLowerInvariant());
        }

        // ── 匹配辅助方法 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 前缀匹配：在 SortedArray 上二分定位第一个 &gt;= prefix 的位置，
        /// 然后向后扫描直到 LowerName 不再以 prefix 开头。O(log n + m)。
        /// 需求 3.2、7.2
        /// </summary>
        private static (List<FileRecord> page, int total) PrefixMatch(
            MemoryIndex index,
            string prefix,
            FileRecord[] sortedArray,
            int offset,
            int maxResults,
            CancellationToken ct)
        {
            var page = new List<FileRecord>(Math.Min(maxResults, 64));
            if (sortedArray.Length == 0 || string.IsNullOrEmpty(prefix))
                return (page, 0);
            var hasDeletedOverlay = index != null && index.HasDeletedOverlay;

            // 二分找第一个 LowerName >= prefix 的位置
            int lo = 0, hi = sortedArray.Length - 1, start = sortedArray.Length;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (string.CompareOrdinal(sortedArray[mid].LowerName, prefix) < 0)
                    lo = mid + 1;
                else
                { start = mid; hi = mid - 1; }
            }

            var total = 0;
            for (var i = start; i < sortedArray.Length; i++)
            {
                if (((i - start + 1) & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                if (!sortedArray[i].LowerName.StartsWith(prefix, StringComparison.Ordinal))
                    break;

                if (!IsSearchVisible(index, hasDeletedOverlay, sortedArray[i]))
                    continue;

                total++;
                if (total > offset && page.Count < maxResults)
                    page.Add(sortedArray[i]);
            }

            return (page, total);
        }

        /// <summary>
        /// 后缀匹配：对 SortedArray 执行线性扫描，返回 LowerName 以 suffix 结尾的记录。
        /// 需求 3.3、7.3
        /// </summary>
        private static (List<FileRecord> page, int total) SuffixMatch(
            MemoryIndex index,
            string suffix,
            FileRecord[] sortedArray,
            int offset,
            int maxResults,
            CancellationToken ct)
        {
            var page = new List<FileRecord>(Math.Min(maxResults, 64));
            var hasDeletedOverlay = index != null && index.HasDeletedOverlay;
            var total = 0;
            var i = 0;
            foreach (var record in sortedArray)
            {
                if ((++i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                if (IsSearchVisible(index, hasDeletedOverlay, record)
                    && record.LowerName.EndsWith(suffix, StringComparison.Ordinal))
                {
                    total++;
                    if (total > offset && page.Count < maxResults)
                        page.Add(record);
                }
            }
            return (page, total);
        }

        /// <summary>
        /// 正则匹配：对 SortedArray 执行线性扫描，使用 <paramref name="pattern"/> 匹配 LowerName。
        /// 若正则无效，通过 <paramref name="progress"/> 上报错误并返回空列表。
        /// 需求 3.4、7.4、7.5
        /// </summary>
        private static (List<FileRecord> page, int total) RegexMatch(
            MemoryIndex index,
            string pattern,
            FileRecord[] sortedArray,
            int offset,
            int maxResults,
            IProgress<string> progress,
            CancellationToken ct = default)
        {
            Regex regex;
            try
            {
                regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException)
            {
                progress?.Report("正则表达式无效");
                return (new List<FileRecord>(), 0);
            }

            var page = new List<FileRecord>(Math.Min(maxResults, 64));
            var hasDeletedOverlay = index != null && index.HasDeletedOverlay;
            var total = 0;
            var i = 0;
            foreach (var record in sortedArray)
            {
                if ((++i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                try
                {
                    if (IsSearchVisible(index, hasDeletedOverlay, record) && regex.IsMatch(record.LowerName))
                    {
                        total++;
                        if (total > offset && page.Count < maxResults)
                            page.Add(record);
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    progress?.Report("正则表达式无效");
                    return (new List<FileRecord>(), 0);
                }
            }
            return (page, total);
        }

        /// <summary>
        /// 将通配符模式转换为等价的正则表达式字符串。
        /// <c>*</c> → <c>.*</c>，<c>?</c> → <c>.</c>，其余字符使用 <see cref="Regex.Escape"/> 转义。
        /// 结果以 <c>^</c> 开头、<c>$</c> 结尾，确保全名匹配语义。
        /// 需求 9.2、9.3、9.7
        /// </summary>
        private static string WildcardToRegex(string pattern)
        {
            var sb = new System.Text.StringBuilder("^");
            foreach (char c in pattern)
            {
                switch (c)
                {
                    case '*': sb.Append(".*"); break;
                    case '?': sb.Append('.');  break;
                    default:  sb.Append(Regex.Escape(c.ToString())); break;
                }
            }
            sb.Append('$');
            return sb.ToString();
        }

        private static bool TryGetSimpleExtensionWildcard(string pattern, out string extension)
        {
            extension = null;
            if (string.IsNullOrEmpty(pattern)
                || pattern.Length < 3
                || pattern[0] != '*'
                || pattern[1] != '.'
                || pattern.IndexOf('?') >= 0)
            {
                return false;
            }

            if (pattern.IndexOf('*', 1) >= 0)
                return false;

            extension = pattern.Substring(1);
            return extension.Length > 1;
        }

        private static bool TrySimplifyWildcard(
            string pattern,
            out MatchMode simplifiedMode,
            out string simplifiedQuery)
        {
            simplifiedMode = MatchMode.Wildcard;
            simplifiedQuery = null;
            if (string.IsNullOrEmpty(pattern) || pattern.IndexOf('?') >= 0)
                return false;

            var firstStar = pattern.IndexOf('*');
            if (firstStar < 0)
                return false;

            var lastStar = pattern.LastIndexOf('*');
            if (firstStar == lastStar)
            {
                if (firstStar == 0)
                {
                    simplifiedMode = MatchMode.Suffix;
                    simplifiedQuery = pattern.Substring(1);
                    return true;
                }

                if (firstStar == pattern.Length - 1)
                {
                    simplifiedMode = MatchMode.Prefix;
                    simplifiedQuery = pattern.Substring(0, pattern.Length - 1);
                    return true;
                }

                return false;
            }

            if (firstStar == 0
                && lastStar == pattern.Length - 1
                && pattern.IndexOf('*', 1) == lastStar)
            {
                simplifiedMode = MatchMode.Contains;
                simplifiedQuery = pattern.Substring(1, pattern.Length - 2);
                return true;
            }

            return false;
        }

        private static (List<FileRecord> page, int total) ExtensionMatch(
            MemoryIndex index,
            string extension,
            Dictionary<string, List<FileRecord>> extensionHashMap,
            int offset,
            int maxResults,
            CancellationToken ct = default)
        {
            var page = new List<FileRecord>(Math.Min(maxResults, 64));
            if (extensionHashMap == null
                || string.IsNullOrEmpty(extension)
                || !extensionHashMap.TryGetValue(extension, out var bucket)
                || bucket == null
                || bucket.Count == 0)
            {
                return (page, 0);
            }

            var total = 0;
            var hasDeletedOverlay = index != null && index.HasDeletedOverlay;
            for (var i = 0; i < bucket.Count; i++)
            {
                if (((i + 1) & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                var record = bucket[i];
                if (!IsSearchVisible(index, hasDeletedOverlay, record))
                    continue;

                total++;
                if (total > offset && page.Count < maxResults)
                    page.Add(record);
            }

            return (page, total);
        }

        /// <summary>
        /// 包含匹配：先用 SortedArray 二分定位精确同名命中，再对候选数组线性扫描。
        /// 需求 3.1、3.3、7.1
        /// </summary>
        private static (List<FileRecord> page, int total) ContainsMatch(
            MemoryIndex index,
            string query,
            FileRecord[] sortedArray,
            int offset,
            int maxResults,
            CancellationToken ct = default)
        {
            var result = ContainsMatchDetailed(
                index,
                query,
                sortedArray,
                offset,
                maxResults,
                collectAllMatches: false,
                ct: ct);
            return (result.page, result.total);
        }

        private static (List<FileRecord> page, int total, FileRecord[] allMatches) ContainsMatchDetailed(
            MemoryIndex index,
            string query,
            FileRecord[] sortedArray,
            int offset,
            int maxResults,
            bool collectAllMatches,
            CancellationToken ct = default)
        {
            if (!collectAllMatches
                && offset == 0
                && maxResults > 0
                && sortedArray != null
                && sortedArray.Length >= 250000)
            {
                return ContainsMatchDetailedParallelFirstPage(
                    index,
                    query,
                    sortedArray,
                    maxResults,
                    ct);
            }

            var stopwatch = Stopwatch.StartNew();
            var page = new List<FileRecord>(Math.Min(maxResults, 64));
            var allMatches = collectAllMatches ? new List<FileRecord>() : null;
            var total = 0;
            var candidateCount = sortedArray?.Length ?? 0;
            var hasDeletedOverlay = index != null && index.HasDeletedOverlay;
            IndexPerfLog.Write("INDEX",
                $"[CONTAINS FALLBACK] outcome=start candidateCount={candidateCount} offset={offset} maxResults={maxResults} query={IndexPerfLog.FormatValue(query)}");

            var i = 0;
            try
            {
                foreach (var record in sortedArray)
                {
                    i++;
                    if ((i & 0xFFF) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    if ((i % 500000) == 0)
                    {
                        IndexPerfLog.Write("INDEX",
                            $"[CONTAINS FALLBACK] outcome=progress scanned={i} candidateCount={candidateCount} matched={total} returned={page.Count} elapsedMs={stopwatch.ElapsedMilliseconds} query={IndexPerfLog.FormatValue(query)}");
                    }

                    if (IsSearchVisible(index, hasDeletedOverlay, record) && NameContains(record, query))
                    {
                        total++;
                        allMatches?.Add(record);
                        if (total > offset && page.Count < maxResults)
                            page.Add(record);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                IndexPerfLog.Write("INDEX",
                    $"[CONTAINS FALLBACK] outcome=canceled scanned={i} candidateCount={candidateCount} matched={total} returned={page.Count} elapsedMs={stopwatch.ElapsedMilliseconds} query={IndexPerfLog.FormatValue(query)}");
                throw;
            }

            stopwatch.Stop();
            IndexPerfLog.Write("INDEX",
                $"[CONTAINS FALLBACK] outcome=success scanned={i} candidateCount={candidateCount} matched={total} returned={page.Count} elapsedMs={stopwatch.ElapsedMilliseconds} query={IndexPerfLog.FormatValue(query)}");
            return (page, total, allMatches == null || allMatches.Count == 0 ? Array.Empty<FileRecord>() : allMatches.ToArray());
        }

        private static (List<FileRecord> page, int total, FileRecord[] allMatches) ContainsMatchDetailedParallelFirstPage(
            MemoryIndex index,
            string query,
            FileRecord[] sortedArray,
            int maxResults,
            CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var page = new List<FileRecord>(Math.Min(maxResults, 64));
            var total = 0;
            var candidateCount = sortedArray?.Length ?? 0;
            var hasDeletedOverlay = index != null && index.HasDeletedOverlay;
            IndexPerfLog.Write("INDEX",
                $"[CONTAINS FALLBACK] outcome=start candidateCount={candidateCount} offset=0 maxResults={maxResults} parallel=true query={IndexPerfLog.FormatValue(query)}");

            var scanIndex = 0;
            for (; scanIndex < sortedArray.Length && page.Count < maxResults; scanIndex++)
            {
                if (((scanIndex + 1) & 0xFFF) == 0)
                    ct.ThrowIfCancellationRequested();

                var record = sortedArray[scanIndex];
                if (record == null)
                    continue;

                if (!IsSearchVisible(index, hasDeletedOverlay, record) || !NameContains(record, query))
                    continue;

                total++;
                page.Add(record);
            }

            long remainingTotal = 0;
            try
            {
                Parallel.For<long>(
                    scanIndex,
                    sortedArray.Length,
                    new ParallelOptions
                    {
                        CancellationToken = ct,
                        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
                    },
                    () => 0L,
                    (i, state, local) =>
                    {
                        if (((i + 1) & 0x3FFF) == 0)
                            ct.ThrowIfCancellationRequested();

                        var record = sortedArray[i];
                        if (record == null)
                            return local;

                        return IsSearchVisible(index, hasDeletedOverlay, record) && NameContains(record, query)
                            ? local + 1
                            : local;
                    },
                    local => Interlocked.Add(ref remainingTotal, local));
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                IndexPerfLog.Write("INDEX",
                    $"[CONTAINS FALLBACK] outcome=canceled scanned={sortedArray.Length} candidateCount={candidateCount} matched={total + remainingTotal} returned={page.Count} elapsedMs={stopwatch.ElapsedMilliseconds} parallel=true query={IndexPerfLog.FormatValue(query)}");
                throw;
            }

            total += checked((int)remainingTotal);
            stopwatch.Stop();
            IndexPerfLog.Write("INDEX",
                $"[CONTAINS FALLBACK] outcome=success scanned={sortedArray.Length} candidateCount={candidateCount} matched={total} returned={page.Count} elapsedMs={stopwatch.ElapsedMilliseconds} parallel=true query={IndexPerfLog.FormatValue(query)}");
            return (page, total, Array.Empty<FileRecord>());
        }

        private static bool NameContains(FileRecord record, string query)
        {
            return record != null
                   && !string.IsNullOrEmpty(record.LowerName)
                   && !string.IsNullOrEmpty(query)
                   && record.LowerName.IndexOf(query, StringComparison.Ordinal) >= 0;
        }

        private static bool IsSearchVisible(MemoryIndex index, bool hasDeletedOverlay, FileRecord record)
        {
            return record != null && (!hasDeletedOverlay || index == null || !index.IsDeleted(record));
        }

        private (List<FileRecord> page, int total, int scanned, int added, long verifyMs) MergeLiveAddedMatches(
            MemoryIndex index,
            List<FileRecord> basePage,
            int baseTotal,
            MatchMode mode,
            string normalizedQuery,
            SearchTypeFilter filter,
            string pathPrefix,
            int offset,
            int maxResults,
            IProgress<string> progress,
            CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var liveRecords = index?.GetLiveAddedRecordsSnapshot() ?? Array.Empty<FileRecord>();
            if (liveRecords.Length == 0)
            {
                stopwatch.Stop();
                return (basePage ?? new List<FileRecord>(), baseTotal, 0, 0, stopwatch.ElapsedMilliseconds);
            }

            Regex regex = null;
            if (mode == MatchMode.Regex)
            {
                try
                {
                    regex = new Regex(normalizedQuery ?? string.Empty, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
                }
                catch (ArgumentException)
                {
                    progress?.Report("正则表达式无效");
                    stopwatch.Stop();
                    return (basePage ?? new List<FileRecord>(), baseTotal, liveRecords.Length, 0, stopwatch.ElapsedMilliseconds);
                }
            }

            var page = basePage ?? new List<FileRecord>(Math.Min(Math.Max(maxResults, 0), 64));
            var total = baseTotal;
            var added = 0;
            var normalizedMaxResults = Math.Max(maxResults, 0);
            var normalizedOffset = Math.Max(offset, 0);
            var normalizedPrefix = string.IsNullOrWhiteSpace(pathPrefix)
                ? null
                : (pathPrefix.EndsWith("\\", StringComparison.Ordinal) ? pathPrefix : pathPrefix + "\\");

            for (var i = 0; i < liveRecords.Length; i++)
            {
                if (((i + 1) & 0x3FF) == 0)
                    ct.ThrowIfCancellationRequested();

                var record = liveRecords[i];
                if (record == null
                    || !MatchesSearchFilter(record, filter)
                    || !LiveRecordMatchesMode(record, mode, normalizedQuery, regex))
                {
                    continue;
                }

                if (normalizedPrefix != null)
                {
                    var fullPath = _enumerator.ResolveFullPath(record.DriveLetter, record.ParentFrn, record.OriginalName)
                                   ?? (record.DriveLetter + ":\\" + record.OriginalName);
                    if (!fullPath.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                total++;
                added++;
                if (total > normalizedOffset && page.Count < normalizedMaxResults)
                    page.Add(record);
            }

            stopwatch.Stop();
            return (page, total, liveRecords.Length, added, stopwatch.ElapsedMilliseconds);
        }

        private static bool LiveRecordMatchesMode(FileRecord record, MatchMode mode, string normalizedQuery, Regex regex)
        {
            if (record == null || string.IsNullOrEmpty(record.LowerName))
                return false;

            switch (mode)
            {
                case MatchMode.Prefix:
                    return !string.IsNullOrEmpty(normalizedQuery)
                           && record.LowerName.StartsWith(normalizedQuery, StringComparison.Ordinal);
                case MatchMode.Suffix:
                    return !string.IsNullOrEmpty(normalizedQuery)
                           && record.LowerName.EndsWith(normalizedQuery, StringComparison.Ordinal);
                case MatchMode.Regex:
                    if (regex == null)
                        return false;
                    try
                    {
                        return regex.IsMatch(record.LowerName);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return false;
                    }
                case MatchMode.Wildcard:
                    if (TryGetSimpleExtensionWildcard(normalizedQuery, out var extension))
                        return record.LowerName.EndsWith(extension, StringComparison.Ordinal);
                    if (TrySimplifyWildcard(normalizedQuery, out var simplifiedMode, out var simplifiedQuery))
                        return LiveRecordMatchesMode(record, simplifiedMode, simplifiedQuery, null);
                    return Regex.IsMatch(record.LowerName, WildcardToRegex(normalizedQuery), RegexOptions.IgnoreCase);
                default:
                    return NameContains(record, normalizedQuery);
            }
        }

        private static int DeduplicateResultsByFullPath(List<ScannedFileInfo> results)
        {
            if (results == null || results.Count <= 1)
                return 0;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var writeIndex = 0;
            var duplicateCount = 0;
            for (var i = 0; i < results.Count; i++)
            {
                var item = results[i];
                var fullPath = item?.FullPath ?? string.Empty;
                if (!seen.Add(fullPath))
                {
                    duplicateCount++;
                    continue;
                }

                results[writeIndex++] = item;
            }

            if (duplicateCount > 0)
            {
                results.RemoveRange(writeIndex, duplicateCount);
            }

            return duplicateCount;
        }

        private static bool TryGetExactNameRange(FileRecord[] sortedArray, string lowerName, out int start, out int end)
        {
            start = 0;
            end = 0;
            if (sortedArray == null || sortedArray.Length == 0 || string.IsNullOrEmpty(lowerName))
                return false;

            var lo = 0;
            var hi = sortedArray.Length;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) / 2);
                var compare = string.CompareOrdinal(sortedArray[mid]?.LowerName, lowerName);
                if (compare < 0)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            start = lo;
            hi = sortedArray.Length;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) / 2);
                var compare = string.CompareOrdinal(sortedArray[mid]?.LowerName, lowerName);
                if (compare <= 0)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            end = lo;
            return start < end;
        }

        private static FileRecord FindRecordByNameParentAndDrive(
            FileRecord[] sortedArray,
            string lowerName,
            ulong parentFrn,
            char driveLetter,
            MemoryIndex index,
            out bool isDeleted)
        {
            isDeleted = false;
            if (!TryGetExactNameRange(sortedArray, lowerName, out var start, out var end))
                return null;

            FileRecord deletedRecord = null;

            for (var i = start; i < end; i++)
            {
                var record = sortedArray[i];
                if (record != null
                    && record.ParentFrn == parentFrn
                    && record.DriveLetter == driveLetter)
                {
                    var deleted = index != null && index.IsDeleted(record);
                    if (!deleted)
                    {
                        isDeleted = false;
                        return record;
                    }

                    deletedRecord = record;
                }
            }

            if (deletedRecord != null)
            {
                isDeleted = true;
                return deletedRecord;
            }

            return null;
        }

        private static FileRecord[] GetCandidateSource(MemoryIndex index, SearchTypeFilter filter, CancellationToken ct)
        {
            switch (filter)
            {
                case SearchTypeFilter.Folder:
                    return index.AreDerivedStructuresReady ? index.DirectorySortedArray : BuildFilteredCandidateSource(index.SortedArray, filter, ct);
                case SearchTypeFilter.Launchable:
                    return index.AreDerivedStructuresReady ? index.LaunchableSortedArray : BuildFilteredCandidateSource(index.SortedArray, filter, ct);
                case SearchTypeFilter.Script:
                    return index.AreDerivedStructuresReady ? index.ScriptSortedArray : BuildFilteredCandidateSource(index.SortedArray, filter, ct);
                case SearchTypeFilter.Log:
                    return index.AreDerivedStructuresReady ? index.LogSortedArray : BuildFilteredCandidateSource(index.SortedArray, filter, ct);
                case SearchTypeFilter.Config:
                    return index.AreDerivedStructuresReady ? index.ConfigSortedArray : BuildFilteredCandidateSource(index.SortedArray, filter, ct);
                default:
                    return index.SortedArray;
            }
        }

        private static FileRecord[] BuildFilteredCandidateSource(FileRecord[] sortedArray, SearchTypeFilter filter, CancellationToken ct)
        {
            if (sortedArray == null || sortedArray.Length == 0)
                return Array.Empty<FileRecord>();

            var result = new List<FileRecord>();
            for (var i = 0; i < sortedArray.Length; i++)
            {
                if (((i + 1) & 0x3FFF) == 0)
                    ct.ThrowIfCancellationRequested();

                var record = sortedArray[i];
                if (MatchesSearchFilter(record, filter))
                    result.Add(record);
            }

            return result.Count == 0 ? Array.Empty<FileRecord>() : result.ToArray();
        }

        private static bool MatchesSearchFilter(FileRecord record, SearchTypeFilter filter)
        {
            if (record == null)
                return false;

            if (filter == SearchTypeFilter.All)
                return true;

            if (filter == SearchTypeFilter.Folder)
                return record.IsDirectory;

            if (record.IsDirectory)
                return false;

            var lowerName = record.LowerName;
            if (string.IsNullOrEmpty(lowerName))
                return false;

            var dotIndex = lowerName.LastIndexOf('.');
            if (dotIndex < 0 || dotIndex == lowerName.Length - 1)
                return false;

            var extension = lowerName.Substring(dotIndex);
            switch (filter)
            {
                case SearchTypeFilter.Launchable:
                    return SearchTypeFilterHelper.IsLaunchableExtension(extension);
                case SearchTypeFilter.Script:
                    return SearchTypeFilterHelper.IsScriptExtension(extension);
                case SearchTypeFilter.Log:
                    return SearchTypeFilterHelper.IsLogExtension(extension);
                case SearchTypeFilter.Config:
                    return SearchTypeFilterHelper.IsConfigExtension(extension);
                default:
                    return false;
            }
        }

        // ── 搜索入口 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 在内存索引中搜索关键词，整合四种匹配路径，不执行任何磁盘读写。
        /// 需求 3.5、3.6、7.6
        /// </summary>
        /// <param name="keyword">搜索关键词，支持 ^前缀、后缀$、/正则/ 和普通包含匹配。</param>
        /// <param name="maxResults">最多返回的结果数量。</param>
        /// <param name="progress">进度回调（用于上报正则无效等错误）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>搜索结果，<see cref="ScannedFileInfo.SizeBytes"/> 和 <see cref="ScannedFileInfo.ModifiedTimeUtc"/> 填默认值。</returns>
        public Task<SearchQueryResult> SearchAsync(
            string keyword,
            int maxResults,
            int offset,
            IProgress<string> progress,
            CancellationToken ct)
        {
            return SearchAsync(keyword, maxResults, offset, SearchTypeFilter.All, progress, ct);
        }

        public Task NotifyDeletedAsync(string fullPath, bool isDirectory, CancellationToken ct)
        {
            return Task.Run(() => MarkDeletedPath(fullPath, isDirectory, "ui-delete"), ct);
        }

        public Task<SearchQueryResult> SearchAsync(
            string keyword,
            int maxResults,
            int offset,
            SearchTypeFilter filter,
            IProgress<string> progress,
            CancellationToken ct)
        {
            return Task.Run(() =>
            {
                Interlocked.Increment(ref _activeSearchCount);
                var idx = _index;
                var totalStopwatch = Stopwatch.StartNew();
                long matchElapsedMilliseconds = 0;
                long resolveElapsedMilliseconds = 0;
                long containsIntersectMilliseconds = 0;
                long containsVerifyMilliseconds = 0;
                string pathPrefix = null;
                string searchTerm = null;
                string normalizedQuery = null;
                string containsMode = null;
                string containsQueryForWarmup = null;
                var mode = MatchMode.Contains;
                var candidateCount = 0;
                var containsCandidateCount = 0;
                var pathPrefilterApplied = false;
                var pathPostFilterRequired = false;
                var candidateSourceDeferredFilter = false;
                var pathStrategy = "none";
                var pathContainsPostingsFirstApplied = false;
                List<FileRecord> pathContainsMatchedPage = null;
                int pathContainsMatchedTotal = 0;
                var pathPreMatchedApplied = false;
                List<FileRecord> pathPreMatchedPage = null;
                int pathPreMatchedTotal = 0;
                var liveAddedAlreadyIncluded = false;
                var containsCacheScopeKey = string.Empty;
                var indexContentVersion = idx.ContentVersion;

                try
                {
                    if (string.IsNullOrWhiteSpace(keyword))
                    {
                        return FinalizeSearchResult(
                            totalStopwatch,
                            "empty-keyword",
                            keyword,
                            pathPrefix,
                            searchTerm,
                            normalizedQuery,
                            mode,
                            filter,
                            offset,
                            maxResults,
                            candidateCount,
                            matchElapsedMilliseconds,
                            resolveElapsedMilliseconds,
                            CreateEmptySearchResult(idx.TotalCount));
                    }

                    // 需求 10.1：路径范围解析，在 DetectMatchMode 之前执行
                    (pathPrefix, searchTerm) = ParsePathScope(keyword);

                    // 若 searchTerm 为空（用户只输入了路径前缀），直接返回空结果
                    if (string.IsNullOrWhiteSpace(searchTerm))
                    {
                        return FinalizeSearchResult(
                            totalStopwatch,
                            "path-only",
                            keyword,
                            pathPrefix,
                            searchTerm,
                            normalizedQuery,
                            mode,
                            filter,
                            offset,
                            maxResults,
                            candidateCount,
                            matchElapsedMilliseconds,
                            resolveElapsedMilliseconds,
                            CreateEmptySearchResult(idx.TotalCount));
                    }

                    (mode, normalizedQuery) = DetectMatchMode(searchTerm);

                    var normalizedOffset = Math.Max(offset, 0);
                    var normalizedMaxResults = Math.Max(maxResults, 0);
                    var fetchLimit = normalizedMaxResults;
                    var fetchOffset = normalizedOffset;
                    FileRecord[] candidateSource;

                    if (pathPrefix != null)
                    {
                        var pathStopwatch = Stopwatch.StartNew();
                        if (TryGetDriveRoot(pathPrefix, out var rootDriveLetter)
                            && mode == MatchMode.Contains
                            && normalizedQuery != null
                            && normalizedQuery.Length <= 2
                            && idx.TryShortContainsHotDriveSearch(
                                normalizedQuery,
                                rootDriveLetter,
                                filter,
                                normalizedOffset,
                                normalizedMaxResults,
                                ct,
                                out var rootShortResult))
                        {
                            pathContainsPostingsFirstApplied = true;
                            pathContainsMatchedPage = rootShortResult.Page;
                            pathContainsMatchedTotal = rootShortResult.Total;
                            containsMode = rootShortResult.Mode;
                            containsQueryForWarmup = normalizedQuery;
                            containsCandidateCount = rootShortResult.CandidateCount;
                            containsVerifyMilliseconds = rootShortResult.VerifyMs;
                            candidateSource = rootShortResult.Page == null
                                ? Array.Empty<FileRecord>()
                                : rootShortResult.Page.ToArray();
                            pathPrefilterApplied = true;
                            pathStrategy = "short-hot-drive";
                            pathStopwatch.Stop();
                            UsnDiagLog.Write(
                                $"[PATH PREFILTER] outcome=success strategy=short-hot-drive elapsedMs={pathStopwatch.ElapsedMilliseconds} " +
                                $"drive={char.ToUpperInvariant(rootDriveLetter)} containsMode={IndexPerfLog.FormatValue(rootShortResult.Mode)} " +
                                $"candidateCount={rootShortResult.CandidateCount} matched={pathContainsMatchedTotal} filter={filter} " +
                                $"pathPrefix={IndexPerfLog.FormatValue(pathPrefix)}");
                        }
                        else if (TryGetDriveRoot(pathPrefix, out rootDriveLetter)
                                 && mode == MatchMode.Contains
                                 && normalizedQuery != null
                                 && normalizedQuery.Length <= 2
                                 && (filter == SearchTypeFilter.All || idx.AreDerivedStructuresReady))
                        {
                            var driveFiltered = idx.SearchDriveFilteredContains(
                                rootDriveLetter,
                                normalizedQuery,
                                filter,
                                normalizedOffset,
                                normalizedMaxResults,
                                ct);
                            pathPreMatchedApplied = true;
                            pathPreMatchedPage = driveFiltered.Page;
                            pathPreMatchedTotal = driveFiltered.Total;
                            containsMode = driveFiltered.Mode;
                            containsQueryForWarmup = normalizedQuery;
                            containsCandidateCount = driveFiltered.CandidateCount;
                            containsVerifyMilliseconds = driveFiltered.VerifyMs;
                            candidateSource = driveFiltered.Page == null
                                ? Array.Empty<FileRecord>()
                                : driveFiltered.Page.ToArray();
                            pathPrefilterApplied = true;
                            pathStrategy = "drive-filtered-scan";
                            pathStopwatch.Stop();
                            UsnDiagLog.Write(
                                $"[PATH PREFILTER] outcome=success strategy=drive-filtered-scan elapsedMs={pathStopwatch.ElapsedMilliseconds} " +
                                $"drive={char.ToUpperInvariant(rootDriveLetter)} candidateCount={driveFiltered.CandidateCount} " +
                                $"matched={pathPreMatchedTotal} filter={filter} pathPrefix={IndexPerfLog.FormatValue(pathPrefix)}");
                        }
                        else if (TryGetDriveRoot(pathPrefix, out rootDriveLetter)
                                 && mode == MatchMode.Contains
                                 && normalizedQuery != null
                                 && (filter == SearchTypeFilter.Folder
                                     || (filter != SearchTypeFilter.All && !idx.HasContainsAccelerator)))
                        {
                            var driveFiltered = idx.SearchDriveFilteredContains(
                                rootDriveLetter,
                                normalizedQuery,
                                filter,
                                normalizedOffset,
                                normalizedMaxResults,
                                ct);
                            pathPreMatchedApplied = true;
                            pathPreMatchedPage = driveFiltered.Page;
                            pathPreMatchedTotal = driveFiltered.Total;
                            containsMode = driveFiltered.Mode;
                            containsQueryForWarmup = normalizedQuery;
                            containsCandidateCount = driveFiltered.CandidateCount;
                            containsVerifyMilliseconds = driveFiltered.VerifyMs;
                            candidateSource = driveFiltered.Page == null
                                ? Array.Empty<FileRecord>()
                                : driveFiltered.Page.ToArray();
                            pathPrefilterApplied = true;
                            pathStrategy = "drive-filtered-scan";
                            pathStopwatch.Stop();
                            UsnDiagLog.Write(
                                $"[PATH PREFILTER] outcome=success strategy=drive-filtered-scan elapsedMs={pathStopwatch.ElapsedMilliseconds} " +
                                $"drive={char.ToUpperInvariant(rootDriveLetter)} candidateCount={driveFiltered.CandidateCount} " +
                                $"matched={pathPreMatchedTotal} filter={filter} pathPrefix={IndexPerfLog.FormatValue(pathPrefix)}");
                        }
                        else if (TryGetDriveRoot(pathPrefix, out rootDriveLetter)
                            && mode == MatchMode.Contains
                            && normalizedQuery != null
                            && normalizedQuery.Length >= 1
                            && TryContainsAccelerated(
                                idx,
                                normalizedQuery,
                                filter == SearchTypeFilter.Folder ? SearchTypeFilter.All : filter,
                                0,
                                int.MaxValue,
                                ct,
                                out var rootContainsResult)
                            && rootContainsResult != null
                            && rootContainsResult.Total <= MaxPostingsFirstPathVerifyRecords)
                        {
                            var driveVerify = FilterContainsMatchesByDriveAndFilter(
                                rootContainsResult.Page,
                                rootDriveLetter,
                                filter,
                                normalizedOffset,
                                normalizedMaxResults,
                                ct);
                            pathContainsPostingsFirstApplied = true;
                            pathContainsMatchedPage = driveVerify.page;
                            pathContainsMatchedTotal = driveVerify.total;
                            liveAddedAlreadyIncluded = rootContainsResult.IncludesLiveOverlay;
                            containsMode = rootContainsResult.Mode + (filter == SearchTypeFilter.All ? "+drive" : "+drive-filter");
                            containsQueryForWarmup = normalizedQuery;
                            containsCandidateCount = rootContainsResult.CandidateCount;
                            containsIntersectMilliseconds = rootContainsResult.IntersectMs;
                            containsVerifyMilliseconds = rootContainsResult.VerifyMs + driveVerify.verifyMs;
                            candidateSource = rootContainsResult.Page == null
                                ? Array.Empty<FileRecord>()
                                : rootContainsResult.Page.ToArray();
                            pathPrefilterApplied = true;
                            pathStrategy = "postings-first-drive";
                            pathStopwatch.Stop();
                            UsnDiagLog.Write(
                                $"[PATH PREFILTER] outcome=success strategy=postings-first-drive elapsedMs={pathStopwatch.ElapsedMilliseconds} " +
                                $"drive={char.ToUpperInvariant(rootDriveLetter)} containsMode={IndexPerfLog.FormatValue(rootContainsResult.Mode)} " +
                                $"containsTotal={rootContainsResult.Total} candidateCount={candidateSource.Length} matched={pathContainsMatchedTotal} " +
                                $"filter={filter} pathPrefix={IndexPerfLog.FormatValue(pathPrefix)}");
                        }
                        else if (TryGetDriveRoot(pathPrefix, out rootDriveLetter)
                                 && mode == MatchMode.Wildcard
                                 && filter == SearchTypeFilter.All
                                 && TryGetSimpleExtensionWildcard(normalizedQuery, out var rootExtension)
                                 && idx.TrySearchDriveExtension(
                                     rootDriveLetter,
                                     rootExtension,
                                     normalizedOffset,
                                     normalizedMaxResults,
                                     ct,
                                     out var rootExtensionResult))
                        {
                            pathPreMatchedApplied = true;
                            pathPreMatchedPage = rootExtensionResult.Page;
                            pathPreMatchedTotal = rootExtensionResult.Total;
                            candidateSource = rootExtensionResult.Page == null
                                ? Array.Empty<FileRecord>()
                                : rootExtensionResult.Page.ToArray();
                            pathPrefilterApplied = true;
                            pathStrategy = "extension-first-drive";
                            pathStopwatch.Stop();
                            UsnDiagLog.Write(
                                $"[PATH PREFILTER] outcome=success strategy=extension-first-drive elapsedMs={pathStopwatch.ElapsedMilliseconds} " +
                                $"drive={char.ToUpperInvariant(rootDriveLetter)} extension={IndexPerfLog.FormatValue(rootExtension)} " +
                                $"extensionCandidates={rootExtensionResult.CandidateCount} matched={pathPreMatchedTotal} filter={filter} " +
                                $"pathPrefix={IndexPerfLog.FormatValue(pathPrefix)}");
                        }
                        else if (TryGetDriveRoot(pathPrefix, out rootDriveLetter))
                        {
                            if (mode == MatchMode.Contains
                                && filter != SearchTypeFilter.All
                                && idx.AreDerivedStructuresReady)
                            {
                                var driveFiltered = idx.SearchDriveFilteredContains(
                                    rootDriveLetter,
                                    normalizedQuery,
                                    filter,
                                    normalizedOffset,
                                    normalizedMaxResults,
                                    ct);
                                pathPreMatchedApplied = true;
                                pathPreMatchedPage = driveFiltered.Page;
                                pathPreMatchedTotal = driveFiltered.Total;
                                containsMode = driveFiltered.Mode;
                                containsQueryForWarmup = normalizedQuery;
                                containsCandidateCount = driveFiltered.CandidateCount;
                                containsVerifyMilliseconds = driveFiltered.VerifyMs;
                                candidateSource = driveFiltered.Page == null
                                    ? Array.Empty<FileRecord>()
                                    : driveFiltered.Page.ToArray();
                                pathPrefilterApplied = true;
                                pathStrategy = "drive-filtered-scan";
                                pathStopwatch.Stop();
                                UsnDiagLog.Write(
                                    $"[PATH PREFILTER] outcome=success strategy=drive-filtered-scan elapsedMs={pathStopwatch.ElapsedMilliseconds} " +
                                    $"drive={char.ToUpperInvariant(rootDriveLetter)} candidateCount={driveFiltered.CandidateCount} " +
                                    $"matched={pathPreMatchedTotal} filter={filter} pathPrefix={IndexPerfLog.FormatValue(pathPrefix)}");
                            }
                            else
                            {
                                candidateSource = idx.GetDriveCandidates(rootDriveLetter, filter, ct);
                                pathPrefilterApplied = true;
                                pathStrategy = "drive-filter";
                                pathStopwatch.Stop();
                                UsnDiagLog.Write(
                                    $"[PATH PREFILTER] outcome=success strategy=drive-filter elapsedMs={pathStopwatch.ElapsedMilliseconds} " +
                                    $"drive={char.ToUpperInvariant(rootDriveLetter)} candidateCount={candidateSource.Length} filter={filter} " +
                                    $"pathPrefix={IndexPerfLog.FormatValue(pathPrefix)}");
                            }
                        }
                        else if (mode == MatchMode.Contains
                                 && normalizedQuery != null
                                 && normalizedQuery.Length <= 2
                                 && _enumerator.TryGetDirectorySubtree(pathPrefix, out var shortPathScope)
                                 && shortPathScope.DirectoryFrns != null
                                 && shortPathScope.DirectoryFrns.Length <= MaxPreferPathFirstDirectories
                                 && idx.ParentSortedArray != null
                                 && idx.ParentSortedArray.Length > 0)
                        {
                            var subtreeContainsResult = idx.SearchSubtreeContains(
                                shortPathScope.DriveLetter,
                                shortPathScope.DirectoryFrns,
                                normalizedQuery,
                                filter,
                                normalizedOffset,
                                normalizedMaxResults,
                                ct);
                            pathPreMatchedApplied = true;
                            pathPreMatchedPage = subtreeContainsResult.Page;
                            pathPreMatchedTotal = subtreeContainsResult.Total;
                            containsMode = subtreeContainsResult.Mode;
                            containsQueryForWarmup = normalizedQuery;
                            containsCandidateCount = subtreeContainsResult.CandidateCount;
                            containsIntersectMilliseconds = 0;
                            containsVerifyMilliseconds = subtreeContainsResult.VerifyMs;
                            candidateSource = Array.Empty<FileRecord>();
                            pathPrefilterApplied = true;
                            pathStrategy = "path-subtree-short";
                            pathStopwatch.Stop();
                            UsnDiagLog.Write(
                                $"[PATH PREFILTER] outcome=success strategy=path-subtree-short elapsedMs={pathStopwatch.ElapsedMilliseconds} " +
                                $"containsMode={IndexPerfLog.FormatValue(subtreeContainsResult.Mode)} containsTotal={subtreeContainsResult.Total} " +
                                $"candidateCount={subtreeContainsResult.CandidateCount} matched={pathPreMatchedTotal} filter={filter} " +
                                $"pathPrefix={IndexPerfLog.FormatValue(pathPrefix)} pathDrive={shortPathScope.DriveLetter} rootFrn={shortPathScope.RootFrn}");
                        }
                        else if (mode == MatchMode.Contains
                                 && normalizedQuery != null
                                 && normalizedQuery.Length <= 2
                                 && _enumerator.TryGetDirectorySubtree(pathPrefix, out var directShortPathScope)
                                 && (idx.ParentSortedArray == null || idx.ParentSortedArray.Length == 0)
                                 && idx.TryShortContainsSearchInPathScope(
                                     normalizedQuery,
                                     filter,
                                     normalizedOffset,
                                     normalizedMaxResults,
                                     directShortPathScope.DriveLetter,
                                     directShortPathScope.DirectoryFrns,
                                     ct,
                                     out var shortPathContainsResult))
                        {
                            pathPreMatchedApplied = true;
                            pathPreMatchedPage = shortPathContainsResult.Page;
                            pathPreMatchedTotal = shortPathContainsResult.Total;
                            liveAddedAlreadyIncluded = shortPathContainsResult.IncludesLiveOverlay;
                            containsMode = shortPathContainsResult.Mode;
                            containsQueryForWarmup = normalizedQuery;
                            containsCandidateCount = shortPathContainsResult.CandidateCount;
                            containsIntersectMilliseconds = 0;
                            containsVerifyMilliseconds = shortPathContainsResult.VerifyMs;
                            candidateSource = Array.Empty<FileRecord>();
                            pathPrefilterApplied = true;
                            pathStrategy = "short-index-path";
                            pathStopwatch.Stop();
                            UsnDiagLog.Write(
                                $"[PATH PREFILTER] outcome=success strategy=short-index-path elapsedMs={pathStopwatch.ElapsedMilliseconds} " +
                                $"containsMode={IndexPerfLog.FormatValue(shortPathContainsResult.Mode)} containsTotal={shortPathContainsResult.Total} " +
                                $"candidateCount={shortPathContainsResult.CandidateCount} matched={pathPreMatchedTotal} filter={filter} " +
                                $"pathPrefix={IndexPerfLog.FormatValue(pathPrefix)} pathDrive={directShortPathScope.DriveLetter} rootFrn={directShortPathScope.RootFrn}");
                        }
                        else if (_enumerator.TryGetDirectorySubtree(pathPrefix, out var preferredPathScope)
                                 && preferredPathScope.DirectoryFrns != null
                                 && preferredPathScope.DirectoryFrns.Length <= MaxPreferPathFirstDirectories)
                        {
                            var pathCandidateCacheHit = TryGetPathCandidateCache(
                                pathPrefix,
                                filter,
                                preferredPathScope,
                                out candidateSource);
                            if (!pathCandidateCacheHit)
                            {
                                candidateSource = idx.GetSubtreeCandidates(
                                    preferredPathScope.DriveLetter,
                                    preferredPathScope.DirectoryFrns,
                                    filter,
                                    ct);
                                UpdatePathCandidateCache(
                                    pathPrefix,
                                    filter,
                                    preferredPathScope,
                                    candidateSource);
                            }

                            pathPrefilterApplied = true;
                            pathStrategy = "path-first-small";
                            pathStopwatch.Stop();
                            UsnDiagLog.Write(
                                $"[PATH PREFILTER] outcome=success strategy=path-first-small elapsedMs={pathStopwatch.ElapsedMilliseconds} " +
                                $"drive={preferredPathScope.DriveLetter} rootFrn={preferredPathScope.RootFrn} directories={preferredPathScope.DirectoryFrns.Length} " +
                                $"candidateCount={candidateSource.Length} filter={filter} cache={(pathCandidateCacheHit ? "hit" : "miss")} " +
                                $"pathPrefix={IndexPerfLog.FormatValue(pathPrefix)}");
                        }
                        else if (mode == MatchMode.Contains
                                 && normalizedQuery != null
                                 && normalizedQuery.Length >= 3
                                 && TryContainsAccelerated(idx, normalizedQuery, filter, 0, int.MaxValue, ct, out var pathContainsResult)
                                 && pathContainsResult != null
                                 && pathContainsResult.Total <= MaxPostingsFirstPathVerifyRecords)
                        {
                            var pathVerify = FilterContainsMatchesByPathPrefix(
                                pathContainsResult.Page,
                                pathPrefix,
                                normalizedOffset,
                                normalizedMaxResults,
                                ct);
                            pathContainsPostingsFirstApplied = true;
                            pathContainsMatchedPage = pathVerify.page;
                            pathContainsMatchedTotal = pathVerify.total;
                            liveAddedAlreadyIncluded = pathContainsResult.IncludesLiveOverlay;
                            containsMode = pathContainsResult.Mode + "+path";
                            containsQueryForWarmup = normalizedQuery;
                            containsCandidateCount = pathContainsResult.CandidateCount;
                            containsIntersectMilliseconds = pathContainsResult.IntersectMs;
                            containsVerifyMilliseconds = pathContainsResult.VerifyMs + pathVerify.verifyMs;
                            candidateSource = pathContainsResult.Page == null
                                ? Array.Empty<FileRecord>()
                                : pathContainsResult.Page.ToArray();
                            pathPrefilterApplied = true;
                            pathStrategy = "postings-first";
                            pathStopwatch.Stop();
                            UsnDiagLog.Write(
                                $"[PATH PREFILTER] outcome=success strategy=postings-first elapsedMs={pathStopwatch.ElapsedMilliseconds} " +
                                $"containsMode={IndexPerfLog.FormatValue(pathContainsResult.Mode)} containsTotal={pathContainsResult.Total} " +
                                $"candidateCount={candidateSource.Length} matched={pathContainsMatchedTotal} filter={filter} " +
                                $"pathPrefix={IndexPerfLog.FormatValue(pathPrefix)}");
                        }
                        else if (_enumerator.TryGetDirectorySubtree(pathPrefix, out var pathScope))
                        {
                            var pathCandidateCacheHit = TryGetPathCandidateCache(
                                pathPrefix,
                                filter,
                                pathScope,
                                out candidateSource);
                            if (!pathCandidateCacheHit)
                            {
                                candidateSource = idx.GetSubtreeCandidates(
                                    pathScope.DriveLetter,
                                    pathScope.DirectoryFrns,
                                    filter,
                                    ct);
                                UpdatePathCandidateCache(
                                    pathPrefix,
                                    filter,
                                    pathScope,
                                    candidateSource);
                            }

                            pathPrefilterApplied = true;
                            pathStrategy = "path-first";
                            pathStopwatch.Stop();
                            UsnDiagLog.Write(
                                $"[PATH PREFILTER] outcome=success strategy=path-first elapsedMs={pathStopwatch.ElapsedMilliseconds} " +
                                $"drive={pathScope.DriveLetter} rootFrn={pathScope.RootFrn} directories={pathScope.DirectoryFrns.Length} " +
                                $"candidateCount={candidateSource.Length} filter={filter} cache={(pathCandidateCacheHit ? "hit" : "miss")} " +
                                $"pathPrefix={IndexPerfLog.FormatValue(pathPrefix)}");
                        }
                        else
                        {
                            candidateSource = GetCandidateSource(idx, filter, ct);
                            fetchLimit = int.MaxValue;
                            fetchOffset = 0;
                            pathPostFilterRequired = true;
                            pathStrategy = "post-filter";
                            pathStopwatch.Stop();
                            UsnDiagLog.Write(
                                $"[PATH PREFILTER] outcome=unresolved strategy=post-filter elapsedMs={pathStopwatch.ElapsedMilliseconds} " +
                                $"fallback=post-filter filter={filter} candidateCount={candidateSource?.Length ?? 0} " +
                                $"pathPrefix={IndexPerfLog.FormatValue(pathPrefix)}");
                        }
                    }
                    else
                    {
                        candidateSourceDeferredFilter = filter != SearchTypeFilter.All
                                                        && !idx.AreDerivedStructuresReady
                                                        && mode == MatchMode.Contains;
                        candidateSource = candidateSourceDeferredFilter
                            ? idx.SortedArray
                            : GetCandidateSource(idx, filter, ct);
                    }

                    candidateCount = candidateSource?.Length ?? 0;
                    containsCacheScopeKey = BuildContainsCacheScopeKey(
                        pathPrefilterApplied ? pathPrefix : null,
                        filter,
                        mode);

                    if (!pathPreMatchedApplied
                        && !pathContainsPostingsFirstApplied
                        && (candidateSource == null || candidateSource.Length == 0)
                        && !idx.HasLiveAddedOverlay)
                    {
                        if (pathPrefix != null)
                            progress?.Report("指定路径下无匹配结果");

                        return FinalizeSearchResult(
                            totalStopwatch,
                            "no-candidates",
                            keyword,
                            pathPrefix,
                            searchTerm,
                            normalizedQuery,
                            mode,
                            filter,
                            offset,
                            maxResults,
                            candidateCount,
                            matchElapsedMilliseconds,
                            resolveElapsedMilliseconds,
                            CreateEmptySearchResult(idx.TotalCount));
                    }

                    List<FileRecord> matched;
                    int totalMatched;
                    var matchStopwatch = Stopwatch.StartNew();
                    if (pathPreMatchedApplied)
                    {
                        matched = pathPreMatchedPage ?? new List<FileRecord>();
                        totalMatched = pathPreMatchedTotal;
                    }
                    else switch (mode)
                    {
                        case MatchMode.Prefix:
                            { var r = PrefixMatch(idx, normalizedQuery, candidateSource, fetchOffset, fetchLimit, ct); matched = r.page; totalMatched = r.total; }
                            break;
                        case MatchMode.Suffix:
                            { var r = SuffixMatch(idx, normalizedQuery, candidateSource, fetchOffset, fetchLimit, ct); matched = r.page; totalMatched = r.total; }
                            break;
                        case MatchMode.Regex:
                            { var r = RegexMatch(idx, normalizedQuery, candidateSource, fetchOffset, fetchLimit, progress, ct); matched = r.page; totalMatched = r.total; }
                            break;
                        case MatchMode.Wildcard:
                            if (filter == SearchTypeFilter.All && TryGetSimpleExtensionWildcard(normalizedQuery, out var extension))
                            {
                                var r = pathPrefilterApplied
                                    ? SuffixMatch(idx, extension, candidateSource, fetchOffset, fetchLimit, ct)
                                    : (idx.HasExtensionIndex
                                        ? ExtensionMatch(idx, extension, idx.ExtensionHashMap, fetchOffset, fetchLimit, ct)
                                        : SuffixMatch(idx, extension, candidateSource, fetchOffset, fetchLimit, ct));
                                matched = r.page;
                                totalMatched = r.total;
                            }
                            else if (TrySimplifyWildcard(normalizedQuery, out var simplifiedMode, out var simplifiedQuery))
                            {
                                switch (simplifiedMode)
                                {
                                    case MatchMode.Prefix:
                                        { var r = PrefixMatch(idx, simplifiedQuery, candidateSource, fetchOffset, fetchLimit, ct); matched = r.page; totalMatched = r.total; }
                                        break;
                                    case MatchMode.Suffix:
                                        { var r = SuffixMatch(idx, simplifiedQuery, candidateSource, fetchOffset, fetchLimit, ct); matched = r.page; totalMatched = r.total; }
                                        break;
                                    default:
                                        if (!pathPrefilterApplied
                                            && TryContainsAccelerated(idx, simplifiedQuery, filter, fetchOffset, fetchLimit, ct, out var wildcardContains))
                                        {
                                            matched = wildcardContains.Page;
                                            totalMatched = wildcardContains.Total;
                                            liveAddedAlreadyIncluded = wildcardContains.IncludesLiveOverlay;
                                            containsMode = wildcardContains.Mode;
                                            containsQueryForWarmup = simplifiedQuery;
                                            containsCandidateCount = wildcardContains.CandidateCount;
                                            containsIntersectMilliseconds = wildcardContains.IntersectMs;
                                            containsVerifyMilliseconds = wildcardContains.VerifyMs;
                                        }
                                        else
                                        {
                                            containsMode = "fallback";
                                            containsCandidateCount = candidateSource?.Length ?? 0;
                                            var r = ContainsMatchWithIncrementalCache(
                                                idx,
                                                containsCacheScopeKey,
                                                indexContentVersion,
                                                simplifiedQuery,
                                                candidateSource,
                                                filter,
                                                pathPostFilterRequired,
                                                fetchOffset,
                                                fetchLimit,
                                                ct);
                                            matched = r.page;
                                            totalMatched = r.total;
                                            containsMode = r.mode;
                                            containsQueryForWarmup = simplifiedQuery;
                                            containsCandidateCount = r.candidateCount;
                                            containsVerifyMilliseconds = r.verifyMs;
                                        }
                                        break;
                                }
                            }
                            else
                            {
                                var r = RegexMatch(idx, WildcardToRegex(normalizedQuery), candidateSource, fetchOffset, fetchLimit, progress, ct);
                                matched = r.page;
                                totalMatched = r.total;
                            }
                            break;
                        default:
                            if (pathContainsPostingsFirstApplied)
                            {
                                matched = pathContainsMatchedPage ?? new List<FileRecord>();
                                totalMatched = pathContainsMatchedTotal;
                            }
                            else if (!pathPrefilterApplied
                                && !PreferFilteredCandidateScan(normalizedQuery, filter, candidateSource)
                                && TryContainsAccelerated(idx, normalizedQuery, filter, fetchOffset, fetchLimit, ct, out var containsResult))
                            {
                                matched = containsResult.Page;
                                totalMatched = containsResult.Total;
                                liveAddedAlreadyIncluded = containsResult.IncludesLiveOverlay;
                                containsMode = containsResult.Mode;
                                containsQueryForWarmup = normalizedQuery;
                                containsCandidateCount = containsResult.CandidateCount;
                                containsIntersectMilliseconds = containsResult.IntersectMs;
                                containsVerifyMilliseconds = containsResult.VerifyMs;
                            }
                            else
                            {
                                if (candidateSourceDeferredFilter)
                                {
                                    candidateSource = GetCandidateSource(idx, filter, ct);
                                    candidateSourceDeferredFilter = false;
                                }

                                containsMode = "fallback";
                                containsCandidateCount = candidateSource?.Length ?? 0;
                                var r = ContainsMatchWithIncrementalCache(
                                    idx,
                                    containsCacheScopeKey,
                                    indexContentVersion,
                                    normalizedQuery,
                                    candidateSource,
                                    filter,
                                    pathPostFilterRequired,
                                    fetchOffset,
                                    fetchLimit,
                                    ct);
                                matched = r.page;
                                totalMatched = r.total;
                                containsMode = r.mode;
                                containsQueryForWarmup = normalizedQuery;
                                containsCandidateCount = r.candidateCount;
                                containsVerifyMilliseconds = r.verifyMs;
                            }
                            break;
                    }
                    QueueContainsWarmupForQuery(idx, containsQueryForWarmup, containsMode, pathPrefilterApplied);

                    if (!liveAddedAlreadyIncluded && idx.HasLiveAddedOverlay)
                    {
                        var liveMerge = MergeLiveAddedMatches(
                            idx,
                            matched,
                            totalMatched,
                            mode,
                            normalizedQuery,
                            filter,
                            pathPrefix,
                            normalizedOffset,
                            normalizedMaxResults,
                            progress,
                            ct);
                        if (liveMerge.scanned > 0)
                        {
                            matched = liveMerge.page;
                            totalMatched = liveMerge.total;
                            containsVerifyMilliseconds += liveMerge.verifyMs;
                            if (liveMerge.added > 0 && mode == MatchMode.Contains)
                                containsMode = string.IsNullOrEmpty(containsMode) ? "live-overlay" : containsMode + "+live-overlay";
                            UsnDiagLog.Write(
                                $"[LIVE DELTA MERGE] scanned={liveMerge.scanned} added={liveMerge.added} total={liveMerge.total} " +
                                $"elapsedMs={liveMerge.verifyMs} filter={filter} mode={mode} pathPrefix={IndexPerfLog.FormatValue(pathPrefix)} " +
                                $"query={IndexPerfLog.FormatValue(normalizedQuery)}");
                        }
                    }

                    matchStopwatch.Stop();
                    matchElapsedMilliseconds = matchStopwatch.ElapsedMilliseconds;

                    ct.ThrowIfCancellationRequested();

                    var resolveStopwatch = Stopwatch.StartNew();

                    // 按需解析完整路径：用 _enumerator 的 FRN 字典（每卷独立缓存）
                    var results = new List<ScannedFileInfo>(Math.Min(matched.Count, normalizedMaxResults));
                    var pathPrefilteredResultPageOnly = pathPrefilterApplied && !pathPostFilterRequired;

                    // 需求 10.2、10.3：路径前缀后置过滤（大小写不敏感）
                    // 确保路径前缀以 \ 结尾，避免误匹配同名前缀目录（如 C:\Users\Desktop2）
                    if (pathPrefilteredResultPageOnly)
                    {
                        var directoryKeys = new HashSet<MftEnumerator.DirectoryPathKey>();
                        foreach (var record in matched)
                        {
                            if (record != null)
                                directoryKeys.Add(new MftEnumerator.DirectoryPathKey(record.DriveLetter, record.ParentFrn));
                        }

                        var directoryPaths = _enumerator.ResolveDirectoryPaths(directoryKeys);
                        foreach (var record in matched)
                        {
                            ct.ThrowIfCancellationRequested();
                            var key = new MftEnumerator.DirectoryPathKey(record.DriveLetter, record.ParentFrn);
                            if (!directoryPaths.TryGetValue(key, out var dirPath) || string.IsNullOrEmpty(dirPath))
                                dirPath = record.DriveLetter + ":";
                            var fullPath = dirPath + "\\" + record.OriginalName;
                            results.Add(new ScannedFileInfo
                            {
                                FullPath = fullPath,
                                FileName = record.OriginalName,
                                SizeBytes = 0,
                                ModifiedTimeUtc = DateTime.MinValue,
                                RootPath = string.Empty,
                                RootDisplayName = string.Empty,
                                IsDirectory = record.IsDirectory
                            });
                        }
                    }
                    else if (pathPostFilterRequired)
                    {
                        var normalizedPrefix = pathPrefix.EndsWith("\\")
                            ? pathPrefix
                            : pathPrefix + "\\";
                        var filteredTotal = 0;
                        foreach (var record in matched)
                        {
                            ct.ThrowIfCancellationRequested();
                            var fullPath = _enumerator.ResolveFullPath(record.DriveLetter, record.ParentFrn, record.OriginalName)
                                           ?? (record.DriveLetter + ":\\" + record.OriginalName);
                            if (!fullPath.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                                continue;

                            filteredTotal++;
                            if (filteredTotal <= normalizedOffset || results.Count >= normalizedMaxResults)
                                continue;

                            results.Add(new ScannedFileInfo
                            {
                                FullPath = fullPath,
                                FileName = record.OriginalName,
                                SizeBytes = 0,
                                ModifiedTimeUtc = DateTime.MinValue,
                                RootPath = string.Empty,
                                RootDisplayName = string.Empty,
                                IsDirectory = record.IsDirectory
                            });
                        }
                        totalMatched = filteredTotal;

                        // 需求 10.7：路径范围内无匹配时上报提示
                        if (totalMatched == 0)
                            progress?.Report("指定路径下无匹配结果");
                    }
                    else
                    {
                        var directoryKeys = new HashSet<MftEnumerator.DirectoryPathKey>();
                        foreach (var record in matched)
                        {
                            if (record != null)
                                directoryKeys.Add(new MftEnumerator.DirectoryPathKey(record.DriveLetter, record.ParentFrn));
                        }

                        var directoryPaths = _enumerator.ResolveDirectoryPaths(directoryKeys);
                        foreach (var record in matched)
                        {
                            ct.ThrowIfCancellationRequested();
                            var key = new MftEnumerator.DirectoryPathKey(record.DriveLetter, record.ParentFrn);
                            if (!directoryPaths.TryGetValue(key, out var dirPath) || string.IsNullOrEmpty(dirPath))
                                dirPath = record.DriveLetter + ":";
                            var fullPath = dirPath + "\\" + record.OriginalName;
                            results.Add(new ScannedFileInfo
                            {
                                FullPath = fullPath,
                                FileName = record.OriginalName,
                                SizeBytes = 0,
                                ModifiedTimeUtc = DateTime.MinValue,
                                RootPath = string.Empty,
                                RootDisplayName = string.Empty,
                                IsDirectory = record.IsDirectory
                            });
                        }
                    }

                    resolveStopwatch.Stop();
                    resolveElapsedMilliseconds = resolveStopwatch.ElapsedMilliseconds;
                    var physicalMatchedCount = totalMatched;
                    var rawReturnedCount = results.Count;
                    var duplicatePathCount = pathPrefilteredResultPageOnly
                        ? 0
                        : DeduplicateResultsByFullPath(results);
                    var physicalTruncated = physicalMatchedCount > normalizedOffset + rawReturnedCount;
                    var uniqueMatchedCount = pathPrefilteredResultPageOnly
                        ? physicalMatchedCount
                        : (physicalTruncated
                            ? Math.Max(results.Count, physicalMatchedCount - duplicatePathCount)
                            : (normalizedOffset == 0
                                ? results.Count
                                : Math.Max(results.Count, physicalMatchedCount - duplicatePathCount)));
                    totalMatched = uniqueMatchedCount;

                    if (mode == MatchMode.Contains || (mode == MatchMode.Wildcard && !string.IsNullOrEmpty(containsMode)))
                    {
                        LogContainsDiagnostics(
                            "success",
                            keyword,
                            searchTerm,
                            normalizedQuery,
                            filter,
                            containsMode ?? "fallback",
                            containsCandidateCount,
                            containsIntersectMilliseconds,
                            containsVerifyMilliseconds,
                            totalMatched);
                    }

                    return FinalizeSearchResult(
                        totalStopwatch,
                        "success",
                        keyword,
                        pathPrefix,
                        searchTerm,
                        normalizedQuery,
                        mode,
                        filter,
                        offset,
                        maxResults,
                        candidateCount,
                        matchElapsedMilliseconds,
                        resolveElapsedMilliseconds,
                        new SearchQueryResult
                        {
                            TotalIndexedCount = idx.TotalCount,
                            TotalMatchedCount = totalMatched,
                            PhysicalMatchedCount = physicalMatchedCount,
                            UniqueMatchedCount = uniqueMatchedCount,
                            DuplicatePathCount = Math.Max(0, physicalMatchedCount - uniqueMatchedCount),
                            IsTruncated = physicalTruncated,
                            IsSnapshotStale = _isSnapshotStale,
                            Results = results
                        },
                        pathStrategy);
                }
                catch (OperationCanceledException)
                {
                    if (mode == MatchMode.Contains || (mode == MatchMode.Wildcard && !string.IsNullOrEmpty(containsMode)))
                    {
                        LogContainsDiagnostics(
                            "canceled",
                            keyword,
                            searchTerm,
                            normalizedQuery,
                            filter,
                            containsMode ?? "fallback",
                            containsCandidateCount,
                            containsIntersectMilliseconds,
                            containsVerifyMilliseconds,
                            0);
                    }

                    LogSearchFailure(
                        totalStopwatch,
                        "canceled",
                        keyword,
                        pathPrefix,
                        searchTerm,
                        normalizedQuery,
                        mode,
                        filter,
                        offset,
                        maxResults,
                        candidateCount,
                        matchElapsedMilliseconds,
                        resolveElapsedMilliseconds,
                        null);
                    throw;
                }
                catch (Exception ex)
                {
                    if (mode == MatchMode.Contains || (mode == MatchMode.Wildcard && !string.IsNullOrEmpty(containsMode)))
                    {
                        LogContainsDiagnostics(
                            "failed",
                            keyword,
                            searchTerm,
                            normalizedQuery,
                            filter,
                            containsMode ?? "fallback",
                            containsCandidateCount,
                            containsIntersectMilliseconds,
                            containsVerifyMilliseconds,
                            0);
                    }

                    LogSearchFailure(
                        totalStopwatch,
                        "failed",
                        keyword,
                        pathPrefix,
                        searchTerm,
                        normalizedQuery,
                        mode,
                        filter,
                        offset,
                        maxResults,
                        candidateCount,
                        matchElapsedMilliseconds,
                        resolveElapsedMilliseconds,
                        ex);
                    throw;
                }
                finally
                {
                    Interlocked.Decrement(ref _activeSearchCount);
                    Interlocked.Exchange(ref _lastSearchCompletedUtcTicks, DateTime.UtcNow.Ticks);
                }
            }, ct);
        }

        private bool MarkDeletedPath(string fullPath, bool isDirectory, string source)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return false;

            var normalizedPath = fullPath.Trim();
            var fileName = Path.GetFileName(normalizedPath.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(fileName))
                return false;

            var idx = _index;
            var lowerFileName = fileName.ToLowerInvariant();
            var candidates = idx.FindByLowerName(lowerFileName);
            if (idx.HasLiveAddedOverlay)
            {
                var liveAdded = idx.GetLiveAddedRecordsSnapshot();
                if (liveAdded.Length > 0)
                {
                    var merged = new List<FileRecord>(candidates.Length + Math.Min(liveAdded.Length, 8));
                    merged.AddRange(candidates);
                    for (var i = 0; i < liveAdded.Length; i++)
                    {
                        var record = liveAdded[i];
                        if (record != null && string.Equals(record.LowerName, lowerFileName, StringComparison.Ordinal))
                            merged.Add(record);
                    }

                    candidates = merged.ToArray();
                }
            }
            for (var i = 0; i < candidates.Length; i++)
            {
                var record = candidates[i];
                if (record == null || record.IsDirectory != isDirectory)
                    continue;

                var candidatePath = _enumerator.ResolveFullPath(record.DriveLetter, record.ParentFrn, record.OriginalName)
                                    ?? (record.DriveLetter + ":\\" + record.OriginalName);
                if (!string.Equals(candidatePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var applied = idx.MarkDeleted(record, source);
                if (!applied)
                {
                    UsnDiagLog.Write(
                        $"[DELETE OVERLAY APPLY] source={IndexPerfLog.FormatValue(source)} outcome=duplicate " +
                        $"path={IndexPerfLog.FormatValue(normalizedPath)} frn={record.Frn} parentFrn={record.ParentFrn}");
                    return false;
                }

                InvalidatePathCandidateCacheIfAffected(record.DriveLetter, record.ParentFrn, record.Frn);
                if (record.IsDirectory)
                    _enumerator.UnregisterFrn(record.DriveLetter, record.Frn);
                ScheduleSnapshotSave();
                IndexChanged?.Invoke(this, new IndexChangedEventArgs(
                    IndexChangeType.Deleted, record.LowerName, candidatePath, isDirectory: record.IsDirectory));
                UsnDiagLog.Write(
                    $"[DELETE OVERLAY APPLY] source={IndexPerfLog.FormatValue(source)} outcome=success " +
                    $"path={IndexPerfLog.FormatValue(normalizedPath)} frn={record.Frn} parentFrn={record.ParentFrn}");
                return true;
            }

            UsnDiagLog.Write(
                $"[DELETE OVERLAY APPLY] source={IndexPerfLog.FormatValue(source)} outcome=not-found " +
                $"path={IndexPerfLog.FormatValue(normalizedPath)} candidates={candidates.Length}");
            return false;
        }

        private static bool TryContainsAccelerated(
            MemoryIndex index,
            string query,
            SearchTypeFilter filter,
            int offset,
            int maxResults,
            CancellationToken ct,
            out MemoryIndex.ContainsSearchResult result)
        {
            result = null;
            return index != null
                && !string.IsNullOrEmpty(query)
                && index.TryContainsSearch(query, filter, offset, maxResults, ct, out result);
        }

        private static bool PreferFilteredCandidateScan(
            string query,
            SearchTypeFilter filter,
            FileRecord[] candidateSource)
        {
            return filter != SearchTypeFilter.All
                   && !string.IsNullOrEmpty(query)
                   && query.Length <= 3
                   && candidateSource != null
                   && candidateSource.Length > 0
                   && candidateSource.Length <= 150000;
        }

        private void QueueContainsWarmupForQuery(
            MemoryIndex index,
            string query,
            string containsMode,
            bool pathPrefilterApplied)
        {
            if (index == null
                || string.IsNullOrEmpty(query)
                || IsShortHotContainsMode(containsMode)
                || (!string.IsNullOrEmpty(containsMode) && containsMode.StartsWith("short-index", StringComparison.Ordinal))
                || string.Equals(containsMode, "trigram", StringComparison.Ordinal))
            {
                return;
            }

            if (query.Length == 1 || query.Length == 2)
            {
                if (!index.IsQuerySupportedByContainsAccelerator(query))
                {
                    QueueContainsAcceleratorWarmup("query-short-needed");
                }

                return;
            }

            if (query.Length >= 3)
            {
                QueueContainsAcceleratorWarmup("query-trigram-needed");
            }
        }

        private static bool IsShortHotContainsMode(string containsMode)
        {
            return !string.IsNullOrEmpty(containsMode)
                   && (containsMode.StartsWith("short-hot-char", StringComparison.Ordinal)
                       || containsMode.StartsWith("short-hot-bigram", StringComparison.Ordinal)
                       || containsMode.StartsWith("short-index-char", StringComparison.Ordinal)
                       || containsMode.StartsWith("short-index-bigram", StringComparison.Ordinal)
                       || containsMode.StartsWith("single-char-", StringComparison.Ordinal)
                       || containsMode.StartsWith("bigram-count", StringComparison.Ordinal));
        }

        private void QueueDefaultShortContainsHotBucketWarmup(MemoryIndex index, string reason, long generation)
        {
            if (index == null || index.TotalCount == 0)
            {
                return;
            }

            var status = index.GetContainsBucketStatus();
            UsnDiagLog.Write(
                $"[CONTAINS SHORT GENERIC] reason={IndexPerfLog.FormatValue(reason)} " +
                $"charReady={status.CharReady} bigramReady={status.BigramReady} records={index.TotalCount}");
            if (!status.CharReady || !status.BigramReady)
            {
                QueueContainsAcceleratorWarmup("short-generic-needed", generation);
            }
        }

        private void QueueShortContainsHotBucketWarmup(MemoryIndex index, string query, string reason)
        {
            if (index == null || string.IsNullOrEmpty(query) || (query.Length != 1 && query.Length != 2))
            {
                return;
            }

            CancellationTokenSource cts;
            lock (_containsWarmupLock)
            {
                if (_shortContainsWarmups.Contains(query))
                {
                    return;
                }

                _shortContainsWarmups.Add(query);
                cts = new CancellationTokenSource();
                _shortContainsWarmupCts = cts;
            }

            _shortContainsWarmupTask = Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    UsnDiagLog.Write(
                        $"[CONTAINS SHORT HOT WARMUP] outcome=start reason={IndexPerfLog.FormatValue(reason)} " +
                        $"query={IndexPerfLog.FormatValue(query)} records={index.TotalCount}");
                    index.TryEnsureShortContainsHotBucket(query, cts.Token);
                    stopwatch.Stop();
                    UsnDiagLog.Write(
                        $"[CONTAINS SHORT HOT WARMUP] outcome=success reason={IndexPerfLog.FormatValue(reason)} " +
                        $"query={IndexPerfLog.FormatValue(query)} elapsedMs={stopwatch.ElapsedMilliseconds} records={index.TotalCount}");
                }
                catch (OperationCanceledException)
                {
                    stopwatch.Stop();
                    UsnDiagLog.Write(
                        $"[CONTAINS SHORT HOT WARMUP] outcome=canceled reason={IndexPerfLog.FormatValue(reason)} " +
                        $"query={IndexPerfLog.FormatValue(query)} elapsedMs={stopwatch.ElapsedMilliseconds} records={index.TotalCount}");
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    UsnDiagLog.Write(
                        $"[CONTAINS SHORT HOT WARMUP] outcome=failed reason={IndexPerfLog.FormatValue(reason)} " +
                        $"query={IndexPerfLog.FormatValue(query)} elapsedMs={stopwatch.ElapsedMilliseconds} " +
                        $"error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
                }
                finally
                {
                    lock (_containsWarmupLock)
                    {
                        _shortContainsWarmups.Remove(query);
                        if (ReferenceEquals(_shortContainsWarmupCts, cts))
                        {
                            _shortContainsWarmupCts = null;
                            _shortContainsWarmupTask = null;
                        }
                    }

                    try
                    {
                        cts.Dispose();
                    }
                    catch
                    {
                    }
                }
            });
        }

        private (List<FileRecord> page, int total, string mode, int candidateCount, long verifyMs) ContainsMatchWithIncrementalCache(
            MemoryIndex index,
            string scopeKey,
            long contentVersion,
            string query,
            FileRecord[] candidateSource,
            SearchTypeFilter filter,
            bool pathPostFilterRequired,
            int offset,
            int maxResults,
            CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            FileRecord[] cachedSource = null;
            var cacheHit = !pathPostFilterRequired
                           && TryGetIncrementalContainsCache(scopeKey, contentVersion, filter, query, out cachedSource);

            var source = cacheHit ? cachedSource : candidateSource;
            var collectAllMatches = !pathPostFilterRequired
                                    && source != null
                                    && source.Length <= MaxIncrementalContainsCacheRecords;
            var result = ContainsMatchDetailed(
                index,
                query,
                source,
                offset,
                maxResults,
                collectAllMatches: collectAllMatches,
                ct: ct);

            stopwatch.Stop();
            if (collectAllMatches)
            {
                UpdateIncrementalContainsCache(scopeKey, contentVersion, filter, query, result.allMatches);
            }

            if (cacheHit)
            {
                UsnDiagLog.Write(
                    $"[CONTAINS CACHE] outcome=hit scope={IndexPerfLog.FormatValue(scopeKey)} query={IndexPerfLog.FormatValue(query)} " +
                    $"sourceCount={cachedSource.Length} matched={result.total} elapsedMs={stopwatch.ElapsedMilliseconds}");
            }
            else
            {
                UsnDiagLog.Write(
                    $"[CONTAINS CACHE] outcome=miss scope={IndexPerfLog.FormatValue(scopeKey)} query={IndexPerfLog.FormatValue(query)} " +
                    $"sourceCount={candidateSource?.Length ?? 0} matched={result.total} elapsedMs={stopwatch.ElapsedMilliseconds}");
            }

            return (
                result.page,
                result.total,
                cacheHit ? "incremental-cache" : "fallback",
                source?.Length ?? 0,
                stopwatch.ElapsedMilliseconds);
        }

        private bool TryGetIncrementalContainsCache(
            string scopeKey,
            long contentVersion,
            SearchTypeFilter filter,
            string query,
            out FileRecord[] cachedMatches)
        {
            cachedMatches = null;
            if (string.IsNullOrEmpty(query))
            {
                return false;
            }

            lock (_containsQueryCacheLock)
            {
                var cache = _containsQueryCache;
                if (cache == null
                    || cache.ContentVersion != contentVersion
                    || cache.Filter != filter
                    || !string.Equals(cache.ScopeKey, scopeKey, StringComparison.Ordinal)
                    || string.IsNullOrEmpty(cache.Query)
                    || !query.StartsWith(cache.Query, StringComparison.Ordinal)
                    || query.Length <= cache.Query.Length
                    || cache.Matches == null
                    || cache.Matches.Length == 0)
                {
                    return false;
                }

                cachedMatches = cache.Matches;
                return true;
            }
        }

        private void UpdateIncrementalContainsCache(
            string scopeKey,
            long contentVersion,
            SearchTypeFilter filter,
            string query,
            FileRecord[] matches)
        {
            if (string.IsNullOrEmpty(query)
                || matches == null
                || matches.Length > MaxIncrementalContainsCacheRecords)
            {
                return;
            }

            lock (_containsQueryCacheLock)
            {
                _containsQueryCache = new ContainsQueryCacheEntry
                {
                    ScopeKey = scopeKey ?? string.Empty,
                    ContentVersion = contentVersion,
                    Filter = filter,
                    Query = query,
                    Matches = matches
                };
            }
        }

        private static string BuildContainsCacheScopeKey(string pathPrefix, SearchTypeFilter filter, MatchMode mode)
        {
            return (pathPrefix ?? "<global>") + "|" + filter + "|" + mode;
        }

        private static bool TryGetDriveRoot(string pathPrefix, out char driveLetter)
        {
            driveLetter = '\0';
            if (string.IsNullOrWhiteSpace(pathPrefix))
                return false;

            var trimmed = pathPrefix.Trim();
            if (trimmed.Length < 2 || trimmed[1] != ':' || !char.IsLetter(trimmed[0]))
                return false;

            var remainder = trimmed.Length == 2
                ? string.Empty
                : trimmed.Substring(2).TrimEnd('\\', '/');
            if (remainder.Length != 0)
                return false;

            driveLetter = char.ToUpperInvariant(trimmed[0]);
            return true;
        }

        private (List<FileRecord> page, int total, long verifyMs) FilterContainsMatchesByPathPrefix(
            IReadOnlyList<FileRecord> matches,
            string pathPrefix,
            int offset,
            int maxResults,
            CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var page = new List<FileRecord>(Math.Min(Math.Max(maxResults, 0), 64));
            var total = 0;
            if (matches == null || matches.Count == 0 || string.IsNullOrWhiteSpace(pathPrefix))
            {
                stopwatch.Stop();
                return (page, total, stopwatch.ElapsedMilliseconds);
            }

            var normalizedPrefix = pathPrefix.EndsWith("\\", StringComparison.Ordinal)
                ? pathPrefix
                : pathPrefix + "\\";
            var normalizedOffset = Math.Max(offset, 0);
            var normalizedMaxResults = Math.Max(maxResults, 0);
            for (var i = 0; i < matches.Count; i++)
            {
                if (((i + 1) & 0x3FF) == 0)
                    ct.ThrowIfCancellationRequested();

                var record = matches[i];
                if (record == null)
                    continue;

                var fullPath = _enumerator.ResolveFullPath(record.DriveLetter, record.ParentFrn, record.OriginalName)
                               ?? (record.DriveLetter + ":\\" + record.OriginalName);
                if (!fullPath.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                total++;
                if (total > normalizedOffset && page.Count < normalizedMaxResults)
                    page.Add(record);
            }

            stopwatch.Stop();
            return (page, total, stopwatch.ElapsedMilliseconds);
        }

        private static (List<FileRecord> page, int total, long verifyMs) FilterContainsMatchesByDrive(
            IReadOnlyList<FileRecord> matches,
            char driveLetter,
            int offset,
            int maxResults,
            CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var page = new List<FileRecord>(Math.Min(Math.Max(maxResults, 0), 64));
            var total = 0;
            if (matches == null || matches.Count == 0)
            {
                stopwatch.Stop();
                return (page, total, stopwatch.ElapsedMilliseconds);
            }

            var dl = char.ToUpperInvariant(driveLetter);
            var normalizedOffset = Math.Max(offset, 0);
            var normalizedMaxResults = Math.Max(maxResults, 0);
            for (var i = 0; i < matches.Count; i++)
            {
                if (((i + 1) & 0xFFF) == 0)
                    ct.ThrowIfCancellationRequested();

                var record = matches[i];
                if (record == null || char.ToUpperInvariant(record.DriveLetter) != dl)
                    continue;

                total++;
                if (total > normalizedOffset && page.Count < normalizedMaxResults)
                    page.Add(record);
            }

            stopwatch.Stop();
            return (page, total, stopwatch.ElapsedMilliseconds);
        }

        private static (List<FileRecord> page, int total, long verifyMs) FilterContainsMatchesByDriveAndFilter(
            IReadOnlyList<FileRecord> matches,
            char driveLetter,
            SearchTypeFilter filter,
            int offset,
            int maxResults,
            CancellationToken ct)
        {
            if (filter == SearchTypeFilter.All)
                return FilterContainsMatchesByDrive(matches, driveLetter, offset, maxResults, ct);

            var stopwatch = Stopwatch.StartNew();
            var page = new List<FileRecord>(Math.Min(Math.Max(maxResults, 0), 64));
            var total = 0;
            if (matches == null || matches.Count == 0)
            {
                stopwatch.Stop();
                return (page, total, stopwatch.ElapsedMilliseconds);
            }

            var dl = char.ToUpperInvariant(driveLetter);
            var normalizedOffset = Math.Max(offset, 0);
            var normalizedMaxResults = Math.Max(maxResults, 0);
            for (var i = 0; i < matches.Count; i++)
            {
                if (((i + 1) & 0xFFF) == 0)
                    ct.ThrowIfCancellationRequested();

                var record = matches[i];
                if (record == null
                    || char.ToUpperInvariant(record.DriveLetter) != dl
                    || !MatchesSearchFilter(record, filter))
                {
                    continue;
                }

                total++;
                if (total > normalizedOffset && page.Count < normalizedMaxResults)
                    page.Add(record);
            }

            stopwatch.Stop();
            return (page, total, stopwatch.ElapsedMilliseconds);
        }

        private bool TryGetPathCandidateCache(
            string pathPrefix,
            SearchTypeFilter filter,
            MftEnumerator.DirectorySubtreeScope scope,
            out FileRecord[] candidates)
        {
            candidates = null;
            lock (_pathCandidateCacheLock)
            {
                var cache = _pathCandidateCache;
                if (cache == null
                    || cache.Filter != filter
                    || scope == null
                    || cache.DriveLetter != scope.DriveLetter
                    || cache.RootFrn != scope.RootFrn
                    || !string.Equals(cache.PathPrefix, pathPrefix, StringComparison.OrdinalIgnoreCase)
                    || cache.Candidates == null)
                {
                    return false;
                }

                candidates = cache.Candidates;
                return true;
            }
        }

        private void UpdatePathCandidateCache(
            string pathPrefix,
            SearchTypeFilter filter,
            MftEnumerator.DirectorySubtreeScope scope,
            FileRecord[] candidates)
        {
            if (string.IsNullOrWhiteSpace(pathPrefix)
                || scope == null
                || candidates == null
                || candidates.Length > MaxPathCandidateCacheRecords)
            {
                return;
            }

            lock (_pathCandidateCacheLock)
            {
                _pathCandidateCache = new PathCandidateCacheEntry
                {
                    PathPrefix = pathPrefix,
                    Filter = filter,
                    DriveLetter = scope.DriveLetter,
                    RootFrn = scope.RootFrn,
                    DirectoryFrns = scope.DirectoryFrns,
                    Candidates = candidates
                };
            }
        }

        private void InvalidatePathCandidateCacheIfAffected(char driveLetter, ulong parentFrn, ulong frn)
        {
            lock (_pathCandidateCacheLock)
            {
                var cache = _pathCandidateCache;
                if (cache == null
                    || cache.DirectoryFrns == null
                    || char.ToUpperInvariant(driveLetter) != char.ToUpperInvariant(cache.DriveLetter))
                {
                    return;
                }

                if (cache.ContainsDirectory(parentFrn) || cache.ContainsDirectory(frn))
                {
                    _pathCandidateCache = null;
                }
            }
        }

        private void InvalidatePathCandidateCacheIfAffected(IReadOnlyList<UsnChangeEntry> changes)
        {
            if (changes == null || changes.Count == 0)
                return;

            lock (_pathCandidateCacheLock)
            {
                var cache = _pathCandidateCache;
                if (cache == null || cache.DirectoryFrns == null)
                    return;

                for (var i = 0; i < changes.Count; i++)
                {
                    var change = changes[i];
                    if (char.ToUpperInvariant(change.DriveLetter) != char.ToUpperInvariant(cache.DriveLetter))
                        continue;

                    if (cache.ContainsDirectory(change.ParentFrn)
                        || cache.ContainsDirectory(change.OldParentFrn)
                        || cache.ContainsDirectory(change.Frn))
                    {
                        _pathCandidateCache = null;
                        return;
                    }
                }
            }
        }

        // ── 私有实现 ────────────────────────────────────────────────────────────

        private int BuildIndex(IProgress<string> progress, CancellationToken ct)
        {
            CancelPendingSnapshotSave();
            CancelBackgroundCatchUp();
            CancelContainsWarmup();
            _usnWatcher.StopWatching();

            if (TryRestoreFromSnapshot(progress, ct, out var restoredCount))
                return restoredCount;

            return BuildIndexFromMft(progress, ct);
        }

        private long NextIndexGeneration()
        {
            return Interlocked.Increment(ref _indexGeneration);
        }

        private void EnsureSearchHotStructuresReady(string reason, CancellationToken ct)
        {
            var index = _index;
            if (index == null)
                return;

            var stopwatch = Stopwatch.StartNew();
            index.EnsureShortQueryStructuresReady(ct, reason);
            stopwatch.Stop();
            UsnDiagLog.Write(
                $"[SEARCH HOT STRUCTURES READY] reason={IndexPerfLog.FormatValue(reason)} elapsedMs={stopwatch.ElapsedMilliseconds} records={index.TotalCount}");
        }

        private long CurrentIndexGeneration => Interlocked.Read(ref _indexGeneration);

        private bool IsCurrentIndex(MemoryIndex index, long generation)
        {
            return index != null
                && generation == CurrentIndexGeneration
                && ReferenceEquals(index, _index);
        }

        private static bool IsSuspiciousSnapshotShrink(int recordCount, int stableRecordCount)
        {
            if (recordCount <= 0 || stableRecordCount <= 0)
                return false;

            if (stableRecordCount < 100000)
                return false;

            return recordCount < stableRecordCount * SuspiciousSnapshotShrinkRatio;
        }

        private SearchQueryResult CreateEmptySearchResult(int totalIndexedCount)
        {
            return new SearchQueryResult
            {
                TotalIndexedCount = totalIndexedCount,
                TotalMatchedCount = 0,
                PhysicalMatchedCount = 0,
                UniqueMatchedCount = 0,
                DuplicatePathCount = 0,
                IsTruncated = false,
                IsSnapshotStale = _isSnapshotStale,
                HostSearchMs = 0,
                Results = new List<ScannedFileInfo>()
            };
        }

        private SearchQueryResult FinalizeSearchResult(
            Stopwatch totalStopwatch,
            string outcome,
            string keyword,
            string pathPrefix,
            string searchTerm,
            string normalizedQuery,
            MatchMode mode,
            SearchTypeFilter filter,
            int offset,
            int maxResults,
            int candidateCount,
            long matchElapsedMilliseconds,
            long resolveElapsedMilliseconds,
            SearchQueryResult result,
            string pathStrategy = null)
        {
            totalStopwatch.Stop();
            if (result != null)
            {
                result.HostSearchMs = totalStopwatch.ElapsedMilliseconds;
                result.ContainsBucketStatus = ContainsBucketStatus;
                result.IsSnapshotStale = _isSnapshotStale;
                if (result.PhysicalMatchedCount == 0 && result.TotalMatchedCount > 0)
                    result.PhysicalMatchedCount = result.TotalMatchedCount;
                if (result.UniqueMatchedCount == 0 && result.TotalMatchedCount > 0)
                    result.UniqueMatchedCount = result.TotalMatchedCount;
                if (result.DuplicatePathCount == 0 && result.PhysicalMatchedCount > result.UniqueMatchedCount)
                    result.DuplicatePathCount = result.PhysicalMatchedCount - result.UniqueMatchedCount;
            }
            UsnDiagLog.Write(
                $"[SEARCH] outcome={outcome} totalMs={totalStopwatch.ElapsedMilliseconds} matchMs={matchElapsedMilliseconds} " +
                $"resolveMs={resolveElapsedMilliseconds} filter={filter} mode={mode} offset={offset} maxResults={maxResults} " +
                $"candidateCount={candidateCount} matched={result.TotalMatchedCount} physicalMatched={result.PhysicalMatchedCount} " +
                $"uniqueMatched={result.UniqueMatchedCount} duplicatePaths={result.DuplicatePathCount} returned={result.Results?.Count ?? 0} " +
                $"truncated={result.IsTruncated} indexed={result.TotalIndexedCount} pathScoped={pathPrefix != null} " +
                $"pathStrategy={IndexPerfLog.FormatValue(pathStrategy ?? (pathPrefix == null ? "none" : "path-first"))} stale={_isSnapshotStale} " +
                $"keyword={IndexPerfLog.FormatValue(keyword)} searchTerm={IndexPerfLog.FormatValue(searchTerm)} " +
                $"normalized={IndexPerfLog.FormatValue(normalizedQuery)} pathPrefix={IndexPerfLog.FormatValue(pathPrefix)}");
            return result;
        }

        private static void LogContainsDiagnostics(
            string outcome,
            string keyword,
            string searchTerm,
            string normalizedQuery,
            SearchTypeFilter filter,
            string mode,
            int candidateCount,
            long intersectMs,
            long verifyMs,
            int matchedCount)
        {
            UsnDiagLog.Write(
                $"[CONTAINS QUERY] outcome={outcome} mode={mode} filter={filter} candidateCount={candidateCount} " +
                $"intersectMs={intersectMs} verifyMs={verifyMs} matched={matchedCount} " +
                $"keyword={IndexPerfLog.FormatValue(keyword)} searchTerm={IndexPerfLog.FormatValue(searchTerm)} " +
                $"normalized={IndexPerfLog.FormatValue(normalizedQuery)}");
        }

        private void LogSearchFailure(
            Stopwatch totalStopwatch,
            string outcome,
            string keyword,
            string pathPrefix,
            string searchTerm,
            string normalizedQuery,
            MatchMode mode,
            SearchTypeFilter filter,
            int offset,
            int maxResults,
            int candidateCount,
            long matchElapsedMilliseconds,
            long resolveElapsedMilliseconds,
            Exception ex)
        {
            totalStopwatch.Stop();
            var error = ex == null
                ? string.Empty
                : $" error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}";
            UsnDiagLog.Write(
                $"[SEARCH] outcome={outcome} totalMs={totalStopwatch.ElapsedMilliseconds} matchMs={matchElapsedMilliseconds} " +
                $"resolveMs={resolveElapsedMilliseconds} filter={filter} mode={mode} offset={offset} maxResults={maxResults} " +
                $"candidateCount={candidateCount} pathScoped={pathPrefix != null} stale={_isSnapshotStale} keyword={IndexPerfLog.FormatValue(keyword)} " +
                $"searchTerm={IndexPerfLog.FormatValue(searchTerm)} normalized={IndexPerfLog.FormatValue(normalizedQuery)} " +
                $"pathPrefix={IndexPerfLog.FormatValue(pathPrefix)}{error}");
        }

        private bool TryRestoreFromSnapshot(IProgress<string> progress, CancellationToken ct, out int restoredCount)
        {
            restoredCount = 0;
            var totalStopwatch = Stopwatch.StartNew();

            progress?.Report("正在加载索引快照...");
            var snapshotLoadStopwatch = Stopwatch.StartNew();
            if (!_snapshotStore.TryLoad(out var snapshot, out var snapshotMetrics) ||
                snapshot?.Records == null || snapshot.Volumes == null ||
                snapshot.Volumes.Length == 0)
            {
                snapshotLoadStopwatch.Stop();
                UsnDiagLog.Write($"[SNAPSHOT LOAD] miss elapsedMs={snapshotLoadStopwatch.ElapsedMilliseconds}");
                return false;
            }
            snapshotLoadStopwatch.Stop();
            UsnDiagLog.Write(
                $"[SNAPSHOT LOAD] hit elapsedMs={snapshotLoadStopwatch.ElapsedMilliseconds} " +
                $"version={snapshotMetrics.Version} fileBytes={snapshotMetrics.FileBytes} records={snapshotMetrics.RecordCount} " +
                $"volumes={snapshotMetrics.VolumeCount} frnEntries={snapshotMetrics.FrnEntryCount} stringPool={snapshotMetrics.StringPoolCount}");

            ct.ThrowIfCancellationRequested();

            progress?.Report("正在恢复内存索引...");
            var restoreStopwatch = Stopwatch.StartNew();
            _enumerator.LoadVolumeSnapshots(snapshot.Volumes);
            _lastVolumeSnapshots = CopyVolumeSnapshots(snapshot.Volumes);
            _index.LoadSortedRecords(
                snapshot.Records,
                buildContainsAccelerator: false,
                takeOwnership: true,
                buildDerivedStructures: false,
                buildShortAsciiStructures: false);
            _currentIndexContentFingerprint = snapshot.ContentFingerprint;
            _lastStableSnapshotRecordCount = Math.Max(_lastStableSnapshotRecordCount, snapshot.Records.Length);
            var generation = NextIndexGeneration();
            var containsPostingsLoaded = _index.TryLoadContainsPostingsSnapshot(snapshot.ContainsPostings, snapshot.ContentFingerprint)
                                         || TryLoadContainsPostingsSnapshotSync(
                                             snapshot.ContentFingerprint,
                                             snapshot.Records.Length,
                                             "snapshot-restore");
            var parentOrderLoaded = _index.TryLoadParentOrderSnapshot(snapshot.ParentOrder, snapshot.ContentFingerprint)
                                    || TryLoadParentOrderSnapshotSync(
                                        snapshot.ContentFingerprint,
                                        snapshot.Records.Length,
                                        "snapshot-restore");
            var shortQueryLoaded = _index.TryLoadShortQuerySnapshot(snapshot.ShortQuery, snapshot.ContentFingerprint)
                                   || TryLoadShortQuerySnapshotSync(
                                       snapshot.ContentFingerprint,
                                       snapshot.Records.Length,
                                       "snapshot-restore");
            if (!containsPostingsLoaded)
            {
                QueueContainsPostingsSnapshotLoad(
                    snapshot.ContentFingerprint,
                    snapshot.Records.Length,
                    _index,
                    generation);
            }
            if (!_index.SupportsContainsAccelerator(MemoryIndex.ContainsWarmupScope.Full))
            {
                var postingsRebuildStopwatch = Stopwatch.StartNew();
                var rebuilt = _index.EnsureContainsAcceleratorReady(MemoryIndex.ContainsWarmupScope.Full, ct);
                postingsRebuildStopwatch.Stop();
                UsnDiagLog.Write(
                    $"[POSTINGS SNAPSHOT RESTORE] outcome={(rebuilt ? "rebuilt-full" : "rebuild-miss")} mode=sync " +
                    $"reason=restore-requires-full-ascii elapsedMs={postingsRebuildStopwatch.ElapsedMilliseconds} records={snapshot.Records.Length}");
                if (rebuilt)
                {
                    containsPostingsLoaded = true;
                    SaveCurrentContainsPostings(_index, "restore-rebuilt-full-ascii", generation);
                }
            }
            if (!parentOrderLoaded && !_index.AreDerivedStructuresReady)
            {
                _index.QueueEnsureDerivedStructures("snapshot-restore-parent-order-miss");
            }
            if (snapshotMetrics.Version < 6)
            {
                QueueSnapshotSave(snapshot.Records, snapshot.Volumes, snapshot.ContentFingerprint, _index, generation);
            }
            _isSnapshotStale = false;
            restoreStopwatch.Stop();
            UsnDiagLog.Write(
                $"[SNAPSHOT RESTORE] elapsedMs={restoreStopwatch.ElapsedMilliseconds} records={snapshot.Records.Length} " +
                $"volumes={snapshot.Volumes.Length} containsPostingsLoaded={containsPostingsLoaded} parentOrderLoaded={parentOrderLoaded} shortQueryLoaded={shortQueryLoaded} " +
                $"postingsLoadMode={(containsPostingsLoaded ? "inline" : "async")}");

            restoredCount = _index.TotalCount;
            totalStopwatch.Stop();
            progress?.Report($"已从快照恢复 {restoredCount} 个对象，可立即搜索");
            UsnDiagLog.Write(
                $"[SNAPSHOT RESTORE TOTAL] totalMs={totalStopwatch.ElapsedMilliseconds} loadMs={snapshotLoadStopwatch.ElapsedMilliseconds} " +
                $"restoreMs={restoreStopwatch.ElapsedMilliseconds} restoredCount={restoredCount}");

            _index.QueueEnsureDerivedStructures("snapshot-restore");
            _index.QueueEnsureShortQueryStructures("snapshot-restore");
            QueueDefaultShortContainsHotBucketWarmup(_index, "snapshot-restore", generation);
            EnsureUsnJournalCapacityInBackground(snapshot.Volumes.Select(v => v.DriveLetter).ToArray());
            StartBackgroundCatchUp(snapshot.Volumes, progress, ct, generation);
            return true;
        }

        private bool TryLoadContainsPostingsSnapshotSync(ulong contentFingerprint, int recordCount, string reason)
        {
            if (contentFingerprint == 0 || recordCount <= 0)
                return false;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var postings = _snapshotStore.TryLoadContainsPostingsSnapshot(contentFingerprint, recordCount);
                var loaded = postings != null && _index.TryLoadContainsPostingsSnapshot(postings, contentFingerprint);
                stopwatch.Stop();
                UsnDiagLog.Write(
                    $"[POSTINGS SNAPSHOT RESTORE] outcome={(loaded ? "success" : "miss")} mode=sync reason={IndexPerfLog.FormatValue(reason)} " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds} records={recordCount}");
                return loaded;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                UsnDiagLog.Write(
                    $"[POSTINGS SNAPSHOT RESTORE] outcome=failed mode=sync reason={IndexPerfLog.FormatValue(reason)} " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
                return false;
            }
        }

        private bool TryLoadShortQuerySnapshotSync(ulong contentFingerprint, int recordCount, string reason)
        {
            if (contentFingerprint == 0 || recordCount <= 0)
                return false;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var snapshot = _snapshotStore.TryLoadShortQuerySnapshot(contentFingerprint, recordCount);
                var loaded = snapshot != null && _index.TryLoadShortQuerySnapshot(snapshot, contentFingerprint);
                stopwatch.Stop();
                UsnDiagLog.Write(
                    $"[SHORT QUERY SNAPSHOT RESTORE] outcome={(loaded ? "success" : "miss")} mode=sync reason={IndexPerfLog.FormatValue(reason)} " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds} records={recordCount}");
                return loaded;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                UsnDiagLog.Write(
                    $"[SHORT QUERY SNAPSHOT RESTORE] outcome=failed mode=sync reason={IndexPerfLog.FormatValue(reason)} " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
                return false;
            }
        }

        private bool TryLoadParentOrderSnapshotSync(ulong contentFingerprint, int recordCount, string reason)
        {
            if (contentFingerprint == 0 || recordCount <= 0)
                return false;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var parentOrder = _snapshotStore.TryLoadParentOrderSnapshot(contentFingerprint, recordCount);
                var loaded = parentOrder != null && _index.TryLoadParentOrderSnapshot(parentOrder, contentFingerprint);
                stopwatch.Stop();
                UsnDiagLog.Write(
                    $"[PARENT ORDER SNAPSHOT RESTORE] outcome={(loaded ? "success" : "miss")} mode=sync reason={IndexPerfLog.FormatValue(reason)} " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds} records={recordCount}");
                return loaded;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                UsnDiagLog.Write(
                    $"[PARENT ORDER SNAPSHOT RESTORE] outcome=failed mode=sync reason={IndexPerfLog.FormatValue(reason)} " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
                return false;
            }
        }

        private void QueueContainsPostingsSnapshotLoad(ulong contentFingerprint, int recordCount, MemoryIndex expectedIndex, long generation)
        {
            if (contentFingerprint == 0 || recordCount <= 0)
            {
                QueueContainsAcceleratorWarmup("snapshot-postings-miss", generation);
                return;
            }

            Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    if (!IsCurrentIndex(expectedIndex, generation))
                    {
                        UsnDiagLog.Write(
                            $"[POSTINGS SNAPSHOT RESTORE] outcome=skip-stale-generation mode=async records={recordCount} " +
                            $"generation={generation} currentGeneration={CurrentIndexGeneration}");
                        return;
                    }
                    var postings = _snapshotStore.TryLoadContainsPostingsSnapshot(contentFingerprint, recordCount);
                    var loaded = postings != null && expectedIndex.TryLoadContainsPostingsSnapshot(postings, contentFingerprint);
                    stopwatch.Stop();
                    UsnDiagLog.Write(
                        $"[POSTINGS SNAPSHOT RESTORE] outcome={(loaded ? "success" : "miss")} mode=async elapsedMs={stopwatch.ElapsedMilliseconds} records={recordCount}");
                    if (!loaded)
                        QueueContainsAcceleratorWarmup("snapshot-postings-miss", generation);
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    UsnDiagLog.Write(
                        $"[POSTINGS SNAPSHOT RESTORE] outcome=failed elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
                    QueueContainsAcceleratorWarmup("snapshot-postings-miss", generation);
                }
            });
        }

        private int BuildIndexFromMft(IProgress<string> progress, CancellationToken ct)
        {
            var totalStopwatch = Stopwatch.StartNew();
            try
            {
                var fixedDrives = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed)
                    .Select(d => d.Name[0])
                    .ToArray();

                if (fixedDrives.Length == 0)
                {
                    totalStopwatch.Stop();
                    UsnDiagLog.Write("[MFT BUILD] outcome=no-fixed-drive totalMs=0 indexedCount=0");
                    return 0;
                }

                UsnDiagLog.Write($"[MFT BUILD] start drives={string.Join(",", fixedDrives)}");

                var buildEnumerator = new MftEnumerator();

                // 多卷并行枚举
                var enumerateStopwatch = Stopwatch.StartNew();
                var perVolume = new (List<FileRecord> records, long nextUsn, ulong journalId, char letter)[fixedDrives.Length];
                var exceptions = new System.Collections.Concurrent.ConcurrentBag<(char, Exception)>();

                System.Threading.Tasks.Parallel.For(0, fixedDrives.Length, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(fixedDrives.Length, Environment.ProcessorCount),
                    CancellationToken = ct
                }, i =>
                {
                    var dl = fixedDrives[i];
                    var buf = new List<FileRecord>(300_000);
                    var volumeStopwatch = Stopwatch.StartNew();
                    try
                    {
                        var enumerator = new MftEnumerator(); // 每线程独立实例
                        var (count, nextUsn, journalId) = enumerator.EnumerateVolumeIntoRecords(dl, buf, ct);
                        buildEnumerator.MergeFrnMap(dl, enumerator);
                        perVolume[i] = (buf, nextUsn, journalId, dl);
                        volumeStopwatch.Stop();
                        UsnDiagLog.Write(
                            $"[MFT ENUM] drive={dl} success elapsedMs={volumeStopwatch.ElapsedMilliseconds} records={count} nextUsn={nextUsn} journalId={journalId}");
                    }
                    catch (OperationCanceledException)
                    {
                        volumeStopwatch.Stop();
                        UsnDiagLog.Write($"[MFT ENUM] drive={dl} canceled elapsedMs={volumeStopwatch.ElapsedMilliseconds}");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        volumeStopwatch.Stop();
                        UsnDiagLog.Write(
                            $"[MFT ENUM] drive={dl} fail elapsedMs={volumeStopwatch.ElapsedMilliseconds} error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
                        exceptions.Add((dl, ex));
                    }
                });
                enumerateStopwatch.Stop();

                foreach (var (dl, ex) in exceptions)
                    progress?.Report($"跳过卷 {dl}:：{ex.Message}");

                ct.ThrowIfCancellationRequested();

                var mergeStopwatch = Stopwatch.StartNew();

                // 合并所有卷的记录
                var total = perVolume.Where(v => v.records != null).Sum(v => v.records.Count);
                var allRecords = new FileRecord[total];
                var offset = 0;
                var successfulDrives = new List<(char letter, long nextUsn, ulong journalId)>();
                foreach (var v in perVolume)
                {
                    if (v.records == null) continue;
                    v.records.CopyTo(allRecords, offset);
                    offset += v.records.Count;
                    successfulDrives.Add((v.letter, v.nextUsn, v.journalId));
                }

                mergeStopwatch.Stop();

                if (successfulDrives.Count == 0 && exceptions.Count > 0 && _index.TotalCount > 0)
                {
                    totalStopwatch.Stop();
                    _isSnapshotStale = true;
                    PublishIndexStatus($"索引快照可能已过期：沿用现有索引 {_index.TotalCount} 个对象；MFT 枚举未获得可用卷，请以管理员身份重建索引", false);
                    UsnDiagLog.Write(
                        $"[MFT BUILD] outcome=preserve-existing totalMs={totalStopwatch.ElapsedMilliseconds} " +
                        $"existingIndexedCount={_index.TotalCount} failedDrives={exceptions.Count} stale=true");
                    return _index.TotalCount;
                }

                var buildStopwatch = Stopwatch.StartNew();
                var newIndex = new MemoryIndex();
                newIndex.Build(allRecords, buildContainsAccelerator: false);
                var contentFingerprint = IndexSnapshotFingerprint.Compute(allRecords);
                buildStopwatch.Stop();
                progress?.Report($"已索引 {newIndex.TotalCount} 个对象");

                var watcherStartStopwatch = Stopwatch.StartNew();
                var volumeSnapshots = buildEnumerator.CreateVolumeSnapshots(successfulDrives.ToArray());

                _usnWatcher.StopWatching();
                _enumerator = buildEnumerator;
                _index = newIndex;
                _currentIndexContentFingerprint = contentFingerprint;
                _lastVolumeSnapshots = CopyVolumeSnapshots(volumeSnapshots);
                _lastStableSnapshotRecordCount = Math.Max(_lastStableSnapshotRecordCount, newIndex.TotalCount);
                var generation = NextIndexGeneration();

                foreach (var (letter, nextUsn, journalId) in successfulDrives)
                    _usnWatcher.StartWatching(letter, nextUsn, journalId, ct);

                watcherStartStopwatch.Stop();
                EnsureUsnJournalCapacityInBackground(successfulDrives.Select(d => d.letter).ToArray());
                QueueDefaultShortContainsHotBucketWarmup(newIndex, "post-full-build", generation);
                QueueContainsAcceleratorWarmup("post-full-build", generation);
                QueueSnapshotSave(allRecords, volumeSnapshots, contentFingerprint, newIndex, generation);
                _isSnapshotStale = false;
                PublishIndexStatus($"已索引 {_index.TotalCount} 个对象", false, requireSearchRefresh: true);

                totalStopwatch.Stop();
                UsnDiagLog.Write(
                    $"[MFT BUILD] success totalMs={totalStopwatch.ElapsedMilliseconds} enumerateMs={enumerateStopwatch.ElapsedMilliseconds} " +
                    $"mergeMs={mergeStopwatch.ElapsedMilliseconds} buildMs={buildStopwatch.ElapsedMilliseconds} " +
                    $"watcherStartMs={watcherStartStopwatch.ElapsedMilliseconds} indexedCount={_index.TotalCount} " +
                    $"successfulDrives={successfulDrives.Count} failedDrives={exceptions.Count}");

                return _index.TotalCount;
            }
            catch (OperationCanceledException)
            {
                totalStopwatch.Stop();
                UsnDiagLog.Write($"[MFT BUILD] canceled totalMs={totalStopwatch.ElapsedMilliseconds}");
                throw;
            }
            catch (Exception ex)
            {
                totalStopwatch.Stop();
                UsnDiagLog.Write($"[MFT BUILD] fail totalMs={totalStopwatch.ElapsedMilliseconds} error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
                throw;
            }
        }

        private void StartBackgroundCatchUp(IReadOnlyList<VolumeSnapshot> volumes, IProgress<string> progress, CancellationToken ct, long generation)
        {
            CancelBackgroundCatchUp();

            if (volumes == null || volumes.Count == 0)
            {
                PublishIndexStatus($"已索引 {_index.TotalCount} 个对象", false);
                QueueDefaultShortContainsHotBucketWarmup(_index, "snapshot-no-catchup", generation);
                QueueContainsAcceleratorWarmup("snapshot-no-catchup", generation);
                return;
            }

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_backgroundCatchUpLock)
            {
                _backgroundCatchUpCts = linkedCts;
                _backgroundCatchUpTask = Task.Run(() => RunBackgroundCatchUp(volumes, progress, linkedCts, generation), linkedCts.Token);
            }
        }

        private sealed class CatchUpVolumeResult
        {
            public char DriveLetter { get; set; }
            public long StartUsn { get; set; }
            public ulong StartJournalId { get; set; }
            public List<UsnChangeEntry> Changes { get; set; }
            public long NextUsn { get; set; }
            public ulong LatestJournalId { get; set; }
            public long ElapsedMs { get; set; }
            public UsnCatchUpResult Result { get; set; }
        }

        private void RunBackgroundCatchUp(IReadOnlyList<VolumeSnapshot> volumes, IProgress<string> progress, CancellationTokenSource backgroundCatchUpCts, long generation)
        {
            var ct = backgroundCatchUpCts.Token;
            try
            {
                var index = _index;
                var enumerator = _enumerator;
                PublishIndexStatus(
                    $"已从快照恢复 {index.TotalCount} 个对象，可立即搜索；后台正在追平 USN...",
                    true);

                var catchUpStopwatch = Stopwatch.StartNew();
                var volumeResults = new CatchUpVolumeResult[volumes.Count];
                System.Threading.Tasks.Parallel.For(0, volumes.Count, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(volumes.Count, Environment.ProcessorCount),
                    CancellationToken = ct
                }, i =>
                {
                    var volume = volumes[i];
                    var volumeCatchUpStopwatch = Stopwatch.StartNew();
                    var result = new CatchUpVolumeResult
                    {
                        DriveLetter = volume.DriveLetter,
                        StartUsn = volume.NextUsn,
                        StartJournalId = volume.JournalId,
                        NextUsn = volume.NextUsn,
                        LatestJournalId = volume.JournalId
                    };

                    result.Result = _usnWatcher.TryCollectCatchUpChanges(
                        volume.DriveLetter,
                        volume.NextUsn,
                        volume.JournalId,
                        ct,
                        out var changes,
                        out var nextUsn,
                        out var latestJournalId);
                    if (result.Result == UsnCatchUpResult.Success)
                    {
                        result.Changes = changes;
                        result.NextUsn = nextUsn;
                        result.LatestJournalId = latestJournalId;
                    }

                    volumeCatchUpStopwatch.Stop();
                    result.ElapsedMs = volumeCatchUpStopwatch.ElapsedMilliseconds;
                    volumeResults[i] = result;
                });

                for (var i = 0; i < volumeResults.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var result = volumeResults[i];
                    if (result.Result != UsnCatchUpResult.Success)
                    {
                        UsnDiagLog.Write(
                            $"[SNAPSHOT CATCHUP] drive={result.DriveLetter} outcome={result.Result} elapsedMs={result.ElapsedMs} " +
                            $"startUsn={result.StartUsn} journalId={result.StartJournalId} latestJournalId={result.LatestJournalId}");

                        if (result.Result == UsnCatchUpResult.JournalExpired)
                        {
                            PublishIndexStatus($"卷 {result.DriveLetter} 的 USN 游标已过期，后台正在恢复该卷；当前结果继续来自现有快照...", true);
                            var rebuiltCount = RecoverExpiredVolumeFromMft(
                                result.DriveLetter,
                                _lastVolumeSnapshots,
                                progress,
                                ct,
                                generation,
                                out var recoveredNextUsn,
                                out var recoveredJournalId);
                            PublishIndexStatus(
                                _isSnapshotStale
                                    ? $"卷 {result.DriveLetter} 恢复失败：当前沿用 {rebuiltCount} 个对象；请以管理员身份重建索引"
                                    : $"已恢复卷 {result.DriveLetter}，共 {rebuiltCount} 个对象",
                                false,
                                requireSearchRefresh: true);
                            if (!_isSnapshotStale)
                            {
                                index = _index;
                                enumerator = _enumerator;
                                generation = CurrentIndexGeneration;
                                result.Result = UsnCatchUpResult.Success;
                                result.Changes = new List<UsnChangeEntry>();
                                result.NextUsn = recoveredNextUsn;
                                result.LatestJournalId = recoveredJournalId;
                                volumeResults[i] = result;
                                continue;
                            }
                        }
                        else
                        {
                            PublishIndexStatus($"后台追平失败：{result.Result}；当前结果继续来自现有快照", false);
                        }
                        return;
                    }

                    UsnDiagLog.Write(
                        $"[SNAPSHOT CATCHUP] drive={result.DriveLetter} elapsedMs={result.ElapsedMs} " +
                        $"startUsn={result.StartUsn} nextUsn={result.NextUsn} journalId={result.LatestJournalId} changes={result.Changes?.Count ?? 0}");
                }

                var applyStopwatch = Stopwatch.StartNew();
                if (!IsCurrentIndex(index, generation) || !ReferenceEquals(enumerator, _enumerator))
                {
                    UsnDiagLog.Write(
                        $"[SNAPSHOT CATCHUP LIVE APPLY] outcome=skip-stale-generation generation={generation} currentGeneration={CurrentIndexGeneration}");
                    PublishIndexStatus($"已索引 {_index.TotalCount} 个对象", false);
                    return;
                }

                var totalChangeCount = 0;
                for (var i = 0; i < volumeResults.Length; i++)
                {
                    totalChangeCount += volumeResults[i].Changes?.Count ?? 0;
                }

                var watcherStartStopwatch = Stopwatch.StartNew();
                var checkpoints = new (char driveLetter, long nextUsn, ulong journalId)[volumeResults.Length];
                for (var i = 0; i < volumeResults.Length; i++)
                {
                    checkpoints[i] = (volumeResults[i].DriveLetter, volumeResults[i].NextUsn, volumeResults[i].LatestJournalId);
                }

                for (var i = 0; i < checkpoints.Length; i++)
                {
                    var checkpoint = checkpoints[i];
                    _usnWatcher.StartWatching(checkpoint.driveLetter, checkpoint.nextUsn, checkpoint.journalId, ct);
                }

                watcherStartStopwatch.Stop();

                var liveDeltaInserted = 0;
                var liveDeltaDeleted = 0;
                var liveDeltaRestored = 0;
                var liveDeltaAlreadyVisible = 0;
                var liveDeltaCompactRequired = false;
                var applyStrategy = totalChangeCount > 0 ? "live-delta" : "none";

                if (totalChangeCount > 0)
                {
                    var allChanges = new List<UsnChangeEntry>(totalChangeCount);
                    for (var i = 0; i < volumeResults.Length; i++)
                    {
                        if (volumeResults[i].Changes != null && volumeResults[i].Changes.Count > 0)
                        {
                            allChanges.AddRange(volumeResults[i].Changes);
                        }
                    }

                    var indexChangedArgs = BuildBatchIndexChangedArgs(allChanges);
                    InvalidatePathCandidateCacheIfAffected(allChanges);
                    enumerator.ApplyUsnChanges(allChanges);
                    for (var start = 0; start < allChanges.Count; start += CatchUpLiveDeltaBatchSize)
                    {
                        ct.ThrowIfCancellationRequested();
                        var count = Math.Min(CatchUpLiveDeltaBatchSize, allChanges.Count - start);
                        var chunk = allChanges.GetRange(start, count);
                        var liveDelta = index.ApplyCatchUpLiveDeltaBatch(chunk);
                        liveDeltaInserted += liveDelta.Inserted;
                        liveDeltaDeleted += liveDelta.Deleted;
                        liveDeltaRestored += liveDelta.Restored;
                        liveDeltaAlreadyVisible += liveDelta.AlreadyVisible;
                        liveDeltaCompactRequired |= liveDelta.CompactRequired;
                    }

                    if (liveDeltaCompactRequired)
                        QueueLiveDeltaCompact("snapshot-catchup", generation);

                    PublishBatchIndexChanges(indexChangedArgs);
                    UsnDiagLog.Write(
                        $"[SNAPSHOT CATCHUP LIVE APPLY] outcome=success strategy={applyStrategy} " +
                        $"changes={totalChangeCount} inserted={liveDeltaInserted} deleted={liveDeltaDeleted} " +
                        $"restored={liveDeltaRestored} alreadyVisible={liveDeltaAlreadyVisible} " +
                        $"compactRequired={liveDeltaCompactRequired} liveDeltaCount={index.LiveDeltaCount}");

                    if (index.LiveDeltaCount > 0)
                    {
                        QueueLiveDeltaCompact("snapshot-catchup-force-merge", generation, force: true);
                    }
                }

                applyStopwatch.Stop();
                catchUpStopwatch.Stop();

                var volumeSnapshots = enumerator.CreateVolumeSnapshots(checkpoints);
                if (totalChangeCount == 0)
                {
                    _lastVolumeSnapshots = CopyVolumeSnapshots(volumeSnapshots);
                    QueueSnapshotSave(index.SortedArray, volumeSnapshots, _currentIndexContentFingerprint, index, generation);
                }
                QueueDefaultShortContainsHotBucketWarmup(index, "post-catchup", generation);
                QueueContainsAcceleratorWarmup("post-catchup", generation);

                UsnDiagLog.Write(
                    $"[SNAPSHOT CATCHUP TOTAL] catchUpMs={catchUpStopwatch.ElapsedMilliseconds} " +
                    $"applyMs={applyStopwatch.ElapsedMilliseconds} totalChanges={totalChangeCount} " +
                    $"watcherStartMs={watcherStartStopwatch.ElapsedMilliseconds} " +
                    $"watcherStartedBeforeCatchupApply=True catchupApplyStrategy={applyStrategy} " +
                    $"indexedCount={index.TotalCount}");

                PublishIndexStatus($"已索引 {_index.TotalCount} 个对象", false);
            }
            catch (OperationCanceledException)
            {
                PublishIndexStatus($"已索引 {_index.TotalCount} 个对象", false);
            }
            catch (Exception ex)
            {
                UsnDiagLog.Write($"[SNAPSHOT CATCHUP FAIL] {ex.GetType().Name}: {ex.Message}");
                PublishIndexStatus($"后台追平失败：{ex.Message}", false);
            }
            finally
            {
                lock (_backgroundCatchUpLock)
                {
                    if (ReferenceEquals(_backgroundCatchUpCts, backgroundCatchUpCts))
                    {
                        _backgroundCatchUpTask = null;
                        _backgroundCatchUpCts = null;
                    }
                }
            }
        }

        private void WaitForSearchIdleBeforeCatchUpApply(CancellationToken ct, int totalChangeCount)
        {
            if (totalChangeCount <= 0)
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var waited = false;
            while (stopwatch.ElapsedMilliseconds < CatchUpApplyMaxDeferMilliseconds)
            {
                ct.ThrowIfCancellationRequested();

                var activeSearches = Volatile.Read(ref _activeSearchCount);
                var lastCompletedTicks = Interlocked.Read(ref _lastSearchCompletedUtcTicks);
                if (activeSearches == 0)
                {
                    if (lastCompletedTicks > 0)
                    {
                        var idleMilliseconds = (DateTime.UtcNow - new DateTime(lastCompletedTicks, DateTimeKind.Utc)).TotalMilliseconds;
                        if (idleMilliseconds >= CatchUpApplyIdleDelayMilliseconds)
                        {
                            break;
                        }
                    }
                    else if (stopwatch.ElapsedMilliseconds >= 2000)
                    {
                        break;
                    }
                }

                waited = true;
                Thread.Sleep(100);
            }

            if (waited || stopwatch.ElapsedMilliseconds > 0)
            {
                UsnDiagLog.Write(
                    $"[SNAPSHOT CATCHUP APPLY WAIT] elapsedMs={stopwatch.ElapsedMilliseconds} " +
                    $"activeSearches={Volatile.Read(ref _activeSearchCount)} totalChanges={totalChangeCount}");
            }
        }

        private int RecoverExpiredVolumeFromMft(
            char driveLetter,
            IReadOnlyList<VolumeSnapshot> existingVolumes,
            IProgress<string> progress,
            CancellationToken ct,
            long previousGeneration,
            out long recoveredNextUsn,
            out ulong recoveredJournalId)
        {
            recoveredNextUsn = 0;
            recoveredJournalId = 0;
            var totalStopwatch = Stopwatch.StartNew();
            var dl = char.ToUpperInvariant(driveLetter);
            var oldIndex = _index;
            var oldEnumerator = _enumerator;
            if (!IsCurrentIndex(oldIndex, previousGeneration) || !ReferenceEquals(oldEnumerator, _enumerator))
            {
                UsnDiagLog.Write(
                    $"[MFT VOLUME RECOVER] outcome=skip-stale-generation drive={dl} generation={previousGeneration} currentGeneration={CurrentIndexGeneration}");
                return _index.TotalCount;
            }

            try
            {
                progress?.Report($"卷 {dl} 的 USN 游标已过期，正在恢复该卷...");
                var enumerateStopwatch = Stopwatch.StartNew();
                var recoveredRecords = new List<FileRecord>(300_000);
                var recoveredEnumerator = new MftEnumerator();
                var (count, nextUsn, journalId) = recoveredEnumerator.EnumerateVolumeIntoRecords(dl, recoveredRecords, ct);
                recoveredNextUsn = nextUsn;
                recoveredJournalId = journalId;
                enumerateStopwatch.Stop();

                ct.ThrowIfCancellationRequested();

                var mergeStopwatch = Stopwatch.StartNew();
                var mergedRecords = ReplaceDriveRecords(oldIndex.SortedArray, recoveredRecords, dl);
                mergeStopwatch.Stop();

                var buildStopwatch = Stopwatch.StartNew();
                var newIndex = new MemoryIndex();
                newIndex.LoadSortedRecords(
                    mergedRecords,
                    buildContainsAccelerator: true,
                    takeOwnership: true,
                    buildDerivedStructures: false,
                    buildShortAsciiStructures: true);
                var contentFingerprint = IndexSnapshotFingerprint.Compute(mergedRecords);
                buildStopwatch.Stop();

                var snapshotStopwatch = Stopwatch.StartNew();
                var newEnumerator = new MftEnumerator();
                newEnumerator.LoadVolumeSnapshots(existingVolumes);
                newEnumerator.ReplaceVolumeFrom(dl, recoveredEnumerator);
                var volumeSnapshots = CreateRecoveredVolumeSnapshots(existingVolumes, dl, nextUsn, journalId, newEnumerator);
                snapshotStopwatch.Stop();

                _index = newIndex;
                _enumerator = newEnumerator;
                _currentIndexContentFingerprint = contentFingerprint;
                _lastVolumeSnapshots = CopyVolumeSnapshots(volumeSnapshots);
                _lastStableSnapshotRecordCount = Math.Max(_lastStableSnapshotRecordCount, newIndex.TotalCount);
                var generation = NextIndexGeneration();
                _isSnapshotStale = false;
                lock (_pathCandidateCacheLock)
                {
                    _pathCandidateCache = null;
                }
                _usnWatcher.StartWatching(dl, nextUsn, journalId, ct);

                QueueDefaultShortContainsHotBucketWarmup(newIndex, "single-volume-recover", generation);
                QueueContainsAcceleratorWarmup("single-volume-recover", generation);
                QueueSnapshotSave(mergedRecords, volumeSnapshots, contentFingerprint, newIndex, generation);

                totalStopwatch.Stop();
                UsnDiagLog.Write(
                    $"[MFT VOLUME RECOVER] outcome=success drive={dl} totalMs={totalStopwatch.ElapsedMilliseconds} " +
                    $"enumerateMs={enumerateStopwatch.ElapsedMilliseconds} mergeMs={mergeStopwatch.ElapsedMilliseconds} " +
                    $"buildMs={buildStopwatch.ElapsedMilliseconds} snapshotMs={snapshotStopwatch.ElapsedMilliseconds} " +
                    $"volumeRecords={count} indexedCount={newIndex.TotalCount} nextUsn={nextUsn} journalId={journalId}");
                return newIndex.TotalCount;
            }
            catch (OperationCanceledException)
            {
                totalStopwatch.Stop();
                UsnDiagLog.Write($"[MFT VOLUME RECOVER] outcome=canceled drive={dl} totalMs={totalStopwatch.ElapsedMilliseconds}");
                throw;
            }
            catch (Exception ex)
            {
                totalStopwatch.Stop();
                _isSnapshotStale = true;
                UsnDiagLog.Write(
                    $"[MFT VOLUME RECOVER] outcome=failed drive={dl} totalMs={totalStopwatch.ElapsedMilliseconds} " +
                    $"error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
                return _index.TotalCount;
            }
        }

        private static FileRecord[] ReplaceDriveRecords(FileRecord[] existingRecords, List<FileRecord> replacementRecords, char driveLetter)
        {
            var dl = char.ToUpperInvariant(driveLetter);
            existingRecords = existingRecords ?? Array.Empty<FileRecord>();
            replacementRecords = replacementRecords ?? new List<FileRecord>();
            var retainedCount = 0;
            for (var i = 0; i < existingRecords.Length; i++)
            {
                var record = existingRecords[i];
                if (record != null && char.ToUpperInvariant(record.DriveLetter) != dl)
                    retainedCount++;
            }

            var merged = new FileRecord[retainedCount + replacementRecords.Count];
            var offset = 0;
            for (var i = 0; i < existingRecords.Length; i++)
            {
                var record = existingRecords[i];
                if (record != null && char.ToUpperInvariant(record.DriveLetter) != dl)
                    merged[offset++] = record;
            }

            replacementRecords.CopyTo(merged, offset);
            return merged;
        }

        private static VolumeSnapshot[] CreateRecoveredVolumeSnapshots(
            IReadOnlyList<VolumeSnapshot> existingVolumes,
            char recoveredDrive,
            long recoveredNextUsn,
            ulong recoveredJournalId,
            MftEnumerator enumerator)
        {
            var checkpoints = new List<(char driveLetter, long nextUsn, ulong journalId)>();
            var dl = char.ToUpperInvariant(recoveredDrive);
            var replaced = false;
            if (existingVolumes != null)
            {
                for (var i = 0; i < existingVolumes.Count; i++)
                {
                    var volume = existingVolumes[i];
                    if (volume == null)
                        continue;

                    var volumeDrive = char.ToUpperInvariant(volume.DriveLetter);
                    if (volumeDrive == dl)
                    {
                        checkpoints.Add((dl, recoveredNextUsn, recoveredJournalId));
                        replaced = true;
                    }
                    else
                    {
                        checkpoints.Add((volumeDrive, volume.NextUsn, volume.JournalId));
                    }
                }
            }

            if (!replaced)
                checkpoints.Add((dl, recoveredNextUsn, recoveredJournalId));

            return enumerator.CreateVolumeSnapshots(checkpoints.ToArray());
        }

        private void EnsureUsnJournalCapacityInBackground(char[] driveLetters)
        {
            if (driveLetters == null || driveLetters.Length == 0)
                return;

            if (Interlocked.Exchange(ref _usnJournalMaintenanceStarted, 1) != 0)
                return;

            var drives = driveLetters
                .Select(char.ToUpperInvariant)
                .Distinct()
                .ToArray();

            Task.Run(() =>
            {
                foreach (var drive in drives)
                {
                    try
                    {
                        EnsureUsnJournalCapacity(drive);
                    }
                    catch (Exception ex)
                    {
                        UsnDiagLog.Write(
                            $"[USN JOURNAL MAINTENANCE] outcome=failed drive={drive} " +
                            $"error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
                    }
                }
            });
        }

        private static void EnsureUsnJournalCapacity(char driveLetter)
        {
            var dl = char.ToUpperInvariant(driveLetter);
            var driveName = dl + ":";
            DriveInfo driveInfo;
            try
            {
                driveInfo = new DriveInfo(driveName);
            }
            catch (Exception ex)
            {
                UsnDiagLog.Write(
                    $"[USN JOURNAL MAINTENANCE] outcome=skip drive={dl} reason=drive-info-failed " +
                    $"error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
                return;
            }

            if (driveInfo.DriveType != DriveType.Fixed)
            {
                UsnDiagLog.Write($"[USN JOURNAL MAINTENANCE] outcome=skip drive={dl} reason=not-fixed");
                return;
            }

            if (!driveInfo.IsReady || !string.Equals(driveInfo.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                UsnDiagLog.Write($"[USN JOURNAL MAINTENANCE] outcome=skip drive={dl} reason=not-ready-or-not-ntfs");
                return;
            }

            if (!TryQueryUsnJournalSize(dl, out var maximumSize, out var allocationDelta))
            {
                UsnDiagLog.Write($"[USN JOURNAL MAINTENANCE] outcome=query-failed drive={dl}");
                return;
            }

            if (maximumSize >= TargetUsnJournalMaximumSize && allocationDelta >= TargetUsnJournalAllocationDelta)
            {
                UsnDiagLog.Write(
                    $"[USN JOURNAL MAINTENANCE] outcome=skip drive={dl} reason=already-large " +
                    $"maximumSize={maximumSize} allocationDelta={allocationDelta}");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "fsutil.exe",
                Arguments = $"usn createjournal m={TargetUsnJournalMaximumSize} a={TargetUsnJournalAllocationDelta} {dl}:",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(psi))
            {
                if (process == null)
                {
                    UsnDiagLog.Write($"[USN JOURNAL MAINTENANCE] outcome=start-failed drive={dl}");
                    return;
                }

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(10000);
                UsnDiagLog.Write(
                    $"[USN JOURNAL MAINTENANCE] outcome={(process.ExitCode == 0 ? "updated" : "failed")} drive={dl} " +
                    $"exitCode={process.ExitCode} oldMaximumSize={maximumSize} oldAllocationDelta={allocationDelta} " +
                    $"targetMaximumSize={TargetUsnJournalMaximumSize} targetAllocationDelta={TargetUsnJournalAllocationDelta} " +
                    $"stdout={IndexPerfLog.FormatValue(output)} stderr={IndexPerfLog.FormatValue(error)}");
            }
        }

        private static bool TryQueryUsnJournalSize(char driveLetter, out ulong maximumSize, out ulong allocationDelta)
        {
            maximumSize = 0;
            allocationDelta = 0;
            var psi = new ProcessStartInfo
            {
                FileName = "fsutil.exe",
                Arguments = $"usn queryjournal {char.ToUpperInvariant(driveLetter)}:",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(psi))
            {
                if (process == null)
                    return false;

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(10000);
                if (process.ExitCode != 0)
                {
                    UsnDiagLog.Write(
                        $"[USN JOURNAL MAINTENANCE] outcome=query-command-failed drive={char.ToUpperInvariant(driveLetter)} " +
                        $"exitCode={process.ExitCode} stderr={IndexPerfLog.FormatValue(error)}");
                    return false;
                }

                maximumSize = ParseFsutilSize(output, "Maximum Size");
                allocationDelta = ParseFsutilSize(output, "Allocation Delta");
                return maximumSize > 0 && allocationDelta > 0;
            }
        }

        private static ulong ParseFsutilSize(string output, string label)
        {
            if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(label))
                return 0;

            var pattern = Regex.Escape(label) + @"\s*:\s*0x(?<hex>[0-9a-fA-F]+)";
            var match = Regex.Match(output, pattern);
            return match.Success && ulong.TryParse(
                match.Groups["hex"].Value,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
                ? value
                : 0;
        }

        private void CancelBackgroundCatchUp()
        {
            CancellationTokenSource cts;
            lock (_backgroundCatchUpLock)
            {
                cts = _backgroundCatchUpCts;
                _backgroundCatchUpCts = null;
                _backgroundCatchUpTask = null;
            }

            if (cts == null)
                return;

            try
            {
                cts.Cancel();
            }
            catch
            {
            }
            finally
            {
                cts.Dispose();
            }
        }

        private void QueueContainsAcceleratorWarmup(string reason)
        {
            QueueContainsAcceleratorWarmup(reason, CurrentIndexGeneration);
        }

        private void QueueContainsAcceleratorWarmup(string reason, long generation)
        {
            var index = _index;
            if (index == null
                || generation != CurrentIndexGeneration
                || index.TotalCount == 0
                || index.SupportsContainsAccelerator(MemoryIndex.ContainsWarmupScope.Full))
            {
                return;
            }

            CancellationTokenSource cts;
            lock (_containsWarmupLock)
            {
                if (_containsWarmupTask != null && !_containsWarmupTask.IsCompleted)
                {
                    UsnDiagLog.Write(
                        $"[CONTAINS WARMUP] outcome=skip-running reason={IndexPerfLog.FormatValue(reason)} records={index.TotalCount}");
                    return;
                }

                try
                {
                    _containsWarmupCts?.Cancel();
                    _containsWarmupCts?.Dispose();
                }
                catch
                {
                }

                cts = new CancellationTokenSource();
                _containsWarmupCts = cts;
                _containsWarmupTask = Task.Run(() => RunContainsAcceleratorWarmup(index, reason, cts, generation), cts.Token);
            }
        }

        private void RunContainsAcceleratorWarmup(MemoryIndex index, string reason, CancellationTokenSource warmupCts, long generation)
        {
            var ct = warmupCts.Token;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                if (index == null || index.TotalCount == 0 || index.SupportsContainsAccelerator(MemoryIndex.ContainsWarmupScope.Full))
                {
                    return;
                }
                if (!IsCurrentIndex(index, generation))
                {
                    UsnDiagLog.Write(
                        $"[CONTAINS WARMUP] outcome=skip-stale-generation reason={IndexPerfLog.FormatValue(reason)} " +
                        $"generation={generation} currentGeneration={CurrentIndexGeneration}");
                    return;
                }

                UsnDiagLog.Write(
                    $"[CONTAINS WARMUP] outcome=start reason={IndexPerfLog.FormatValue(reason)} " +
                    $"records={index.TotalCount}");
                index.TryEnsureContainsAccelerator(MemoryIndex.ContainsWarmupScope.Short, ct);
                UsnDiagLog.Write(
                    $"[CONTAINS WARMUP] outcome=stage reason={IndexPerfLog.FormatValue(reason)} " +
                    $"stage=short-ready elapsedMs={stopwatch.ElapsedMilliseconds} records={index.TotalCount}");
                index.TryEnsureContainsAccelerator(MemoryIndex.ContainsWarmupScope.TrigramOnly, ct);
                UsnDiagLog.Write(
                    $"[CONTAINS WARMUP] outcome=stage reason={IndexPerfLog.FormatValue(reason)} " +
                    $"stage=trigram-ready elapsedMs={stopwatch.ElapsedMilliseconds} records={index.TotalCount}");
                if (!BuildShortContainsBuckets)
                {
                    stopwatch.Stop();
                    UsnDiagLog.Write(
                        $"[CONTAINS WARMUP] outcome=trigram-only reason={IndexPerfLog.FormatValue(reason)} " +
                        $"elapsedMs={stopwatch.ElapsedMilliseconds} records={index.TotalCount}");
                    if (!IsCurrentIndex(index, generation))
                        return;
                    PublishIndexStatus("索引已就绪；多字符桶已就绪，短查询走低内存扫描", false);
                    SaveCurrentContainsPostings(index, reason, generation);
                    return;
                }

                if (IsCurrentIndex(index, generation))
                    PublishIndexStatus("索引已就绪；多字符桶已就绪，单字符/双字符桶构建中...", false);
                index.TryEnsureContainsAccelerator(MemoryIndex.ContainsWarmupScope.Full, ct);
                stopwatch.Stop();
                UsnDiagLog.Write(
                    $"[CONTAINS WARMUP] outcome=success reason={IndexPerfLog.FormatValue(reason)} " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds} records={index.TotalCount}");
                if (!IsCurrentIndex(index, generation))
                    return;
                PublishIndexStatus("索引和全部搜索桶已就绪", false);
                SaveCurrentContainsPostings(index, reason, generation);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                UsnDiagLog.Write(
                    $"[CONTAINS WARMUP] outcome=canceled reason={IndexPerfLog.FormatValue(reason)} " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                UsnDiagLog.Write(
                    $"[CONTAINS WARMUP] outcome=failed reason={IndexPerfLog.FormatValue(reason)} " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
            }
            finally
            {
                lock (_containsWarmupLock)
                {
                    if (ReferenceEquals(_containsWarmupCts, warmupCts))
                    {
                        _containsWarmupTask = null;
                        _containsWarmupCts = null;
                    }
                }

                try
                {
                    warmupCts.Dispose();
                }
                catch
                {
                }
            }
        }

        private void SaveCurrentContainsPostings(MemoryIndex index, string reason, long generation)
        {
            if (index == null)
            {
                UsnDiagLog.Write(
                    $"[POSTINGS SNAPSHOT SAVE] outcome=skip-null-index reason={IndexPerfLog.FormatValue(reason)}");
                return;
            }
            if (!IsCurrentIndex(index, generation))
            {
                UsnDiagLog.Write(
                    $"[POSTINGS SNAPSHOT SAVE] outcome=skip-stale-generation reason={IndexPerfLog.FormatValue(reason)} " +
                    $"generation={generation} currentGeneration={CurrentIndexGeneration}");
                return;
            }

            var postings = index.ExportContainsPostingsSnapshot(out var exportedFingerprint);
            if (postings == null || postings.RecordCount <= 0)
            {
                UsnDiagLog.Write(
                    $"[POSTINGS SNAPSHOT SAVE] outcome=skip-empty reason={IndexPerfLog.FormatValue(reason)}");
                return;
            }

            var expectedFingerprint = _currentIndexContentFingerprint;
            if (expectedFingerprint == 0)
            {
                UsnDiagLog.Write(
                    $"[POSTINGS SNAPSHOT SAVE] outcome=skip-empty-fingerprint reason={IndexPerfLog.FormatValue(reason)} records={postings.RecordCount}");
                return;
            }

            if (exportedFingerprint != expectedFingerprint)
            {
                UsnDiagLog.Write(
                    $"[POSTINGS SNAPSHOT SAVE] outcome=skip-fingerprint-mismatch reason={IndexPerfLog.FormatValue(reason)} " +
                    $"expectedFingerprint={expectedFingerprint} exportedFingerprint={exportedFingerprint} records={postings.RecordCount}");
                return;
            }

            lock (_snapshotWriteLock)
            {
                UsnDiagLog.Write(
                    $"[POSTINGS SNAPSHOT SAVE] outcome=write-start reason={IndexPerfLog.FormatValue(reason)} " +
                    $"fingerprint={expectedFingerprint} exportedFingerprint={exportedFingerprint} records={postings.RecordCount}");
                _snapshotStore.SaveContainsPostingsSnapshot(expectedFingerprint, postings);
            }
        }

        private void QueueLiveDeltaCompact(string reason)
        {
            QueueLiveDeltaCompact(reason, CurrentIndexGeneration, force: false);
        }

        private void QueueLiveDeltaCompact(string reason, long generation)
        {
            QueueLiveDeltaCompact(reason, generation, force: false);
        }

        private void QueueLiveDeltaCompact(string reason, long generation, bool force)
        {
            var index = _index;
            if (index == null || generation != CurrentIndexGeneration || index.LiveDeltaCount == 0)
                return;

            lock (_containsWarmupLock)
            {
                if (_liveDeltaCompactTask != null && !_liveDeltaCompactTask.IsCompleted)
                {
                    UsnDiagLog.Write(
                        $"[LIVE DELTA COMPACT] outcome=skip-running reason={IndexPerfLog.FormatValue(reason)} liveDeltaCount={index.LiveDeltaCount}");
                    return;
                }

                var nowTicks = DateTime.UtcNow.Ticks;
                var lastTicks = Interlocked.Read(ref _lastLiveDeltaCompactAttemptUtcTicks);
                if (!force
                    && lastTicks > 0
                    && (new TimeSpan(nowTicks - lastTicks)).TotalMilliseconds < LiveDeltaCompactMinIntervalMilliseconds)
                {
                    UsnDiagLog.Write(
                        $"[LIVE DELTA COMPACT] outcome=skip-throttled reason={IndexPerfLog.FormatValue(reason)} liveDeltaCount={index.LiveDeltaCount}");
                    return;
                }

                Interlocked.Exchange(ref _lastLiveDeltaCompactAttemptUtcTicks, nowTicks);

                _liveDeltaCompactCts?.Cancel();
                _liveDeltaCompactCts?.Dispose();
                _liveDeltaCompactCts = new CancellationTokenSource();
                var cts = _liveDeltaCompactCts;
                _liveDeltaCompactTask = Task.Run(() => RunLiveDeltaCompact(index, reason, cts, generation), cts.Token);
            }
        }

        private async Task RunLiveDeltaCompact(MemoryIndex index, string reason, CancellationTokenSource compactCts, long generation)
        {
            var ct = compactCts.Token;
            try
            {
                await Task.Delay(LiveDeltaCompactDelayMilliseconds, ct).ConfigureAwait(false);
                if (!IsCurrentIndex(index, generation))
                {
                    UsnDiagLog.Write(
                        $"[LIVE DELTA COMPACT] outcome=skip-stale-generation reason={IndexPerfLog.FormatValue(reason)} " +
                        $"generation={generation} currentGeneration={CurrentIndexGeneration}");
                    return;
                }
                UsnDiagLog.Write(
                    $"[LIVE DELTA COMPACT] outcome=start reason={IndexPerfLog.FormatValue(reason)} liveDeltaCount={index.LiveDeltaCount}");

                long compactMs;
                var compacted = index.TryCompactLiveDeltaOverlay(out compactMs, ct);
                UsnDiagLog.Write(
                    $"[LIVE DELTA COMPACT] outcome={(compacted ? "success" : "stale-or-empty")} reason={IndexPerfLog.FormatValue(reason)} " +
                    $"compactMs={compactMs} liveDeltaCount={index.LiveDeltaCount} records={index.TotalCount} rebuildContains=async");

                if (compacted)
                {
                    if (!IsCurrentIndex(index, generation))
                    {
                        UsnDiagLog.Write(
                            $"[LIVE DELTA COMPACT] outcome=skip-save-stale-generation reason={IndexPerfLog.FormatValue(reason)} " +
                            $"generation={generation} currentGeneration={CurrentIndexGeneration}");
                        return;
                    }
                    _currentIndexContentFingerprint = IndexSnapshotFingerprint.Compute(index.SortedArray);
                    QueueSnapshotSave(index.SortedArray, _lastVolumeSnapshots, _currentIndexContentFingerprint, index, generation);
                    QueueContainsAcceleratorWarmup("post-live-delta-compact", generation);
                }
            }
            catch (OperationCanceledException)
            {
                UsnDiagLog.Write(
                    $"[LIVE DELTA COMPACT] outcome=canceled reason={IndexPerfLog.FormatValue(reason)}");
            }
            catch (Exception ex)
            {
                UsnDiagLog.Write(
                    $"[LIVE DELTA COMPACT] outcome=failed reason={IndexPerfLog.FormatValue(reason)} error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
            }
            finally
            {
                lock (_containsWarmupLock)
                {
                    if (ReferenceEquals(_liveDeltaCompactCts, compactCts))
                    {
                        _liveDeltaCompactCts = null;
                        _liveDeltaCompactTask = null;
                    }
                }

                compactCts.Dispose();
            }
        }

        private void CancelContainsWarmup()
        {
            CancellationTokenSource cts;
            CancellationTokenSource shortCts;
            CancellationTokenSource compactCts;
            lock (_containsWarmupLock)
            {
                cts = _containsWarmupCts;
                shortCts = _shortContainsWarmupCts;
                compactCts = _liveDeltaCompactCts;
                _containsWarmupCts = null;
                _containsWarmupTask = null;
                _shortContainsWarmupCts = null;
                _shortContainsWarmupTask = null;
                _liveDeltaCompactCts = null;
                _liveDeltaCompactTask = null;
                _shortContainsWarmups.Clear();
            }

            CancelAndDispose(cts);
            CancelAndDispose(shortCts);
            CancelAndDispose(compactCts);
        }

        private static void CancelAndDispose(CancellationTokenSource cts)
        {
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            try
            {
                cts.Dispose();
            }
            catch
            {
            }
        }

        private void PublishIndexStatus(string message, bool isBackgroundCatchUpInProgress, bool requireSearchRefresh = false)
        {
            _currentStatusMessage = message ?? string.Empty;
            _isBackgroundCatchUpInProgress = isBackgroundCatchUpInProgress;
            IndexStatusChanged?.Invoke(this, new IndexStatusChangedEventArgs(
                _currentStatusMessage,
                _index.TotalCount,
                isBackgroundCatchUpInProgress,
                requireSearchRefresh,
                ContainsBucketStatus));
        }

        private List<IndexChangedEventArgs> BuildBatchIndexChangedArgs(IReadOnlyList<UsnChangeEntry> changes)
        {
            var result = new List<IndexChangedEventArgs>(changes?.Count ?? 0);
            if (changes == null || changes.Count == 0)
                return result;

            for (var i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                switch (change.Kind)
                {
                    case UsnChangeKind.Create:
                        result.Add(new IndexChangedEventArgs(
                            IndexChangeType.Created,
                            change.LowerName,
                            _enumerator.ResolveFullPath(change.DriveLetter, change.ParentFrn, change.OriginalName),
                            isDirectory: change.IsDirectory));
                        break;

                    case UsnChangeKind.Delete:
                        result.Add(new IndexChangedEventArgs(
                            IndexChangeType.Deleted,
                            change.LowerName,
                            _enumerator.ResolveFullPath(change.DriveLetter, change.ParentFrn, change.OriginalName),
                            isDirectory: change.IsDirectory));
                        break;

                    case UsnChangeKind.Rename:
                        result.Add(new IndexChangedEventArgs(
                            IndexChangeType.Renamed,
                            change.OldLowerName,
                            _enumerator.ResolveFullPath(change.DriveLetter, change.ParentFrn, change.OriginalName),
                            _enumerator.ResolveFullPath(change.DriveLetter, change.OldParentFrn, change.OldLowerName),
                            change.OriginalName,
                            change.LowerName,
                            change.IsDirectory));
                        break;
                }
            }

            return result;
        }

        private void PublishBatchIndexChanges(IReadOnlyList<IndexChangedEventArgs> changes)
        {
            if (changes == null || changes.Count == 0)
                return;

            for (var i = 0; i < changes.Count; i++)
                IndexChanged?.Invoke(this, changes[i]);
        }

        private void QueueSnapshotSave(
            FileRecord[] records,
            VolumeSnapshot[] volumeSnapshots,
            ulong contentFingerprint = 0,
            MemoryIndex expectedIndex = null,
            long generation = 0)
        {
            if (records == null || volumeSnapshots == null || volumeSnapshots.Length == 0)
                return;

            var fingerprint = contentFingerprint != 0
                ? contentFingerprint
                : IndexSnapshotFingerprint.Compute(records);
            var recordCopy = new FileRecord[records.Length];
            Array.Copy(records, recordCopy, records.Length);
            var volumeCopy = new VolumeSnapshot[volumeSnapshots.Length];
            Array.Copy(volumeSnapshots, volumeCopy, volumeSnapshots.Length);

            Task.Run(() =>
            {
                try
                {
                    if (expectedIndex != null && !IsCurrentIndex(expectedIndex, generation))
                    {
                        UsnDiagLog.Write(
                            $"[SNAPSHOT SAVE] outcome=skip-stale-generation records={recordCopy.Length} " +
                            $"generation={generation} currentGeneration={CurrentIndexGeneration}");
                        return;
                    }
                    if (IsSuspiciousSnapshotShrink(recordCopy.Length, _lastStableSnapshotRecordCount))
                    {
                        UsnDiagLog.Write(
                            $"[SNAPSHOT SAVE] outcome=skip-suspicious-shrink records={recordCopy.Length} " +
                            $"lastStable={_lastStableSnapshotRecordCount}");
                        return;
                    }

                    IndexSnapshotSaveMetrics metrics;
                    long elapsedMilliseconds;
                    lock (_snapshotWriteLock)
                    {
                        var saveStopwatch = Stopwatch.StartNew();
                        var containsPostings = expectedIndex != null
                            ? expectedIndex.ExportContainsPostingsSnapshot()
                            : _index.ExportContainsPostingsSnapshot();
                        ulong shortQueryFingerprint;
                        var shortQuerySnapshot = expectedIndex != null
                            ? expectedIndex.ExportShortQuerySnapshot(out shortQueryFingerprint)
                            : _index.ExportShortQuerySnapshot(out shortQueryFingerprint);
                        if (shortQueryFingerprint != 0 && shortQueryFingerprint != fingerprint)
                        {
                            shortQuerySnapshot = null;
                        }
                        ulong parentOrderFingerprint;
                        var parentOrder = expectedIndex != null
                            ? expectedIndex.ExportParentOrderSnapshot(out parentOrderFingerprint)
                            : _index.ExportParentOrderSnapshot(out parentOrderFingerprint);
                        if (parentOrderFingerprint != 0 && parentOrderFingerprint != fingerprint)
                        {
                            parentOrder = null;
                        }
                        metrics = _snapshotStore.Save(new IndexSnapshot(
                            recordCopy,
                            volumeCopy,
                            containsPostings: containsPostings,
                            contentFingerprint: fingerprint,
                            shortQuerySnapshot: shortQuerySnapshot,
                            parentOrder: parentOrder));
                        saveStopwatch.Stop();
                        elapsedMilliseconds = saveStopwatch.ElapsedMilliseconds;
                    }

                    if (metrics != null)
                    {
                        _lastStableSnapshotRecordCount = Math.Max(_lastStableSnapshotRecordCount, metrics.RecordCount);
                        UsnDiagLog.Write(
                            $"[SNAPSHOT SAVE] elapsedMs={elapsedMilliseconds} version={metrics.Version} " +
                            $"fileBytes={metrics.FileBytes} records={metrics.RecordCount} volumes={metrics.VolumeCount} frnEntries={metrics.FrnEntryCount}");
                    }
                }
                catch (Exception ex)
                {
                    UsnDiagLog.Write($"[SNAPSHOT SAVE QUEUED FAIL] {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        private void ScheduleSnapshotSave()
        {
            CancellationTokenSource cts;
            var delayMilliseconds = SnapshotSaveDebounceMilliseconds;
            lock (_snapshotSaveLock)
            {
                var old = _snapshotSaveCts;
                var now = DateTime.UtcNow;
                if (_pendingSnapshotSaveStartedUtc == DateTime.MinValue)
                {
                    _pendingSnapshotSaveStartedUtc = now;
                }
                else if ((now - _pendingSnapshotSaveStartedUtc).TotalMilliseconds >= SnapshotSaveMaxDeferredMilliseconds)
                {
                    delayMilliseconds = 0;
                    _pendingSnapshotSaveStartedUtc = now;
                }

                if (old != null)
                {
                    old.Cancel();
                    old.Dispose();
                }

                cts = new CancellationTokenSource();
                _snapshotSaveCts = cts;
            }

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMilliseconds, cts.Token).ConfigureAwait(false);
                    SaveCurrentSnapshot("debounce");
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    lock (_snapshotSaveLock)
                    {
                        if (ReferenceEquals(_snapshotSaveCts, cts))
                        {
                            _snapshotSaveCts = null;
                            _pendingSnapshotSaveStartedUtc = DateTime.MinValue;
                        }
                    }
                }
            });
        }


        private void StartPeriodicSnapshotSaveLoop()
        {
            lock (_periodicSnapshotSaveLock)
            {
                if (_periodicSnapshotSaveCts != null)
                {
                    return;
                }

                var cts = new CancellationTokenSource();
                _periodicSnapshotSaveCts = cts;
                _periodicSnapshotSaveTask = Task.Run(() => RunPeriodicSnapshotSaveLoop(cts));
            }
        }

        private async Task RunPeriodicSnapshotSaveLoop(CancellationTokenSource periodicSnapshotSaveCts)
        {
            var ct = periodicSnapshotSaveCts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(SnapshotPeriodicSaveMilliseconds, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    if (ShouldDeferSnapshotSave())
                    {
                        UsnDiagLog.Write("[SNAPSHOT PERIODIC SKIP] reason=search-active");
                        continue;
                    }

                    SaveCurrentSnapshot("periodic");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                UsnDiagLog.Write($"[SNAPSHOT PERIODIC FAIL] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                lock (_periodicSnapshotSaveLock)
                {
                    if (ReferenceEquals(_periodicSnapshotSaveCts, periodicSnapshotSaveCts))
                    {
                        _periodicSnapshotSaveCts = null;
                        _periodicSnapshotSaveTask = null;
                    }
                }

                periodicSnapshotSaveCts.Dispose();
            }
        }

        private void StopPeriodicSnapshotSaveLoop()
        {
            CancellationTokenSource cts;
            lock (_periodicSnapshotSaveLock)
            {
                cts = _periodicSnapshotSaveCts;
                _periodicSnapshotSaveCts = null;
                _periodicSnapshotSaveTask = null;
            }

            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch
            {
            }
        }
        private void CancelPendingSnapshotSave()
        {
            lock (_snapshotSaveLock)
            {
                var cts = _snapshotSaveCts;
                _snapshotSaveCts = null;
                _pendingSnapshotSaveStartedUtc = DateTime.MinValue;
                if (cts == null)
                    return;

                cts.Cancel();
                cts.Dispose();
            }
        }

        private void SaveCurrentSnapshot(string reason)
        {
            try
            {
                var index = _index;
                var generation = CurrentIndexGeneration;
                var liveDeltaCount = index?.LiveDeltaCount ?? 0;
                if (liveDeltaCount > 0)
                {
                    long compactMs;
                    if (index != null && index.TryCompactLiveDeltaOverlay(out compactMs))
                    {
                        UsnDiagLog.Write(
                            $"[SNAPSHOT SAVE COMPACT] outcome=success reason={reason} compactMs={compactMs} records={index.TotalCount}");
                        if (!IsCurrentIndex(index, generation))
                        {
                            UsnDiagLog.Write(
                                $"[SNAPSHOT SAVE COMPACT] outcome=skip-stale-generation reason={reason} " +
                                $"generation={generation} currentGeneration={CurrentIndexGeneration}");
                            return;
                        }

                        _currentIndexContentFingerprint = IndexSnapshotFingerprint.Compute(index.SortedArray);
                        liveDeltaCount = index.LiveDeltaCount;
                    }

                    if (liveDeltaCount == 0)
                    {
                        CancelPendingSnapshotSave();
                    }
                    else
                    {
                        UsnDiagLog.Write(
                            $"[SNAPSHOT SAVE DEFER] reason={reason} liveDeltaCount={liveDeltaCount} " +
                            "message=base-snapshot-not-compacted");
                        return;
                    }
                }

                if (!string.Equals(reason, "shutdown", StringComparison.OrdinalIgnoreCase)
                    && ShouldDeferSnapshotSave())
                {
                    UsnDiagLog.Write($"[SNAPSHOT SAVE DEFER] reason={reason} searchActive={Volatile.Read(ref _activeSearchCount)}");
                    ScheduleSnapshotSave();
                    return;
                }

                var totalStopwatch = Stopwatch.StartNew();
                var checkpoints = _usnWatcher.GetVolumeCheckpoints();
                VolumeSnapshot[] volumeSnapshots;
                long volumeSnapshotMilliseconds;
                if (checkpoints == null || checkpoints.Length == 0)
                {
                    volumeSnapshots = CopyVolumeSnapshots(_lastVolumeSnapshots);
                    if (volumeSnapshots == null || volumeSnapshots.Length == 0)
                        return;
                    volumeSnapshotMilliseconds = 0;
                }
                else
                {
                    var volumeStopwatch = Stopwatch.StartNew();
                    volumeSnapshots = _enumerator.CreateVolumeSnapshots(checkpoints);
                    volumeSnapshotMilliseconds = volumeStopwatch.ElapsedMilliseconds;
                    _lastVolumeSnapshots = CopyVolumeSnapshots(volumeSnapshots);
                    UsnDiagLog.Write(
                        $"[SNAPSHOT SAVE VOLUMES] reason={reason} source=checkpoints elapsedMs={volumeSnapshotMilliseconds} volumes={volumeSnapshots.Length}");
                }

                if (!IsCurrentIndex(index, generation))
                    return;

                var records = index.SortedArray;
                if (records == null || records.Length == 0)
                    return;
                if (IsSuspiciousSnapshotShrink(records.Length, _lastStableSnapshotRecordCount))
                {
                    UsnDiagLog.Write(
                        $"[SNAPSHOT SAVE LIVE] outcome=skip-suspicious-shrink reason={reason} " +
                        $"records={records.Length} lastStable={_lastStableSnapshotRecordCount}");
                    return;
                }

                var prepStopwatch = Stopwatch.StartNew();
                var recordCopy = new FileRecord[records.Length];
                Array.Copy(records, recordCopy, records.Length);
                var recordCopyMilliseconds = prepStopwatch.ElapsedMilliseconds;

                IndexSnapshotSaveMetrics metrics;
                long elapsedMilliseconds;
                lock (_snapshotWriteLock)
                {
                    var saveStopwatch = Stopwatch.StartNew();
                    if (!IsCurrentIndex(index, generation))
                        return;
                    var containsPostings = index.ExportContainsPostingsSnapshot();
                    var shortQuerySnapshot = index.ExportShortQuerySnapshot(out var shortQueryFingerprint);
                    var fingerprint = IndexSnapshotFingerprint.Compute(recordCopy);
                    if (shortQueryFingerprint != 0 && shortQueryFingerprint != fingerprint)
                    {
                        shortQuerySnapshot = null;
                    }
                    var parentOrder = index.ExportParentOrderSnapshot(out var parentOrderFingerprint);
                    if (parentOrderFingerprint != 0 && parentOrderFingerprint != fingerprint)
                    {
                        parentOrder = null;
                    }
                    _currentIndexContentFingerprint = fingerprint;
                    metrics = _snapshotStore.Save(new IndexSnapshot(
                        recordCopy,
                        volumeSnapshots,
                        containsPostings: containsPostings,
                        contentFingerprint: fingerprint,
                        shortQuerySnapshot: shortQuerySnapshot,
                        parentOrder: parentOrder));
                    saveStopwatch.Stop();
                    elapsedMilliseconds = saveStopwatch.ElapsedMilliseconds;
                }

                if (metrics != null)
                {
                    _lastStableSnapshotRecordCount = Math.Max(_lastStableSnapshotRecordCount, metrics.RecordCount);
                    totalStopwatch.Stop();
                    UsnDiagLog.Write(
                        $"[SNAPSHOT SAVE LIVE] reason={reason} elapsedMs={elapsedMilliseconds} version={metrics.Version} " +
                        $"fileBytes={metrics.FileBytes} records={metrics.RecordCount} volumes={metrics.VolumeCount} frnEntries={metrics.FrnEntryCount}");
                    UsnDiagLog.Write(
                        $"[SNAPSHOT SAVE LIVE DETAIL] reason={reason} totalMs={totalStopwatch.ElapsedMilliseconds} " +
                        $"recordCopyMs={recordCopyMilliseconds} volumeSnapshotMs={volumeSnapshotMilliseconds} saveMs={elapsedMilliseconds}");
                }
            }
            catch (Exception ex)
            {
                UsnDiagLog.Write($"[SNAPSHOT SAVE LIVE FAIL] reason={reason} {ex.GetType().Name}: {ex.Message}");
            }
        }

        private bool ShouldDeferSnapshotSave()
        {
            if (Volatile.Read(ref _activeSearchCount) > 0)
                return true;

            var ticks = Interlocked.Read(ref _lastSearchCompletedUtcTicks);
            if (ticks <= 0)
                return false;

            return (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalMilliseconds
                   < SnapshotSearchIdleDelayMilliseconds;
        }

        private static VolumeSnapshot[] CopyVolumeSnapshots(VolumeSnapshot[] volumes)
        {
            if (volumes == null || volumes.Length == 0)
                return Array.Empty<VolumeSnapshot>();

            var copy = new VolumeSnapshot[volumes.Length];
            Array.Copy(volumes, copy, volumes.Length);
            return copy;
        }

        private sealed class ContainsQueryCacheEntry
        {
            public string ScopeKey { get; set; }
            public long ContentVersion { get; set; }
            public SearchTypeFilter Filter { get; set; }
            public string Query { get; set; }
            public FileRecord[] Matches { get; set; }
        }

        private sealed class PathCandidateCacheEntry
        {
            public string PathPrefix { get; set; }
            public char DriveLetter { get; set; }
            public ulong RootFrn { get; set; }
            public SearchTypeFilter Filter { get; set; }
            public ulong[] DirectoryFrns { get; set; }
            public FileRecord[] Candidates { get; set; }
            private HashSet<ulong> _directorySet;

            public bool ContainsDirectory(ulong frn)
            {
                if (frn == 0 || DirectoryFrns == null || DirectoryFrns.Length == 0)
                    return false;

                if (_directorySet == null)
                    _directorySet = new HashSet<ulong>(DirectoryFrns);

                return _directorySet.Contains(frn);
            }
        }

    }

    public enum IndexChangeType { Created, Deleted, Renamed }

    public sealed class IndexStatusChangedEventArgs : EventArgs
    {
        public IndexStatusChangedEventArgs(string message, int indexedCount, bool isBackgroundCatchUpInProgress, bool requireSearchRefresh, ContainsBucketStatus containsBucketStatus = null)
        {
            Message = message;
            IndexedCount = indexedCount;
            IsBackgroundCatchUpInProgress = isBackgroundCatchUpInProgress;
            RequireSearchRefresh = requireSearchRefresh;
            ContainsBucketStatus = containsBucketStatus ?? ContainsBucketStatus.Empty;
        }

        public string Message { get; }
        public int IndexedCount { get; }
        public bool IsBackgroundCatchUpInProgress { get; }
        public bool RequireSearchRefresh { get; }
        public ContainsBucketStatus ContainsBucketStatus { get; }
    }

    public sealed class IndexChangedEventArgs : EventArgs
    {
        public IndexChangedEventArgs(IndexChangeType type, string lowerName, string fullPath,
            string oldFullPath = null, string newOriginalName = null, string newLowerName = null, bool isDirectory = false)
        {
            Type            = type;
            LowerName       = lowerName;
            FullPath        = fullPath;
            OldFullPath     = oldFullPath;
            NewOriginalName = newOriginalName;
            NewLowerName    = newLowerName;
            IsDirectory     = isDirectory;
        }

        public IndexChangeType Type            { get; }
        /// <summary>变更文件名小写（用于匹配当前搜索词）。</summary>
        public string          LowerName       { get; }
        /// <summary>完整路径（Created/Renamed 时有值，Deleted 时为 null）。</summary>
        public string          FullPath        { get; }
        /// <summary>重命名前的完整路径（仅 Renamed）。</summary>
        public string          OldFullPath     { get; }
        /// <summary>重命名后的原始文件名（仅 Renamed）。</summary>
        public string          NewOriginalName { get; }
        /// <summary>重命名后的文件名小写（仅 Renamed）。</summary>
        public string          NewLowerName    { get; }
        /// <summary>是否为目录。</summary>
        public bool            IsDirectory     { get; }
    }
}

    /// <summary>索引服务性能诊断日志包装器。</summary>
    internal static class UsnDiagLog
    {
        public static void Write(string msg)
        {
            MftScanner.IndexPerfLog.Write("INDEX", msg);
        }
    }
