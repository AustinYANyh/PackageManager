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
                var newIndex = new MemoryIndex();
                var allRecords = new List<FileRecord>(1_000_000);

                foreach (var drive in DriveInfo.GetDrives())
                {
                    ct.ThrowIfCancellationRequested();

                    if (drive.DriveType != DriveType.Fixed)
                        continue;

                    var driveLetter = drive.Name[0];

                    try
                    {
                        var entries = _enumerator.EnumerateVolume(driveLetter, ct);
                        var volumeRecords = BuildVolumeRecords(driveLetter, entries, ct);
                        allRecords.AddRange(volumeRecords);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _progress?.Report($"跳过卷 {driveLetter}:：{ex.Message}");
                    }
                }

                ct.ThrowIfCancellationRequested();
                newIndex.Build(allRecords);

                // 原子替换索引引用（需求 6.5）
                Interlocked.Exchange(ref _index, newIndex);

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
                fullPath:     args.FullPath,
                isDirectory:  args.IsDirectory);
            _index.Insert(record);
        }

        /// <summary>文件删除：从索引中移除记录。需求 6.3</summary>
        private void OnFileDeleted(object sender, UsnFileDeletedEventArgs args)
        {
            _index.Remove(args.LowerName, args.FullPath);
        }

        /// <summary>文件重命名：先移除旧记录，再插入新记录。需求 6.4</summary>
        private void OnFileRenamed(object sender, UsnFileRenamedEventArgs args)
        {
            _index.Rename(args.OldLowerName, args.OldFullPath, args.NewRecord);
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
                // 关键词为空时返回空结果（需求 3.5）
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    return new SearchQueryResult
                    {
                        TotalIndexedCount = _index.TotalCount,
                        TotalMatchedCount = 0,
                        IsTruncated = false,
                        Results = new List<ScannedFileInfo>()
                    };
                }

                // 获取当前索引快照（无锁读，需求 3.5）
                var idx = _index;

                // 识别匹配模式
                var (mode, normalizedQuery) = DetectMatchMode(keyword);

                // 根据模式调用对应匹配方法
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
                    default: // Contains
                        matched = ContainsMatch(normalizedQuery, idx.ExactHashMap, idx.SortedArray, maxResults);
                        break;
                }

                ct.ThrowIfCancellationRequested();

                // 将 FileRecord 列表转换为 SearchQueryResult（需求 3.6、7.6）
                var results = new List<ScannedFileInfo>(matched.Count);
                foreach (var record in matched)
                {
                    results.Add(new ScannedFileInfo
                    {
                        FullPath = record.FullPath,
                        FileName = record.OriginalName,
                        SizeBytes = 0,
                        ModifiedTimeUtc = DateTime.MinValue,
                        RootPath = string.Empty,
                        RootDisplayName = string.Empty,
                        IsDirectory = record.IsDirectory
                    });
                }

                return new SearchQueryResult
                {
                    TotalIndexedCount = idx.TotalCount,
                    TotalMatchedCount = results.Count,
                    IsTruncated = results.Count >= maxResults,
                    Results = results
                };
            }, ct);
        }

        // ── 私有实现 ────────────────────────────────────────────────────────────

        private int BuildIndex(IProgress<string> progress, CancellationToken ct)
        {
            var allRecords = new List<FileRecord>(1_000_000);
            var successfulDrives = new List<char>();

            foreach (var drive in DriveInfo.GetDrives())
            {
                ct.ThrowIfCancellationRequested();

                if (drive.DriveType != DriveType.Fixed)
                    continue;

                var driveLetter = drive.Name[0]; // e.g. 'C'

                try
                {
                    var entries = _enumerator.EnumerateVolume(driveLetter, ct);
                    var volumeRecords = BuildVolumeRecords(driveLetter, entries, ct);
                    allRecords.AddRange(volumeRecords);
                    successfulDrives.Add(driveLetter);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (InvalidOperationException ex)
                {
                    // 权限不足或无法打开卷，跳过并上报原因（需求 1.5）
                    progress?.Report($"跳过卷 {driveLetter}:：{ex.Message}");
                }
                catch (Exception ex)
                {
                    progress?.Report($"跳过卷 {driveLetter}:：{ex.Message}");
                }
            }

            ct.ThrowIfCancellationRequested();

            _index.Build(allRecords);

            // 需求 1.4：上报已索引总数
            progress?.Report($"已索引 {_index.TotalCount} 个对象");

            // 需求 6.1：BuildIndex 完成后为每个成功扫描的卷启动 UsnWatcher
            foreach (var driveLetter in successfulDrives)
            {
                _usnWatcher.StartWatching(driveLetter, 0, 0, ct);
            }

            return _index.TotalCount;
        }

        /// <summary>
        /// 将单个卷的 <see cref="RawMftEntry"/> 序列转换为 <see cref="FileRecord"/> 列表。
        /// 先收集所有条目建立 FRN→(name, parentFrn) 字典，再逐条解析完整路径。
        /// </summary>
        private static List<FileRecord> BuildVolumeRecords(
            char driveLetter,
            IEnumerable<RawMftEntry> entries,
            CancellationToken ct)
        {
            // 第一遍：收集所有条目
            var entryMap = new Dictionary<ulong, (string name, ulong parentFrn, bool isDir)>(200_000);
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(e.FileName))
                    entryMap[e.Frn] = (e.FileName, e.ParentFrn, e.IsDirectory);
            }

            // 路径缓存，避免重复向上遍历
            var pathCache = new Dictionary<ulong, string>(entryMap.Count);

            var records = new List<FileRecord>(entryMap.Count);

            // 第二遍：解析完整路径并构建 FileRecord
            foreach (var kv in entryMap)
            {
                ct.ThrowIfCancellationRequested();

                var frn = kv.Key;
                var (name, parentFrn, isDir) = kv.Value;

                var fullPath = ResolveFullPath(frn, name, parentFrn, driveLetter, entryMap, pathCache);
                if (fullPath == null)
                    continue;

                records.Add(new FileRecord(
                    lowerName: name.ToLowerInvariant(),
                    originalName: name,
                    fullPath: fullPath,
                    isDirectory: isDir));
            }

            return records;
        }

        /// <summary>
        /// 通过向上遍历父 FRN 链，解析文件或目录的完整路径。
        /// NTFS 根目录的 FRN 通常为 5（或其他已知根 FRN），当父 FRN 不在 entryMap 中时视为卷根。
        /// </summary>
        private static string ResolveFullPath(
            ulong frn,
            string name,
            ulong parentFrn,
            char driveLetter,
            Dictionary<ulong, (string name, ulong parentFrn, bool isDir)> entryMap,
            Dictionary<ulong, string> pathCache)
        {
            // 如果已缓存此 FRN 的目录路径，直接拼接
            if (pathCache.TryGetValue(frn, out var cached))
                return cached;

            var dirPath = ResolveDirectoryPath(parentFrn, driveLetter, entryMap, pathCache);
            if (dirPath == null)
                return null;

            var fullPath = dirPath.Length > 0
                ? dirPath + "\\" + name
                : driveLetter + ":\\" + name;

            // 缓存此条目自身的目录路径（供其子项使用）
            pathCache[frn] = fullPath;
            return fullPath;
        }

        /// <summary>
        /// 递归解析目录的完整路径（带缓存）。
        /// </summary>
        private static string ResolveDirectoryPath(
            ulong dirFrn,
            char driveLetter,
            Dictionary<ulong, (string name, ulong parentFrn, bool isDir)> entryMap,
            Dictionary<ulong, string> pathCache)
        {
            // 防止无限循环（FRN 5 是 NTFS 根，其父 FRN 也是 5）
            const int maxDepth = 64;
            var depth = 0;

            var segments = new List<string>(8);
            var current = dirFrn;

            while (true)
            {
                if (depth++ > maxDepth)
                    return null; // 路径过深，放弃

                if (pathCache.TryGetValue(current, out var cachedPath))
                {
                    // 找到缓存节点，拼接剩余 segments
                    return BuildPath(cachedPath, segments);
                }

                if (!entryMap.TryGetValue(current, out var entry))
                {
                    // 父 FRN 不在 map 中 → 已到达卷根
                    var root = driveLetter + ":";
                    return BuildPath(root, segments);
                }

                // 检测根节点：父 FRN 等于自身（NTFS 根目录特征）
                if (entry.parentFrn == current)
                {
                    var root = driveLetter + ":";
                    pathCache[current] = root;
                    return BuildPath(root, segments);
                }

                segments.Add(entry.name);
                current = entry.parentFrn;
            }
        }

        /// <summary>
        /// 将 segments（逆序）拼接到 basePath 后面，构成完整路径。
        /// </summary>
        private static string BuildPath(string basePath, List<string> segments)
        {
            if (segments.Count == 0)
                return basePath;

            var parts = new string[segments.Count + 1];
            parts[0] = basePath;
            for (var i = 0; i < segments.Count; i++)
                parts[i + 1] = segments[segments.Count - 1 - i]; // 逆序还原

            return string.Join("\\", parts);
        }
    }
}
