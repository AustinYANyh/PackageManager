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
    public sealed class IndexService
    {
        private readonly MftEnumerator _enumerator = new MftEnumerator();
        private readonly IndexSnapshotStore _snapshotStore = new IndexSnapshotStore();
        private readonly UsnWatcher _usnWatcher = new UsnWatcher();
        private volatile MemoryIndex _index = new MemoryIndex();
        private readonly object _snapshotSaveLock = new object();
        private bool _watcherInitialized;
        private CancellationTokenSource _snapshotSaveCts;

        private const int SnapshotSaveDebounceMilliseconds = 5000;

        // 保存 progress 引用，供 OnJournalOverflow 使用（需求 6.5）
        private IProgress<string> _progress;

        /// <summary>当前内存索引实例（供 SearchAsync 使用）。</summary>
        public MemoryIndex Index => _index;

        /// <summary>文件系统增量变更事件，携带变更类型和文件信息，供 UI 直接更新列表。</summary>
        public event EventHandler<IndexChangedEventArgs> IndexChanged;

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
            return Task.Run(() => BuildIndex(progress, ct), ct);
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
            return Task.Run(() =>
            {
                CancelPendingSnapshotSave();
                _usnWatcher.StopWatching();
                _index.Build(Array.Empty<FileRecord>());
                var result = BuildIndexFromMft(_progress, ct);
                return result;
            }, ct);
        }

        public void Shutdown()
        {
            CancelPendingSnapshotSave();
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
            _usnWatcher.JournalOverflow += OnJournalOverflow;
            _watcherInitialized = true;
        }

        /// <summary>文件创建：插入新 FileRecord，同时更新 FRN 字典供路径解析。需求 6.2</summary>
        private void OnFileCreated(object sender, UsnFileCreatedEventArgs args)
        {
            _enumerator.RegisterFrn(args.DriveLetter, args.Frn, args.FileName, args.ParentFrn, args.IsDirectory);

            var lowerName = args.FileName.ToLowerInvariant();

            // 同一文件的 USN 事件可能触发多次（创建+写入+关闭），用 FRN 去重
            // 如果索引里已有相同 (lowerName, parentFrn, driveLetter) 的记录，跳过
            var idx = _index;
            if (idx.ExactHashMap.TryGetValue(lowerName, out var existing)
                && existing.Exists(r => r.ParentFrn == args.ParentFrn && r.DriveLetter == args.DriveLetter))
                return;

            var record = new FileRecord(
                lowerName:    lowerName,
                originalName: args.FileName,
                parentFrn:    args.ParentFrn,
                driveLetter:  args.DriveLetter,
                isDirectory:  args.IsDirectory);
            _index.Insert(record);
            var fullPath = _enumerator.ResolveFullPath(args.DriveLetter, args.ParentFrn, args.FileName);
            ScheduleSnapshotSave();
            IndexChanged?.Invoke(this, new IndexChangedEventArgs(
                IndexChangeType.Created, lowerName, fullPath));
        }

        /// <summary>文件删除：从索引中移除记录。需求 6.3</summary>
        private void OnFileDeleted(object sender, UsnFileDeletedEventArgs args)
        {
            // 删除前先解析路径（FRN 字典此时还未清除），供 UI 精确匹配
            var fullPath = _enumerator.ResolveFullPath(args.DriveLetter, args.ParentFrn, args.LowerName);
            _index.Remove(args.LowerName, args.ParentFrn, args.DriveLetter);
            ScheduleSnapshotSave();
            IndexChanged?.Invoke(this, new IndexChangedEventArgs(
                IndexChangeType.Deleted, args.LowerName, fullPath));
        }

        /// <summary>文件重命名：更新 FRN 字典，先移除旧记录再插入新记录。需求 6.4</summary>
        private void OnFileRenamed(object sender, UsnFileRenamedEventArgs args)
        {
            var oldFullPath = _enumerator.ResolveFullPath(
                args.DriveLetter, args.OldParentFrn, args.OldLowerName);
            _enumerator.RegisterFrn(args.DriveLetter, args.NewFrn,
                args.NewRecord.OriginalName, args.NewRecord.ParentFrn, args.NewRecord.IsDirectory);
            _index.Rename(args.OldLowerName, args.OldParentFrn, args.DriveLetter, args.NewRecord);
            var newFullPath = _enumerator.ResolveFullPath(
                args.DriveLetter, args.NewRecord.ParentFrn, args.NewRecord.OriginalName);
            ScheduleSnapshotSave();
            IndexChanged?.Invoke(this, new IndexChangedEventArgs(
                IndexChangeType.Renamed, args.OldLowerName, newFullPath,
                oldFullPath,
                args.NewRecord.OriginalName, args.NewRecord.LowerName));
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
            string prefix,
            FileRecord[] sortedArray,
            int offset,
            int maxResults,
            CancellationToken ct)
        {
            var page = new List<FileRecord>(Math.Min(maxResults, 64));
            if (sortedArray.Length == 0 || string.IsNullOrEmpty(prefix))
                return (page, 0);

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
            string suffix,
            FileRecord[] sortedArray,
            int offset,
            int maxResults,
            CancellationToken ct)
        {
            var page = new List<FileRecord>(Math.Min(maxResults, 64));
            var total = 0;
            var i = 0;
            foreach (var record in sortedArray)
            {
                if ((++i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                if (record.LowerName.EndsWith(suffix, StringComparison.Ordinal))
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
            var total = 0;
            var i = 0;
            foreach (var record in sortedArray)
            {
                if ((++i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                try
                {
                    if (regex.IsMatch(record.LowerName))
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

        /// <summary>
        /// 包含匹配：先查 ExactHashMap（精确命中），再对 SortedArray 执行二分定位后线性扫描。
        /// 需求 3.1、3.3、7.1
        /// </summary>
        private static (List<FileRecord> page, int total) ContainsMatch(
            string query,
            Dictionary<string, List<FileRecord>> exactHashMap,
            FileRecord[] sortedArray,
            int offset,
            int maxResults,
            CancellationToken ct = default)
        {
            var page = new List<FileRecord>(Math.Min(maxResults, 64));
            var total = 0;

            if (exactHashMap.TryGetValue(query, out var exactBucket))
            {
                foreach (var r in exactBucket)
                {
                    total++;
                    if (total > offset && page.Count < maxResults)
                        page.Add(r);
                }
            }

            var i = 0;
            foreach (var record in sortedArray)
            {
                if ((++i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                if (record.LowerName == query) continue;
                if (record.LowerName.Contains(query))
                {
                    total++;
                    if (total > offset && page.Count < maxResults)
                        page.Add(record);
                }
            }

            return (page, total);
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
            return Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(keyword))
                    return new SearchQueryResult
                    {
                        TotalIndexedCount = _index.TotalCount,
                        TotalMatchedCount = 0,
                        IsTruncated = false,
                        Results = new List<ScannedFileInfo>()
                    };

                var idx = _index;

                // 需求 10.1：路径范围解析，在 DetectMatchMode 之前执行
                var (pathPrefix, searchTerm) = ParsePathScope(keyword);

                // 若 searchTerm 为空（用户只输入了路径前缀），直接返回空结果
                if (string.IsNullOrWhiteSpace(searchTerm))
                    return new SearchQueryResult
                    {
                        TotalIndexedCount = idx.TotalCount,
                        TotalMatchedCount = 0,
                        IsTruncated = false,
                        Results = new List<ScannedFileInfo>()
                    };

                var (mode, normalizedQuery) = DetectMatchMode(searchTerm);

                var normalizedOffset = Math.Max(offset, 0);
                var normalizedMaxResults = Math.Max(maxResults, 0);
                var fetchLimit = pathPrefix != null ? int.MaxValue : normalizedMaxResults;
                var fetchOffset = pathPrefix != null ? 0 : normalizedOffset;

                List<FileRecord> matched;
                int totalMatched;
                switch (mode)
                {
                    case MatchMode.Prefix:
                        { var r = PrefixMatch(normalizedQuery, idx.SortedArray, fetchOffset, fetchLimit, ct); matched = r.page; totalMatched = r.total; }
                        break;
                    case MatchMode.Suffix:
                        { var r = SuffixMatch(normalizedQuery, idx.SortedArray, fetchOffset, fetchLimit, ct); matched = r.page; totalMatched = r.total; }
                        break;
                    case MatchMode.Regex:
                        { var r = RegexMatch(normalizedQuery, idx.SortedArray, fetchOffset, fetchLimit, progress, ct); matched = r.page; totalMatched = r.total; }
                        break;
                    case MatchMode.Wildcard:
                        { var r = RegexMatch(WildcardToRegex(normalizedQuery), idx.SortedArray, fetchOffset, fetchLimit, progress, ct); matched = r.page; totalMatched = r.total; }
                        break;
                    default:
                        { var r = ContainsMatch(normalizedQuery, idx.ExactHashMap, idx.SortedArray, fetchOffset, fetchLimit, ct); matched = r.page; totalMatched = r.total; }
                        break;
                }

                ct.ThrowIfCancellationRequested();

                // 按需解析完整路径：用 _enumerator 的 FRN 字典（每卷独立缓存）
                var results = new List<ScannedFileInfo>(Math.Min(matched.Count, normalizedMaxResults));

                // 需求 10.2、10.3：路径前缀后置过滤（大小写不敏感）
                // 确保路径前缀以 \ 结尾，避免误匹配同名前缀目录（如 C:\Users\Desktop2）
                if (pathPrefix != null)
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
                    foreach (var record in matched)
                    {
                        ct.ThrowIfCancellationRequested();
                        var fullPath = _enumerator.ResolveFullPath(record.DriveLetter, record.ParentFrn, record.OriginalName);
                        results.Add(new ScannedFileInfo
                        {
                            FullPath = fullPath ?? (record.DriveLetter + ":\\" + record.OriginalName),
                            FileName = record.OriginalName,
                            SizeBytes = 0,
                            ModifiedTimeUtc = DateTime.MinValue,
                            RootPath = string.Empty,
                            RootDisplayName = string.Empty,
                            IsDirectory = record.IsDirectory
                        });
                    }
                }

                return new SearchQueryResult
                {
                    TotalIndexedCount = idx.TotalCount,
                    TotalMatchedCount = totalMatched,
                    IsTruncated       = totalMatched > normalizedOffset + results.Count,
                    Results           = results
                };
            }, ct);
        }

        // ── 私有实现 ────────────────────────────────────────────────────────────

        private int BuildIndex(IProgress<string> progress, CancellationToken ct)
        {
            CancelPendingSnapshotSave();
            _usnWatcher.StopWatching();

            if (TryRestoreFromSnapshot(progress, ct, out var restoredCount))
                return restoredCount;

            return BuildIndexFromMft(progress, ct);
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
            _index.LoadSortedRecords(snapshot.Records);
            restoreStopwatch.Stop();
            UsnDiagLog.Write($"[SNAPSHOT RESTORE] elapsedMs={restoreStopwatch.ElapsedMilliseconds} records={snapshot.Records.Length} volumes={snapshot.Volumes.Length}");

            var checkpoints = new (char driveLetter, long nextUsn, ulong journalId)[snapshot.Volumes.Length];
            progress?.Report("正在追平 USN 变更...");
            var catchUpStopwatch = Stopwatch.StartNew();
            for (var i = 0; i < snapshot.Volumes.Length; i++)
            {
                ct.ThrowIfCancellationRequested();

                var volume = snapshot.Volumes[i];
                var volumeCatchUpStopwatch = Stopwatch.StartNew();
                if (!_usnWatcher.TryCatchUp(volume.DriveLetter, volume.NextUsn, volume.JournalId, ct,
                        out var nextUsn, out var latestJournalId))
                {
                    volumeCatchUpStopwatch.Stop();
                    catchUpStopwatch.Stop();
                    UsnDiagLog.Write(
                        $"[SNAPSHOT CATCHUP] drive={volume.DriveLetter} expired elapsedMs={volumeCatchUpStopwatch.ElapsedMilliseconds} " +
                        $"startUsn={volume.NextUsn} journalId={volume.JournalId}");
                    progress?.Report("索引快照已过期，正在重建索引...");
                    _index.Build(Array.Empty<FileRecord>());
                    return false;
                }
                volumeCatchUpStopwatch.Stop();
                UsnDiagLog.Write(
                    $"[SNAPSHOT CATCHUP] drive={volume.DriveLetter} elapsedMs={volumeCatchUpStopwatch.ElapsedMilliseconds} " +
                    $"startUsn={volume.NextUsn} nextUsn={nextUsn} journalId={latestJournalId}");

                checkpoints[i] = (volume.DriveLetter, nextUsn, latestJournalId);
            }
            catchUpStopwatch.Stop();

            var watcherStartStopwatch = Stopwatch.StartNew();
            var volumeSnapshots = _enumerator.CreateVolumeSnapshots(checkpoints);

            for (var i = 0; i < checkpoints.Length; i++)
            {
                var checkpoint = checkpoints[i];
                _usnWatcher.StartWatching(checkpoint.driveLetter, checkpoint.nextUsn, checkpoint.journalId, ct);
            }
            watcherStartStopwatch.Stop();

            restoredCount = _index.TotalCount;
            totalStopwatch.Stop();
            progress?.Report($"已从快照恢复 {restoredCount} 个对象");
            UsnDiagLog.Write(
                $"[SNAPSHOT RESTORE TOTAL] totalMs={totalStopwatch.ElapsedMilliseconds} loadMs={snapshotLoadStopwatch.ElapsedMilliseconds} " +
                $"restoreMs={restoreStopwatch.ElapsedMilliseconds} catchUpMs={catchUpStopwatch.ElapsedMilliseconds} " +
                $"watcherStartMs={watcherStartStopwatch.ElapsedMilliseconds} restoredCount={restoredCount}");

            QueueSnapshotSave(_index.SortedArray, volumeSnapshots);
            return true;
        }

        private int BuildIndexFromMft(IProgress<string> progress, CancellationToken ct)
        {
            var fixedDrives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed)
                .Select(d => d.Name[0])
                .ToArray();

            if (fixedDrives.Length == 0) return 0;

            // 多卷并行枚举
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
                try
                {
                    var enumerator = new MftEnumerator(); // 每线程独立实例
                    var (count, nextUsn, journalId) = enumerator.EnumerateVolumeIntoRecords(dl, buf, ct);
                    // 把 frnMap 合并到主 enumerator
                    _enumerator.MergeFrnMap(dl, enumerator);
                    perVolume[i] = (buf, nextUsn, journalId, dl);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { exceptions.Add((dl, ex)); }
            });

            foreach (var (dl, ex) in exceptions)
                progress?.Report($"跳过卷 {dl}:：{ex.Message}");

            ct.ThrowIfCancellationRequested();

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

            _index.Build(allRecords);
            progress?.Report($"已索引 {_index.TotalCount} 个对象");

            var volumeSnapshots = _enumerator.CreateVolumeSnapshots(successfulDrives.ToArray());

            foreach (var (letter, nextUsn, journalId) in successfulDrives)
                _usnWatcher.StartWatching(letter, nextUsn, journalId, ct);

            QueueSnapshotSave(allRecords, volumeSnapshots);

            return _index.TotalCount;
        }

        private void QueueSnapshotSave(FileRecord[] records, VolumeSnapshot[] volumeSnapshots)
        {
            if (records == null || volumeSnapshots == null || volumeSnapshots.Length == 0)
                return;

            var recordCopy = new FileRecord[records.Length];
            Array.Copy(records, recordCopy, records.Length);
            var volumeCopy = new VolumeSnapshot[volumeSnapshots.Length];
            Array.Copy(volumeSnapshots, volumeCopy, volumeSnapshots.Length);

            Task.Run(() =>
            {
                try
                {
                    var saveStopwatch = Stopwatch.StartNew();
                    var metrics = _snapshotStore.Save(new IndexSnapshot(recordCopy, volumeCopy));
                    saveStopwatch.Stop();
                    if (metrics != null)
                    {
                        UsnDiagLog.Write(
                            $"[SNAPSHOT SAVE] elapsedMs={saveStopwatch.ElapsedMilliseconds} version={metrics.Version} " +
                            $"fileBytes={metrics.FileBytes} records={metrics.RecordCount} volumes={metrics.VolumeCount} frnEntries={metrics.FrnEntryCount}");
                    }
                }
                catch
                {
                }
            });
        }

        private void ScheduleSnapshotSave()
        {
            CancellationTokenSource cts;
            lock (_snapshotSaveLock)
            {
                var old = _snapshotSaveCts;
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
                    await Task.Delay(SnapshotSaveDebounceMilliseconds, cts.Token).ConfigureAwait(false);
                    SaveCurrentSnapshot("debounce");
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        private void CancelPendingSnapshotSave()
        {
            lock (_snapshotSaveLock)
            {
                var cts = _snapshotSaveCts;
                _snapshotSaveCts = null;
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
                var checkpoints = _usnWatcher.GetVolumeCheckpoints();
                if (checkpoints == null || checkpoints.Length == 0)
                    return;

                var records = _index.SortedArray;
                if (records == null || records.Length == 0)
                    return;

                var recordCopy = new FileRecord[records.Length];
                Array.Copy(records, recordCopy, records.Length);
                var volumeSnapshots = _enumerator.CreateVolumeSnapshots(checkpoints);

                var saveStopwatch = Stopwatch.StartNew();
                var metrics = _snapshotStore.Save(new IndexSnapshot(recordCopy, volumeSnapshots));
                saveStopwatch.Stop();

                if (metrics != null)
                {
                    UsnDiagLog.Write(
                        $"[SNAPSHOT SAVE LIVE] reason={reason} elapsedMs={saveStopwatch.ElapsedMilliseconds} version={metrics.Version} " +
                        $"fileBytes={metrics.FileBytes} records={metrics.RecordCount} volumes={metrics.VolumeCount} frnEntries={metrics.FrnEntryCount}");
                }
            }
            catch
            {
            }
        }

    }

    public enum IndexChangeType { Created, Deleted, Renamed }

    public sealed class IndexChangedEventArgs : EventArgs
    {
        public IndexChangedEventArgs(IndexChangeType type, string lowerName, string fullPath,
            string oldFullPath = null, string newOriginalName = null, string newLowerName = null)
        {
            Type            = type;
            LowerName       = lowerName;
            FullPath        = fullPath;
            OldFullPath     = oldFullPath;
            NewOriginalName = newOriginalName;
            NewLowerName    = newLowerName;
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
    }
}

    /// <summary>轻量诊断日志，写到桌面 usn_diag.log，用完后删除。</summary>
    internal static class UsnDiagLog
    {
        private static readonly string _path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "usn_diag.log");
        private static readonly object _lock = new object();

        public static void Write(string msg)
        {
            try
            {
                lock (_lock)
                    System.IO.File.AppendAllText(_path,
                        $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
            catch { }
        }
    }
