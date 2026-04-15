using System;
using System.Collections.Generic;
using System.IO;
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
        private readonly UsnWatcher _usnWatcher = new UsnWatcher();
        private volatile MemoryIndex _index = new MemoryIndex();

        // 保存 progress 引用，供 OnJournalOverflow 使用（需求 6.5）
        private IProgress<string> _progress;

        /// <summary>当前内存索引实例（供 SearchAsync 使用）。</summary>
        public MemoryIndex Index => _index;

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
                // 先清空旧索引释放内存，再构建新索引，避免两份数据同时存在
                _index.Build(Array.Empty<FileRecord>());

                var allRecords = new List<FileRecord>(500_000);
                var successfulDrives = new List<(char letter, long nextUsn, ulong journalId)>();

                foreach (var drive in DriveInfo.GetDrives())
                {
                    ct.ThrowIfCancellationRequested();
                    if (drive.DriveType != DriveType.Fixed) continue;
                    var driveLetter = drive.Name[0];
                    try
                    {
                        var (entries, nextUsn, journalId) = _enumerator.EnumerateVolumeWithUsn(driveLetter, ct);
                        BuildVolumeRecords(driveLetter, entries, allRecords);
                        successfulDrives.Add((driveLetter, nextUsn, journalId));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { _progress?.Report($"跳过卷 {driveLetter}:：{ex.Message}"); }
                }

                ct.ThrowIfCancellationRequested();
                _index.Build(allRecords);

                _usnWatcher.StopWatching();
                foreach (var (letter, nextUsn, journalId) in successfulDrives)
                    _usnWatcher.StartWatching(letter, nextUsn, journalId, ct);

                _progress?.Report($"已索引 {_index.TotalCount} 个对象");
                return _index.TotalCount;
            }, ct);
        }

        // ── UsnWatcher 集成 ──────────────────────────────────────────────────────

        /// <summary>
        /// 订阅 UsnWatcher 事件。需求 6.1–6.5
        /// </summary>
        private void SetupUsnWatcher()
        {
            _usnWatcher.FileCreated    += OnFileCreated;
            _usnWatcher.FileDeleted    += OnFileDeleted;
            _usnWatcher.FileRenamed    += OnFileRenamed;
            _usnWatcher.JournalOverflow += OnJournalOverflow;
        }

        /// <summary>文件创建：插入新 FileRecord。需求 6.2</summary>
        private void OnFileCreated(object sender, UsnFileCreatedEventArgs args)
        {
            var record = new FileRecord(
                lowerName:    args.FileName.ToLowerInvariant(),
                originalName: args.FileName,
                parentFrn:    args.ParentFrn,
                driveLetter:  args.DriveLetter,
                isDirectory:  args.IsDirectory);
            _index.Insert(record);
        }

        /// <summary>文件删除：从索引中移除记录。需求 6.3</summary>
        private void OnFileDeleted(object sender, UsnFileDeletedEventArgs args)
        {
            _index.Remove(args.LowerName, args.ParentFrn, args.DriveLetter);
        }

        /// <summary>文件重命名：先移除旧记录，再插入新记录。需求 6.4</summary>
        private void OnFileRenamed(object sender, UsnFileRenamedEventArgs args)
        {
            _index.Rename(args.OldLowerName, args.OldParentFrn, args.DriveLetter, args.NewRecord);
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
        private enum MatchMode { Contains, Prefix, Suffix, Regex }

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
        private static (MatchMode mode, string normalizedQuery) DetectMatchMode(string keyword)
        {
            if (keyword.StartsWith("^"))
                return (MatchMode.Prefix, keyword.Substring(1).ToLowerInvariant());

            if (keyword.EndsWith("$"))
                return (MatchMode.Suffix, keyword.Substring(0, keyword.Length - 1).ToLowerInvariant());

            if (keyword.Length >= 3 && keyword.StartsWith("/") && keyword.EndsWith("/"))
                return (MatchMode.Regex, keyword.Substring(1, keyword.Length - 2));

            return (MatchMode.Contains, keyword.ToLowerInvariant());
        }

        // ── 匹配辅助方法 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 前缀匹配：在 SortedArray 上二分定位第一个 &gt;= prefix 的位置，
        /// 然后向后扫描直到 LowerName 不再以 prefix 开头。O(log n + m)。
        /// 需求 3.2、7.2
        /// </summary>
        private static List<FileRecord> PrefixMatch(string prefix, FileRecord[] sortedArray, int maxResults)
        {
            var results = new List<FileRecord>(Math.Min(maxResults, 64));
            if (sortedArray.Length == 0 || string.IsNullOrEmpty(prefix))
                return results;

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

            for (var i = start; i < sortedArray.Length && results.Count < maxResults; i++)
            {
                if (!sortedArray[i].LowerName.StartsWith(prefix, StringComparison.Ordinal))
                    break;
                results.Add(sortedArray[i]);
            }

            return results;
        }

        /// <summary>
        /// 后缀匹配：对 SortedArray 执行线性扫描，返回 LowerName 以 suffix 结尾的记录。
        /// 需求 3.3、7.3
        /// </summary>
        private static List<FileRecord> SuffixMatch(string suffix, FileRecord[] sortedArray, int maxResults)
        {
            var results = new List<FileRecord>(Math.Min(maxResults, 64));
            foreach (var record in sortedArray)
            {
                if (record.LowerName.EndsWith(suffix, StringComparison.Ordinal))
                {
                    results.Add(record);
                    if (results.Count >= maxResults)
                        break;
                }
            }
            return results;
        }

        /// <summary>
        /// 正则匹配：对 SortedArray 执行线性扫描，使用 <paramref name="pattern"/> 匹配 LowerName。
        /// 若正则无效，通过 <paramref name="progress"/> 上报错误并返回空列表。
        /// 需求 3.4、7.4、7.5
        /// </summary>
        private static List<FileRecord> RegexMatch(
            string pattern,
            FileRecord[] sortedArray,
            int maxResults,
            IProgress<string> progress)
        {
            Regex regex;
            try
            {
                regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException)
            {
                progress?.Report("正则表达式无效");
                return new List<FileRecord>();
            }

            var results = new List<FileRecord>(Math.Min(maxResults, 64));
            foreach (var record in sortedArray)
            {
                try
                {
                    if (regex.IsMatch(record.LowerName))
                    {
                        results.Add(record);
                        if (results.Count >= maxResults)
                            break;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    progress?.Report("正则表达式无效");
                    return new List<FileRecord>();
                }
            }
            return results;
        }

        /// <summary>
        /// 包含匹配：先查 ExactHashMap（精确命中），再对 SortedArray 执行二分定位后线性扫描。
        /// 需求 3.1、3.3、7.1
        /// </summary>
        private static List<FileRecord> ContainsMatch(
            string query,
            Dictionary<string, List<FileRecord>> exactHashMap,
            FileRecord[] sortedArray,
            int maxResults)
        {
            var results = new List<FileRecord>(Math.Min(maxResults, 64));

            // 先尝试精确匹配（O(1)）
            if (exactHashMap.TryGetValue(query, out var exactBucket))
            {
                foreach (var r in exactBucket)
                {
                    results.Add(r);
                    if (results.Count >= maxResults)
                        return results;
                }
            }

            // 再对 SortedArray 做包含扫描（跳过已精确命中的记录）
            // 用二分找到第一个 LowerName >= query 的位置作为扫描起点，
            // 但包含匹配需要全量扫描，因此直接线性扫描整个数组。
            foreach (var record in sortedArray)
            {
                // 跳过已通过精确匹配加入的记录
                if (record.LowerName == query)
                    continue;

                if (record.LowerName.Contains(query))
                {
                    results.Add(record);
                    if (results.Count >= maxResults)
                        break;
                }
            }

            return results;
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
                var (mode, normalizedQuery) = DetectMatchMode(keyword);

                List<FileRecord> matched;
                switch (mode)
                {
                    case MatchMode.Prefix:
                        matched = PrefixMatch(normalizedQuery, idx.SortedArray, maxResults);
                        break;
                    case MatchMode.Suffix:
                        matched = SuffixMatch(normalizedQuery, idx.SortedArray, maxResults);
                        break;
                    case MatchMode.Regex:
                        matched = RegexMatch(normalizedQuery, idx.SortedArray, maxResults, progress);
                        break;
                    default:
                        matched = ContainsMatch(normalizedQuery, idx.ExactHashMap, idx.SortedArray, maxResults);
                        break;
                }

                ct.ThrowIfCancellationRequested();

                // 按需解析完整路径：用 _enumerator 的 FRN 字典（每卷独立缓存）
                var results = new List<ScannedFileInfo>(matched.Count);
                foreach (var record in matched)
                {
                    var fullPath = _enumerator.ResolveFullPath(record.DriveLetter, record.ParentFrn, record.OriginalName);
                    results.Add(new ScannedFileInfo
                    {
                        FullPath        = fullPath ?? (record.DriveLetter + ":\\" + record.OriginalName),
                        FileName        = record.OriginalName,
                        SizeBytes       = 0,
                        ModifiedTimeUtc = DateTime.MinValue,
                        RootPath        = string.Empty,
                        RootDisplayName = string.Empty,
                        IsDirectory     = record.IsDirectory
                    });
                }

                return new SearchQueryResult
                {
                    TotalIndexedCount = idx.TotalCount,
                    TotalMatchedCount = results.Count,
                    IsTruncated       = results.Count >= maxResults,
                    Results           = results
                };
            }, ct);
        }

        // ── 私有实现 ────────────────────────────────────────────────────────────

        private int BuildIndex(IProgress<string> progress, CancellationToken ct)
        {
            var allRecords = new List<FileRecord>(500_000);
            var successfulDrives = new List<(char letter, long nextUsn, ulong journalId)>();

            foreach (var drive in DriveInfo.GetDrives())
            {
                ct.ThrowIfCancellationRequested();
                if (drive.DriveType != DriveType.Fixed) continue;
                var driveLetter = drive.Name[0];
                try
                {
                    var (entries, nextUsn, journalId) = _enumerator.EnumerateVolumeWithUsn(driveLetter, ct);
                    BuildVolumeRecords(driveLetter, entries, allRecords);
                    successfulDrives.Add((driveLetter, nextUsn, journalId));
                    progress?.Report($"卷 {driveLetter}: 已枚举 {entries.Count} 条");
                }
                catch (OperationCanceledException) { throw; }
                catch (InvalidOperationException ex) { progress?.Report($"跳过卷 {driveLetter}:：{ex.Message}"); }
                catch (Exception ex) { progress?.Report($"跳过卷 {driveLetter}:：{ex.Message}"); }
            }

            ct.ThrowIfCancellationRequested();
            _index.Build(allRecords);
            progress?.Report($"已索引 {_index.TotalCount} 个对象");

            foreach (var (letter, nextUsn, journalId) in successfulDrives)
                _usnWatcher.StartWatching(letter, nextUsn, journalId, ct);

            return _index.TotalCount;
        }

        /// <summary>
        /// 将 MFT 条目转换为 FileRecord（只存文件名+ParentFrn，不拼路径）并追加到 allRecords。
        /// </summary>
        private static void BuildVolumeRecords(
            char driveLetter,
            List<MftEnumerator.RawMftEntry> entries,
            List<FileRecord> allRecords)
        {
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.FileName)) continue;
                allRecords.Add(new FileRecord(
                    lowerName:    e.FileName.ToLowerInvariant(),
                    originalName: e.FileName,
                    parentFrn:    e.ParentFrn,
                    driveLetter:  driveLetter,
                    isDirectory:  e.IsDirectory));
            }
        }

    }
}
