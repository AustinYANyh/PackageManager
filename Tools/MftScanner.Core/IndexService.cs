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
    public sealed class IndexService : ISharedIndexService
    {
        private readonly MftEnumerator _enumerator = new MftEnumerator();
        private readonly IndexSnapshotStore _snapshotStore = new IndexSnapshotStore();
        private readonly UsnWatcher _usnWatcher = new UsnWatcher();
        private volatile MemoryIndex _index = new MemoryIndex();
        private readonly object _snapshotSaveLock = new object();
        private readonly object _snapshotWriteLock = new object();
        private readonly object _backgroundCatchUpLock = new object();
        private readonly object _containsWarmupLock = new object();
        private bool _watcherInitialized;
        private CancellationTokenSource _snapshotSaveCts;
        private DateTime _pendingSnapshotSaveStartedUtc = DateTime.MinValue;
        private CancellationTokenSource _backgroundCatchUpCts;
        private Task _backgroundCatchUpTask;
        private CancellationTokenSource _containsWarmupCts;
        private Task _containsWarmupTask;
        private volatile bool _isBackgroundCatchUpInProgress;
        private volatile string _currentStatusMessage = string.Empty;
        private readonly object _periodicSnapshotSaveLock = new object();
        private CancellationTokenSource _periodicSnapshotSaveCts;
        private Task _periodicSnapshotSaveTask;

        private const int SnapshotSaveDebounceMilliseconds = 5000;
        private const int SnapshotPeriodicSaveMilliseconds = 120000;
        private const int SnapshotSaveMaxDeferredMilliseconds = 30000;

        // 保存 progress 引用，供 OnJournalOverflow 使用（需求 6.5）
        private IProgress<string> _progress;

        /// <summary>当前内存索引实例（供 SearchAsync 使用）。</summary>
        public MemoryIndex Index => _index;
        public int IndexedCount => _index.TotalCount;
        public bool IsBackgroundCatchUpInProgress => _isBackgroundCatchUpInProgress;
        public string CurrentStatusMessage => _currentStatusMessage;

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
                    _usnWatcher.StopWatching();
                    _index.Build(Array.Empty<FileRecord>());
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
                isDirectory:  args.IsDirectory,
                frn:          args.Frn);
            _index.Insert(record);
            var fullPath = _enumerator.ResolveFullPath(args.DriveLetter, args.ParentFrn, args.FileName);
            ScheduleSnapshotSave();
            IndexChanged?.Invoke(this, new IndexChangedEventArgs(
                IndexChangeType.Created, lowerName, fullPath, isDirectory: args.IsDirectory));
        }

        /// <summary>文件删除：从索引中移除记录。需求 6.3</summary>
        private void OnFileDeleted(object sender, UsnFileDeletedEventArgs args)
        {
            // 删除前先解析路径（FRN 字典此时还未清除），供 UI 精确匹配
            var fullPath = _enumerator.ResolveFullPath(args.DriveLetter, args.ParentFrn, args.LowerName);
            _index.Remove(args.Frn, args.LowerName, args.ParentFrn, args.DriveLetter);
            ScheduleSnapshotSave();
            IndexChanged?.Invoke(this, new IndexChangedEventArgs(
                IndexChangeType.Deleted, args.LowerName, fullPath, isDirectory: false));
        }

        /// <summary>文件重命名：更新 FRN 字典，先移除旧记录再插入新记录。需求 6.4</summary>
        private void OnFileRenamed(object sender, UsnFileRenamedEventArgs args)
        {
            var oldFullPath = _enumerator.ResolveFullPath(
                args.DriveLetter, args.OldParentFrn, args.OldLowerName);
            _enumerator.RegisterFrn(args.DriveLetter, args.NewFrn,
                args.NewRecord.OriginalName, args.NewRecord.ParentFrn, args.NewRecord.IsDirectory);
            _index.Rename(args.NewFrn, args.OldLowerName, args.OldParentFrn, args.DriveLetter, args.NewRecord);
            var newFullPath = _enumerator.ResolveFullPath(
                args.DriveLetter, args.NewRecord.ParentFrn, args.NewRecord.OriginalName);
            ScheduleSnapshotSave();
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

            var total = bucket.Count;
            for (var i = Math.Max(offset, 0); i < bucket.Count && page.Count < maxResults; i++)
            {
                if (((i + 1) & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                page.Add(bucket[i]);
            }

            return (page, total);
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
            var stopwatch = Stopwatch.StartNew();
            var page = new List<FileRecord>(Math.Min(maxResults, 64));
            var total = 0;
            var hasExactBucket = false;
            var candidateCount = sortedArray?.Length ?? 0;
            IndexPerfLog.Write("INDEX",
                $"[CONTAINS FALLBACK] outcome=start candidateCount={candidateCount} offset={offset} maxResults={maxResults} query={IndexPerfLog.FormatValue(query)}");

            if (exactHashMap != null && exactHashMap.TryGetValue(query, out var exactBucket))
            {
                hasExactBucket = true;
                foreach (var r in exactBucket)
                {
                    total++;
                    if (total > offset && page.Count < maxResults)
                        page.Add(r);
                }
            }

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

                    if (hasExactBucket && record.LowerName == query) continue;
                    if (record.LowerName.Contains(query))
                    {
                        total++;
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
            return (page, total);
        }

        private static FileRecord[] GetCandidateSource(MemoryIndex index, SearchTypeFilter filter)
        {
            switch (filter)
            {
                case SearchTypeFilter.Folder:
                    return index.DirectorySortedArray;
                case SearchTypeFilter.Launchable:
                    return index.LaunchableSortedArray;
                case SearchTypeFilter.Script:
                    return index.ScriptSortedArray;
                case SearchTypeFilter.Log:
                    return index.LogSortedArray;
                case SearchTypeFilter.Config:
                    return index.ConfigSortedArray;
                default:
                    return index.SortedArray;
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
                var mode = MatchMode.Contains;
                var candidateCount = 0;
                var containsCandidateCount = 0;

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
                    var fetchLimit = pathPrefix != null ? int.MaxValue : normalizedMaxResults;
                    var fetchOffset = pathPrefix != null ? 0 : normalizedOffset;
                    var candidateSource = GetCandidateSource(idx, filter);
                    candidateCount = candidateSource?.Length ?? 0;

                    if (candidateSource == null || candidateSource.Length == 0)
                    {
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
                    switch (mode)
                    {
                        case MatchMode.Prefix:
                            { var r = PrefixMatch(normalizedQuery, candidateSource, fetchOffset, fetchLimit, ct); matched = r.page; totalMatched = r.total; }
                            break;
                        case MatchMode.Suffix:
                            { var r = SuffixMatch(normalizedQuery, candidateSource, fetchOffset, fetchLimit, ct); matched = r.page; totalMatched = r.total; }
                            break;
                        case MatchMode.Regex:
                            { var r = RegexMatch(normalizedQuery, candidateSource, fetchOffset, fetchLimit, progress, ct); matched = r.page; totalMatched = r.total; }
                            break;
                        case MatchMode.Wildcard:
                            if (filter == SearchTypeFilter.All && TryGetSimpleExtensionWildcard(normalizedQuery, out var extension))
                            {
                                var r = ExtensionMatch(extension, idx.ExtensionHashMap, fetchOffset, fetchLimit, ct);
                                matched = r.page;
                                totalMatched = r.total;
                            }
                            else if (TrySimplifyWildcard(normalizedQuery, out var simplifiedMode, out var simplifiedQuery))
                            {
                                switch (simplifiedMode)
                                {
                                    case MatchMode.Prefix:
                                        { var r = PrefixMatch(simplifiedQuery, candidateSource, fetchOffset, fetchLimit, ct); matched = r.page; totalMatched = r.total; }
                                        break;
                                    case MatchMode.Suffix:
                                        { var r = SuffixMatch(simplifiedQuery, candidateSource, fetchOffset, fetchLimit, ct); matched = r.page; totalMatched = r.total; }
                                        break;
                                    default:
                                        if (TryContainsAccelerated(idx, simplifiedQuery, filter, fetchOffset, fetchLimit, ct, out var wildcardContains))
                                        {
                                            matched = wildcardContains.Page;
                                            totalMatched = wildcardContains.Total;
                                            containsMode = wildcardContains.Mode;
                                            containsCandidateCount = wildcardContains.CandidateCount;
                                            containsIntersectMilliseconds = wildcardContains.IntersectMs;
                                            containsVerifyMilliseconds = wildcardContains.VerifyMs;
                                        }
                                        else
                                        {
                                            containsMode = "fallback";
                                            containsCandidateCount = candidateSource?.Length ?? 0;
                                            var r = ContainsMatch(simplifiedQuery, filter == SearchTypeFilter.All ? idx.ExactHashMap : null, candidateSource, fetchOffset, fetchLimit, ct);
                                            matched = r.page;
                                            totalMatched = r.total;
                                        }
                                        break;
                                }
                            }
                            else
                            {
                                var r = RegexMatch(WildcardToRegex(normalizedQuery), candidateSource, fetchOffset, fetchLimit, progress, ct);
                                matched = r.page;
                                totalMatched = r.total;
                            }
                            break;
                        default:
                            if (TryContainsAccelerated(idx, normalizedQuery, filter, fetchOffset, fetchLimit, ct, out var containsResult))
                            {
                                matched = containsResult.Page;
                                totalMatched = containsResult.Total;
                                containsMode = containsResult.Mode;
                                containsCandidateCount = containsResult.CandidateCount;
                                containsIntersectMilliseconds = containsResult.IntersectMs;
                                containsVerifyMilliseconds = containsResult.VerifyMs;
                            }
                            else
                            {
                                containsMode = "fallback";
                                containsCandidateCount = candidateSource?.Length ?? 0;
                                var r = ContainsMatch(normalizedQuery, filter == SearchTypeFilter.All ? idx.ExactHashMap : null, candidateSource, fetchOffset, fetchLimit, ct);
                                matched = r.page;
                                totalMatched = r.total;
                            }
                            break;
                    }
                    matchStopwatch.Stop();
                    matchElapsedMilliseconds = matchStopwatch.ElapsedMilliseconds;

                    ct.ThrowIfCancellationRequested();

                    var resolveStopwatch = Stopwatch.StartNew();

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
                            var fullPath = _enumerator.ResolveFullPath(record.DriveLetter, record.ParentFrn, record.OriginalName)
                                           ?? (record.DriveLetter + ":\\" + record.OriginalName);
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
                            IsTruncated = totalMatched > normalizedOffset + results.Count,
                            Results = results
                        });
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
            }, ct);
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

        private static SearchQueryResult CreateEmptySearchResult(int totalIndexedCount)
        {
            return new SearchQueryResult
            {
                TotalIndexedCount = totalIndexedCount,
                TotalMatchedCount = 0,
                IsTruncated = false,
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
            SearchQueryResult result)
        {
            totalStopwatch.Stop();
            if (result != null)
            {
                result.HostSearchMs = totalStopwatch.ElapsedMilliseconds;
            }
            UsnDiagLog.Write(
                $"[SEARCH] outcome={outcome} totalMs={totalStopwatch.ElapsedMilliseconds} matchMs={matchElapsedMilliseconds} " +
                $"resolveMs={resolveElapsedMilliseconds} filter={filter} mode={mode} offset={offset} maxResults={maxResults} " +
                $"candidateCount={candidateCount} matched={result.TotalMatchedCount} returned={result.Results?.Count ?? 0} " +
                $"truncated={result.IsTruncated} indexed={result.TotalIndexedCount} pathScoped={pathPrefix != null} " +
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
                $"candidateCount={candidateCount} pathScoped={pathPrefix != null} keyword={IndexPerfLog.FormatValue(keyword)} " +
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
            _index.LoadSortedRecords(snapshot.Records, buildContainsAccelerator: false);
            restoreStopwatch.Stop();
            UsnDiagLog.Write($"[SNAPSHOT RESTORE] elapsedMs={restoreStopwatch.ElapsedMilliseconds} records={snapshot.Records.Length} volumes={snapshot.Volumes.Length}");

            restoredCount = _index.TotalCount;
            totalStopwatch.Stop();
            progress?.Report($"已从快照恢复 {restoredCount} 个对象，可立即搜索");
            UsnDiagLog.Write(
                $"[SNAPSHOT RESTORE TOTAL] totalMs={totalStopwatch.ElapsedMilliseconds} loadMs={snapshotLoadStopwatch.ElapsedMilliseconds} " +
                $"restoreMs={restoreStopwatch.ElapsedMilliseconds} restoredCount={restoredCount}");

            StartBackgroundCatchUp(snapshot.Volumes, progress, ct);
            return true;
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
                        // 把 frnMap 合并到主 enumerator
                        _enumerator.MergeFrnMap(dl, enumerator);
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

                var buildStopwatch = Stopwatch.StartNew();
                _index.Build(allRecords, buildContainsAccelerator: false);
                buildStopwatch.Stop();
                progress?.Report($"已索引 {_index.TotalCount} 个对象");

                var watcherStartStopwatch = Stopwatch.StartNew();
                var volumeSnapshots = _enumerator.CreateVolumeSnapshots(successfulDrives.ToArray());

                foreach (var (letter, nextUsn, journalId) in successfulDrives)
                    _usnWatcher.StartWatching(letter, nextUsn, journalId, ct);

                watcherStartStopwatch.Stop();
                QueueContainsAcceleratorWarmup("post-full-build");
                QueueSnapshotSave(allRecords, volumeSnapshots);
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

        private void StartBackgroundCatchUp(IReadOnlyList<VolumeSnapshot> volumes, IProgress<string> progress, CancellationToken ct)
        {
            CancelBackgroundCatchUp();

            if (volumes == null || volumes.Count == 0)
            {
                PublishIndexStatus($"已索引 {_index.TotalCount} 个对象", false);
                QueueContainsAcceleratorWarmup("snapshot-no-catchup");
                return;
            }

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_backgroundCatchUpLock)
            {
                _backgroundCatchUpCts = linkedCts;
                _backgroundCatchUpTask = Task.Run(() => RunBackgroundCatchUp(volumes, progress, linkedCts), linkedCts.Token);
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
            public bool IsExpired { get; set; }
        }

        private void RunBackgroundCatchUp(IReadOnlyList<VolumeSnapshot> volumes, IProgress<string> progress, CancellationTokenSource backgroundCatchUpCts)
        {
            var ct = backgroundCatchUpCts.Token;
            try
            {
                PublishIndexStatus(
                    $"已从快照恢复 {_index.TotalCount} 个对象，可立即搜索；后台正在追平 USN...",
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
                        StartJournalId = volume.JournalId
                    };

                    if (_usnWatcher.TryCollectCatchUpChanges(
                            volume.DriveLetter,
                            volume.NextUsn,
                            volume.JournalId,
                            ct,
                            out var changes,
                            out var nextUsn,
                            out var latestJournalId))
                    {
                        result.Changes = changes;
                        result.NextUsn = nextUsn;
                        result.LatestJournalId = latestJournalId;
                    }
                    else
                    {
                        result.IsExpired = true;
                    }

                    volumeCatchUpStopwatch.Stop();
                    result.ElapsedMs = volumeCatchUpStopwatch.ElapsedMilliseconds;
                    volumeResults[i] = result;
                });

                for (var i = 0; i < volumeResults.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var result = volumeResults[i];
                    if (result.IsExpired)
                    {
                        catchUpStopwatch.Stop();
                        UsnDiagLog.Write(
                            $"[SNAPSHOT CATCHUP] drive={result.DriveLetter} expired elapsedMs={result.ElapsedMs} " +
                            $"startUsn={result.StartUsn} journalId={result.StartJournalId}");

                        PublishIndexStatus("索引快照已过期，后台正在重建索引...", true);
                        var rebuiltCount = BuildIndexFromMft(progress, ct);
                        PublishIndexStatus($"已重建 {rebuiltCount} 个对象", false, requireSearchRefresh: true);
                        return;
                    }

                    UsnDiagLog.Write(
                        $"[SNAPSHOT CATCHUP] drive={result.DriveLetter} elapsedMs={result.ElapsedMs} " +
                        $"startUsn={result.StartUsn} nextUsn={result.NextUsn} journalId={result.LatestJournalId} changes={result.Changes?.Count ?? 0}");
                }

                var applyStopwatch = Stopwatch.StartNew();
                var totalChangeCount = 0;
                for (var i = 0; i < volumeResults.Length; i++)
                {
                    totalChangeCount += volumeResults[i].Changes?.Count ?? 0;
                }

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
                    _enumerator.ApplyUsnChanges(allChanges);
                    _index.ApplyBatch(allChanges, rebuildContainsAccelerator: false);
                    PublishBatchIndexChanges(indexChangedArgs);
                }

                applyStopwatch.Stop();
                catchUpStopwatch.Stop();

                var watcherStartStopwatch = Stopwatch.StartNew();
                var checkpoints = new (char driveLetter, long nextUsn, ulong journalId)[volumeResults.Length];
                for (var i = 0; i < volumeResults.Length; i++)
                {
                    checkpoints[i] = (volumeResults[i].DriveLetter, volumeResults[i].NextUsn, volumeResults[i].LatestJournalId);
                }

                var volumeSnapshots = _enumerator.CreateVolumeSnapshots(checkpoints);

                for (var i = 0; i < checkpoints.Length; i++)
                {
                    var checkpoint = checkpoints[i];
                    _usnWatcher.StartWatching(checkpoint.driveLetter, checkpoint.nextUsn, checkpoint.journalId, ct);
                }

                watcherStartStopwatch.Stop();
                QueueSnapshotSave(_index.SortedArray, volumeSnapshots);
                QueueContainsAcceleratorWarmup("post-catchup");

                UsnDiagLog.Write(
                    $"[SNAPSHOT CATCHUP TOTAL] catchUpMs={catchUpStopwatch.ElapsedMilliseconds} " +
                    $"applyMs={applyStopwatch.ElapsedMilliseconds} totalChanges={totalChangeCount} " +
                    $"watcherStartMs={watcherStartStopwatch.ElapsedMilliseconds} indexedCount={_index.TotalCount}");

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
            var index = _index;
            if (index == null || index.TotalCount == 0 || index.HasContainsAccelerator)
            {
                return;
            }

            CancellationTokenSource cts;
            lock (_containsWarmupLock)
            {
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
                _containsWarmupTask = Task.Run(() => RunContainsAcceleratorWarmup(index, reason, cts), cts.Token);
            }
        }

        private void RunContainsAcceleratorWarmup(MemoryIndex index, string reason, CancellationTokenSource warmupCts)
        {
            var ct = warmupCts.Token;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                if (index == null || index.TotalCount == 0 || index.HasContainsAccelerator)
                {
                    return;
                }

                UsnDiagLog.Write(
                    $"[CONTAINS WARMUP] outcome=start reason={IndexPerfLog.FormatValue(reason)} " +
                    $"records={index.TotalCount}");
                index.TryEnsureContainsAccelerator(MemoryIndex.ContainsWarmupScope.TrigramOnly, ct);
                UsnDiagLog.Write(
                    $"[CONTAINS WARMUP] outcome=stage reason={IndexPerfLog.FormatValue(reason)} " +
                    $"stage=trigram-ready elapsedMs={stopwatch.ElapsedMilliseconds} records={index.TotalCount}");
                index.TryEnsureContainsAccelerator(MemoryIndex.ContainsWarmupScope.Full, ct);
                stopwatch.Stop();
                UsnDiagLog.Write(
                    $"[CONTAINS WARMUP] outcome=success reason={IndexPerfLog.FormatValue(reason)} " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds} records={index.TotalCount}");
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

        private void CancelContainsWarmup()
        {
            CancellationTokenSource cts;
            lock (_containsWarmupLock)
            {
                cts = _containsWarmupCts;
                _containsWarmupCts = null;
                _containsWarmupTask = null;
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
                requireSearchRefresh));
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
                    IndexSnapshotSaveMetrics metrics;
                    long elapsedMilliseconds;
                    lock (_snapshotWriteLock)
                    {
                        var saveStopwatch = Stopwatch.StartNew();
                        metrics = _snapshotStore.Save(new IndexSnapshot(recordCopy, volumeCopy));
                        saveStopwatch.Stop();
                        elapsedMilliseconds = saveStopwatch.ElapsedMilliseconds;
                    }

                    if (metrics != null)
                    {
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
                var totalStopwatch = Stopwatch.StartNew();
                var checkpoints = _usnWatcher.GetVolumeCheckpoints();
                if (checkpoints == null || checkpoints.Length == 0)
                    return;

                var records = _index.SortedArray;
                if (records == null || records.Length == 0)
                    return;

                var prepStopwatch = Stopwatch.StartNew();
                var recordCopy = new FileRecord[records.Length];
                Array.Copy(records, recordCopy, records.Length);
                var recordCopyMilliseconds = prepStopwatch.ElapsedMilliseconds;

                prepStopwatch.Restart();
                var volumeSnapshots = _enumerator.CreateVolumeSnapshots(checkpoints);
                var volumeSnapshotMilliseconds = prepStopwatch.ElapsedMilliseconds;

                IndexSnapshotSaveMetrics metrics;
                long elapsedMilliseconds;
                lock (_snapshotWriteLock)
                {
                    var saveStopwatch = Stopwatch.StartNew();
                    metrics = _snapshotStore.Save(new IndexSnapshot(recordCopy, volumeSnapshots));
                    saveStopwatch.Stop();
                    elapsedMilliseconds = saveStopwatch.ElapsedMilliseconds;
                }

                if (metrics != null)
                {
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

    }

    public enum IndexChangeType { Created, Deleted, Renamed }

    public sealed class IndexStatusChangedEventArgs : EventArgs
    {
        public IndexStatusChangedEventArgs(string message, int indexedCount, bool isBackgroundCatchUpInProgress, bool requireSearchRefresh)
        {
            Message = message;
            IndexedCount = indexedCount;
            IsBackgroundCatchUpInProgress = isBackgroundCatchUpInProgress;
            RequireSearchRefresh = requireSearchRefresh;
        }

        public string Message { get; }
        public int IndexedCount { get; }
        public bool IsBackgroundCatchUpInProgress { get; }
        public bool RequireSearchRefresh { get; }
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
