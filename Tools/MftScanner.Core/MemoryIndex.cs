using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MftScanner
{
    public sealed class ContainsBucketStatus
    {
        public static readonly ContainsBucketStatus Empty = new ContainsBucketStatus();

        public bool CharReady { get; set; }
        public bool BigramReady { get; set; }
        public bool TrigramReady { get; set; }
        public bool IsOverlayOverflowed { get; set; }
        public long Epoch { get; set; }

        public bool IsFullReady => CharReady && BigramReady && TrigramReady && !IsOverlayOverflowed;
    }

    public sealed partial class MemoryIndex
    {
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
        private long _contentVersion;

        /// <summary>扩展名匹配：扩展名小写（如 ".log"）→ FileRecord 列表。</summary>
        public Dictionary<string, List<FileRecord>> ExtensionHashMap { get; private set; }
            = new Dictionary<string, List<FileRecord>>();

        /// <summary>有序数组：按 LowerName 字典序排列。</summary>
        public FileRecord[] SortedArray { get; private set; } = Array.Empty<FileRecord>();
        public FileRecord[] ParentSortedArray { get; private set; } = Array.Empty<FileRecord>();
        public FileRecord[] DirectorySortedArray { get; private set; } = Array.Empty<FileRecord>();
        public FileRecord[] LaunchableSortedArray { get; private set; } = Array.Empty<FileRecord>();
        public FileRecord[] ScriptSortedArray { get; private set; } = Array.Empty<FileRecord>();
        public FileRecord[] LogSortedArray { get; private set; } = Array.Empty<FileRecord>();
        public FileRecord[] ConfigSortedArray { get; private set; } = Array.Empty<FileRecord>();
        private bool _derivedStructuresReady = true;
        private ContainsAccelerator _containsAccelerator = ContainsAccelerator.Empty;
        private ContainsOverlay _containsOverlay = ContainsOverlay.Empty;
        private HashSet<RecordKey> _deletedOverlayKeys = new HashSet<RecordKey>();
        private bool _containsAcceleratorReady = true;
        private long _containsAcceleratorEpoch;
        private readonly object _liveOverlayPublishLock = new object();
        private readonly object _shortContainsHotBucketLock = new object();
        private Dictionary<string, ShortContainsHotBucket> _shortContainsHotBuckets = new Dictionary<string, ShortContainsHotBucket>(StringComparer.Ordinal);
        private readonly LinkedList<string> _shortContainsHotBucketLru = new LinkedList<string>();
        private int[][] _singleCharAsciiBitsets;
        private int[] _singleCharAsciiCounts;
        private int[][] _singleCharAsciiDriveCounts;
        private int _singleCharAsciiRecordCount;
        private int[] _bigramAsciiCounts;
        private int[][] _bigramAsciiDriveCounts;
        private FileRecord[][] _bigramAsciiPageSamples;
        private int _bigramAsciiRecordCount;
        private const int MaxShortContainsHotBuckets = 12;
        private const int MaxShortContainsHotBucketPostings = 12000000;
        private const int SingleCharAsciiLimit = 128;
        private const int BigramAsciiLimit = 128;
        private const int BigramAsciiTokenCount = BigramAsciiLimit * BigramAsciiLimit;
        private const int BigramAsciiPageSampleLimit = 16384;
        private readonly List<PendingContainsMutation> _pendingContainsMutations = new List<PendingContainsMutation>();
        private bool _pendingContainsMutationsOverflowed;
        private const int MaxPendingContainsMutations = 65536;
        private const int DefaultMaxLiveDeltaMutations = 65536;
        private const int CatchUpMaxLiveDeltaMutations = 65536;

        public int TotalCount
        {
            get
            {
                var overlay = Volatile.Read(ref _containsOverlay) ?? ContainsOverlay.Empty;
                var deleted = Volatile.Read(ref _deletedOverlayKeys);
                var total = (SortedArray?.Length ?? 0) + overlay.AddedCount - (deleted?.Count ?? 0);
                return total < 0 ? 0 : total;
            }
        }

        public long ContentVersion => Interlocked.Read(ref _contentVersion);

        public bool AreDerivedStructuresReady => Volatile.Read(ref _derivedStructuresReady);

        public int LiveDeltaCount
        {
            get
            {
                var overlay = Volatile.Read(ref _containsOverlay) ?? ContainsOverlay.Empty;
                var deleted = Volatile.Read(ref _deletedOverlayKeys);
                return overlay.AddedCount + (deleted?.Count ?? 0);
            }
        }

        public bool HasLiveAddedOverlay
        {
            get
            {
                var overlay = Volatile.Read(ref _containsOverlay) ?? ContainsOverlay.Empty;
                return overlay.AddedCount > 0;
            }
        }

        public bool HasExtensionIndex
        {
            get
            {
                var map = ExtensionHashMap;
                return map != null && map.Count > 0;
            }
        }

        public bool HasDeletedOverlay
        {
            get
            {
                var keys = Volatile.Read(ref _deletedOverlayKeys);
                return keys != null && keys.Count > 0;
            }
        }

        public bool IsDeleted(FileRecord record)
        {
            if (record == null)
                return false;

            var keys = Volatile.Read(ref _deletedOverlayKeys);
            return keys != null
                   && keys.Count > 0
                   && keys.Contains(RecordKey.FromRecord(record));
        }

        public FileRecord[] GetLiveAddedRecordsSnapshot()
        {
            var overlay = Volatile.Read(ref _containsOverlay) ?? ContainsOverlay.Empty;
            if (overlay.AddedCount == 0)
                return Array.Empty<FileRecord>();

            var records = overlay.AddedRecords;
            if (records == null || records.Count == 0)
                return Array.Empty<FileRecord>();

            var result = new List<FileRecord>(records.Count);
            for (var i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (record != null && !IsDeleted(record))
                    result.Add(record);
            }

            if (result.Count > 1)
                result.Sort(ByLowerName);

            return result.Count == 0 ? Array.Empty<FileRecord>() : result.ToArray();
        }

        private ContainsOverlay GetLiveOverlaySnapshot()
        {
            return Volatile.Read(ref _containsOverlay) ?? ContainsOverlay.Empty;
        }

        public bool HasContainsAccelerator
        {
            get
            {
                var accelerator = Volatile.Read(ref _containsAccelerator);
                return HasShortAsciiStructuresReady()
                       || (Volatile.Read(ref _containsAcceleratorReady)
                           && accelerator != null
                           && !accelerator.IsEmpty);
            }
        }

        public ContainsBucketStatus GetContainsBucketStatus()
        {
            var accelerator = Volatile.Read(ref _containsAccelerator);
            var overlay = Volatile.Read(ref _containsOverlay) ?? ContainsOverlay.Empty;
            var shortReady = HasShortAsciiStructuresReady();
            var trigramReady = Volatile.Read(ref _containsAcceleratorReady)
                               && accelerator != null
                               && accelerator.HasTrigramBucket;

            return new ContainsBucketStatus
            {
                CharReady = shortReady,
                BigramReady = shortReady,
                TrigramReady = trigramReady,
                IsOverlayOverflowed = overlay.IsOverflowed,
                Epoch = Interlocked.Read(ref _containsAcceleratorEpoch)
            };
        }

        public sealed class ContainsSearchResult
        {
            public List<FileRecord> Page { get; set; } = new List<FileRecord>();
            public int Total { get; set; }
            public string Mode { get; set; } = "fallback";
            public int CandidateCount { get; set; }
            public long IntersectMs { get; set; }
            public long VerifyMs { get; set; }
            public bool IncludesLiveOverlay { get; set; }
        }

        public sealed class MatchSearchResult
        {
            public List<FileRecord> Page { get; set; } = new List<FileRecord>();
            public int Total { get; set; }
            public int CandidateCount { get; set; }
            public long VerifyMs { get; set; }
        }

        public bool IsQuerySupportedByContainsAccelerator(string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return false;
            }

            var accelerator = Volatile.Read(ref _containsAccelerator);
            if (query.Length <= 2)
            {
                return HasShortAsciiStructuresReady();
            }

            return Volatile.Read(ref _containsAcceleratorReady)
                   && accelerator != null
                   && !accelerator.IsEmpty
                   && accelerator.Supports(query);
        }

        public sealed class LiveDeltaApplyResult
        {
            public int Changes { get; set; }
            public int Inserted { get; set; }
            public int Deleted { get; set; }
            public int Restored { get; set; }
            public int AlreadyVisible { get; set; }
            public int OverlayAdds { get; set; }
            public int OverlayDeletes { get; set; }
            public bool CompactRequired { get; set; }
        }

        private sealed class ShortContainsHotBucket
        {
            public ShortContainsHotBucket(string query, long contentVersion, int[] postings)
            {
                Query = query ?? string.Empty;
                ContentVersion = contentVersion;
                Postings = postings ?? Array.Empty<int>();
            }

            public string Query { get; }
            public long ContentVersion { get; }
            public int[] Postings { get; }
        }

        public enum ContainsWarmupScope
        {
            Short,
            TrigramOnly,
            Full
        }

        private static readonly IComparer<FileRecord> ByLowerName =
            Comparer<FileRecord>.Create((a, b) => string.CompareOrdinal(a.LowerName, b.LowerName));

        private static readonly IComparer<FileRecord> ByParentThenLowerName =
            Comparer<FileRecord>.Create(CompareByParentThenLowerName);

        public bool TryContainsSearch(
            string query,
            SearchTypeFilter filter,
            int offset,
            int maxResults,
            CancellationToken ct,
            out ContainsSearchResult result)
        {
            result = null;
            if (string.IsNullOrEmpty(query))
            {
                return false;
            }

            var accelerator = Volatile.Read(ref _containsAccelerator);
            var overlay = Volatile.Read(ref _containsOverlay) ?? ContainsOverlay.Empty;

            if (!Volatile.Read(ref _containsAcceleratorReady)
                || accelerator == null
                || accelerator.IsEmpty)
            {
                if (TryShortAsciiSearch(query, filter, offset, maxResults, null, ct, overlay, out result))
                {
                    return true;
                }

                if (TryShortNonAsciiSearch(query, filter, offset, maxResults, null, ct, overlay, out result))
                {
                    return true;
                }

                if (TryShortContainsHotSearch(query, filter, offset, maxResults, ct, out result))
                {
                    return true;
                }

                return false;
            }

            if (!accelerator.Supports(query))
            {
                if (TryShortAsciiSearch(query, filter, offset, maxResults, null, ct, overlay, out result))
                {
                    return true;
                }

                if (TryShortNonAsciiSearch(query, filter, offset, maxResults, null, ct, overlay, out result))
                {
                    return true;
                }

                return TryShortContainsHotSearch(query, filter, offset, maxResults, ct, out result);
            }

            result = accelerator.Search(query, filter, offset, maxResults, ct, overlay);
            return true;
        }

        public bool TryEnsureShortContainsHotBucket(string query, CancellationToken ct)
        {
            if (!IsShortContainsQuery(query))
            {
                return false;
            }

            var contentVersion = ContentVersion;
            ShortContainsHotBucket existing;
            lock (_shortContainsHotBucketLock)
            {
                if (_shortContainsHotBuckets.TryGetValue(query, out existing)
                    && existing != null
                    && existing.ContentVersion == contentVersion)
                {
                    TouchShortContainsHotBucket(query);
                    return true;
                }
            }

            FileRecord[] snapshot;
            _lock.EnterReadLock();
            try
            {
                if (ContentVersion != contentVersion)
                {
                    return false;
                }

                snapshot = SortedArray;
            }
            finally
            {
                _lock.ExitReadLock();
            }

            var stopwatch = Stopwatch.StartNew();
            var postings = BuildShortContainsPostings(snapshot, query, ct);
            stopwatch.Stop();

            if (ContentVersion != contentVersion)
            {
                IndexPerfLog.Write("INDEX",
                    $"[CONTAINS SHORT HOT BUILD] outcome=stale query={IndexPerfLog.FormatValue(query)} elapsedMs={stopwatch.ElapsedMilliseconds}");
                return false;
            }

            var bucket = new ShortContainsHotBucket(query, contentVersion, postings);
            lock (_shortContainsHotBucketLock)
            {
                _shortContainsHotBuckets[query] = bucket;
                TouchShortContainsHotBucket(query);
                TrimShortContainsHotBuckets();
            }

            IndexPerfLog.Write("INDEX",
                $"[CONTAINS SHORT HOT BUILD] outcome=success query={IndexPerfLog.FormatValue(query)} " +
                $"records={(snapshot == null ? 0 : snapshot.Length)} postings={postings.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return true;
        }

        internal ContainsPostingsSnapshot ExportContainsPostingsSnapshot()
        {
            return ExportContainsPostingsSnapshot(out _);
        }

        internal ContainsPostingsSnapshot ExportContainsPostingsSnapshot(out ulong contentFingerprint)
        {
            contentFingerprint = 0;
            var accelerator = Volatile.Read(ref _containsAccelerator);
            if (!Volatile.Read(ref _containsAcceleratorReady)
                || accelerator == null
                || accelerator.IsEmpty
                || (!accelerator.HasCharBucket && !accelerator.HasBigramBucket && !accelerator.HasTrigramBucket))
            {
                return null;
            }

            contentFingerprint = accelerator.ContentFingerprint;
            if (contentFingerprint == 0)
                return null;

            return accelerator.ExportSnapshot();
        }

        internal bool TryLoadContainsPostingsSnapshot(ContainsPostingsSnapshot snapshot, ulong contentFingerprint = 0)
        {
            if (snapshot == null)
                return false;

            _lock.EnterWriteLock();
            try
            {
                if (snapshot.RecordCount != (SortedArray?.Length ?? 0))
                    return false;

                var accelerator = ContainsAccelerator.FromSnapshot(SortedArray, snapshot, contentFingerprint);
                if (accelerator == null || accelerator.IsEmpty)
                    return false;

                _containsAccelerator = accelerator;
                _containsAcceleratorReady = true;
                Interlocked.Increment(ref _containsAcceleratorEpoch);
                _pendingContainsMutations.Clear();
                _pendingContainsMutationsOverflowed = false;
                IndexPerfLog.Write("INDEX",
                    $"[CONTAINS SNAPSHOT LOAD] outcome=success records={SortedArray.Length} buckets={snapshot.BucketCount} bytes={snapshot.TotalBytes} " +
                    $"charReady={accelerator.HasCharBucket} bigramReady={accelerator.HasBigramBucket} trigram={accelerator.HasTrigramBucket} " +
                    $"shortAscii={snapshot.ShortAsciiTokensIncluded}");
                return true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        internal int[] ExportParentOrderSnapshot(out ulong contentFingerprint)
        {
            contentFingerprint = 0;
            FileRecord[] records;
            FileRecord[] parentArr;
            _lock.EnterReadLock();
            try
            {
                records = SortedArray;
                parentArr = ParentSortedArray;
                if (records == null
                    || records.Length == 0
                    || parentArr == null
                    || parentArr.Length != records.Length)
                {
                    return null;
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            var lookup = new Dictionary<FileRecord, int>(records.Length, ReferenceEqualityComparer<FileRecord>.Instance);
            for (var i = 0; i < records.Length; i++)
            {
                if (records[i] != null)
                    lookup[records[i]] = i;
            }

            var order = new int[parentArr.Length];
            for (var i = 0; i < parentArr.Length; i++)
            {
                var record = parentArr[i];
                if (record == null || !lookup.TryGetValue(record, out var sortedIndex))
                    return null;

                order[i] = sortedIndex;
            }

            contentFingerprint = IndexSnapshotFingerprint.Compute(records);
            return order;
        }

        internal bool TryLoadParentOrderSnapshot(int[] parentOrder, ulong contentFingerprint = 0)
        {
            if (parentOrder == null || parentOrder.Length == 0)
                return false;

            _lock.EnterWriteLock();
            try
            {
                var records = SortedArray;
                if (records == null || records.Length != parentOrder.Length)
                    return false;

                var parentArr = new FileRecord[parentOrder.Length];
                for (var i = 0; i < parentOrder.Length; i++)
                {
                    var sortedIndex = parentOrder[i];
                    if ((uint)sortedIndex >= (uint)records.Length)
                    {
                        return false;
                    }

                    var record = records[sortedIndex];
                    if (record == null)
                    {
                        return false;
                    }

                    parentArr[i] = record;
                }

                ParentSortedArray = parentArr;
                IndexPerfLog.Write("INDEX",
                    $"[PARENT ORDER SNAPSHOT LOAD] outcome=success records={parentArr.Length} fingerprint={contentFingerprint}");
                return true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        internal bool EnsureContainsAcceleratorReady(ContainsWarmupScope scope, CancellationToken ct)
        {
            return TryEnsureContainsAccelerator(scope, ct);
        }

        public FileRecord[] GetSubtreeCandidates(
            char driveLetter,
            IReadOnlyList<ulong> directoryFrns,
            SearchTypeFilter filter,
            CancellationToken ct)
        {
            if (directoryFrns == null || directoryFrns.Count == 0)
                return Array.Empty<FileRecord>();

            var dl = char.ToUpperInvariant(driveLetter);
            var candidates = new List<FileRecord>();
            _lock.EnterReadLock();
            try
            {
                var parentArr = ParentSortedArray;
                if (parentArr == null || parentArr.Length == 0)
                {
                    var directorySet = new HashSet<ulong>(directoryFrns);
                    var sorted = SortedArray;
                    if (sorted != null && sorted.Length >= 250000)
                    {
                        long parallelTotal = 0;
                        var parallelCandidates = new System.Collections.Concurrent.ConcurrentBag<List<FileRecord>>();
                        Parallel.For<List<FileRecord>>(
                            0,
                            sorted.Length,
                            new ParallelOptions
                            {
                                CancellationToken = ct,
                                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
                            },
                            () => new List<FileRecord>(),
                            (recordIndex, state, local) =>
                            {
                                if (((recordIndex + 1) & 0x3FFF) == 0)
                                    ct.ThrowIfCancellationRequested();

                                var record = sorted[recordIndex];
                                if (record != null
                                    && char.ToUpperInvariant(record.DriveLetter) == dl
                                    && directorySet.Contains(record.ParentFrn)
                                    && !IsDeleted(record)
                                    && MatchesFilter(record, filter))
                                {
                                    local.Add(record);
                                    Interlocked.Increment(ref parallelTotal);
                                }

                                return local;
                            },
                            local =>
                            {
                                if (local.Count > 0)
                                    parallelCandidates.Add(local);
                            });

                        if (parallelTotal == 0)
                            return Array.Empty<FileRecord>();

                        var merged = new List<FileRecord>(checked((int)parallelTotal));
                        foreach (var local in parallelCandidates)
                        {
                            merged.AddRange(local);
                        }

                        var parallelResult = merged.ToArray();
                        Array.Sort(parallelResult, ByLowerName);
                        return parallelResult;
                    }

                    for (var i = 0; i < sorted.Length; i++)
                    {
                        if (((i + 1) & 0x3FFF) == 0)
                            ct.ThrowIfCancellationRequested();

                        var record = sorted[i];
                        if (record == null
                            || char.ToUpperInvariant(record.DriveLetter) != dl
                            || !directorySet.Contains(record.ParentFrn)
                            || IsDeleted(record)
                            || !MatchesFilter(record, filter))
                        {
                            continue;
                        }

                        candidates.Add(record);
                    }

                    return candidates.Count == 0 ? Array.Empty<FileRecord>() : candidates.ToArray();
                }

                for (var i = 0; i < directoryFrns.Count; i++)
                {
                    if (((i + 1) & 0x3FF) == 0)
                        ct.ThrowIfCancellationRequested();

                    var parentFrn = directoryFrns[i];
                    var start = LowerBoundByParent(parentArr, dl, parentFrn);
                    for (var j = start; j < parentArr.Length; j++)
                    {
                        var record = parentArr[j];
                        if (!IsSameParent(record, dl, parentFrn))
                            break;

                        if (!IsDeleted(record) && MatchesFilter(record, filter))
                            candidates.Add(record);
                    }
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            if (candidates.Count == 0)
                return Array.Empty<FileRecord>();

            var result = candidates.ToArray();
            Array.Sort(result, ByLowerName);
            return result;
        }

        public ContainsSearchResult SearchSubtreeContains(
            char driveLetter,
            IReadOnlyList<ulong> directoryFrns,
            string query,
            SearchTypeFilter filter,
            int offset,
            int maxResults,
            CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var matched = new List<FileRecord>(Math.Min(32768, Math.Max(Math.Max(maxResults, 0), 4096)));
            var candidateCount = 0;
            if (directoryFrns == null || directoryFrns.Count == 0 || string.IsNullOrEmpty(query))
            {
                stopwatch.Stop();
                return new ContainsSearchResult
                {
                    Mode = "path-subtree",
                    CandidateCount = 0,
                    Total = 0,
                    Page = new List<FileRecord>(),
                    VerifyMs = stopwatch.ElapsedMilliseconds
                };
            }

            var dl = char.ToUpperInvariant(driveLetter);
            _lock.EnterReadLock();
            try
            {
                var parentArr = ParentSortedArray;
                if (parentArr == null || parentArr.Length == 0)
                {
                    stopwatch.Stop();
                    return new ContainsSearchResult
                    {
                        Mode = "path-subtree-miss",
                        CandidateCount = 0,
                        Total = 0,
                        Page = new List<FileRecord>(),
                        VerifyMs = stopwatch.ElapsedMilliseconds
                    };
                }

                for (var i = 0; i < directoryFrns.Count; i++)
                {
                    if (((i + 1) & 0x3FF) == 0)
                        ct.ThrowIfCancellationRequested();

                    var parentFrn = directoryFrns[i];
                    var start = LowerBoundByParent(parentArr, dl, parentFrn);
                    for (var j = start; j < parentArr.Length; j++)
                    {
                        var record = parentArr[j];
                        if (!IsSameParent(record, dl, parentFrn))
                            break;

                        if (IsDeleted(record) || !MatchesFilter(record, filter))
                            continue;

                        candidateCount++;
                        if (NameContains(record, query))
                            matched.Add(record);
                    }
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            if (matched.Count > 1)
                matched.Sort(ByLowerName);

            var normalizedOffset = Math.Max(offset, 0);
            var normalizedMaxResults = Math.Max(maxResults, 0);
            var page = new List<FileRecord>(Math.Min(normalizedMaxResults, 64));
            var end = normalizedMaxResults <= 0
                ? normalizedOffset
                : Math.Min(matched.Count, normalizedOffset + normalizedMaxResults);
            for (var i = normalizedOffset; i < end; i++)
            {
                page.Add(matched[i]);
            }

            stopwatch.Stop();
            return new ContainsSearchResult
            {
                Mode = query.Length <= 2 ? "path-subtree-short" : "path-subtree",
                CandidateCount = candidateCount,
                Total = matched.Count,
                Page = page,
                VerifyMs = stopwatch.ElapsedMilliseconds
            };
        }

        public FileRecord[] GetDriveCandidates(
            char driveLetter,
            SearchTypeFilter filter,
            CancellationToken ct)
        {
            var dl = char.ToUpperInvariant(driveLetter);
            var candidates = new List<FileRecord>();
            _lock.EnterReadLock();
            try
            {
                var source = GetCandidateArrayForFilter(filter);
                for (var i = 0; i < source.Length; i++)
                {
                    if (((i + 1) & 0x3FFF) == 0)
                        ct.ThrowIfCancellationRequested();

                    var record = source[i];
                    if (record == null
                        || char.ToUpperInvariant(record.DriveLetter) != dl
                        || IsDeleted(record)
                        || !MatchesFilter(record, filter))
                    {
                        continue;
                    }

                    candidates.Add(record);
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            return candidates.Count == 0 ? Array.Empty<FileRecord>() : candidates.ToArray();
        }

        private FileRecord[] GetCandidateArrayForFilter(SearchTypeFilter filter)
        {
            if (!_derivedStructuresReady)
                return SortedArray;

            switch (filter)
            {
                case SearchTypeFilter.Folder:
                    return DirectorySortedArray ?? Array.Empty<FileRecord>();
                case SearchTypeFilter.Launchable:
                    return LaunchableSortedArray ?? Array.Empty<FileRecord>();
                case SearchTypeFilter.Script:
                    return ScriptSortedArray ?? Array.Empty<FileRecord>();
                case SearchTypeFilter.Log:
                    return LogSortedArray ?? Array.Empty<FileRecord>();
                case SearchTypeFilter.Config:
                    return ConfigSortedArray ?? Array.Empty<FileRecord>();
                default:
                    return SortedArray;
            }
        }

        public bool TrySearchDriveExtension(
            char driveLetter,
            string extension,
            int offset,
            int maxResults,
            CancellationToken ct,
            out MatchSearchResult result)
        {
            result = null;
            if (string.IsNullOrEmpty(extension))
                return false;

            var stopwatch = Stopwatch.StartNew();
            var page = new List<FileRecord>(Math.Min(Math.Max(maxResults, 0), 64));
            var total = 0;
            var dl = char.ToUpperInvariant(driveLetter);
            var normalizedOffset = Math.Max(offset, 0);
            var normalizedMaxResults = Math.Max(maxResults, 0);
            var hasDeletedOverlay = HasDeletedOverlay;

            _lock.EnterReadLock();
            try
            {
                if (ExtensionHashMap == null
                    || !ExtensionHashMap.TryGetValue(extension, out var bucket)
                    || bucket == null)
                {
                    return false;
                }

                for (var i = 0; i < bucket.Count; i++)
                {
                    if (((i + 1) & 0xFFF) == 0)
                        ct.ThrowIfCancellationRequested();

                    var record = bucket[i];
                    if (record == null
                        || char.ToUpperInvariant(record.DriveLetter) != dl
                        || (hasDeletedOverlay && IsDeleted(record)))
                    {
                        continue;
                    }

                    total++;
                    if (total > normalizedOffset && page.Count < normalizedMaxResults)
                        page.Add(record);
                }

                stopwatch.Stop();
                result = new MatchSearchResult
                {
                    CandidateCount = bucket.Count,
                    Total = total,
                    Page = page,
                    VerifyMs = stopwatch.ElapsedMilliseconds
                };
                return true;
            }
            finally
            {
                if (stopwatch.IsRunning)
                    stopwatch.Stop();
                _lock.ExitReadLock();
            }
        }

        public ContainsSearchResult SearchDriveFilteredContains(
            char driveLetter,
            string query,
            SearchTypeFilter filter,
            int offset,
            int maxResults,
            CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var page = new List<FileRecord>(Math.Min(Math.Max(maxResults, 0), 64));
            var total = 0;
            var candidateCount = 0;
            var dl = char.ToUpperInvariant(driveLetter);
            var normalizedOffset = Math.Max(offset, 0);
            var normalizedMaxResults = Math.Max(maxResults, 0);
            var hasDeletedOverlay = HasDeletedOverlay;
            var filterAll = filter == SearchTypeFilter.All;

            _lock.EnterReadLock();
            try
            {
                var source = GetCandidateArrayForFilter(filter);
                if (source == null || source.Length == 0)
                {
                    return new ContainsSearchResult
                    {
                        Mode = "drive-filtered-scan",
                        CandidateCount = 0,
                        Total = 0,
                        Page = page,
                        VerifyMs = stopwatch.ElapsedMilliseconds
                    };
                }

                if (source.Length >= 250000 && normalizedOffset == 0 && normalizedMaxResults > 0)
                {
                    var scanIndex = 0;
                    var sequentialPageScanLimit = Math.Min(source.Length, 262144);
                    for (; scanIndex < sequentialPageScanLimit && page.Count < normalizedMaxResults; scanIndex++)
                    {
                        if (((scanIndex + 1) & 0x3FFF) == 0)
                            ct.ThrowIfCancellationRequested();

                        var record = source[scanIndex];
                        if (record == null
                            || char.ToUpperInvariant(record.DriveLetter) != dl
                            || (hasDeletedOverlay && IsDeleted(record))
                            || (!filterAll && !MatchesFilter(record, filter)))
                        {
                            continue;
                        }

                        candidateCount++;
                        if (!NameContains(record, query))
                            continue;

                        total++;
                        page.Add(record);
                    }

                    long remainingTotal = 0;
                    long remainingCandidates = 0;
                    Parallel.For<long[]>(
                        scanIndex,
                        source.Length,
                        new ParallelOptions
                        {
                            CancellationToken = ct,
                            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
                        },
                        () => new long[2],
                        (i, state, local) =>
                        {
                            if (((i + 1) & 0x3FFF) == 0)
                                ct.ThrowIfCancellationRequested();

                            var record = source[i];
                            if (record == null
                                || char.ToUpperInvariant(record.DriveLetter) != dl
                                || (hasDeletedOverlay && IsDeleted(record))
                                || (!filterAll && !MatchesFilter(record, filter)))
                            {
                                return local;
                            }

                            local[0]++;
                            if (NameContains(record, query))
                                local[1]++;
                            return local;
                        },
                        local =>
                        {
                            Interlocked.Add(ref remainingCandidates, local[0]);
                            Interlocked.Add(ref remainingTotal, local[1]);
                        });

                    candidateCount += checked((int)remainingCandidates);
                    total += checked((int)remainingTotal);
                    stopwatch.Stop();
                    return new ContainsSearchResult
                    {
                        Mode = "drive-filtered-scan-parallel",
                        CandidateCount = candidateCount,
                        Total = total,
                        Page = page,
                        VerifyMs = stopwatch.ElapsedMilliseconds
                    };
                }

                for (var i = 0; i < source.Length; i++)
                {
                    if (((i + 1) & 0x3FFF) == 0)
                        ct.ThrowIfCancellationRequested();

                    var record = source[i];
                    if (record == null
                        || char.ToUpperInvariant(record.DriveLetter) != dl
                        || (hasDeletedOverlay && IsDeleted(record))
                        || (!filterAll && !MatchesFilter(record, filter)))
                    {
                        continue;
                    }

                    candidateCount++;
                    if (!NameContains(record, query))
                        continue;

                    total++;
                    if (total > normalizedOffset && page.Count < normalizedMaxResults)
                        page.Add(record);
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            stopwatch.Stop();
            return new ContainsSearchResult
            {
                Mode = "drive-filtered-scan",
                CandidateCount = candidateCount,
                Total = total,
                Page = page,
                VerifyMs = stopwatch.ElapsedMilliseconds
            };
        }

        public void LoadSortedRecords(
            IReadOnlyList<FileRecord> sortedRecords,
            bool buildContainsAccelerator = true,
            bool takeOwnership = false,
            bool buildDerivedStructures = true,
            bool buildShortAsciiStructures = true)
        {
            var totalStopwatch = Stopwatch.StartNew();
            var arr = takeOwnership && sortedRecords is FileRecord[] ownedRecords
                ? ownedRecords
                : CopyRecords(sortedRecords);
            var verifyStopwatch = Stopwatch.StartNew();
            var sortedAlready = IsSortedByLowerName(arr);
            verifyStopwatch.Stop();
            long sortMilliseconds = 0;
            if (!sortedAlready)
            {
                var sortStopwatch = Stopwatch.StartNew();
                Array.Sort(arr, ByLowerName);
                sortStopwatch.Stop();
                sortMilliseconds = sortStopwatch.ElapsedMilliseconds;
            }

            Dictionary<string, List<FileRecord>> extensionMap;
            FileRecord[] parentArr;
            FileRecord[] directoryArr;
            FileRecord[] launchableArr;
            FileRecord[] scriptArr;
            FileRecord[] logArr;
            FileRecord[] configArr;
            if (buildDerivedStructures)
            {
                BuildDerivedStructures(
                    arr,
                    out extensionMap,
                    out parentArr,
                    out directoryArr,
                    out launchableArr,
                    out scriptArr,
                    out logArr,
                    out configArr);
            }
            else
            {
                extensionMap = BuildExtensionHashMap(arr);
                parentArr = Array.Empty<FileRecord>();
                directoryArr = Array.Empty<FileRecord>();
                launchableArr = Array.Empty<FileRecord>();
                scriptArr = Array.Empty<FileRecord>();
                logArr = Array.Empty<FileRecord>();
                configArr = Array.Empty<FileRecord>();
            }

            Publish(
                arr,
                extensionMap,
                parentArr,
                directoryArr,
                launchableArr,
                scriptArr,
                logArr,
                configArr,
                buildContainsAccelerator ? ContainsAccelerator.Build(arr, ContainsAcceleratorBucketKinds.All) : null,
                buildContainsAccelerator,
                buildDerivedStructures,
                buildShortAsciiStructures);
            totalStopwatch.Stop();
            IndexPerfLog.Write("INDEX",
                $"[LOAD SORTED RECORDS] records={arr.Length} sortedAlready={sortedAlready} " +
                $"verifyMs={verifyStopwatch.ElapsedMilliseconds} sortMs={sortMilliseconds} " +
                $"derived={buildDerivedStructures} shortAscii={buildShortAsciiStructures} totalMs={totalStopwatch.ElapsedMilliseconds}");
        }

        public void Build(IReadOnlyList<FileRecord> records, bool buildContainsAccelerator = true)
        {
            if (records == null || records.Count == 0)
            {
                Publish(
                    Array.Empty<FileRecord>(),
                    new Dictionary<string, List<FileRecord>>(),
                    Array.Empty<FileRecord>(),
                    Array.Empty<FileRecord>(),
                    Array.Empty<FileRecord>(),
                    Array.Empty<FileRecord>(),
                    Array.Empty<FileRecord>(),
                    Array.Empty<FileRecord>(),
                    ContainsAccelerator.Empty,
                    true,
                    derivedStructuresReady: true);
                return;
            }

            var arr = CopyRecords(records);
            Array.Sort(arr, ByLowerName);
            BuildDerivedStructures(
                arr,
                out var extensionMap,
                out var parentArr,
                out var directoryArr,
                out var launchableArr,
                out var scriptArr,
                out var logArr,
                out var configArr);
            Publish(
                arr,
                extensionMap,
                parentArr,
                directoryArr,
                launchableArr,
                scriptArr,
                logArr,
                configArr,
                buildContainsAccelerator ? ContainsAccelerator.Build(arr, ContainsAcceleratorBucketKinds.All) : null,
                buildContainsAccelerator,
                derivedStructuresReady: true);
        }

        private bool TryShortContainsHotSearch(
            string query,
            SearchTypeFilter filter,
            int offset,
            int maxResults,
            CancellationToken ct,
            out ContainsSearchResult result)
        {
            result = null;
            if (!IsShortContainsQuery(query))
            {
                return false;
            }

            ShortContainsHotBucket bucket;
            var contentVersion = ContentVersion;
            lock (_shortContainsHotBucketLock)
            {
                if (!_shortContainsHotBuckets.TryGetValue(query, out bucket)
                    || bucket == null
                    || bucket.ContentVersion != contentVersion)
                {
                    return false;
                }

                TouchShortContainsHotBucket(query);
            }

            var verifyStopwatch = Stopwatch.StartNew();
            var postings = bucket.Postings ?? Array.Empty<int>();
            var page = new List<FileRecord>(Math.Min(Math.Max(maxResults, 0), 64));
            var total = 0;
            var normalizedOffset = Math.Max(offset, 0);
            var normalizedMaxResults = Math.Max(maxResults, 0);
            var hasDeletedOverlay = HasDeletedOverlay;
            var filterAll = filter == SearchTypeFilter.All;
            FileRecord[] records;
            _lock.EnterReadLock();
            try
            {
                if (ContentVersion != bucket.ContentVersion)
                {
                    return false;
                }

                records = SortedArray;
            }
            finally
            {
                _lock.ExitReadLock();
            }

            for (var i = 0; i < postings.Length; i++)
            {
                if (((i + 1) & 0xFFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                var recordId = postings[i];
                var index = recordId - 1;
                if (index < 0 || index >= records.Length)
                {
                    continue;
                }

                var record = records[index];
                if (record == null
                    || (hasDeletedOverlay && IsDeleted(record))
                    || (!filterAll && !MatchesFilter(record, filter)))
                {
                    continue;
                }

                total++;
                if (total > normalizedOffset && page.Count < normalizedMaxResults)
                {
                    page.Add(record);
                }
            }

            verifyStopwatch.Stop();
            result = new ContainsSearchResult
            {
                Mode = query.Length == 1 ? "short-hot-char" : "short-hot-bigram",
                CandidateCount = postings.Length,
                Total = total,
                Page = page,
                VerifyMs = verifyStopwatch.ElapsedMilliseconds
            };
            return true;
        }

        public bool TryShortContainsHotDriveSearch(
            string query,
            char driveLetter,
            SearchTypeFilter filter,
            int offset,
            int maxResults,
            CancellationToken ct,
            out ContainsSearchResult result)
        {
            result = null;
            if (TryShortAsciiSearch(query, filter, offset, maxResults, char.ToUpperInvariant(driveLetter), ct, GetLiveOverlaySnapshot(), out result))
            {
                return true;
            }

            if (TryShortNonAsciiSearch(query, filter, offset, maxResults, char.ToUpperInvariant(driveLetter), ct, GetLiveOverlaySnapshot(), out result))
            {
                return true;
            }

            ShortContainsHotBucket bucket;
            var contentVersion = ContentVersion;
            lock (_shortContainsHotBucketLock)
            {
                if (!_shortContainsHotBuckets.TryGetValue(query, out bucket)
                    || bucket == null
                    || bucket.ContentVersion != contentVersion)
                {
                    return false;
                }

                TouchShortContainsHotBucket(query);
            }

            var verifyStopwatch = Stopwatch.StartNew();
            var postings = bucket.Postings ?? Array.Empty<int>();
            var page = new List<FileRecord>(Math.Min(Math.Max(maxResults, 0), 64));
            var total = 0;
            var candidateCount = 0;
            var normalizedOffset = Math.Max(offset, 0);
            var normalizedMaxResults = Math.Max(maxResults, 0);
            var dl = char.ToUpperInvariant(driveLetter);
            var hasDeletedOverlay = HasDeletedOverlay;
            var filterAll = filter == SearchTypeFilter.All;
            FileRecord[] records;
            _lock.EnterReadLock();
            try
            {
                if (ContentVersion != bucket.ContentVersion)
                {
                    return false;
                }

                records = SortedArray;
            }
            finally
            {
                _lock.ExitReadLock();
            }

            for (var i = 0; i < postings.Length; i++)
            {
                if (((i + 1) & 0xFFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                var recordId = postings[i];
                var index = recordId - 1;
                if (index < 0 || index >= records.Length)
                {
                    continue;
                }

                var record = records[index];
                if (record == null
                    || char.ToUpperInvariant(record.DriveLetter) != dl
                    || (hasDeletedOverlay && IsDeleted(record))
                    || (!filterAll && !MatchesFilter(record, filter)))
                {
                    continue;
                }

                candidateCount++;
                total++;
                if (total > normalizedOffset && page.Count < normalizedMaxResults)
                {
                    page.Add(record);
                }
            }

            verifyStopwatch.Stop();
            result = new ContainsSearchResult
            {
                Mode = query.Length == 1 ? "short-hot-char+drive" : "short-hot-bigram+drive",
                CandidateCount = candidateCount,
                Total = total,
                Page = page,
                VerifyMs = verifyStopwatch.ElapsedMilliseconds
            };
            return true;
        }

        private bool TryShortAsciiSearch(
            string query,
            SearchTypeFilter filter,
            int offset,
            int maxResults,
            char? driveLetter,
            CancellationToken ct,
            ContainsOverlay overlay,
            out ContainsSearchResult result)
        {
            overlay = overlay ?? ContainsOverlay.Empty;
            if (TrySingleCharBitmapSearch(query, filter, offset, maxResults, driveLetter, ct, overlay, out result)
                || TryBigramAsciiCountSearch(query, filter, offset, maxResults, driveLetter, ct, overlay, out result))
            {
                if (overlay.AddedCount > 0)
                    result.IncludesLiveOverlay = true;

                return true;
            }

            return false;
        }

        private bool TryBigramAsciiCountSearch(
            string query,
            SearchTypeFilter filter,
            int offset,
            int maxResults,
            char? driveLetter,
            CancellationToken ct,
            ContainsOverlay overlay,
            out ContainsSearchResult result)
        {
            result = null;
            if (string.IsNullOrEmpty(query)
                || query.Length != 2
                || query[0] >= BigramAsciiLimit
                || query[1] >= BigramAsciiLimit
                || filter != SearchTypeFilter.All)
            {
                return false;
            }

            var token = PackAsciiBigram(query[0], query[1]);
            FileRecord[] records;
            int recordCount;
            _lock.EnterReadLock();
            try
            {
                records = SortedArray;
                recordCount = records?.Length ?? 0;
                var counts = Volatile.Read(ref _bigramAsciiCounts);
                if (counts == null
                    || Volatile.Read(ref _bigramAsciiRecordCount) != recordCount
                    || token < 0
                    || token >= counts.Length
                    || recordCount == 0)
                {
                    return false;
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            var stopwatch = Stopwatch.StartNew();
            var hasDrive = driveLetter.HasValue;
            var dl = hasDrive ? char.ToUpperInvariant(driveLetter.Value) : '\0';
            var total = TryGetPrecomputedBigramTotal(token, dl, hasDrive, out var precomputedTotal)
                ? precomputedTotal
                : -1;
            var activeOverlay = overlay ?? ContainsOverlay.Empty;
            var hasDeletedOverlay = HasDeletedOverlay || activeOverlay.RemovedCount > 0;
            if (total >= 0 && hasDeletedOverlay)
            {
                total -= CountDeletedBigramMatches(query, hasDrive ? dl : (char?)null, activeOverlay);
                if (total < 0)
                    total = 0;
            }

            var page = new List<FileRecord>(Math.Min(Math.Max(maxResults, 0), 64));
            var normalizedOffset = Math.Max(offset, 0);
            var normalizedMaxResults = Math.Max(maxResults, 0);
            var pageMatched = 0;
            var sampleRecords = TryGetBigramPageSample(token);

            if (sampleRecords != null
                && normalizedMaxResults > 0
                && normalizedOffset + normalizedMaxResults <= sampleRecords.Length)
            {
                for (var sampleIndex = 0; sampleIndex < sampleRecords.Length; sampleIndex++)
                {
                    if (((sampleIndex + 1) & 0x3FF) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    var record = sampleRecords[sampleIndex];
                    if (record == null
                        || (hasDrive && char.ToUpperInvariant(record.DriveLetter) != dl)
                        || (hasDeletedOverlay && IsDeleted(record, activeOverlay))
                        || !NameContains(record, query))
                    {
                        continue;
                    }

                    pageMatched++;
                    if (pageMatched > normalizedOffset && page.Count < normalizedMaxResults)
                    {
                        page.Add(record);
                    }

                    if (page.Count >= normalizedMaxResults)
                    {
                        break;
                    }
                }
            }
            else if (normalizedMaxResults > 0)
            {
                for (var i = 0; i < recordCount; i++)
                {
                    if (((i + 1) & 0x3FFF) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    var record = records[i];
                    if (record == null
                        || (hasDrive && char.ToUpperInvariant(record.DriveLetter) != dl)
                        || (hasDeletedOverlay && IsDeleted(record, activeOverlay))
                        || !NameContains(record, query))
                    {
                        continue;
                    }

                    pageMatched++;
                    if (pageMatched > normalizedOffset && page.Count < normalizedMaxResults)
                    {
                        page.Add(record);
                    }

                    if (total >= 0 && page.Count >= normalizedMaxResults)
                    {
                        break;
                    }
                }
            }

            if (total < 0)
            {
                total = pageMatched;
            }

            AddShortAsciiOverlayMatches(query, filter, hasDrive ? dl : (char?)null, normalizedOffset, normalizedMaxResults, page, ref total, ct, activeOverlay);
            stopwatch.Stop();

            result = new ContainsSearchResult
            {
                Mode = driveLetter.HasValue ? "bigram-count+drive" : "bigram-count",
                CandidateCount = total,
                Total = total,
                Page = page,
                VerifyMs = stopwatch.ElapsedMilliseconds
            };
            return true;
        }

        private FileRecord[] TryGetBigramPageSample(int token)
        {
            var samples = Volatile.Read(ref _bigramAsciiPageSamples);
            if (samples == null || (uint)token >= (uint)samples.Length)
            {
                return null;
            }

            return samples[token];
        }

        private static bool IsShortContainsQuery(string query)
        {
            return query != null && (query.Length == 1 || query.Length == 2);
        }

        private bool TrySingleCharBitmapSearch(
            string query,
            SearchTypeFilter filter,
            int offset,
            int maxResults,
            char? driveLetter,
            CancellationToken ct,
            ContainsOverlay overlay,
            out ContainsSearchResult result)
        {
            result = null;
            if (string.IsNullOrEmpty(query) || query.Length != 1)
            {
                return false;
            }

            var ch = query[0];
            if (ch >= SingleCharAsciiLimit)
            {
                return false;
            }

            var activeOverlay = overlay ?? ContainsOverlay.Empty;
            int[] bitset = null;
            FileRecord[] records;
            int recordCount;
            _lock.EnterReadLock();
            try
            {
                records = SortedArray;
                recordCount = records?.Length ?? 0;
                var bitsets = Volatile.Read(ref _singleCharAsciiBitsets);
                var counts = Volatile.Read(ref _singleCharAsciiCounts);
                if (counts == null
                    || _singleCharAsciiRecordCount != recordCount
                    || recordCount == 0)
                {
                    return false;
                }

                if (bitsets != null)
                {
                    bitset = bitsets[ch];
                }

                if ((bitset == null || bitset.Length == 0) && counts[ch] == 0 && activeOverlay.AddedCount == 0)
                {
                    result = new ContainsSearchResult
                    {
                        Mode = driveLetter.HasValue ? "single-char-count+drive" : "single-char-count",
                        CandidateCount = 0,
                        Total = 0,
                        Page = new List<FileRecord>(),
                        VerifyMs = 0
                    };
                    return true;
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            var stopwatch = Stopwatch.StartNew();
            var page = new List<FileRecord>(Math.Min(Math.Max(maxResults, 0), 64));
            var candidateCount = 0;
            var normalizedOffset = Math.Max(offset, 0);
            var normalizedMaxResults = Math.Max(maxResults, 0);
            var hasDeletedOverlay = HasDeletedOverlay || activeOverlay.RemovedCount > 0;
            var filterAll = filter == SearchTypeFilter.All;
            var hasDrive = driveLetter.HasValue;
            var dl = hasDrive ? char.ToUpperInvariant(driveLetter.Value) : '\0';
            var total = TryGetPrecomputedSingleCharTotal(ch, dl, hasDrive, filter, out var precomputedTotal)
                ? precomputedTotal
                : -1;
            if (total >= 0 && hasDeletedOverlay)
            {
                total -= CountDeletedSingleCharMatches(ch, hasDrive ? dl : (char?)null, filter, activeOverlay);
                if (total < 0)
                {
                    total = 0;
                }
            }

            var pageMatched = 0;
            if (bitset != null && bitset.Length > 0)
            {
                for (var wordIndex = 0; wordIndex < bitset.Length; wordIndex++)
                {
                    if (((wordIndex + 1) & 0x3FF) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    var bits = unchecked((uint)bitset[wordIndex]);
                    while (bits != 0)
                    {
                        var bit = GetLowestSetBitIndex(bits);
                        bits &= bits - 1;
                        var index = (wordIndex << 5) + bit;
                        if ((uint)index >= (uint)recordCount)
                        {
                            continue;
                        }

                        var record = records[index];
                        if (record == null
                            || (hasDrive && char.ToUpperInvariant(record.DriveLetter) != dl)
                            || (hasDeletedOverlay && IsDeleted(record, activeOverlay))
                            || (!filterAll && !MatchesFilter(record, filter)))
                        {
                            continue;
                        }

                        candidateCount++;
                        pageMatched++;

                        if (pageMatched > normalizedOffset && page.Count < normalizedMaxResults)
                        {
                            page.Add(record);
                        }

                        if (total >= 0 && page.Count >= normalizedMaxResults)
                        {
                            break;
                        }
                    }

                    if (total >= 0 && page.Count >= normalizedMaxResults)
                    {
                        break;
                    }
                }
            }
            else
            {
                for (var index = 0; index < recordCount; index++)
                {
                    if (((index + 1) & 0x3FFF) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    var record = records[index];
                    if (record == null
                        || (hasDrive && char.ToUpperInvariant(record.DriveLetter) != dl)
                        || (hasDeletedOverlay && IsDeleted(record, activeOverlay))
                        || (!filterAll && !MatchesFilter(record, filter))
                        || !NameContains(record, query))
                    {
                        continue;
                    }

                    candidateCount++;
                    pageMatched++;
                    if (pageMatched > normalizedOffset && page.Count < normalizedMaxResults)
                    {
                        page.Add(record);
                    }

                    if (total >= 0 && page.Count >= normalizedMaxResults)
                    {
                        break;
                    }
                }
            }

            if (total < 0)
            {
                total = pageMatched;
            }

            AddShortAsciiOverlayMatches(query, filter, hasDrive ? dl : (char?)null, normalizedOffset, normalizedMaxResults, page, ref total, ct, activeOverlay);
            stopwatch.Stop();

            result = new ContainsSearchResult
            {
                Mode = bitset != null && bitset.Length > 0
                    ? (driveLetter.HasValue ? "single-char-bitmap+drive" : "single-char-bitmap")
                    : (driveLetter.HasValue ? "single-char-count+drive" : "single-char-count"),
                CandidateCount = total >= 0 ? total : candidateCount,
                Total = total,
                Page = page,
                VerifyMs = stopwatch.ElapsedMilliseconds
            };
            return true;
        }

        private bool TryGetPrecomputedBigramTotal(
            int token,
            char driveLetter,
            bool hasDrive,
            out int total)
        {
            total = 0;
            if ((uint)token >= BigramAsciiTokenCount)
            {
                return false;
            }

            if (hasDrive)
            {
                var driveCounts = Volatile.Read(ref _bigramAsciiDriveCounts);
                var driveIndex = char.ToUpperInvariant(driveLetter) - 'A';
                if (driveCounts == null
                    || driveIndex < 0
                    || driveIndex >= driveCounts.Length
                    || driveCounts[driveIndex] == null)
                {
                    return false;
                }

                total = driveCounts[driveIndex][token];
                return true;
            }

            var counts = Volatile.Read(ref _bigramAsciiCounts);
            if (counts == null || token >= counts.Length)
            {
                return false;
            }

            total = counts[token];
            return true;
        }

        private int CountDeletedBigramMatches(string query, char? driveLetter)
        {
            return CountDeletedBigramMatches(query, driveLetter, null);
        }

        private int CountDeletedBigramMatches(string query, char? driveLetter, ContainsOverlay overlay)
        {
            var keys = Volatile.Read(ref _deletedOverlayKeys);
            var overlayRemoved = overlay?.GetRemovedKeysSnapshot();
            if ((keys == null || keys.Count == 0)
                && (overlayRemoved == null || overlayRemoved.Count == 0)
                || string.IsNullOrEmpty(query)
                || query.Length != 2)
            {
                return 0;
            }

            var total = 0;
            var hasDrive = driveLetter.HasValue;
            var dl = hasDrive ? char.ToUpperInvariant(driveLetter.Value) : '\0';
            CountDeletedBigramMatches(keys, query, hasDrive, dl, null, ref total);
            CountDeletedBigramMatches(overlayRemoved, query, hasDrive, dl, keys, ref total);
            return total;
        }

        private static void CountDeletedBigramMatches(HashSet<RecordKey> keys, string query, bool hasDrive, char dl, HashSet<RecordKey> alreadyCounted, ref int total)
        {
            if (keys == null || keys.Count == 0)
                return;

            foreach (var key in keys)
            {
                if (alreadyCounted != null && alreadyCounted.Contains(key))
                    continue;

                if (key.LowerName == null
                    || key.LowerName.IndexOf(query, StringComparison.Ordinal) < 0
                    || (hasDrive && char.ToUpperInvariant(key.DriveLetter) != dl))
                {
                    continue;
                }

                total++;
            }
        }

        private bool TryGetPrecomputedSingleCharTotal(
            char ch,
            char driveLetter,
            bool hasDrive,
            SearchTypeFilter filter,
            out int total)
        {
            total = 0;
            if (filter != SearchTypeFilter.All || ch >= SingleCharAsciiLimit)
            {
                return false;
            }

            if (hasDrive)
            {
                var driveCounts = Volatile.Read(ref _singleCharAsciiDriveCounts);
                var driveIndex = char.ToUpperInvariant(driveLetter) - 'A';
                if (driveCounts == null
                    || driveIndex < 0
                    || driveIndex >= driveCounts.Length
                    || driveCounts[driveIndex] == null)
                {
                    return false;
                }

                total = driveCounts[driveIndex][ch];
                return true;
            }

            var counts = Volatile.Read(ref _singleCharAsciiCounts);
            if (counts == null || ch >= counts.Length)
            {
                return false;
            }

            total = counts[ch];
            return true;
        }

        private int CountDeletedSingleCharMatches(char ch, char? driveLetter, SearchTypeFilter filter)
        {
            return CountDeletedSingleCharMatches(ch, driveLetter, filter, null);
        }

        private int CountDeletedSingleCharMatches(char ch, char? driveLetter, SearchTypeFilter filter, ContainsOverlay overlay)
        {
            var keys = Volatile.Read(ref _deletedOverlayKeys);
            var overlayRemoved = overlay?.GetRemovedKeysSnapshot();
            if ((keys == null || keys.Count == 0)
                && (overlayRemoved == null || overlayRemoved.Count == 0))
            {
                return 0;
            }

            var total = 0;
            var filterAll = filter == SearchTypeFilter.All;
            var hasDrive = driveLetter.HasValue;
            var dl = hasDrive ? char.ToUpperInvariant(driveLetter.Value) : '\0';
            CountDeletedSingleCharMatches(keys, ch, hasDrive, dl, filterAll, null, ref total);
            CountDeletedSingleCharMatches(overlayRemoved, ch, hasDrive, dl, filterAll, keys, ref total);
            return total;
        }

        private static void CountDeletedSingleCharMatches(HashSet<RecordKey> keys, char ch, bool hasDrive, char dl, bool filterAll, HashSet<RecordKey> alreadyCounted, ref int total)
        {
            if (keys == null || keys.Count == 0)
                return;

            foreach (var key in keys)
            {
                if (alreadyCounted != null && alreadyCounted.Contains(key))
                    continue;

                if (key.LowerName == null
                    || key.LowerName.IndexOf(ch) < 0
                    || (hasDrive && char.ToUpperInvariant(key.DriveLetter) != dl))
                {
                    continue;
                }

                if (!filterAll)
                {
                    continue;
                }

                total++;
            }
        }

        private bool IsDeleted(FileRecord record, ContainsOverlay overlay)
        {
            if (record == null)
                return false;

            var key = RecordKey.FromRecord(record);
            return IsDeleted(record)
                   || (overlay != null && overlay.ContainsRemoved(key));
        }

        private static void AddShortAsciiOverlayMatches(
            string query,
            SearchTypeFilter filter,
            char? driveLetter,
            int offset,
            int maxResults,
            List<FileRecord> page,
            ref int total,
            CancellationToken ct,
            ContainsOverlay overlay)
        {
            if (overlay == null || overlay.AddedRecords.Count == 0)
                return;

            var hasDrive = driveLetter.HasValue;
            var dl = hasDrive ? char.ToUpperInvariant(driveLetter.Value) : '\0';
            for (var i = 0; i < overlay.AddedRecords.Count; i++)
            {
                if (((i + 1) & 0x7FF) == 0)
                    ct.ThrowIfCancellationRequested();

                var record = overlay.AddedRecords[i];
                if (record == null
                    || overlay.ContainsRemoved(RecordKey.FromRecord(record))
                    || (hasDrive && char.ToUpperInvariant(record.DriveLetter) != dl)
                    || !NameContains(record, query)
                    || !MatchesFilter(record, filter))
                {
                    continue;
                }

                total++;
                if (total > offset && page.Count < maxResults)
                    page.Add(record);
            }
        }

        private static int GetLowestSetBitIndex(uint value)
        {
            var index = 0;
            while ((value & 1u) == 0u)
            {
                value >>= 1;
                index++;
            }

            return index;
        }

        private static int[] BuildShortContainsPostings(FileRecord[] records, string query, CancellationToken ct)
        {
            if (records == null || records.Length == 0 || string.IsNullOrEmpty(query))
            {
                return Array.Empty<int>();
            }

            var postings = new List<int>(Math.Min(records.Length, 262144));
            for (var i = 0; i < records.Length; i++)
            {
                if (((i + 1) & 0xFFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                if (NameContains(records[i], query))
                {
                    postings.Add(i + 1);
                }
            }

            return postings.Count == 0 ? Array.Empty<int>() : postings.ToArray();
        }

        private static bool NameContains(FileRecord record, string query)
        {
            return record != null
                   && !string.IsNullOrEmpty(record.LowerName)
                   && !string.IsNullOrEmpty(query)
                   && record.LowerName.IndexOf(query, StringComparison.Ordinal) >= 0;
        }

        private static bool IsSortedByLowerName(FileRecord[] records)
        {
            if (records == null || records.Length < 2)
            {
                return true;
            }

            for (var i = 1; i < records.Length; i++)
            {
                if (ByLowerName.Compare(records[i - 1], records[i]) > 0)
                {
                    return false;
                }
            }

            return true;
        }

        private void TouchShortContainsHotBucket(string query)
        {
            var node = _shortContainsHotBucketLru.Find(query);
            if (node != null)
            {
                _shortContainsHotBucketLru.Remove(node);
            }

            _shortContainsHotBucketLru.AddFirst(query);
        }

        private void TrimShortContainsHotBuckets()
        {
            var totalPostings = 0;
            foreach (var bucket in _shortContainsHotBuckets.Values)
            {
                totalPostings += bucket?.Postings?.Length ?? 0;
            }

            while (_shortContainsHotBuckets.Count > MaxShortContainsHotBuckets
                   || totalPostings > MaxShortContainsHotBucketPostings)
            {
                var last = _shortContainsHotBucketLru.Last;
                if (last == null)
                {
                    _shortContainsHotBuckets.Clear();
                    break;
                }

                var key = last.Value;
                _shortContainsHotBucketLru.RemoveLast();
                ShortContainsHotBucket removed;
                if (_shortContainsHotBuckets.TryGetValue(key, out removed))
                {
                    totalPostings -= removed?.Postings?.Length ?? 0;
                    _shortContainsHotBuckets.Remove(key);
                    IndexPerfLog.Write("INDEX",
                        $"[CONTAINS SHORT HOT EVICT] query={IndexPerfLog.FormatValue(key)} postings={removed?.Postings?.Length ?? 0}");
                }
            }
        }

        private void ClearShortContainsHotBuckets()
        {
            lock (_shortContainsHotBucketLock)
            {
                _shortContainsHotBuckets.Clear();
                _shortContainsHotBucketLru.Clear();
            }
        }

        private void ClearShortAsciiIndexes()
        {
            _singleCharAsciiBitsets = null;
            _singleCharAsciiCounts = null;
            _singleCharAsciiDriveCounts = null;
            _singleCharAsciiRecordCount = 0;
            _bigramAsciiCounts = null;
            _bigramAsciiDriveCounts = null;
            _bigramAsciiPageSamples = null;
            _bigramAsciiRecordCount = 0;
            _nonAsciiShortQueryIndex = NonAsciiShortQueryIndex.Empty;
        }

        private void AddShortAsciiIndexesRecord(FileRecord record)
        {
            UpdateShortAsciiIndexesRecord(record, 1);
        }

        private void RemoveShortAsciiIndexesRecord(FileRecord record)
        {
            UpdateShortAsciiIndexesRecord(record, -1);
        }

        private void UpdateShortAsciiIndexesRecord(FileRecord record, int delta)
        {
            if (record == null || string.IsNullOrEmpty(record.LowerName) || (delta != 1 && delta != -1))
            {
                return;
            }

            UpdateSingleCharAsciiIndexForRecord(record, delta);
            UpdateBigramAsciiIndexForRecord(record, delta);
        }

        private void UpdateSingleCharAsciiIndexForRecord(FileRecord record, int delta)
        {
            var counts = _singleCharAsciiCounts;
            var driveCounts = _singleCharAsciiDriveCounts;
            if (counts == null || driveCounts == null)
            {
                return;
            }

            var lowerName = record.LowerName;
            ulong maskLow = 0;
            ulong maskHigh = 0;
            for (var i = 0; i < lowerName.Length; i++)
            {
                var ch = lowerName[i];
                if (ch >= SingleCharAsciiLimit)
                {
                    continue;
                }

                if (ch < 64)
                {
                    maskLow |= 1UL << ch;
                }
                else
                {
                    maskHigh |= 1UL << (ch - 64);
                }
            }

            var driveIndex = char.ToUpperInvariant(record.DriveLetter) - 'A';
            ApplySingleCharMaskDelta(counts, driveCounts, driveIndex, maskLow, 0, delta);
            ApplySingleCharMaskDelta(counts, driveCounts, driveIndex, maskHigh, 64, delta);
            _singleCharAsciiRecordCount = Math.Max(0, _singleCharAsciiRecordCount + delta);
        }

        private static void ApplySingleCharMaskDelta(int[] counts, int[][] driveCounts, int driveIndex, ulong mask, int offset, int delta)
        {
            while (mask != 0)
            {
                var bit = GetLowestSetBitIndex(mask);
                mask &= mask - 1;
                var ch = offset + bit;
                counts[ch] = Math.Max(0, counts[ch] + delta);
                if (driveIndex >= 0 && driveIndex < driveCounts.Length && driveCounts[driveIndex] != null)
                {
                    driveCounts[driveIndex][ch] = Math.Max(0, driveCounts[driveIndex][ch] + delta);
                }
            }
        }

        private void UpdateBigramAsciiIndexForRecord(FileRecord record, int delta)
        {
            var counts = _bigramAsciiCounts;
            var driveCounts = _bigramAsciiDriveCounts;
            if (counts == null || driveCounts == null)
            {
                return;
            }

            var tokens = GetUniqueAsciiBigrams(record.LowerName);
            if (tokens == null || tokens.Count == 0)
            {
                _bigramAsciiRecordCount = Math.Max(0, _bigramAsciiRecordCount + delta);
                return;
            }

            var driveIndex = char.ToUpperInvariant(record.DriveLetter) - 'A';
            var samples = _bigramAsciiPageSamples;
            foreach (var token in tokens)
            {
                var oldCount = counts[token];
                var newCount = Math.Max(0, oldCount + delta);
                counts[token] = newCount;
                if (driveIndex >= 0 && driveIndex < driveCounts.Length && driveCounts[driveIndex] != null)
                {
                    driveCounts[driveIndex][token] = Math.Max(0, driveCounts[driveIndex][token] + delta);
                }

                if (samples != null)
                {
                    UpdateBigramAsciiPageSample(samples, token, record, delta, oldCount, newCount);
                }
            }

            _bigramAsciiRecordCount = Math.Max(0, _bigramAsciiRecordCount + delta);
        }

        private static List<int> GetUniqueAsciiBigrams(string lowerName)
        {
            if (string.IsNullOrEmpty(lowerName) || lowerName.Length < 2)
            {
                return null;
            }

            var tokens = new List<int>(Math.Min(lowerName.Length - 1, 16));
            var seen = new HashSet<int>();
            for (var i = 0; i < lowerName.Length - 1; i++)
            {
                var first = lowerName[i];
                var second = lowerName[i + 1];
                if (first >= BigramAsciiLimit || second >= BigramAsciiLimit)
                {
                    continue;
                }

                var token = PackAsciiBigram(first, second);
                if (seen.Add(token))
                {
                    tokens.Add(token);
                }
            }

            return tokens;
        }

        private static void UpdateBigramAsciiPageSample(
            FileRecord[][] samples,
            int token,
            FileRecord record,
            int delta,
            int oldCount,
            int newCount)
        {
            if ((uint)token >= (uint)samples.Length)
            {
                return;
            }

            var current = samples[token];
            if (delta > 0)
            {
                if (newCount > BigramAsciiPageSampleLimit)
                {
                    samples[token] = null;
                    return;
                }

                if (current == null)
                {
                    samples[token] = new[] { record };
                    return;
                }

                if (ContainsRecordIdentity(current, record))
                {
                    return;
                }

                var next = new FileRecord[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[next.Length - 1] = record;
                Array.Sort(next, ByLowerName);
                samples[token] = next;
                return;
            }

            if (current == null || oldCount > BigramAsciiPageSampleLimit)
            {
                return;
            }

            var removeAt = -1;
            for (var i = 0; i < current.Length; i++)
            {
                if (IsRecordIdentityMatch(current[i], record.Frn, record.LowerName, record.ParentFrn, record.DriveLetter))
                {
                    removeAt = i;
                    break;
                }
            }

            if (removeAt < 0)
            {
                return;
            }

            if (current.Length == 1)
            {
                samples[token] = null;
                return;
            }

            var updated = new FileRecord[current.Length - 1];
            if (removeAt > 0)
            {
                Array.Copy(current, 0, updated, 0, removeAt);
            }

            if (removeAt < current.Length - 1)
            {
                Array.Copy(current, removeAt + 1, updated, removeAt, current.Length - removeAt - 1);
            }

            samples[token] = updated;
        }

        private static bool ContainsRecordIdentity(FileRecord[] records, FileRecord record)
        {
            if (records == null || record == null)
            {
                return false;
            }

            for (var i = 0; i < records.Length; i++)
            {
                if (IsRecordIdentityMatch(records[i], record.Frn, record.LowerName, record.ParentFrn, record.DriveLetter))
                {
                    return true;
                }
            }

            return false;
        }

        public void Insert(FileRecord record)
        {
            _lock.EnterWriteLock();
            try
            {
                if (TryGetIndexedExtension(record.LowerName, out var extension))
                {
                    if (!ExtensionHashMap.TryGetValue(extension, out var extensionBucket))
                        ExtensionHashMap[extension] = extensionBucket = new List<FileRecord>();
                    InsertIntoSortedBucket(extensionBucket, record);
                }

                InsertIntoFilterBuckets(record);
                if (_containsAcceleratorReady)
                {
                    AppendContainsOverlay(PendingContainsMutation.ForInsert(record));
                }
                else
                {
                    EnqueuePendingContainsInsert(record);
                }

                var arr = SortedArray;
                var idx = Array.BinarySearch(arr, record, ByLowerName);
                if (idx < 0) idx = ~idx;
                var newArr = new FileRecord[arr.Length + 1];
                Array.Copy(arr, 0, newArr, 0, idx);
                newArr[idx] = record;
                Array.Copy(arr, idx, newArr, idx + 1, arr.Length - idx);
                SortedArray = newArr;
                ParentSortedArray = InsertIntoSortedArray(ParentSortedArray, record, ByParentThenLowerName);
                RemoveDeletedOverlayKey(RecordKey.FromRecord(record));
                Interlocked.Increment(ref _contentVersion);
                ClearShortContainsHotBuckets();
                AddShortAsciiIndexesRecord(record);
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>
        /// 移除记录。优先按 FRN 精确匹配；若 FRN 不可用，再退化到 (lowerName, parentFrn, driveLetter)。
        /// </summary>
        public void Remove(ulong frn, string lowerName, ulong parentFrn, char driveLetter)
        {
            _lock.EnterWriteLock();
            try
            {
                var key = new RecordKey(frn, lowerName, parentFrn, driveLetter);
                var addedToOverlay = AddDeletedOverlayKey(key);
                if (_containsAcceleratorReady)
                {
                    AppendContainsOverlay(PendingContainsMutation.ForRemove(key));
                }
                else
                {
                    EnqueuePendingContainsRemove(key);
                }

                if (addedToOverlay)
                    Interlocked.Increment(ref _contentVersion);
                ClearShortContainsHotBuckets();
                IndexPerfLog.Write("INDEX",
                    $"[DELETE OVERLAY] source=remove added={addedToOverlay} overlayCount={DeletedOverlayCount} " +
                    $"drive={char.ToUpperInvariant(driveLetter)} frn={frn} parentFrn={parentFrn} lowerName={IndexPerfLog.FormatValue(lowerName)}");
            }
            finally { _lock.ExitWriteLock(); }
        }

        public bool MarkDeleted(FileRecord record, string source)
        {
            if (record == null)
                return false;

            return MarkDeleted(record.Frn, record.LowerName, record.ParentFrn, record.DriveLetter, record.IsDirectory, source);
        }

        public bool MarkDeleted(ulong frn, string lowerName, ulong parentFrn, char driveLetter, string source)
        {
            return MarkDeleted(frn, lowerName, parentFrn, driveLetter, false, source);
        }

        public bool MarkDeleted(ulong frn, string lowerName, ulong parentFrn, char driveLetter, bool isDirectory, string source)
        {
            var result = ApplyLiveDeltaBatch(new[]
            {
                new UsnChangeEntry(
                    UsnChangeKind.Delete,
                    frn,
                    lowerName,
                    lowerName,
                    parentFrn,
                    driveLetter,
                    isDirectory)
            });

            var applied = result.Deleted > 0 || result.Restored > 0 || result.Inserted > 0;
            IndexPerfLog.Write("INDEX",
                $"[DELETE OVERLAY] source={IndexPerfLog.FormatValue(source)} added={applied} overlayCount={DeletedOverlayCount} " +
                $"drive={char.ToUpperInvariant(driveLetter)} frn={frn} parentFrn={parentFrn} lowerName={IndexPerfLog.FormatValue(lowerName)} " +
                $"inserted={result.Inserted} deleted={result.Deleted} restored={result.Restored} overlayAdds={result.OverlayAdds} overlayDeletes={result.OverlayDeletes}");
            return applied;
        }

        public bool TryRestoreDeleted(FileRecord record, string source)
        {
            if (record == null)
                return false;

            var result = ApplyLiveDeltaBatch(new[]
            {
                new UsnChangeEntry(
                    UsnChangeKind.Create,
                    record.Frn,
                    record.LowerName,
                    record.OriginalName,
                    record.ParentFrn,
                    record.DriveLetter,
                    record.IsDirectory)
            });

            var restored = result.Restored > 0 || result.Inserted > 0;
            IndexPerfLog.Write("INDEX",
                $"[DELETE OVERLAY RESTORE] source={IndexPerfLog.FormatValue(source)} outcome={(restored ? "restored-tombstone" : "not-deleted")} " +
                $"overlayCount={DeletedOverlayCount} drive={char.ToUpperInvariant(record.DriveLetter)} frn={record.Frn} " +
                $"parentFrn={record.ParentFrn} lowerName={IndexPerfLog.FormatValue(record.LowerName)}");
            return restored;
        }

        public FileRecord[] FindByLowerName(string lowerName)
        {
            if (string.IsNullOrEmpty(lowerName))
                return Array.Empty<FileRecord>();

            _lock.EnterReadLock();
            try
            {
                var arr = SortedArray;
                var start = LowerBoundByLowerName(arr, lowerName);
                var end = start;
                while (end < arr.Length && string.CompareOrdinal(arr[end]?.LowerName, lowerName) == 0)
                    end++;

                if (end <= start)
                    return Array.Empty<FileRecord>();

                var result = new FileRecord[end - start];
                Array.Copy(arr, start, result, 0, result.Length);
                return result;
            }
            finally { _lock.ExitReadLock(); }
        }

        public void Rename(ulong frn, string oldLowerName, ulong oldParentFrn, char driveLetter, FileRecord newRecord)
        {
            _lock.EnterWriteLock();
            try
            {
                if (TryGetIndexedExtension(oldLowerName, out var oldExtension)
                    && ExtensionHashMap.TryGetValue(oldExtension, out var oldExtensionBucket))
                {
                    RemoveFromBucket(oldExtensionBucket, frn, oldLowerName, oldParentFrn, driveLetter);
                    if (oldExtensionBucket.Count == 0) ExtensionHashMap.Remove(oldExtension);
                }

                RemoveFromFilterBuckets(frn, oldLowerName, oldParentFrn, driveLetter);
                ParentSortedArray = RemoveFromSortedArray(ParentSortedArray, frn, oldLowerName, oldParentFrn, driveLetter);

                var arr = SortedArray;
                for (var i = 0; i < arr.Length; i++)
                {
                    var match = IsRecordIdentityMatch(arr[i], frn, oldLowerName, oldParentFrn, driveLetter);
                    if (match)
                    {
                        var tmp = new FileRecord[arr.Length - 1];
                        Array.Copy(arr, 0, tmp, 0, i);
                        Array.Copy(arr, i + 1, tmp, i, arr.Length - i - 1);
                        arr = tmp;
                        break;
                    }
                }

                if (TryGetIndexedExtension(newRecord.LowerName, out var newExtension))
                {
                    if (!ExtensionHashMap.TryGetValue(newExtension, out var newExtensionBucket))
                        ExtensionHashMap[newExtension] = newExtensionBucket = new List<FileRecord>();
                    InsertIntoSortedBucket(newExtensionBucket, newRecord);
                }

                InsertIntoFilterBuckets(newRecord);
                if (_containsAcceleratorReady)
                {
                    AppendContainsOverlay(PendingContainsMutation.ForRemove(new RecordKey(frn, oldLowerName, oldParentFrn, driveLetter)));
                    AppendContainsOverlay(PendingContainsMutation.ForInsert(newRecord));
                }
                else
                {
                    EnqueuePendingContainsRemove(new RecordKey(frn, oldLowerName, oldParentFrn, driveLetter));
                    EnqueuePendingContainsInsert(newRecord);
                }

                var insertIdx = Array.BinarySearch(arr, newRecord, ByLowerName);
                if (insertIdx < 0) insertIdx = ~insertIdx;
                var newArr = new FileRecord[arr.Length + 1];
                Array.Copy(arr, 0, newArr, 0, insertIdx);
                newArr[insertIdx] = newRecord;
                Array.Copy(arr, insertIdx, newArr, insertIdx + 1, arr.Length - insertIdx);
                SortedArray = newArr;
                ParentSortedArray = InsertIntoSortedArray(ParentSortedArray, newRecord, ByParentThenLowerName);
                Interlocked.Increment(ref _contentVersion);
                ClearShortContainsHotBuckets();
                RemoveShortAsciiIndexesRecord(new FileRecord(oldLowerName, oldLowerName, oldParentFrn, driveLetter, false, frn));
                AddShortAsciiIndexesRecord(newRecord);
            }
            finally { _lock.ExitWriteLock(); }
        }

        private static FileRecord[] CopyRecords(IReadOnlyList<FileRecord> records)
        {
            if (records == null || records.Count == 0)
                return Array.Empty<FileRecord>();

            var arr = new FileRecord[records.Count];
            var stringPool = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < records.Count; i++)
            {
                arr[i] = CopyRecordWithPooledStrings(records[i], stringPool);
            }

            return arr;
        }

        private static FileRecord CopyRecordWithPooledStrings(FileRecord record, Dictionary<string, string> stringPool)
        {
            if (record == null)
                return null;

            var lowerName = PoolString(record.LowerName, stringPool);
            var originalName = string.Equals(record.OriginalName, lowerName, StringComparison.Ordinal)
                ? lowerName
                : PoolString(record.OriginalName, stringPool);
            return new FileRecord(
                lowerName,
                originalName,
                record.ParentFrn,
                record.DriveLetter,
                record.IsDirectory,
                record.Frn);
        }

        private static string PoolString(string value, Dictionary<string, string> stringPool)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            if (stringPool.TryGetValue(value, out var pooled))
                return pooled;

            stringPool[value] = value;
            return value;
        }

        private static int CompareByParentThenLowerName(FileRecord a, FileRecord b)
        {
            if (ReferenceEquals(a, b))
                return 0;
            if (a == null)
                return -1;
            if (b == null)
                return 1;

            var driveCompare = char.ToUpperInvariant(a.DriveLetter)
                .CompareTo(char.ToUpperInvariant(b.DriveLetter));
            if (driveCompare != 0)
                return driveCompare;

            var parentCompare = a.ParentFrn.CompareTo(b.ParentFrn);
            if (parentCompare != 0)
                return parentCompare;

            var nameCompare = string.CompareOrdinal(a.LowerName, b.LowerName);
            if (nameCompare != 0)
                return nameCompare;

            return a.Frn.CompareTo(b.Frn);
        }

        private static int LowerBoundByParent(FileRecord[] parentArr, char driveLetter, ulong parentFrn)
        {
            var lo = 0;
            var hi = parentArr?.Length ?? 0;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) / 2);
                var record = parentArr[mid];
                var compare = CompareParentToKey(record, driveLetter, parentFrn);
                if (compare < 0)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
        }

        private static int CompareParentToKey(FileRecord record, char driveLetter, ulong parentFrn)
        {
            if (record == null)
                return -1;

            var driveCompare = char.ToUpperInvariant(record.DriveLetter).CompareTo(driveLetter);
            if (driveCompare != 0)
                return driveCompare;

            return record.ParentFrn.CompareTo(parentFrn);
        }

        private static bool IsSameParent(FileRecord record, char driveLetter, ulong parentFrn)
        {
            return record != null
                   && char.ToUpperInvariant(record.DriveLetter) == driveLetter
                   && record.ParentFrn == parentFrn;
        }

        private static Dictionary<string, List<FileRecord>> BuildExtensionHashMap(FileRecord[] arr)
        {
            var map = new Dictionary<string, List<FileRecord>>();
            foreach (var record in arr)
            {
                if (!TryGetIndexedExtension(record?.LowerName, out var extension))
                    continue;

                if (!map.TryGetValue(extension, out var bucket))
                    map[extension] = bucket = new List<FileRecord>();
                bucket.Add(record);
            }

            return map;
        }

        private static void BuildDerivedStructures(
            FileRecord[] arr,
            out Dictionary<string, List<FileRecord>> extensionMap,
            out FileRecord[] parentArr,
            out FileRecord[] directoryArr,
            out FileRecord[] launchableArr,
            out FileRecord[] scriptArr,
            out FileRecord[] logArr,
            out FileRecord[] configArr)
        {
            if (arr == null || arr.Length == 0)
            {
                extensionMap = new Dictionary<string, List<FileRecord>>();
                parentArr = Array.Empty<FileRecord>();
                directoryArr = Array.Empty<FileRecord>();
                launchableArr = Array.Empty<FileRecord>();
                scriptArr = Array.Empty<FileRecord>();
                logArr = Array.Empty<FileRecord>();
                configArr = Array.Empty<FileRecord>();
                return;
            }

            extensionMap = new Dictionary<string, List<FileRecord>>();
            var directories = new List<FileRecord>();
            var launchables = new List<FileRecord>();
            var scripts = new List<FileRecord>();
            var logs = new List<FileRecord>();
            var configs = new List<FileRecord>();
            parentArr = new FileRecord[arr.Length];
            Array.Copy(arr, parentArr, arr.Length);
            Array.Sort(parentArr, ByParentThenLowerName);

            for (var i = 0; i < arr.Length; i++)
            {
                var record = arr[i];
                if (TryGetIndexedExtension(record.LowerName, out var extension))
                {
                    if (!extensionMap.TryGetValue(extension, out var extensionBucket))
                    {
                        extensionBucket = new List<FileRecord>();
                        extensionMap[extension] = extensionBucket;
                    }

                    extensionBucket.Add(record);
                }

                if (record.IsDirectory)
                {
                    directories.Add(record);
                    continue;
                }

                if (TryGetIndexedExtension(record.LowerName, out extension))
                {
                    if (SearchTypeFilterHelper.IsLaunchableExtension(extension))
                    {
                        launchables.Add(record);
                    }

                    if (SearchTypeFilterHelper.IsScriptExtension(extension))
                    {
                        scripts.Add(record);
                    }

                    if (SearchTypeFilterHelper.IsLogExtension(extension))
                    {
                        logs.Add(record);
                    }

                    if (SearchTypeFilterHelper.IsConfigExtension(extension))
                    {
                        configs.Add(record);
                    }
                }
            }

            directoryArr = directories.Count == 0 ? Array.Empty<FileRecord>() : directories.ToArray();
            launchableArr = launchables.Count == 0 ? Array.Empty<FileRecord>() : launchables.ToArray();
            scriptArr = scripts.Count == 0 ? Array.Empty<FileRecord>() : scripts.ToArray();
            logArr = logs.Count == 0 ? Array.Empty<FileRecord>() : logs.ToArray();
            configArr = configs.Count == 0 ? Array.Empty<FileRecord>() : configs.ToArray();
        }

        private static FileRecord[] BuildFilteredArray(FileRecord[] arr, SearchTypeFilter filter)
        {
            if (arr == null || arr.Length == 0)
                return Array.Empty<FileRecord>();

            var filtered = new List<FileRecord>();
            for (var i = 0; i < arr.Length; i++)
            {
                if (MatchesFilter(arr[i], filter))
                    filtered.Add(arr[i]);
            }

            return filtered.Count == 0 ? Array.Empty<FileRecord>() : filtered.ToArray();
        }

        private sealed class SingleCharAsciiIndex
        {
            public int[][] Bitsets { get; set; }
            public int[] Counts { get; set; }
            public int[][] DriveCounts { get; set; }
        }

        private sealed class BigramAsciiIndex
        {
            public int[] Counts { get; set; }
            public int[][] DriveCounts { get; set; }
            public FileRecord[][] PageSamples { get; set; }
        }

        private sealed class SingleCharAsciiCountLocal
        {
            public SingleCharAsciiCountLocal()
            {
                Counts = new int[SingleCharAsciiLimit];
                DriveCounts = new int[26][];
                for (var i = 0; i < DriveCounts.Length; i++)
                {
                    DriveCounts[i] = new int[SingleCharAsciiLimit];
                }
            }

            public int[] Counts { get; }
            public int[][] DriveCounts { get; }
        }

        private sealed class BigramAsciiCountLocal
        {
            private int _seenGeneration;

            public BigramAsciiCountLocal()
            {
                Counts = new int[BigramAsciiTokenCount];
                DriveCounts = new int[26][];
                Seen = new int[BigramAsciiTokenCount];
                for (var i = 0; i < DriveCounts.Length; i++)
                {
                    DriveCounts[i] = new int[BigramAsciiTokenCount];
                }
            }

            public int[] Counts { get; }
            public int[][] DriveCounts { get; }
            public int[] Seen { get; }

            public int NextSeenGeneration()
            {
                _seenGeneration++;
                if (_seenGeneration == int.MaxValue)
                {
                    Array.Clear(Seen, 0, Seen.Length);
                    _seenGeneration = 1;
                }

                return _seenGeneration;
            }
        }

        private static SingleCharAsciiIndex BuildSingleCharAsciiIndex(FileRecord[] arr)
        {
            if (arr == null || arr.Length == 0)
            {
                return new SingleCharAsciiIndex
                {
                    Bitsets = null,
                    Counts = null,
                    DriveCounts = null
                };
            }

            var stopwatch = Stopwatch.StartNew();
            var counts = new int[SingleCharAsciiLimit];
            var driveCounts = new int[26][];
            for (var i = 0; i < driveCounts.Length; i++)
            {
                driveCounts[i] = new int[SingleCharAsciiLimit];
            }
            var mergeLock = new object();

            Parallel.For(
                0,
                arr.Length,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
                () => new SingleCharAsciiCountLocal(),
                (recordIndex, state, local) =>
                {
                    var record = arr[recordIndex];
                    var lowerName = record?.LowerName;
                    if (string.IsNullOrEmpty(lowerName))
                    {
                        return local;
                    }

                    ulong maskLow = 0;
                    ulong maskHigh = 0;
                    for (var j = 0; j < lowerName.Length; j++)
                    {
                        var ch = lowerName[j];
                        if (ch >= SingleCharAsciiLimit)
                        {
                            continue;
                        }

                        if (ch < 64)
                        {
                            maskLow |= 1UL << ch;
                        }
                        else
                        {
                            maskHigh |= 1UL << (ch - 64);
                        }
                    }

                    var driveIndex = char.ToUpperInvariant(record.DriveLetter) - 'A';
                    CountSingleCharMaskBits(local.Counts, local.DriveCounts, driveIndex, maskLow, 0);
                    CountSingleCharMaskBits(local.Counts, local.DriveCounts, driveIndex, maskHigh, 64);
                    return local;
                },
                local =>
                {
                    lock (mergeLock)
                    {
                        for (var i = 0; i < SingleCharAsciiLimit; i++)
                        {
                            counts[i] += local.Counts[i];
                        }

                        for (var drive = 0; drive < driveCounts.Length; drive++)
                        {
                            for (var i = 0; i < SingleCharAsciiLimit; i++)
                            {
                                driveCounts[drive][i] += local.DriveCounts[drive][i];
                            }
                        }
                    }
                });

            stopwatch.Stop();
            IndexPerfLog.Write("INDEX",
                $"[CONTAINS SINGLE CHAR COUNT BUILD] outcome=success records={arr.Length} chars={SingleCharAsciiLimit} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return new SingleCharAsciiIndex
            {
                Bitsets = null,
                Counts = counts,
                DriveCounts = driveCounts
            };
        }

        private static BigramAsciiIndex BuildBigramAsciiIndex(FileRecord[] arr)
        {
            if (arr == null || arr.Length == 0)
            {
                return new BigramAsciiIndex
                {
                    Counts = null,
                    DriveCounts = null,
                    PageSamples = null
                };
            }

            var stopwatch = Stopwatch.StartNew();
            var counts = new int[BigramAsciiTokenCount];
            var driveCounts = new int[26][];
            for (var i = 0; i < driveCounts.Length; i++)
            {
                driveCounts[i] = new int[BigramAsciiTokenCount];
            }
            var mergeLock = new object();

            Parallel.For(
                0,
                arr.Length,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
                () => new BigramAsciiCountLocal(),
                (recordIndex, state, local) =>
                {
                    var record = arr[recordIndex];
                    var lowerName = record?.LowerName;
                    if (string.IsNullOrEmpty(lowerName) || lowerName.Length < 2)
                    {
                        return local;
                    }

                    var generation = local.NextSeenGeneration();
                    var driveIndex = char.ToUpperInvariant(record.DriveLetter) - 'A';
                    for (var j = 0; j < lowerName.Length - 1; j++)
                    {
                        var first = lowerName[j];
                        var second = lowerName[j + 1];
                        if (first >= BigramAsciiLimit || second >= BigramAsciiLimit)
                        {
                            continue;
                        }

                        var token = PackAsciiBigram(first, second);
                        if (local.Seen[token] == generation)
                        {
                            continue;
                        }

                        local.Seen[token] = generation;
                        local.Counts[token]++;
                        if (driveIndex >= 0 && driveIndex < local.DriveCounts.Length)
                        {
                            local.DriveCounts[driveIndex][token]++;
                        }
                    }

                    return local;
                },
                local =>
                {
                    lock (mergeLock)
                    {
                        for (var i = 0; i < BigramAsciiTokenCount; i++)
                        {
                            counts[i] += local.Counts[i];
                        }

                        for (var drive = 0; drive < driveCounts.Length; drive++)
                        {
                            for (var i = 0; i < BigramAsciiTokenCount; i++)
                            {
                                driveCounts[drive][i] += local.DriveCounts[drive][i];
                            }
                        }
                    }
                });

            var samples = BuildBigramAsciiPageSamples(arr, counts);
            stopwatch.Stop();
            IndexPerfLog.Write("INDEX",
                $"[CONTAINS BIGRAM COUNT BUILD] outcome=success records={arr.Length} tokens={BigramAsciiTokenCount} sampleLimit={BigramAsciiPageSampleLimit} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return new BigramAsciiIndex
            {
                Counts = counts,
                DriveCounts = driveCounts,
                PageSamples = samples
            };
        }

        private static void BuildShortAsciiIndexes(
            FileRecord[] arr,
            out SingleCharAsciiIndex singleCharAsciiIndex,
            out BigramAsciiIndex bigramAsciiIndex,
            out NonAsciiShortQueryIndex nonAsciiShortQueryIndex)
        {
            BuildShortQueryIndexes(arr, out singleCharAsciiIndex, out bigramAsciiIndex, out nonAsciiShortQueryIndex);
        }

        private static FileRecord[][] BuildBigramAsciiPageSamples(FileRecord[] arr, int[] counts)
        {
            if (arr == null || arr.Length == 0 || counts == null || counts.Length != BigramAsciiTokenCount)
            {
                return null;
            }

            var workerCount = Math.Max(1, Math.Min(Math.Max(1, Environment.ProcessorCount - 1), arr.Length / 32768));
            if (workerCount <= 1)
            {
                return BuildBigramAsciiPageSamplesRange(arr, counts, 0, arr.Length);
            }

            var localSamples = new List<FileRecord>[workerCount][];
            Parallel.For(0, workerCount, new ParallelOptions { MaxDegreeOfParallelism = workerCount }, worker =>
            {
                var start = (arr.Length * worker) / workerCount;
                var end = (arr.Length * (worker + 1)) / workerCount;
                localSamples[worker] = BuildBigramAsciiPageSampleListsRange(arr, counts, start, end);
            });

            var samples = new FileRecord[BigramAsciiTokenCount][];
            for (var token = 0; token < BigramAsciiTokenCount; token++)
            {
                if (counts[token] <= 0 || counts[token] > BigramAsciiPageSampleLimit)
                {
                    continue;
                }

                var merged = new List<FileRecord>(Math.Min(counts[token], BigramAsciiPageSampleLimit));
                for (var worker = 0; worker < localSamples.Length && merged.Count < BigramAsciiPageSampleLimit; worker++)
                {
                    var local = localSamples[worker]?[token];
                    if (local == null || local.Count == 0)
                    {
                        continue;
                    }

                    var remaining = BigramAsciiPageSampleLimit - merged.Count;
                    if (local.Count <= remaining)
                    {
                        merged.AddRange(local);
                    }
                    else
                    {
                        for (var i = 0; i < remaining; i++)
                        {
                            merged.Add(local[i]);
                        }
                    }
                }

                if (merged.Count > 0)
                {
                    samples[token] = merged.ToArray();
                }
            }

            return samples;
        }

        private static FileRecord[][] BuildBigramAsciiPageSamplesRange(FileRecord[] arr, int[] counts, int start, int end)
        {
            var lists = BuildBigramAsciiPageSampleListsRange(arr, counts, start, end);
            var samples = new FileRecord[BigramAsciiTokenCount][];
            for (var token = 0; token < lists.Length; token++)
            {
                var list = lists[token];
                if (list != null && list.Count > 0)
                {
                    samples[token] = list.ToArray();
                }
            }

            return samples;
        }

        private static List<FileRecord>[] BuildBigramAsciiPageSampleListsRange(FileRecord[] arr, int[] counts, int start, int end)
        {
            var lists = new List<FileRecord>[BigramAsciiTokenCount];
            var seen = new int[BigramAsciiTokenCount];
            var generation = 0;
            for (var recordIndex = start; recordIndex < end; recordIndex++)
            {
                    var record = arr[recordIndex];
                    var lowerName = record?.LowerName;
                if (string.IsNullOrEmpty(lowerName) || lowerName.Length < 2)
                {
                    continue;
                }

                generation++;
                if (generation == int.MaxValue)
                {
                    Array.Clear(seen, 0, seen.Length);
                    generation = 1;
                }

                for (var i = 0; i < lowerName.Length - 1; i++)
                {
                    var first = lowerName[i];
                    var second = lowerName[i + 1];
                    if (first >= BigramAsciiLimit || second >= BigramAsciiLimit)
                    {
                        continue;
                    }

                    var token = PackAsciiBigram(first, second);
                    if (counts[token] > BigramAsciiPageSampleLimit || seen[token] == generation)
                    {
                        continue;
                    }

                    seen[token] = generation;
                    var list = lists[token];
                    if (list == null)
                    {
                        list = new List<FileRecord>(Math.Min(counts[token], BigramAsciiPageSampleLimit));
                        lists[token] = list;
                    }

                    if (list.Count < BigramAsciiPageSampleLimit)
                    {
                        list.Add(record);
                    }
                }
            }

            return lists;
        }

        private static int PackAsciiBigram(char first, char second)
        {
            return (first * BigramAsciiLimit) + second;
        }

        private static void CountSingleCharMaskBits(
            int[] counts,
            int[][] driveCounts,
            int driveIndex,
            ulong mask,
            int offset)
        {
            while (mask != 0)
            {
                var charOffset = GetLowestSetBitIndex(mask);
                mask &= mask - 1;
                var ch = offset + charOffset;
                counts[ch]++;
                if (driveIndex >= 0 && driveIndex < driveCounts.Length)
                {
                    driveCounts[driveIndex][ch]++;
                }
            }
        }

        private static int GetLowestSetBitIndex(ulong value)
        {
            var index = 0;
            while ((value & 1UL) == 0UL)
            {
                value >>= 1;
                index++;
            }

            return index;
        }

        private void Publish(
            FileRecord[] arr,
            Dictionary<string, List<FileRecord>> extensionMap,
            FileRecord[] parentArr,
            FileRecord[] directoryArr,
            FileRecord[] launchableArr,
            FileRecord[] scriptArr,
            FileRecord[] logArr,
            FileRecord[] configArr,
            ContainsAccelerator containsAccelerator,
            bool containsAcceleratorReady,
            bool derivedStructuresReady,
            bool buildShortAsciiStructures = true)
        {
            SingleCharAsciiIndex singleCharAsciiIndex = null;
            BigramAsciiIndex bigramAsciiIndex = null;
            NonAsciiShortQueryIndex nonAsciiShortQueryIndex = null;
            if (buildShortAsciiStructures)
            {
                BuildShortAsciiIndexes(arr, out singleCharAsciiIndex, out bigramAsciiIndex, out nonAsciiShortQueryIndex);
            }
            _lock.EnterWriteLock();
            try
            {
                ExtensionHashMap = extensionMap;
                SortedArray = arr;
                ParentSortedArray = parentArr ?? Array.Empty<FileRecord>();
                DirectorySortedArray = directoryArr;
                LaunchableSortedArray = launchableArr;
                ScriptSortedArray = scriptArr;
                LogSortedArray = logArr;
                ConfigSortedArray = configArr;
                _derivedStructuresReady = derivedStructuresReady;
                _containsAccelerator = containsAcceleratorReady
                    ? (containsAccelerator ?? ContainsAccelerator.Empty)
                    : ContainsAccelerator.Empty;
                _containsOverlay = ContainsOverlay.Empty;
                Volatile.Write(ref _deletedOverlayKeys, new HashSet<RecordKey>());
                _containsAcceleratorReady = containsAcceleratorReady;
                Interlocked.Increment(ref _containsAcceleratorEpoch);
                _pendingContainsMutations.Clear();
                _pendingContainsMutationsOverflowed = false;
                Interlocked.Increment(ref _contentVersion);
                ClearShortContainsHotBuckets();
                if (buildShortAsciiStructures)
                {
                    _singleCharAsciiBitsets = singleCharAsciiIndex.Bitsets;
                    _singleCharAsciiCounts = singleCharAsciiIndex.Counts;
                    _singleCharAsciiDriveCounts = singleCharAsciiIndex.DriveCounts;
                    _singleCharAsciiRecordCount = arr?.Length ?? 0;
                    _bigramAsciiCounts = bigramAsciiIndex.Counts;
                    _bigramAsciiDriveCounts = bigramAsciiIndex.DriveCounts;
                    _bigramAsciiPageSamples = bigramAsciiIndex.PageSamples;
                    _bigramAsciiRecordCount = arr?.Length ?? 0;
                    _nonAsciiShortQueryIndex = nonAsciiShortQueryIndex ?? NonAsciiShortQueryIndex.Empty;
                }
                else
                {
                    ClearShortAsciiIndexes();
                }
            }
            finally { _lock.ExitWriteLock(); }
        }

        public void ApplyBatch(IReadOnlyList<UsnChangeEntry> changes, bool rebuildContainsAccelerator = true)
        {
            if (changes == null || changes.Count == 0)
                return;

            var deltaByKey = new Dictionary<RecordKey, FileRecord>(changes.Count * 2);
            for (var i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                switch (change.Kind)
                {
                    case UsnChangeKind.Create:
                        deltaByKey[RecordKey.FromChange(change)] = change.ToRecord();
                        break;
                    case UsnChangeKind.Delete:
                        deltaByKey[RecordKey.FromChange(change)] = null;
                        break;
                    case UsnChangeKind.Rename:
                        deltaByKey[RecordKey.FromRenameOld(change)] = null;
                        deltaByKey[RecordKey.FromChange(change)] = change.ToRecord();
                        break;
                }
            }

            if (deltaByKey.Count == 0)
                return;

            FileRecord[] baseArr;
            long baseContentVersion;
            _lock.EnterReadLock();
            try
            {
                baseArr = SortedArray;
                baseContentVersion = ContentVersion;
            }
            finally
            {
                _lock.ExitReadLock();
            }

            var computeStopwatch = Stopwatch.StartNew();
            var survivors = new FileRecord[baseArr.Length];
            var survivorCount = 0;
            foreach (var record in baseArr)
            {
                if (deltaByKey.ContainsKey(RecordKey.FromRecord(record)))
                {
                    continue;
                }

                survivors[survivorCount++] = record;
            }

            var inserted = new List<FileRecord>(deltaByKey.Count);
            foreach (var entry in deltaByKey)
            {
                if (entry.Value != null)
                {
                    inserted.Add(entry.Value);
                }
            }

            if (inserted.Count > 1)
            {
                inserted.Sort(ByLowerName);
            }

            var arr = MergeSortedRecords(survivors, survivorCount, inserted);
            var singleCharAsciiIndex = BuildSingleCharAsciiIndex(arr);
            var bigramAsciiIndex = BuildBigramAsciiIndex(arr);
            var nonAsciiShortQueryIndex = BuildNonAsciiShortQueryIndex(arr, CancellationToken.None);
            BuildDerivedStructures(
                arr,
                out var extensionMap,
                out var parentArr,
                out var directoryArr,
                out var launchableArr,
                out var scriptArr,
                out var logArr,
                out var configArr);
            var rebuiltAccelerator = rebuildContainsAccelerator
                ? ContainsAccelerator.Build(arr, ContainsAcceleratorBucketKinds.All)
                : null;
            computeStopwatch.Stop();

            var publishStopwatch = Stopwatch.StartNew();
            _lock.EnterWriteLock();
            try
            {
                if (ContentVersion != baseContentVersion)
                {
                    IndexPerfLog.Write("INDEX",
                        $"[APPLY BATCH] outcome=stale-retry changes={changes.Count} baseVersion={baseContentVersion} currentVersion={ContentVersion}");
                    ApplyBatchUnderWriteLock(deltaByKey, rebuildContainsAccelerator);
                    return;
                }

                ExtensionHashMap = extensionMap;
                SortedArray = arr;
                ParentSortedArray = parentArr;
                DirectorySortedArray = directoryArr;
                LaunchableSortedArray = launchableArr;
                ScriptSortedArray = scriptArr;
                LogSortedArray = logArr;
                ConfigSortedArray = configArr;
                _derivedStructuresReady = true;
                if (rebuildContainsAccelerator)
                {
                    _containsAccelerator = rebuiltAccelerator ?? ContainsAccelerator.Empty;
                    _containsOverlay = ContainsOverlay.Empty;
                    _containsAcceleratorReady = true;
                    Interlocked.Increment(ref _containsAcceleratorEpoch);
                    _pendingContainsMutations.Clear();
                    _pendingContainsMutationsOverflowed = false;
                }
                else if (_containsAcceleratorReady)
                {
                    AppendContainsOverlay(deltaByKey);
                }
                else
                {
                    EnqueuePendingContainsMutations(deltaByKey);
                }
                Interlocked.Increment(ref _contentVersion);
                ClearShortContainsHotBuckets();
                _singleCharAsciiBitsets = singleCharAsciiIndex.Bitsets;
                _singleCharAsciiCounts = singleCharAsciiIndex.Counts;
                _singleCharAsciiDriveCounts = singleCharAsciiIndex.DriveCounts;
                _singleCharAsciiRecordCount = arr?.Length ?? 0;
                _bigramAsciiCounts = bigramAsciiIndex.Counts;
                _bigramAsciiDriveCounts = bigramAsciiIndex.DriveCounts;
                _bigramAsciiPageSamples = bigramAsciiIndex.PageSamples;
                _bigramAsciiRecordCount = arr?.Length ?? 0;
                _nonAsciiShortQueryIndex = nonAsciiShortQueryIndex ?? NonAsciiShortQueryIndex.Empty;
            }
            finally
            {
                _lock.ExitWriteLock();
                publishStopwatch.Stop();
            }

            IndexPerfLog.Write("INDEX",
                $"[APPLY BATCH] outcome=success changes={changes.Count} records={arr.Length} computeMs={computeStopwatch.ElapsedMilliseconds} publishMs={publishStopwatch.ElapsedMilliseconds}");
        }

        public LiveDeltaApplyResult ApplyLiveDeltaBatch(
            IReadOnlyList<UsnChangeEntry> changes,
            int maxLiveDeltaMutations = DefaultMaxLiveDeltaMutations)
        {
            var result = new LiveDeltaApplyResult
            {
                Changes = changes?.Count ?? 0
            };
            if (changes == null || changes.Count == 0)
                return result;

            var mutations = new List<PendingContainsMutation>(changes.Count * 2);
            lock (_liveOverlayPublishLock)
            {
                var baseRecords = SortedArray;
                for (var i = 0; i < changes.Count; i++)
                {
                    var change = changes[i];
                    switch (change.Kind)
                    {
                        case UsnChangeKind.Create:
                            ApplyLiveCreate(baseRecords, change.ToRecord(), mutations, result);
                            break;

                        case UsnChangeKind.Delete:
                            ApplyLiveDelete(baseRecords, RecordKey.FromChange(change), change.Frn, change.DriveLetter, mutations, result);
                            break;

                        case UsnChangeKind.Rename:
                            ApplyLiveDelete(baseRecords, RecordKey.FromRenameOld(change), change.Frn, change.DriveLetter, mutations, result);
                            ApplyLiveCreate(baseRecords, change.ToRecord(), mutations, result);
                            break;
                    }
                }

                if (mutations.Count > 1)
                {
                    mutations = CoalesceContainsMutations(mutations);
                }

                if (mutations.Count > 0)
                {
                    var overlay = Volatile.Read(ref _containsOverlay) ?? ContainsOverlay.Empty;
                    var updatedOverlay = overlay.WithMutations(mutations, maxLiveDeltaMutations);
                    Volatile.Write(ref _containsOverlay, updatedOverlay);
                    if (updatedOverlay.IsOverflowed)
                    {
                        result.CompactRequired = true;
                    }
                }

                if (mutations.Count > 0 || result.Deleted > 0 || result.Restored > 0)
                {
                    Interlocked.Increment(ref _contentVersion);
                    Interlocked.Increment(ref _containsAcceleratorEpoch);
                }

                result.OverlayAdds = (Volatile.Read(ref _containsOverlay) ?? ContainsOverlay.Empty).AddedCount;
                result.OverlayDeletes = DeletedOverlayCount;
                if (result.OverlayAdds + result.OverlayDeletes > maxLiveDeltaMutations)
                    result.CompactRequired = true;

                return result;
            }
        }

        public LiveDeltaApplyResult ApplyCatchUpLiveDeltaBatch(IReadOnlyList<UsnChangeEntry> changes)
        {
            return ApplyLiveDeltaBatch(changes, CatchUpMaxLiveDeltaMutations);
        }

        private static List<PendingContainsMutation> CoalesceContainsMutations(List<PendingContainsMutation> mutations)
        {
            var byKey = new Dictionary<RecordKey, PendingContainsMutation>();
            for (var i = 0; i < mutations.Count; i++)
            {
                var mutation = mutations[i];
                var key = mutation.Kind == PendingContainsMutationKind.Insert
                    ? RecordKey.FromRecord(mutation.Record)
                    : mutation.Key;
                if (!key.IsValid)
                    continue;

                byKey[key] = mutation.Kind == PendingContainsMutationKind.Insert
                    ? PendingContainsMutation.ForInsert(mutation.Record)
                    : mutation.Kind == PendingContainsMutationKind.Restore
                        ? PendingContainsMutation.ForRestore(key)
                        : PendingContainsMutation.ForRemove(key);
            }

            return byKey.Count == mutations.Count
                ? mutations
                : new List<PendingContainsMutation>(byKey.Values);
        }

        public bool TryCompactLiveDeltaOverlay(out long compactMilliseconds, CancellationToken ct = default(CancellationToken))
        {
            compactMilliseconds = 0;
            FileRecord[] baseArr;
            ContainsOverlay overlay;
            HashSet<RecordKey> deletedKeys;
            long baseContentVersion;

            _lock.EnterReadLock();
            try
            {
                overlay = Volatile.Read(ref _containsOverlay) ?? ContainsOverlay.Empty;
                deletedKeys = Volatile.Read(ref _deletedOverlayKeys) ?? new HashSet<RecordKey>();
                if (overlay.AddedCount == 0 && deletedKeys.Count == 0)
                    return false;

                baseArr = SortedArray ?? Array.Empty<FileRecord>();
                baseContentVersion = ContentVersion;
            }
            finally
            {
                _lock.ExitReadLock();
            }

            var stopwatch = Stopwatch.StartNew();
            var removedKeys = overlay.GetRemovedKeysSnapshot();
            if (deletedKeys.Count > 0)
            {
                if (removedKeys.Count == 0)
                    removedKeys = new HashSet<RecordKey>(deletedKeys);
                else
                    removedKeys.UnionWith(deletedKeys);
            }

            var survivors = new FileRecord[baseArr.Length];
            var survivorCount = 0;
            for (var i = 0; i < baseArr.Length; i++)
            {
                if (((i + 1) & 0x3FFF) == 0)
                    ct.ThrowIfCancellationRequested();

                var record = baseArr[i];
                if (record == null || removedKeys.Contains(RecordKey.FromRecord(record)))
                    continue;

                survivors[survivorCount++] = record;
            }

            var inserted = overlay.GetAddedRecordsSnapshot();
            if (inserted.Count > 1)
                inserted.Sort(ByLowerName);

            var arr = MergeSortedRecords(survivors, survivorCount, inserted);
            var singleCharAsciiIndex = BuildSingleCharAsciiIndex(arr);
            var bigramAsciiIndex = BuildBigramAsciiIndex(arr);
            var nonAsciiShortQueryIndex = BuildNonAsciiShortQueryIndex(arr, ct);
            var containsAccelerator = ContainsAccelerator.Build(arr, ContainsAcceleratorBucketKinds.All, ct);
            BuildDerivedStructures(
                arr,
                out var extensionMap,
                out var parentArr,
                out var directoryArr,
                out var launchableArr,
                out var scriptArr,
                out var logArr,
                out var configArr);
            stopwatch.Stop();
            compactMilliseconds = stopwatch.ElapsedMilliseconds;

            _lock.EnterWriteLock();
            try
            {
                if (ContentVersion != baseContentVersion
                    || !ReferenceEquals(overlay, Volatile.Read(ref _containsOverlay))
                    || !ReferenceEquals(deletedKeys, Volatile.Read(ref _deletedOverlayKeys)))
                {
                    return false;
                }

                ExtensionHashMap = extensionMap;
                SortedArray = arr;
                ParentSortedArray = parentArr;
                DirectorySortedArray = directoryArr;
                LaunchableSortedArray = launchableArr;
                ScriptSortedArray = scriptArr;
                LogSortedArray = logArr;
                ConfigSortedArray = configArr;
                _derivedStructuresReady = true;
                _containsAccelerator = containsAccelerator ?? ContainsAccelerator.Empty;
                _containsOverlay = ContainsOverlay.Empty;
                Volatile.Write(ref _deletedOverlayKeys, new HashSet<RecordKey>());
                _containsAcceleratorReady = containsAccelerator != null && !containsAccelerator.IsEmpty;
                Interlocked.Increment(ref _containsAcceleratorEpoch);
                _pendingContainsMutations.Clear();
                _pendingContainsMutationsOverflowed = false;
                Interlocked.Increment(ref _contentVersion);
                ClearShortContainsHotBuckets();
                _singleCharAsciiBitsets = singleCharAsciiIndex.Bitsets;
                _singleCharAsciiCounts = singleCharAsciiIndex.Counts;
                _singleCharAsciiDriveCounts = singleCharAsciiIndex.DriveCounts;
                _singleCharAsciiRecordCount = arr?.Length ?? 0;
                _bigramAsciiCounts = bigramAsciiIndex.Counts;
                _bigramAsciiDriveCounts = bigramAsciiIndex.DriveCounts;
                _bigramAsciiPageSamples = bigramAsciiIndex.PageSamples;
                _bigramAsciiRecordCount = arr?.Length ?? 0;
                _nonAsciiShortQueryIndex = nonAsciiShortQueryIndex ?? NonAsciiShortQueryIndex.Empty;
                return true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        private void ApplyLiveCreate(
            FileRecord[] baseRecords,
            FileRecord record,
            List<PendingContainsMutation> mutations,
            LiveDeltaApplyResult result)
        {
            if (record == null)
                return;

            var key = RecordKey.FromRecord(record);
            if (!key.IsValid)
                return;

            var restored = RemoveDeletedOverlayKey(key);
            if (restored)
            {
                mutations.Add(PendingContainsMutation.ForRestore(key));
                result.Restored++;
            }

            if (FindRecordByKey(baseRecords, key) != null)
            {
                if (!restored)
                    result.AlreadyVisible++;
                return;
            }

            mutations.Add(PendingContainsMutation.ForInsert(record));
            result.Inserted++;
        }

        private void ApplyLiveDelete(
            FileRecord[] baseRecords,
            RecordKey key,
            ulong frn,
            char driveLetter,
            List<PendingContainsMutation> mutations,
            LiveDeltaApplyResult result)
        {
            if (!key.IsValid)
                return;

            var overlay = Volatile.Read(ref _containsOverlay) ?? ContainsOverlay.Empty;
            var removedOverlayKeys = overlay.GetAddedKeysByFrn(frn, driveLetter);
            for (var i = 0; i < removedOverlayKeys.Count; i++)
            {
                mutations.Add(PendingContainsMutation.ForRemove(removedOverlayKeys[i]));
                result.Deleted++;
            }

            if (overlay.ContainsAdded(key))
            {
                if (!removedOverlayKeys.Contains(key))
                {
                    mutations.Add(PendingContainsMutation.ForRemove(key));
                    result.Deleted++;
                }
                return;
            }

            if (removedOverlayKeys.Count > 0 && FindRecordByKey(baseRecords, key) == null)
            {
                return;
            }

            var added = AddDeletedOverlayKey(key);
            mutations.Add(PendingContainsMutation.ForRemove(key));
            if (added)
                result.Deleted++;
        }

        private void ApplyBatchUnderWriteLock(Dictionary<RecordKey, FileRecord> deltaByKey, bool rebuildContainsAccelerator)
        {
            var baseArr = SortedArray;
            var survivors = new FileRecord[baseArr.Length];
            var survivorCount = 0;
            foreach (var record in baseArr)
            {
                if (deltaByKey.ContainsKey(RecordKey.FromRecord(record)))
                {
                    continue;
                }

                survivors[survivorCount++] = record;
            }

            var inserted = new List<FileRecord>(deltaByKey.Count);
            foreach (var entry in deltaByKey)
            {
                if (entry.Value != null)
                {
                    inserted.Add(entry.Value);
                }
            }

            if (inserted.Count > 1)
            {
                inserted.Sort(ByLowerName);
            }

            var arr = MergeSortedRecords(survivors, survivorCount, inserted);
            var singleCharAsciiIndex = BuildSingleCharAsciiIndex(arr);
            var bigramAsciiIndex = BuildBigramAsciiIndex(arr);
            var nonAsciiShortQueryIndex = BuildNonAsciiShortQueryIndex(arr, CancellationToken.None);
            BuildDerivedStructures(
                arr,
                out var extensionMap,
                out var parentArr,
                out var directoryArr,
                out var launchableArr,
                out var scriptArr,
                out var logArr,
                out var configArr);
            ExtensionHashMap = extensionMap;
            SortedArray = arr;
            ParentSortedArray = parentArr;
            DirectorySortedArray = directoryArr;
            LaunchableSortedArray = launchableArr;
            ScriptSortedArray = scriptArr;
            LogSortedArray = logArr;
            ConfigSortedArray = configArr;
            _derivedStructuresReady = true;
            if (rebuildContainsAccelerator)
            {
                _containsAccelerator = ContainsAccelerator.Build(arr, ContainsAcceleratorBucketKinds.All);
                _containsOverlay = ContainsOverlay.Empty;
                _containsAcceleratorReady = true;
                Interlocked.Increment(ref _containsAcceleratorEpoch);
                _pendingContainsMutations.Clear();
                _pendingContainsMutationsOverflowed = false;
            }
            else if (_containsAcceleratorReady)
            {
                AppendContainsOverlay(deltaByKey);
            }
            else
            {
                EnqueuePendingContainsMutations(deltaByKey);
            }
            Interlocked.Increment(ref _contentVersion);
            ClearShortContainsHotBuckets();
            _singleCharAsciiBitsets = singleCharAsciiIndex.Bitsets;
            _singleCharAsciiCounts = singleCharAsciiIndex.Counts;
            _singleCharAsciiDriveCounts = singleCharAsciiIndex.DriveCounts;
            _singleCharAsciiRecordCount = arr?.Length ?? 0;
            _bigramAsciiCounts = bigramAsciiIndex.Counts;
            _bigramAsciiDriveCounts = bigramAsciiIndex.DriveCounts;
            _bigramAsciiPageSamples = bigramAsciiIndex.PageSamples;
            _bigramAsciiRecordCount = arr?.Length ?? 0;
            _nonAsciiShortQueryIndex = nonAsciiShortQueryIndex ?? NonAsciiShortQueryIndex.Empty;
        }

        private static FileRecord[] MergeSortedRecords(FileRecord[] survivors, int survivorCount, List<FileRecord> inserted)
        {
            if (survivorCount == 0)
            {
                return inserted == null || inserted.Count == 0
                    ? Array.Empty<FileRecord>()
                    : inserted.ToArray();
            }

            if (inserted == null || inserted.Count == 0)
            {
                if (survivorCount == survivors.Length)
                {
                    return survivors;
                }

                var trimmed = new FileRecord[survivorCount];
                Array.Copy(survivors, 0, trimmed, 0, survivorCount);
                return trimmed;
            }

            var merged = new FileRecord[survivorCount + inserted.Count];
            var left = 0;
            var right = 0;
            var index = 0;
            while (left < survivorCount && right < inserted.Count)
            {
                if (ByLowerName.Compare(survivors[left], inserted[right]) <= 0)
                {
                    merged[index++] = survivors[left++];
                }
                else
                {
                    merged[index++] = inserted[right++];
                }
            }

            while (left < survivorCount)
            {
                merged[index++] = survivors[left++];
            }

            while (right < inserted.Count)
            {
                merged[index++] = inserted[right++];
            }

            return merged;
        }

        public bool TryEnsureContainsAccelerator(ContainsWarmupScope scope, CancellationToken ct)
        {
            switch (scope)
            {
                case ContainsWarmupScope.Short:
                    EnsureShortAsciiStructuresReady(ct, "contains-short-warmup");
                    return HasShortAsciiStructuresReady();

                case ContainsWarmupScope.TrigramOnly:
                    return TryEnsureTrigramContainsAccelerator(ct, "contains-trigram-warmup");

                case ContainsWarmupScope.Full:
                    EnsureShortAsciiStructuresReady(ct, "contains-full-warmup");
                    return TryEnsureTrigramContainsAccelerator(ct, "contains-full-warmup");
            }

            return false;
        }

        public bool SupportsContainsAccelerator(ContainsWarmupScope scope)
        {
            switch (scope)
            {
                case ContainsWarmupScope.Short:
                    return HasShortAsciiStructuresReady();
                case ContainsWarmupScope.TrigramOnly:
                    return HasTrigramContainsAcceleratorReady();
                case ContainsWarmupScope.Full:
                    return HasShortAsciiStructuresReady()
                           && HasTrigramContainsAcceleratorReady();
                default:
                    return false;
            }
        }

        private bool TryEnsureTrigramContainsAccelerator(CancellationToken ct, string reason)
        {
            while (!ct.IsCancellationRequested)
            {
                FileRecord[] snapshot;
                long epoch;
                ContainsAccelerator currentAccelerator;
                _lock.EnterReadLock();
                try
                {
                    currentAccelerator = Volatile.Read(ref _containsAccelerator);
                    if (HasTrigramContainsAcceleratorReady())
                    {
                        return true;
                    }

                    snapshot = SortedArray;
                    epoch = _containsAcceleratorEpoch;
                }
                finally
                {
                    _lock.ExitReadLock();
                }

                var accelerator = ContainsAccelerator.Build(snapshot, ContainsAcceleratorBucketKinds.All, currentAccelerator, ct);

                _lock.EnterWriteLock();
                try
                {
                    if (HasTrigramContainsAcceleratorReady())
                    {
                        return true;
                    }

                    if (_pendingContainsMutationsOverflowed)
                    {
                        _pendingContainsMutations.Clear();
                        _pendingContainsMutationsOverflowed = false;
                        continue;
                    }

                    var publishOverlay = Volatile.Read(ref _containsOverlay) ?? ContainsOverlay.Empty;
                    publishOverlay = publishOverlay.WithMutations(_pendingContainsMutations, MaxPendingContainsMutations);
                    publishOverlay = publishOverlay.PruneForBase(accelerator);
                    _containsAccelerator = accelerator ?? ContainsAccelerator.Empty;
                    _containsAcceleratorReady = true;
                    _containsOverlay = publishOverlay;
                    _pendingContainsMutations.Clear();
                    _pendingContainsMutationsOverflowed = false;
                    Interlocked.Increment(ref _containsAcceleratorEpoch);
                    IndexPerfLog.Write("INDEX",
                        $"[CONTAINS PUBLISH] outcome=success scope=TrigramOnly overlayAdds={_containsOverlay.AddedCount} overlayRemoves={_containsOverlay.RemovedCount}");
                    return true;
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }

            ct.ThrowIfCancellationRequested();
            return false;
        }

        private bool HasShortAsciiStructuresReady()
        {
            return HasShortQueryStructuresReady();
        }

        private bool HasTrigramContainsAcceleratorReady()
        {
            var accelerator = Volatile.Read(ref _containsAccelerator);
            return Volatile.Read(ref _containsAcceleratorReady)
                   && accelerator != null
                   && !accelerator.IsEmpty
                   && accelerator.HasTrigramBucket;
        }

        private void InsertIntoFilterBuckets(FileRecord record)
        {
            if (record == null)
                return;

            if (record.IsDirectory)
                DirectorySortedArray = InsertIntoSortedArray(DirectorySortedArray, record);

            if (MatchesFilter(record, SearchTypeFilter.Launchable))
                LaunchableSortedArray = InsertIntoSortedArray(LaunchableSortedArray, record);
            if (MatchesFilter(record, SearchTypeFilter.Script))
                ScriptSortedArray = InsertIntoSortedArray(ScriptSortedArray, record);
            if (MatchesFilter(record, SearchTypeFilter.Log))
                LogSortedArray = InsertIntoSortedArray(LogSortedArray, record);
            if (MatchesFilter(record, SearchTypeFilter.Config))
                ConfigSortedArray = InsertIntoSortedArray(ConfigSortedArray, record);
        }

        private void RemoveFromFilterBuckets(ulong frn, string lowerName, ulong parentFrn, char driveLetter)
        {
            DirectorySortedArray = RemoveFromSortedArray(DirectorySortedArray, frn, lowerName, parentFrn, driveLetter);
            LaunchableSortedArray = RemoveFromSortedArray(LaunchableSortedArray, frn, lowerName, parentFrn, driveLetter);
            ScriptSortedArray = RemoveFromSortedArray(ScriptSortedArray, frn, lowerName, parentFrn, driveLetter);
            LogSortedArray = RemoveFromSortedArray(LogSortedArray, frn, lowerName, parentFrn, driveLetter);
            ConfigSortedArray = RemoveFromSortedArray(ConfigSortedArray, frn, lowerName, parentFrn, driveLetter);
        }

        public void EnsureDerivedStructures(CancellationToken ct = default)
        {
            if (AreDerivedStructuresReady)
                return;

            FileRecord[] snapshot;
            long contentVersion;
            _lock.EnterReadLock();
            try
            {
                if (_derivedStructuresReady)
                    return;

                snapshot = SortedArray;
                contentVersion = ContentVersion;
            }
            finally
            {
                _lock.ExitReadLock();
            }

            ct.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            BuildDerivedStructures(
                snapshot,
                out var extensionMap,
                out var parentArr,
                out var directoryArr,
                out var launchableArr,
                out var scriptArr,
                out var logArr,
                out var configArr);
            stopwatch.Stop();

            _lock.EnterWriteLock();
            try
            {
                if (_derivedStructuresReady || ContentVersion != contentVersion)
                    return;

                ExtensionHashMap = extensionMap;
                ParentSortedArray = parentArr;
                DirectorySortedArray = directoryArr;
                LaunchableSortedArray = launchableArr;
                ScriptSortedArray = scriptArr;
                LogSortedArray = logArr;
                ConfigSortedArray = configArr;
                _derivedStructuresReady = true;
                IndexPerfLog.Write("INDEX",
                    $"[DERIVED STRUCTURES] outcome=success records={snapshot?.Length ?? 0} elapsedMs={stopwatch.ElapsedMilliseconds}");
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public void QueueEnsureDerivedStructures(string reason)
        {
            if (AreDerivedStructuresReady)
                return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    IndexPerfLog.Write("INDEX",
                        $"[DERIVED STRUCTURES] outcome=start reason={IndexPerfLog.FormatValue(reason)} records={TotalCount}");
                    EnsureDerivedStructures(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    IndexPerfLog.Write("INDEX",
                        $"[DERIVED STRUCTURES] outcome=failed reason={IndexPerfLog.FormatValue(reason)} error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
                }
            });
        }

        public void QueueEnsureShortAsciiStructures(string reason)
        {
            var recordCount = SortedArray?.Length ?? 0;
            if (recordCount == 0
                || (Volatile.Read(ref _singleCharAsciiCounts) != null
                    && Volatile.Read(ref _singleCharAsciiRecordCount) == recordCount
                    && Volatile.Read(ref _bigramAsciiCounts) != null
                    && Volatile.Read(ref _bigramAsciiRecordCount) == recordCount))
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    EnsureShortAsciiStructures(CancellationToken.None, reason);
                }
                catch (Exception ex)
                {
                    IndexPerfLog.Write("INDEX",
                        $"[SHORT ASCII STRUCTURES] outcome=failed reason={IndexPerfLog.FormatValue(reason)} error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
                }
            });
        }

        public void EnsureShortAsciiStructuresReady(CancellationToken ct, string reason)
        {
            while (!ct.IsCancellationRequested)
            {
                EnsureShortAsciiStructures(ct, reason);

                var recordCount = SortedArray?.Length ?? 0;
                if (recordCount == 0 || HasShortQueryStructuresReady())
                {
                    return;
                }

                Thread.Sleep(25);
            }

            ct.ThrowIfCancellationRequested();
        }

        private void EnsureShortAsciiStructures(CancellationToken ct, string reason)
        {
            FileRecord[] snapshot;
            _lock.EnterReadLock();
            try
            {
                snapshot = SortedArray ?? Array.Empty<FileRecord>();
                if (snapshot.Length == 0 || HasShortQueryStructuresReady())
                {
                    return;
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            ct.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            var singleCharAsciiIndex = BuildSingleCharAsciiIndex(snapshot);
            var bigramAsciiIndex = BuildBigramAsciiIndex(snapshot);
            var nonAsciiShortQueryIndex = BuildNonAsciiShortQueryIndex(snapshot, ct);
            stopwatch.Stop();

            _lock.EnterWriteLock();
            try
            {
                if (!ReferenceEquals(snapshot, SortedArray))
                    return;

                _singleCharAsciiBitsets = singleCharAsciiIndex.Bitsets;
                _singleCharAsciiCounts = singleCharAsciiIndex.Counts;
                _singleCharAsciiDriveCounts = singleCharAsciiIndex.DriveCounts;
                _singleCharAsciiRecordCount = snapshot.Length;
                _bigramAsciiCounts = bigramAsciiIndex.Counts;
                _bigramAsciiDriveCounts = bigramAsciiIndex.DriveCounts;
                _bigramAsciiPageSamples = bigramAsciiIndex.PageSamples;
                _bigramAsciiRecordCount = snapshot.Length;
                _nonAsciiShortQueryIndex = nonAsciiShortQueryIndex ?? NonAsciiShortQueryIndex.Empty;
                IndexPerfLog.Write("INDEX",
                    $"[SHORT QUERY STRUCTURES] outcome=success reason={IndexPerfLog.FormatValue(reason)} records={snapshot.Length} elapsedMs={stopwatch.ElapsedMilliseconds}");
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        private static FileRecord[] InsertIntoSortedArray(FileRecord[] target, FileRecord record)
        {
            return InsertIntoSortedArray(target, record, ByLowerName);
        }

        private static FileRecord[] InsertIntoSortedArray(FileRecord[] target, FileRecord record, IComparer<FileRecord> comparer)
        {
            var arr = target ?? Array.Empty<FileRecord>();
            var idx = Array.BinarySearch(arr, record, comparer ?? ByLowerName);
            if (idx < 0)
                idx = ~idx;

            var newArr = new FileRecord[arr.Length + 1];
            Array.Copy(arr, 0, newArr, 0, idx);
            newArr[idx] = record;
            Array.Copy(arr, idx, newArr, idx + 1, arr.Length - idx);
            return newArr;
        }

        private static FileRecord[] RemoveFromSortedArray(FileRecord[] target, ulong frn, string lowerName, ulong parentFrn, char driveLetter)
        {
            var arr = target;
            if (arr == null || arr.Length == 0)
                return arr;

            for (var i = 0; i < arr.Length; i++)
            {
                var match = IsRecordIdentityMatch(arr[i], frn, lowerName, parentFrn, driveLetter);
                if (!match)
                    continue;

                var newArr = new FileRecord[arr.Length - 1];
                Array.Copy(arr, 0, newArr, 0, i);
                Array.Copy(arr, i + 1, newArr, i, arr.Length - i - 1);
                return newArr;
            }

            return arr;
        }

        private static bool MatchesFilter(FileRecord record, SearchTypeFilter filter)
        {
            if (record == null)
                return false;

            if (filter == SearchTypeFilter.All)
                return true;

            if (filter == SearchTypeFilter.Folder)
                return record.IsDirectory;

            if (record.IsDirectory || !TryGetIndexedExtension(record.LowerName, out var extension))
                return false;

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

        private static bool TryGetIndexedExtension(string lowerName, out string extension)
        {
            extension = null;
            if (string.IsNullOrEmpty(lowerName))
                return false;

            var dotIndex = lowerName.LastIndexOf('.');
            if (dotIndex < 0 || dotIndex == lowerName.Length - 1)
                return false;

            extension = lowerName.Substring(dotIndex);
            return extension.Length > 1;
        }

        private static void InsertIntoSortedBucket(List<FileRecord> bucket, FileRecord record)
        {
            var insertIndex = bucket.BinarySearch(record, ByLowerName);
            if (insertIndex < 0)
                insertIndex = ~insertIndex;
            bucket.Insert(insertIndex, record);
        }

        private static void RemoveFromBucket(List<FileRecord> bucket, ulong frn, string lowerName, ulong parentFrn, char driveLetter)
        {
            if (bucket == null || bucket.Count == 0)
                return;

            if (frn != 0)
            {
                bucket.RemoveAll(r => IsRecordIdentityMatch(r, frn, lowerName, parentFrn, driveLetter));
                return;
            }

            bucket.RemoveAll(r => r.LowerName == lowerName
                                  && r.DriveLetter == driveLetter
                && (parentFrn == 0 || r.ParentFrn == parentFrn));
        }

        private static bool IsRecordIdentityMatch(
            FileRecord record,
            ulong frn,
            string lowerName,
            ulong parentFrn,
            char driveLetter)
        {
            if (record == null || record.DriveLetter != driveLetter)
                return false;

            if (frn != 0)
            {
                if (record.Frn != frn)
                    return false;

                if (parentFrn != 0 && record.ParentFrn != parentFrn)
                    return false;

                return string.IsNullOrEmpty(lowerName)
                       || string.Equals(record.LowerName, lowerName, StringComparison.Ordinal);
            }

            return string.Equals(record.LowerName, lowerName, StringComparison.Ordinal)
                   && (parentFrn == 0 || record.ParentFrn == parentFrn);
        }

        private int DeletedOverlayCount
        {
            get
            {
                var keys = Volatile.Read(ref _deletedOverlayKeys);
                return keys == null ? 0 : keys.Count;
            }
        }

        private bool AddDeletedOverlayKey(RecordKey key)
        {
            if (!key.IsValid)
                return false;

            var current = Volatile.Read(ref _deletedOverlayKeys) ?? new HashSet<RecordKey>();
            if (current.Contains(key))
                return false;

            var next = new HashSet<RecordKey>(current);
            next.Add(key);
            Volatile.Write(ref _deletedOverlayKeys, next);
            return true;
        }

        private bool RemoveDeletedOverlayKey(RecordKey key)
        {
            if (!key.IsValid)
                return false;

            var current = Volatile.Read(ref _deletedOverlayKeys);
            if (current == null || current.Count == 0 || !current.Contains(key))
                return false;

            var next = new HashSet<RecordKey>(current);
            next.Remove(key);
            Volatile.Write(ref _deletedOverlayKeys, next);
            return true;
        }

        private static int LowerBoundByLowerName(FileRecord[] records, string lowerName)
        {
            var lo = 0;
            var hi = records?.Length ?? 0;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) / 2);
                var compare = string.CompareOrdinal(records[mid]?.LowerName, lowerName);
                if (compare < 0)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
        }

        private static FileRecord FindRecordByKey(FileRecord[] records, RecordKey key)
        {
            if (records == null || records.Length == 0 || !key.IsValid)
                return null;

            var start = LowerBoundByLowerName(records, key.LowerName);
            for (var i = start; i < records.Length; i++)
            {
                var record = records[i];
                if (record == null)
                    continue;

                var compare = string.CompareOrdinal(record.LowerName, key.LowerName);
                if (compare != 0)
                    break;

                if (RecordKey.FromRecord(record).Equals(key))
                    return record;
            }

            return null;
        }

        private void EnqueuePendingContainsInsert(FileRecord record)
        {
            EnqueuePendingContainsMutation(PendingContainsMutation.ForInsert(record));
        }

        private void EnqueuePendingContainsRemove(RecordKey key)
        {
            EnqueuePendingContainsMutation(PendingContainsMutation.ForRemove(key));
        }

        private void EnqueuePendingContainsMutation(PendingContainsMutation mutation)
        {
            if (_pendingContainsMutationsOverflowed)
            {
                return;
            }

            if (_pendingContainsMutations.Count >= MaxPendingContainsMutations)
            {
                _pendingContainsMutations.Clear();
                _pendingContainsMutationsOverflowed = true;
                return;
            }

            _pendingContainsMutations.Add(mutation);
        }

        private void AppendContainsOverlay(PendingContainsMutation mutation)
        {
            if (!_containsAcceleratorReady)
            {
                EnqueuePendingContainsMutation(mutation);
                return;
            }

            var overlay = Volatile.Read(ref _containsOverlay) ?? ContainsOverlay.Empty;
            var updatedOverlay = overlay.WithMutation(mutation, MaxPendingContainsMutations);
            Volatile.Write(ref _containsOverlay, updatedOverlay);
            if (updatedOverlay.IsOverflowed)
            {
                EnqueuePendingContainsMutation(mutation);
                IndexPerfLog.Write("INDEX",
                    $"[CONTAINS OVERLAY] outcome=overflow adds={overlay.AddedCount} removes={overlay.RemovedCount}");
                return;
            }

            Interlocked.Increment(ref _containsAcceleratorEpoch);
        }

        private void AppendContainsOverlay(Dictionary<RecordKey, FileRecord> deltaByKey)
        {
            if (deltaByKey == null || deltaByKey.Count == 0)
            {
                return;
            }

            foreach (var entry in deltaByKey)
            {
                AppendContainsOverlay(entry.Value == null
                    ? PendingContainsMutation.ForRemove(entry.Key)
                    : PendingContainsMutation.ForInsert(entry.Value));
            }
        }

        private void EnqueuePendingContainsMutations(Dictionary<RecordKey, FileRecord> deltaByKey)
        {
            if (deltaByKey == null || deltaByKey.Count == 0 || _pendingContainsMutationsOverflowed)
            {
                return;
            }

            foreach (var entry in deltaByKey)
            {
                EnqueuePendingContainsMutation(entry.Value == null
                    ? PendingContainsMutation.ForRemove(entry.Key)
                    : PendingContainsMutation.ForInsert(entry.Value));

                if (_pendingContainsMutationsOverflowed)
                {
                    return;
                }
            }
        }

        #if false
        private sealed class ContainsAccelerator
        {
            [Flags]
            private enum BucketKinds
            {
                None = 0,
                Char = 1,
                Bigram = 2,
                Trigram = 4,
                All = Char | Bigram | Trigram
            }

            private readonly List<FileRecord> _recordsById;
            private readonly Dictionary<RecordKey, int> _recordIdsByKey;
            private readonly Dictionary<char, List<int>> _charBuckets = new Dictionary<char, List<int>>();
            private readonly Dictionary<uint, List<int>> _bigramBuckets = new Dictionary<uint, List<int>>();
            private readonly Dictionary<ulong, List<int>> _trigramBuckets = new Dictionary<ulong, List<int>>();
            private readonly BucketKinds _builtBuckets;
            private int _nextRecordId = 1;

            public static ContainsAccelerator Empty => new ContainsAccelerator();

            public bool IsEmpty => _recordIdsByKey.Count == 0;
            public bool IsComplete => _builtBuckets == BucketKinds.All;

            private ContainsAccelerator()
                : this(0, BucketKinds.None)
            {
            }

            private ContainsAccelerator(int recordCapacity, BucketKinds builtBuckets)
            {
                _builtBuckets = builtBuckets;
                _recordsById = recordCapacity > 0
                    ? new List<FileRecord>(recordCapacity + 1)
                    : new List<FileRecord>(1);
                _recordsById.Add(null);
                _recordIdsByKey = recordCapacity > 0
                    ? new Dictionary<RecordKey, int>(recordCapacity)
                    : new Dictionary<RecordKey, int>();
            }

            public bool Supports(string query)
            {
                if (string.IsNullOrEmpty(query))
                {
                    return false;
                }

            if (query.Length == 1)
            {
                return (_builtBuckets & BucketKinds.Char) != 0;
                }

                if (query.Length == 2)
                {
                    return (_builtBuckets & BucketKinds.Bigram) != 0;
                }

                return (_builtBuckets & BucketKinds.Trigram) != 0;
            }

            public bool Supports(ContainsAcceleratorBucketKinds requiredBuckets)
            {
                var flags = ToBucketKinds(requiredBuckets);
                return (_builtBuckets & flags) == flags;
            }

            public static ContainsAccelerator Build(
                FileRecord[] records,
                ContainsAcceleratorBucketKinds requiredBuckets,
                CancellationToken ct = default)
            {
                var builtBuckets = ToBucketKinds(requiredBuckets);
                var accelerator = new ContainsAccelerator(records?.Length ?? 0, builtBuckets);
                if (records == null || records.Length == 0)
                {
                    return accelerator;
                }

                for (var i = 0; i < records.Length; i++)
                {
                    if (((i + 1) & 0x7FF) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    accelerator.AddRecord(records[i], appendOnly: true);
                }

                return accelerator;
            }

            public ContainsAccelerator WithInserted(FileRecord record)
            {
                AddRecord(record, appendOnly: false);
                return this;
            }

            public ContainsAccelerator WithRemoved(RecordKey key)
            {
                RemoveRecord(key);
                return this;
            }

            public ContainsSearchResult Search(string query, SearchTypeFilter filter, int offset, int maxResults, CancellationToken ct)
            {
                var normalizedOffset = Math.Max(offset, 0);
                var normalizedMaxResults = Math.Max(maxResults, 0);
                if (query.Length == 1)
                {
                    return SearchSingleChar(query[0], filter, normalizedOffset, normalizedMaxResults, ct);
                }

                if (query.Length == 2)
                {
                    return SearchBigram(PackBigram(query[0], query[1]), filter, normalizedOffset, normalizedMaxResults, ct);
                }

                return SearchTrigram(query, filter, normalizedOffset, normalizedMaxResults, ct);
            }

            private ContainsSearchResult SearchSingleChar(char token, SearchTypeFilter filter, int offset, int maxResults, CancellationToken ct)
            {
                if (!_charBuckets.TryGetValue(token, out var bucket) || bucket == null || bucket.Count == 0)
                {
                    return new ContainsSearchResult { Mode = "char" };
                }

                return BuildBucketResult(bucket, filter, offset, maxResults, "char", ct);
            }

            private ContainsSearchResult SearchBigram(uint token, SearchTypeFilter filter, int offset, int maxResults, CancellationToken ct)
            {
                if (!_bigramBuckets.TryGetValue(token, out var bucket) || bucket == null || bucket.Count == 0)
                {
                    return new ContainsSearchResult { Mode = "bigram" };
                }

                return BuildBucketResult(bucket, filter, offset, maxResults, "bigram", ct);
            }

            private ContainsSearchResult SearchTrigram(string query, SearchTypeFilter filter, int offset, int maxResults, CancellationToken ct)
            {
                var trigramKeys = BuildUniqueTrigramKeys(query);
                if (trigramKeys.Count == 0)
                {
                    return new ContainsSearchResult { Mode = "fallback" };
                }

                var postingLists = new List<List<int>>(trigramKeys.Count);
                for (var i = 0; i < trigramKeys.Count; i++)
                {
                    if (!_trigramBuckets.TryGetValue(trigramKeys[i], out var bucket) || bucket == null || bucket.Count == 0)
                    {
                        return new ContainsSearchResult { Mode = "trigram" };
                    }

                    postingLists.Add(bucket);
                }

                postingLists.Sort((a, b) => a.Count.CompareTo(b.Count));
                var intersectStopwatch = Stopwatch.StartNew();
                var primary = postingLists[0];
                var others = new HashSet<int>[postingLists.Count - 1];
                for (var i = 1; i < postingLists.Count; i++)
                {
                    others[i - 1] = new HashSet<int>(postingLists[i]);
                }

                var candidates = new List<int>(primary.Count);
                var lastRecordId = int.MinValue;
                for (var i = 0; i < primary.Count; i++)
                {
                    if (((i + 1) & 0xFFF) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    var recordId = primary[i];
                    if (recordId == lastRecordId)
                    {
                        continue;
                    }

                    lastRecordId = recordId;
                    var matched = true;
                    for (var j = 0; j < others.Length; j++)
                    {
                        if (!others[j].Contains(recordId))
                        {
                            matched = false;
                            break;
                        }
                    }

                    if (matched)
                    {
                        candidates.Add(recordId);
                    }
                }

                intersectStopwatch.Stop();

                var result = new ContainsSearchResult
                {
                    Mode = "trigram",
                    CandidateCount = candidates.Count,
                    IntersectMs = intersectStopwatch.ElapsedMilliseconds
                };

                var verifyStopwatch = Stopwatch.StartNew();
                var page = new List<FileRecord>(Math.Min(maxResults, 64));
                var total = 0;
                for (var i = 0; i < candidates.Count; i++)
                {
                    if (((i + 1) & 0xFFF) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    var record = _recordsById[candidates[i]];
                    if (!record.LowerName.Contains(query) || !MatchesFilter(record, filter))
                    {
                        continue;
                    }

                    total++;
                    if (total > offset && page.Count < maxResults)
                    {
                        page.Add(record);
                    }
                }

                verifyStopwatch.Stop();
                result.Total = total;
                result.Page = page;
                result.VerifyMs = verifyStopwatch.ElapsedMilliseconds;
                return result;
            }

            private ContainsSearchResult BuildBucketResult(List<int> bucket, SearchTypeFilter filter, int offset, int maxResults, string mode, CancellationToken ct)
            {
                var page = new List<FileRecord>(Math.Min(maxResults, 64));
                var total = 0;
                var lastRecordId = int.MinValue;
                for (var i = 0; i < bucket.Count; i++)
                {
                    if (((i + 1) & 0xFFF) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    var recordId = bucket[i];
                    if (recordId == lastRecordId)
                    {
                        continue;
                    }

                    lastRecordId = recordId;
                    var record = _recordsById[recordId];
                    if (filter != SearchTypeFilter.All && !MatchesFilter(record, filter))
                    {
                        continue;
                    }

                    total++;
                    if (total > offset && page.Count < maxResults)
                    {
                        page.Add(record);
                    }
                }

                return new ContainsSearchResult
                {
                    Mode = mode,
                    CandidateCount = total,
                    Total = total,
                    Page = page
                };
            }

            private void AddRecord(FileRecord record, bool appendOnly)
            {
                if (record == null)
                {
                    return;
                }

                var key = RecordKey.FromRecord(record);
                if (_recordIdsByKey.ContainsKey(key))
                {
                    return;
                }

                var recordId = _nextRecordId++;
                _recordIdsByKey[key] = recordId;
                _recordsById.Add(record);

                if ((_builtBuckets & BucketKinds.Char) != 0)
                {
                    AddToCharBuckets(recordId, record.LowerName, appendOnly);
                }

                if ((_builtBuckets & BucketKinds.Bigram) != 0)
                {
                    AddToBigramBuckets(recordId, record.LowerName, appendOnly);
                }

                if ((_builtBuckets & BucketKinds.Trigram) != 0)
                {
                    AddToTrigramBuckets(recordId, record.LowerName, appendOnly);
                }
            }

            private void RemoveRecord(RecordKey key)
            {
                if (!_recordIdsByKey.TryGetValue(key, out var recordId)
                    || recordId <= 0
                    || recordId >= _recordsById.Count)
                {
                    return;
                }

                var record = _recordsById[recordId];
                if (record == null)
                {
                    return;
                }

                _recordIdsByKey.Remove(key);
                _recordsById[recordId] = null;
                if ((_builtBuckets & BucketKinds.Char) != 0)
                {
                    RemoveFromCharBuckets(recordId, record.LowerName);
                }

                if ((_builtBuckets & BucketKinds.Bigram) != 0)
                {
                    RemoveFromBigramBuckets(recordId, record.LowerName);
                }

                if ((_builtBuckets & BucketKinds.Trigram) != 0)
                {
                    RemoveFromTrigramBuckets(recordId, record.LowerName);
                }
            }

            private void AddToCharBuckets(int recordId, string lowerName, bool appendOnly)
            {
                if (string.IsNullOrEmpty(lowerName))
                {
                    return;
                }

                if (appendOnly)
                {
                    for (var i = 0; i < lowerName.Length; i++)
                    {
                        AddToBucket(_charBuckets, lowerName[i], recordId, appendOnly: true);
                    }

                    return;
                }

                var seen = new HashSet<char>();
                for (var i = 0; i < lowerName.Length; i++)
                {
                    var token = lowerName[i];
                    if (!seen.Add(token))
                    {
                        continue;
                    }

                    AddToBucket(_charBuckets, token, recordId, appendOnly);
                }
            }

            private void AddToBigramBuckets(int recordId, string lowerName, bool appendOnly)
            {
                if (string.IsNullOrEmpty(lowerName) || lowerName.Length < 2)
                {
                    return;
                }

                if (appendOnly)
                {
                    for (var i = 0; i < lowerName.Length - 1; i++)
                    {
                        AddToBucket(_bigramBuckets, PackBigram(lowerName[i], lowerName[i + 1]), recordId, appendOnly: true);
                    }

                    return;
                }

                var seen = new HashSet<uint>();
                for (var i = 0; i < lowerName.Length - 1; i++)
                {
                    var token = PackBigram(lowerName[i], lowerName[i + 1]);
                    if (!seen.Add(token))
                    {
                        continue;
                    }

                    AddToBucket(_bigramBuckets, token, recordId, appendOnly);
                }
            }

            private void AddToTrigramBuckets(int recordId, string lowerName, bool appendOnly)
            {
                if (string.IsNullOrEmpty(lowerName) || lowerName.Length < 3)
                {
                    return;
                }

                if (appendOnly)
                {
                    for (var i = 0; i < lowerName.Length - 2; i++)
                    {
                        AddToBucket(_trigramBuckets, PackTrigram(lowerName[i], lowerName[i + 1], lowerName[i + 2]), recordId, appendOnly: true);
                    }

                    return;
                }

                var seen = new HashSet<ulong>();
                for (var i = 0; i < lowerName.Length - 2; i++)
                {
                    var token = PackTrigram(lowerName[i], lowerName[i + 1], lowerName[i + 2]);
                    if (!seen.Add(token))
                    {
                        continue;
                    }

                    AddToBucket(_trigramBuckets, token, recordId, appendOnly);
                }
            }

            private void RemoveFromCharBuckets(int recordId, string lowerName)
            {
                if (string.IsNullOrEmpty(lowerName))
                {
                    return;
                }

                var seen = new HashSet<char>();
                for (var i = 0; i < lowerName.Length; i++)
                {
                    if (!seen.Add(lowerName[i]))
                    {
                        continue;
                    }

                    RemoveFromBucket(_charBuckets, lowerName[i], recordId);
                }
            }

            private void RemoveFromBigramBuckets(int recordId, string lowerName)
            {
                if (string.IsNullOrEmpty(lowerName) || lowerName.Length < 2)
                {
                    return;
                }

                var seen = new HashSet<uint>();
                for (var i = 0; i < lowerName.Length - 1; i++)
                {
                    var token = PackBigram(lowerName[i], lowerName[i + 1]);
                    if (!seen.Add(token))
                    {
                        continue;
                    }

                    RemoveFromBucket(_bigramBuckets, token, recordId);
                }
            }

            private void RemoveFromTrigramBuckets(int recordId, string lowerName)
            {
                if (string.IsNullOrEmpty(lowerName) || lowerName.Length < 3)
                {
                    return;
                }

                var seen = new HashSet<ulong>();
                for (var i = 0; i < lowerName.Length - 2; i++)
                {
                    var token = PackTrigram(lowerName[i], lowerName[i + 1], lowerName[i + 2]);
                    if (!seen.Add(token))
                    {
                        continue;
                    }

                    RemoveFromBucket(_trigramBuckets, token, recordId);
                }
            }

            private void AddToBucket<TKey>(Dictionary<TKey, List<int>> buckets, TKey key, int recordId, bool appendOnly)
            {
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new List<int>();
                    buckets[key] = bucket;
                }

                if (appendOnly || bucket.Count == 0)
                {
                    if (bucket.Count == 0 || bucket[bucket.Count - 1] != recordId)
                    {
                        bucket.Add(recordId);
                    }

                    return;
                }

                var insertIndex = FindInsertIndex(bucket, recordId);
                bucket.Insert(insertIndex, recordId);
            }

            private void RemoveFromBucket<TKey>(Dictionary<TKey, List<int>> buckets, TKey key, int recordId)
            {
                if (!buckets.TryGetValue(key, out var bucket) || bucket == null || bucket.Count == 0)
                {
                    return;
                }

                while (bucket.Remove(recordId))
                {
                }

                if (bucket.Count == 0)
                {
                    buckets.Remove(key);
                }
            }

            private int FindInsertIndex(List<int> bucket, int recordId)
            {
                var target = _recordsById[recordId];
                var lo = 0;
                var hi = bucket.Count;
                while (lo < hi)
                {
                    var mid = lo + ((hi - lo) / 2);
                    var currentId = bucket[mid];
                    var current = _recordsById[currentId];
                    var compare = ByLowerName.Compare(current, target);
                    if (compare == 0)
                    {
                        compare = currentId.CompareTo(recordId);
                    }

                    if (compare <= 0)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                return lo;
            }

            private static List<ulong> BuildUniqueTrigramKeys(string query)
            {
                var keys = new List<ulong>(Math.Max(query.Length - 2, 1));
                var seen = new HashSet<ulong>();
                for (var i = 0; i < query.Length - 2; i++)
                {
                    var token = PackTrigram(query[i], query[i + 1], query[i + 2]);
                    if (!seen.Add(token))
                    {
                        continue;
                    }

                    keys.Add(token);
                }

                return keys;
            }

            private static uint PackBigram(char first, char second)
            {
                return ((uint)first << 16) | second;
            }

            private static ulong PackTrigram(char first, char second, char third)
            {
                return ((ulong)first << 32) | ((ulong)second << 16) | third;
            }

            private static BucketKinds ToBucketKinds(ContainsAcceleratorBucketKinds requiredBuckets)
            {
                BucketKinds result = BucketKinds.None;
                if ((requiredBuckets & ContainsAcceleratorBucketKinds.Char) != 0)
                {
                    result |= BucketKinds.Char;
                }

                if ((requiredBuckets & ContainsAcceleratorBucketKinds.Bigram) != 0)
                {
                    result |= BucketKinds.Bigram;
                }

                if ((requiredBuckets & ContainsAcceleratorBucketKinds.Trigram) != 0)
                {
                    result |= BucketKinds.Trigram;
                }

                return result;
            }
        }

        #endif

        [Flags]
        private enum ContainsAcceleratorBucketKinds
        {
            None = 0,
            Char = 1,
            Bigram = 2,
            Trigram = 4,
            Short = Char | Bigram,
            All = Char | Bigram | Trigram
        }

        private struct PendingContainsMutation
        {
            public PendingContainsMutationKind Kind;
            public RecordKey Key;
            public FileRecord Record;

            public static PendingContainsMutation ForInsert(FileRecord record)
            {
                return new PendingContainsMutation
                {
                    Kind = PendingContainsMutationKind.Insert,
                    Record = record
                };
            }

            public static PendingContainsMutation ForRemove(RecordKey key)
            {
                return new PendingContainsMutation
                {
                    Kind = PendingContainsMutationKind.Remove,
                    Key = key
                };
            }

            public static PendingContainsMutation ForRestore(RecordKey key)
            {
                return new PendingContainsMutation
                {
                    Kind = PendingContainsMutationKind.Restore,
                    Key = key
                };
            }

            public void ApplyTo(ContainsAccelerator accelerator)
            {
                if (accelerator == null)
                {
                    return;
                }

                if (Kind == PendingContainsMutationKind.Insert)
                {
                    accelerator.WithInserted(Record);
                    return;
                }

                if (Kind == PendingContainsMutationKind.Remove)
                {
                    accelerator.WithRemoved(Key);
                }
            }
        }

        private enum PendingContainsMutationKind
        {
            Insert,
            Remove,
            Restore
        }

        private sealed class ContainsOverlay
        {
            public static readonly ContainsOverlay Empty = new ContainsOverlay(
                new Dictionary<RecordKey, FileRecord>(),
                new HashSet<RecordKey>(),
                new Dictionary<FrnDriveKey, List<RecordKey>>(),
                false);

            private readonly Dictionary<RecordKey, FileRecord> _addedByKey;
            private readonly HashSet<RecordKey> _removedKeys;
            private readonly Dictionary<FrnDriveKey, List<RecordKey>> _addedKeysByFrn;

            private ContainsOverlay(
                Dictionary<RecordKey, FileRecord> addedByKey,
                HashSet<RecordKey> removedKeys,
                Dictionary<FrnDriveKey, List<RecordKey>> addedKeysByFrn,
                bool isOverflowed)
            {
                _addedByKey = addedByKey ?? new Dictionary<RecordKey, FileRecord>();
                _removedKeys = removedKeys ?? new HashSet<RecordKey>();
                _addedKeysByFrn = addedKeysByFrn ?? new Dictionary<FrnDriveKey, List<RecordKey>>();
                IsOverflowed = isOverflowed;
                AddedRecords = new List<FileRecord>(_addedByKey.Values);
            }

            public IReadOnlyList<FileRecord> AddedRecords { get; }

            public bool IsOverflowed { get; }

            public int AddedCount => _addedByKey.Count;

            public int RemovedCount => _removedKeys.Count;

            public bool ContainsRemoved(RecordKey key)
            {
                return _removedKeys.Contains(key);
            }

            public bool ContainsAdded(RecordKey key)
            {
                return _addedByKey.ContainsKey(key);
            }

            public HashSet<RecordKey> GetRemovedKeysSnapshot()
            {
                return _removedKeys.Count == 0
                    ? new HashSet<RecordKey>()
                    : new HashSet<RecordKey>(_removedKeys);
            }

            public List<FileRecord> GetAddedRecordsSnapshot()
            {
                return AddedRecords.Count == 0
                    ? new List<FileRecord>(0)
                    : new List<FileRecord>(AddedRecords);
            }

            public List<RecordKey> GetAddedKeysByFrn(ulong frn, char driveLetter)
            {
                if (frn == 0 || _addedKeysByFrn.Count == 0)
                {
                    return new List<RecordKey>(0);
                }

                return _addedKeysByFrn.TryGetValue(new FrnDriveKey(frn, driveLetter), out var matches)
                    ? new List<RecordKey>(matches)
                    : new List<RecordKey>(0);
            }

            public ContainsOverlay WithMutation(PendingContainsMutation mutation, int maxMutations)
            {
                var addedByKey = new Dictionary<RecordKey, FileRecord>(_addedByKey);
                var removedKeys = new HashSet<RecordKey>(_removedKeys);
                ApplyMutation(addedByKey, removedKeys, mutation);
                if (addedByKey.Count + removedKeys.Count > maxMutations)
                {
                    return new ContainsOverlay(addedByKey, removedKeys, BuildAddedKeysByFrn(addedByKey), true);
                }

                return new ContainsOverlay(addedByKey, removedKeys, BuildAddedKeysByFrn(addedByKey), IsOverflowed);
            }

            public ContainsOverlay WithMutations(IReadOnlyList<PendingContainsMutation> mutations, int maxMutations)
            {
                if (mutations == null || mutations.Count == 0)
                {
                    return this;
                }

                var addedByKey = new Dictionary<RecordKey, FileRecord>(_addedByKey);
                var removedKeys = new HashSet<RecordKey>(_removedKeys);
                for (var i = 0; i < mutations.Count; i++)
                {
                    ApplyMutation(addedByKey, removedKeys, mutations[i]);
                    if (addedByKey.Count + removedKeys.Count > maxMutations)
                    {
                        return new ContainsOverlay(addedByKey, removedKeys, BuildAddedKeysByFrn(addedByKey), true);
                    }
                }

                return new ContainsOverlay(addedByKey, removedKeys, BuildAddedKeysByFrn(addedByKey), IsOverflowed);
            }

            public ContainsOverlay PruneForBase(ContainsAccelerator accelerator)
            {
                if (accelerator == null || IsOverflowed || (AddedCount == 0 && RemovedCount == 0))
                {
                    return this;
                }

                var addedByKey = new Dictionary<RecordKey, FileRecord>();
                foreach (var entry in _addedByKey)
                {
                    if (!accelerator.ContainsRecord(entry.Key))
                    {
                        addedByKey[entry.Key] = entry.Value;
                    }
                }

                var removedKeys = new HashSet<RecordKey>();
                foreach (var key in _removedKeys)
                {
                    if (accelerator.ContainsRecord(key))
                    {
                        removedKeys.Add(key);
                    }
                }

                return addedByKey.Count == 0 && removedKeys.Count == 0
                    ? Empty
                    : new ContainsOverlay(addedByKey, removedKeys, BuildAddedKeysByFrn(addedByKey), false);
            }

            public static ContainsOverlay FromPending(IReadOnlyList<PendingContainsMutation> pending)
            {
                if (pending == null || pending.Count == 0)
                {
                    return Empty;
                }

                var addedByKey = new Dictionary<RecordKey, FileRecord>();
                var removedKeys = new HashSet<RecordKey>();
                for (var i = 0; i < pending.Count; i++)
                {
                    ApplyMutation(addedByKey, removedKeys, pending[i]);
                }

                return new ContainsOverlay(addedByKey, removedKeys, BuildAddedKeysByFrn(addedByKey), false);
            }

            private static void ApplyMutation(
                Dictionary<RecordKey, FileRecord> addedByKey,
                HashSet<RecordKey> removedKeys,
                PendingContainsMutation mutation)
            {
                if (mutation.Kind == PendingContainsMutationKind.Insert)
                {
                    var key = RecordKey.FromRecord(mutation.Record);
                    removedKeys.Remove(key);
                    addedByKey[key] = mutation.Record;
                    return;
                }

                if (mutation.Kind == PendingContainsMutationKind.Restore)
                {
                    removedKeys.Remove(mutation.Key);
                    return;
                }

                addedByKey.Remove(mutation.Key);
                removedKeys.Add(mutation.Key);
            }

            private static Dictionary<FrnDriveKey, List<RecordKey>> BuildAddedKeysByFrn(
                Dictionary<RecordKey, FileRecord> addedByKey)
            {
                if (addedByKey == null || addedByKey.Count == 0)
                    return new Dictionary<FrnDriveKey, List<RecordKey>>();

                var index = new Dictionary<FrnDriveKey, List<RecordKey>>();
                foreach (var key in addedByKey.Keys)
                {
                    if (key.Frn == 0)
                        continue;

                    var frnKey = new FrnDriveKey(key.Frn, key.DriveLetter);
                    if (!index.TryGetValue(frnKey, out var keys))
                    {
                        keys = new List<RecordKey>(1);
                        index[frnKey] = keys;
                    }

                    keys.Add(key);
                }

                return index;
            }
        }

        private struct FrnDriveKey : IEquatable<FrnDriveKey>
        {
            public FrnDriveKey(ulong frn, char driveLetter)
            {
                Frn = frn;
                DriveLetter = char.ToUpperInvariant(driveLetter);
            }

            public ulong Frn { get; }
            public char DriveLetter { get; }

            public bool Equals(FrnDriveKey other)
            {
                return Frn == other.Frn && DriveLetter == other.DriveLetter;
            }

            public override bool Equals(object obj)
            {
                return obj is FrnDriveKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (Frn.GetHashCode() * 397) ^ DriveLetter.GetHashCode();
                }
            }
        }

        private struct RecordKey : IEquatable<RecordKey>
        {
            public RecordKey(ulong frn, string lowerName, ulong parentFrn, char driveLetter)
            {
                Frn = frn;
                LowerName = lowerName;
                ParentFrn = parentFrn;
                DriveLetter = driveLetter;
            }

            public ulong Frn { get; }
            public string LowerName { get; }
            public ulong ParentFrn { get; }
            public char DriveLetter { get; }

            public bool IsValid => DriveLetter != '\0'
                                   && !string.IsNullOrEmpty(LowerName)
                                   && (Frn != 0 || ParentFrn != 0);

            public static RecordKey FromRecord(FileRecord record)
            {
                return new RecordKey(record?.Frn ?? 0, record?.LowerName, record?.ParentFrn ?? 0, record?.DriveLetter ?? '\0');
            }

            public static RecordKey FromChange(UsnChangeEntry change)
            {
                return new RecordKey(change?.Frn ?? 0, change?.LowerName, change?.ParentFrn ?? 0, change?.DriveLetter ?? '\0');
            }

            public static RecordKey FromRenameOld(UsnChangeEntry change)
            {
                return new RecordKey(change?.Frn ?? 0, change?.OldLowerName, change?.OldParentFrn ?? 0, change?.DriveLetter ?? '\0');
            }

            public bool Equals(RecordKey other)
            {
                if (Frn != 0 || other.Frn != 0)
                {
                    return Frn == other.Frn
                           && ParentFrn == other.ParentFrn
                           && DriveLetter == other.DriveLetter
                           && string.Equals(LowerName, other.LowerName, StringComparison.Ordinal);
                }

                return ParentFrn == other.ParentFrn
                       && DriveLetter == other.DriveLetter
                       && string.Equals(LowerName, other.LowerName, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is RecordKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    if (Frn != 0)
                    {
                        var frnHash = Frn.GetHashCode();
                        frnHash = (frnHash * 397) ^ ParentFrn.GetHashCode();
                        frnHash = (frnHash * 397) ^ DriveLetter.GetHashCode();
                        frnHash = (frnHash * 397) ^ (LowerName != null ? StringComparer.Ordinal.GetHashCode(LowerName) : 0);
                        return frnHash;
                    }

                    var hash = LowerName != null ? StringComparer.Ordinal.GetHashCode(LowerName) : 0;
                    hash = (hash * 397) ^ ParentFrn.GetHashCode();
                    hash = (hash * 397) ^ DriveLetter.GetHashCode();
                    return hash;
                }
            }
        }
    }
}
