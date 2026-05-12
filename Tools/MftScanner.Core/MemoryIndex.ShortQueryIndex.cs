using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MftScanner
{
    public sealed partial class MemoryIndex
    {
        private NonAsciiShortQueryIndex _nonAsciiShortQueryIndex = NonAsciiShortQueryIndex.Empty;

        private static void BuildShortQueryIndexes(
            FileRecord[] arr,
            out SingleCharAsciiIndex singleCharAsciiIndex,
            out BigramAsciiIndex bigramAsciiIndex,
            out NonAsciiShortQueryIndex nonAsciiShortQueryIndex)
        {
            singleCharAsciiIndex = BuildSingleCharAsciiIndex(arr);
            bigramAsciiIndex = BuildBigramAsciiIndex(arr);
            nonAsciiShortQueryIndex = BuildNonAsciiShortQueryIndex(arr, CancellationToken.None);
        }

        private static NonAsciiShortQueryIndex BuildNonAsciiShortQueryIndex(FileRecord[] records, CancellationToken ct)
        {
            if (records == null || records.Length == 0)
            {
                return NonAsciiShortQueryIndex.Empty;
            }

            var stopwatch = Stopwatch.StartNew();
            var singleBuckets = new Dictionary<int, List<int>>();
            var bigramBuckets = new Dictionary<ulong, List<int>>();

            for (var i = 0; i < records.Length; i++)
            {
                if (((i + 1) & 0xFFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                var record = records[i];
                var lowerName = record?.LowerName;
                if (string.IsNullOrEmpty(lowerName))
                {
                    continue;
                }

                var recordId = i + 1;
                var seenSingles = new HashSet<int>();
                var seenBigrams = new HashSet<ulong>();

                for (var j = 0; j < lowerName.Length; j++)
                {
                    var ch = lowerName[j];
                    if (ch < SingleCharAsciiLimit)
                    {
                        continue;
                    }

                    if (seenSingles.Add(ch))
                    {
                        AddPosting(singleBuckets, ch, recordId);
                    }

                    if (j >= lowerName.Length - 1)
                    {
                        continue;
                    }

                    var next = lowerName[j + 1];
                    if (next < SingleCharAsciiLimit)
                    {
                        continue;
                    }

                    var token = PackShortQueryBigram(ch, next);
                    if (seenBigrams.Add(token))
                    {
                        AddPosting(bigramBuckets, token, recordId);
                    }
                }
            }

            stopwatch.Stop();
            IndexPerfLog.Write("INDEX",
                $"[CONTAINS SHORT QUERY BUILD] outcome=success records={records.Length} nonAsciiSingles={singleBuckets.Count} nonAsciiBigrams={bigramBuckets.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return NonAsciiShortQueryIndex.FromBuckets(records.Length, singleBuckets, bigramBuckets);
        }

        internal ShortQuerySnapshot ExportShortQuerySnapshot(out ulong contentFingerprint)
        {
            contentFingerprint = 0;
            var records = SortedArray;
            if (records == null || records.Length == 0)
            {
                return new ShortQuerySnapshot(0, Array.Empty<int>(), Array.Empty<int[]>(), Array.Empty<int>(), Array.Empty<int[]>(), Array.Empty<ShortQueryPageSampleSnapshot>(), Array.Empty<ShortQueryPostingSnapshot>(), Array.Empty<ShortQueryPostingSnapshot>());
            }

            if (!HasShortQueryStructuresReady())
            {
                EnsureShortQueryStructures(CancellationToken.None, "short-query-export");
            }

            var recordCount = records.Length;
            var singleCounts = CloneIntArray(_singleCharAsciiCounts, 128);
            var singleDriveCounts = CloneIntMatrix(_singleCharAsciiDriveCounts, 26, 128);
            var bigramCounts = CloneIntArray(_bigramAsciiCounts, BigramAsciiTokenCount);
            var bigramDriveCounts = CloneIntMatrix(_bigramAsciiDriveCounts, 26, BigramAsciiTokenCount);
            var bigramSamples = CloneBigramAsciiPageSamples(records, _bigramAsciiPageSamples);
            var nonAsciiIndex = Volatile.Read(ref _nonAsciiShortQueryIndex) ?? NonAsciiShortQueryIndex.Empty;

            contentFingerprint = nonAsciiIndex.ContentFingerprint != 0
                ? nonAsciiIndex.ContentFingerprint
                : IndexSnapshotFingerprint.Compute(records);

            return new ShortQuerySnapshot(
                recordCount,
                singleCounts,
                singleDriveCounts,
                bigramCounts,
                bigramDriveCounts,
                bigramSamples,
                nonAsciiIndex.ExportSingles(),
                nonAsciiIndex.ExportBigrams());
        }

        internal bool TryLoadShortQuerySnapshot(ShortQuerySnapshot snapshot, ulong contentFingerprint = 0)
        {
            if (snapshot == null)
            {
                return false;
            }

            _lock.EnterWriteLock();
            try
            {
                if ((SortedArray?.Length ?? 0) != snapshot.RecordCount)
                {
                    return false;
                }

                var records = SortedArray ?? Array.Empty<FileRecord>();
                if ((snapshot.SingleCharAsciiCounts?.Length ?? 0) != 128
                    || (snapshot.SingleCharAsciiDriveCounts?.Length ?? 0) != 26
                    || (snapshot.BigramAsciiCounts?.Length ?? 0) != BigramAsciiTokenCount
                    || (snapshot.BigramAsciiDriveCounts?.Length ?? 0) != 26)
                {
                    return false;
                }

                for (var i = 0; i < snapshot.SingleCharAsciiDriveCounts.Length; i++)
                {
                    if (snapshot.SingleCharAsciiDriveCounts[i] == null
                        || snapshot.SingleCharAsciiDriveCounts[i].Length != 128)
                    {
                        return false;
                    }
                }

                for (var i = 0; i < snapshot.BigramAsciiDriveCounts.Length; i++)
                {
                    if (snapshot.BigramAsciiDriveCounts[i] == null
                        || snapshot.BigramAsciiDriveCounts[i].Length != BigramAsciiTokenCount)
                    {
                        return false;
                    }
                }

                _singleCharAsciiBitsets = null;
                _singleCharAsciiCounts = CloneIntArray(snapshot.SingleCharAsciiCounts, 128);
                _singleCharAsciiDriveCounts = CloneIntMatrix(snapshot.SingleCharAsciiDriveCounts, 26, 128);
                _singleCharAsciiRecordCount = snapshot.RecordCount;
                _bigramAsciiCounts = CloneIntArray(snapshot.BigramAsciiCounts, BigramAsciiTokenCount);
                _bigramAsciiDriveCounts = CloneIntMatrix(snapshot.BigramAsciiDriveCounts, 26, BigramAsciiTokenCount);
                var bigramAsciiPageSamples = ExpandBigramAsciiPageSamples(records, snapshot.BigramAsciiPageSamples);
                if (NeedsBigramAsciiPageSampleRefresh(_bigramAsciiCounts, bigramAsciiPageSamples))
                {
                    var sampleStopwatch = Stopwatch.StartNew();
                    bigramAsciiPageSamples = BuildBigramAsciiPageSamples(records, _bigramAsciiCounts);
                    sampleStopwatch.Stop();
                    IndexPerfLog.Write("INDEX",
                        $"[SHORT QUERY SNAPSHOT LOAD] outcome=refreshed-bigram-samples sampleLimit={BigramAsciiPageSampleLimit} elapsedMs={sampleStopwatch.ElapsedMilliseconds}");
                }

                _bigramAsciiPageSamples = bigramAsciiPageSamples;
                _bigramAsciiRecordCount = snapshot.RecordCount;
                _nonAsciiShortQueryIndex = NonAsciiShortQueryIndex.FromSnapshot(snapshot, contentFingerprint);
                ClearShortContainsHotBuckets();
                IndexPerfLog.Write("INDEX",
                    $"[SHORT QUERY SNAPSHOT LOAD] outcome=success records={snapshot.RecordCount} nonAsciiBuckets={_nonAsciiShortQueryIndex.BucketCount} " +
                    $"nonAsciiPostings={_nonAsciiShortQueryIndex.PostingCount}");
                return true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        private static bool NeedsBigramAsciiPageSampleRefresh(int[] counts, FileRecord[][] samples)
        {
            if (counts == null || counts.Length != BigramAsciiTokenCount)
            {
                return false;
            }

            samples = samples ?? Array.Empty<FileRecord[]>();
            for (var token = 0; token < counts.Length; token++)
            {
                var count = counts[token];
                if (count <= 0 || count > BigramAsciiPageSampleLimit)
                {
                    continue;
                }

                var sample = token < samples.Length ? samples[token] : null;
                if (sample == null || sample.Length < Math.Min(count, BigramAsciiPageSampleLimit))
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryShortContainsSearchInPathScope(
            string query,
            SearchTypeFilter filter,
            int offset,
            int maxResults,
            char driveLetter,
            IReadOnlyList<ulong> directoryFrns,
            CancellationToken ct,
            out ContainsSearchResult result)
        {
            result = null;
            if (string.IsNullOrEmpty(query) || query.Length > 2 || directoryFrns == null || directoryFrns.Count == 0)
            {
                return false;
            }

            var directorySet = new HashSet<ulong>(directoryFrns);
            var dl = char.ToUpperInvariant(driveLetter);
            var activeOverlay = GetLiveOverlaySnapshot();
            if (query.Length == 1 && query[0] < SingleCharAsciiLimit)
            {
                return TrySingleCharAsciiPathSearch(query, filter, offset, maxResults, dl, directorySet, ct, activeOverlay, out result);
            }

            if (query.Length == 2 && query[0] < SingleCharAsciiLimit && query[1] < SingleCharAsciiLimit)
            {
                return TryBigramAsciiPathSearch(query, filter, offset, maxResults, dl, directorySet, ct, activeOverlay, out result);
            }

            return TryNonAsciiPathSearch(query, filter, offset, maxResults, dl, directorySet, ct, activeOverlay, out result);
        }

        public void QueueEnsureShortQueryStructures(string reason)
        {
            var recordCount = SortedArray?.Length ?? 0;
            if (recordCount == 0 || HasShortQueryStructuresReady())
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    EnsureShortQueryStructures(CancellationToken.None, reason);
                }
                catch (Exception ex)
                {
                    IndexPerfLog.Write("INDEX",
                        $"[SHORT QUERY STRUCTURES] outcome=failed reason={IndexPerfLog.FormatValue(reason)} error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
                }
            });
        }

        public void EnsureShortQueryStructuresReady(CancellationToken ct, string reason)
        {
            while (!ct.IsCancellationRequested)
            {
                EnsureShortQueryStructures(ct, reason);

                var recordCount = SortedArray?.Length ?? 0;
                if (recordCount == 0 || HasShortQueryStructuresReady())
                {
                    return;
                }

                Thread.Sleep(25);
            }

            ct.ThrowIfCancellationRequested();
        }

        private bool TrySingleCharAsciiPathSearch(
            string query,
            SearchTypeFilter filter,
            int offset,
            int maxResults,
            char driveLetter,
            HashSet<ulong> directorySet,
            CancellationToken ct,
            ContainsOverlay overlay,
            out ContainsSearchResult result)
        {
            result = null;
            if (string.IsNullOrEmpty(query) || query.Length != 1 || query[0] >= SingleCharAsciiLimit)
            {
                return false;
            }

            var activeOverlay = overlay ?? ContainsOverlay.Empty;
            var normalizedOffset = Math.Max(offset, 0);
            var normalizedMaxResults = Math.Max(maxResults, 0);
            var page = new List<FileRecord>(Math.Min(normalizedMaxResults <= 0 ? 0 : normalizedMaxResults, 64));
            var total = 0;
            var candidateCount = 0;
            var filterAll = filter == SearchTypeFilter.All;
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
                    bitset = bitsets[query[0]];
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            var hasDeletedOverlay = HasDeletedOverlay || activeOverlay.RemovedCount > 0;
            var seen = new HashSet<PathMatchKey>();
            var pageMatched = 0;
            var stopwatch = Stopwatch.StartNew();

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
                            || char.ToUpperInvariant(record.DriveLetter) != driveLetter
                            || (directorySet != null && !directorySet.Contains(record.ParentFrn))
                            || (hasDeletedOverlay && IsDeleted(record, activeOverlay))
                            || (!filterAll && !MatchesFilter(record, filter)))
                        {
                            continue;
                        }

                        var pathKey = new PathMatchKey(record.DriveLetter, record.ParentFrn, record.LowerName);
                        if (!seen.Add(pathKey))
                        {
                            continue;
                        }

                        candidateCount++;
                        total++;
                        if (total > normalizedOffset && page.Count < normalizedMaxResults)
                        {
                            page.Add(record);
                            pageMatched++;
                        }
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
                        || char.ToUpperInvariant(record.DriveLetter) != driveLetter
                        || (directorySet != null && !directorySet.Contains(record.ParentFrn))
                        || (hasDeletedOverlay && IsDeleted(record, activeOverlay))
                        || (!filterAll && !MatchesFilter(record, filter))
                        || !NameContains(record, query))
                    {
                        continue;
                    }

                    var pathKey = new PathMatchKey(record.DriveLetter, record.ParentFrn, record.LowerName);
                    if (!seen.Add(pathKey))
                    {
                        continue;
                    }

                    candidateCount++;
                    total++;
                    if (total > normalizedOffset && page.Count < normalizedMaxResults)
                    {
                        page.Add(record);
                        pageMatched++;
                    }
                }
            }

            AddShortAsciiOverlayMatchesInScope(
                query,
                filter,
                driveLetter,
                directorySet,
                normalizedOffset,
                normalizedMaxResults,
                page,
                ref total,
                ct,
                activeOverlay,
                seen);

            stopwatch.Stop();
            result = new ContainsSearchResult
            {
                Mode = "short-index-char+path",
                CandidateCount = candidateCount,
                Total = total,
                Page = page,
                VerifyMs = stopwatch.ElapsedMilliseconds,
                IncludesLiveOverlay = activeOverlay.AddedCount > 0
            };
            return true;
        }

        private bool TryBigramAsciiPathSearch(
            string query,
            SearchTypeFilter filter,
            int offset,
            int maxResults,
            char driveLetter,
            HashSet<ulong> directorySet,
            CancellationToken ct,
            ContainsOverlay overlay,
            out ContainsSearchResult result)
        {
            result = null;
            if (string.IsNullOrEmpty(query)
                || query.Length != 2
                || query[0] >= SingleCharAsciiLimit
                || query[1] >= SingleCharAsciiLimit)
            {
                return false;
            }

            var activeOverlay = overlay ?? ContainsOverlay.Empty;
            var normalizedOffset = Math.Max(offset, 0);
            var normalizedMaxResults = Math.Max(maxResults, 0);
            var page = new List<FileRecord>(Math.Min(normalizedMaxResults <= 0 ? 0 : normalizedMaxResults, 64));
            var total = 0;
            var candidateCount = 0;
            var filterAll = filter == SearchTypeFilter.All;
            FileRecord[] records;
            int recordCount;
            _lock.EnterReadLock();
            try
            {
                records = SortedArray;
                recordCount = records?.Length ?? 0;
                var counts = Volatile.Read(ref _bigramAsciiCounts);
                if (counts == null
                    || _bigramAsciiRecordCount != recordCount
                    || recordCount == 0)
                {
                    return false;
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            var hasDeletedOverlay = HasDeletedOverlay || activeOverlay.RemovedCount > 0;
            var seen = new HashSet<PathMatchKey>();
            var stopwatch = Stopwatch.StartNew();
            for (var i = 0; i < recordCount; i++)
            {
                if (((i + 1) & 0x3FFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                var record = records[i];
                if (record == null
                    || char.ToUpperInvariant(record.DriveLetter) != driveLetter
                    || (directorySet != null && !directorySet.Contains(record.ParentFrn))
                    || (hasDeletedOverlay && IsDeleted(record, activeOverlay))
                    || (!filterAll && !MatchesFilter(record, filter))
                    || !NameContains(record, query))
                {
                    continue;
                }

                var pathKey = new PathMatchKey(record.DriveLetter, record.ParentFrn, record.LowerName);
                if (!seen.Add(pathKey))
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

            AddShortAsciiOverlayMatchesInScope(
                query,
                filter,
                driveLetter,
                directorySet,
                normalizedOffset,
                normalizedMaxResults,
                page,
                ref total,
                ct,
                activeOverlay,
                seen);

            stopwatch.Stop();
            result = new ContainsSearchResult
            {
                Mode = "short-index-bigram+path",
                CandidateCount = candidateCount,
                Total = total,
                Page = page,
                VerifyMs = stopwatch.ElapsedMilliseconds,
                IncludesLiveOverlay = activeOverlay.AddedCount > 0
            };
            return true;
        }

        private bool TryNonAsciiPathSearch(
            string query,
            SearchTypeFilter filter,
            int offset,
            int maxResults,
            char driveLetter,
            HashSet<ulong> directorySet,
            CancellationToken ct,
            ContainsOverlay overlay,
            out ContainsSearchResult result)
        {
            result = null;
            if (string.IsNullOrEmpty(query) || query.Length > 2)
            {
                return false;
            }

            if (query.Length == 1 && query[0] < SingleCharAsciiLimit)
            {
                return false;
            }

            if (query.Length == 2 && query[0] < SingleCharAsciiLimit && query[1] < SingleCharAsciiLimit)
            {
                return false;
            }

            var activeOverlay = overlay ?? ContainsOverlay.Empty;
            FileRecord[] records;
            NonAsciiShortQueryIndex index;
            int[] postings;
            _lock.EnterReadLock();
            try
            {
                records = SortedArray;
                index = Volatile.Read(ref _nonAsciiShortQueryIndex) ?? NonAsciiShortQueryIndex.Empty;
                if (records == null || records.Length == 0 || index.RecordCount != records.Length)
                {
                    return false;
                }

                if (!index.TryGetPostings(query, out postings))
                {
                    postings = Array.Empty<int>();
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            var normalizedOffset = Math.Max(offset, 0);
            var normalizedMaxResults = Math.Max(maxResults, 0);
            var page = new List<FileRecord>(Math.Min(normalizedMaxResults <= 0 ? 0 : normalizedMaxResults, 64));
            var total = 0;
            var candidateCount = 0;
            var filterAll = filter == SearchTypeFilter.All;
            var hasDeletedOverlay = HasDeletedOverlay || activeOverlay.RemovedCount > 0;
            var seen = new HashSet<PathMatchKey>();
            var stopwatch = Stopwatch.StartNew();

            for (var i = 0; i < postings.Length; i++)
            {
                if (((i + 1) & 0x7FF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                var recordId = postings[i];
                var indexId = recordId - 1;
                if ((uint)indexId >= (uint)records.Length)
                {
                    continue;
                }

                var record = records[indexId];
                if (record == null
                    || char.ToUpperInvariant(record.DriveLetter) != driveLetter
                    || (directorySet != null && !directorySet.Contains(record.ParentFrn))
                    || (hasDeletedOverlay && IsDeleted(record, activeOverlay))
                    || (!filterAll && !MatchesFilter(record, filter))
                    || !NameContains(record, query))
                {
                    continue;
                }

                var pathKey = new PathMatchKey(record.DriveLetter, record.ParentFrn, record.LowerName);
                if (!seen.Add(pathKey))
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

            AddNonAsciiOverlayMatchesInScope(
                query,
                filter,
                driveLetter,
                directorySet,
                normalizedOffset,
                normalizedMaxResults,
                page,
                ref total,
                ct,
                activeOverlay,
                seen);

            stopwatch.Stop();
            result = new ContainsSearchResult
            {
                Mode = query.Length == 1 ? "short-index-char+path" : "short-index-bigram+path",
                CandidateCount = candidateCount,
                Total = total,
                Page = page,
                VerifyMs = stopwatch.ElapsedMilliseconds,
                IncludesLiveOverlay = activeOverlay.AddedCount > 0
            };
            return true;
        }

        private static void AddShortAsciiOverlayMatchesInScope(
            string query,
            SearchTypeFilter filter,
            char driveLetter,
            HashSet<ulong> directorySet,
            int offset,
            int maxResults,
            List<FileRecord> page,
            ref int total,
            CancellationToken ct,
            ContainsOverlay overlay,
            HashSet<PathMatchKey> seen)
        {
            if (overlay == null || overlay.AddedRecords.Count == 0)
                return;

            var dl = char.ToUpperInvariant(driveLetter);
            for (var i = 0; i < overlay.AddedRecords.Count; i++)
            {
                if (((i + 1) & 0x7FF) == 0)
                    ct.ThrowIfCancellationRequested();

                var record = overlay.AddedRecords[i];
                if (record == null
                    || overlay.ContainsRemoved(RecordKey.FromRecord(record))
                    || char.ToUpperInvariant(record.DriveLetter) != dl
                    || (directorySet != null && !directorySet.Contains(record.ParentFrn))
                    || !NameContains(record, query)
                    || !MatchesFilter(record, filter))
                {
                    continue;
                }

                var pathKey = new PathMatchKey(record.DriveLetter, record.ParentFrn, record.LowerName);
                if (seen != null && !seen.Add(pathKey))
                {
                    continue;
                }

                total++;
                if (total > offset && page.Count < maxResults)
                    page.Add(record);
            }
        }

        private static void AddNonAsciiOverlayMatchesInScope(
            string query,
            SearchTypeFilter filter,
            char driveLetter,
            HashSet<ulong> directorySet,
            int offset,
            int maxResults,
            List<FileRecord> page,
            ref int total,
            CancellationToken ct,
            ContainsOverlay overlay,
            HashSet<PathMatchKey> seen)
        {
            if (overlay == null || overlay.AddedRecords.Count == 0)
                return;

            var dl = char.ToUpperInvariant(driveLetter);
            for (var i = 0; i < overlay.AddedRecords.Count; i++)
            {
                if (((i + 1) & 0x7FF) == 0)
                    ct.ThrowIfCancellationRequested();

                var record = overlay.AddedRecords[i];
                if (record == null
                    || overlay.ContainsRemoved(RecordKey.FromRecord(record))
                    || char.ToUpperInvariant(record.DriveLetter) != dl
                    || (directorySet != null && !directorySet.Contains(record.ParentFrn))
                    || !NameContains(record, query)
                    || !MatchesFilter(record, filter))
                {
                    continue;
                }

                var pathKey = new PathMatchKey(record.DriveLetter, record.ParentFrn, record.LowerName);
                if (seen != null && !seen.Add(pathKey))
                {
                    continue;
                }

                total++;
                if (total > offset && page.Count < maxResults)
                    page.Add(record);
            }
        }

        private void EnsureShortQueryStructures(CancellationToken ct, string reason)
        {
            FileRecord[] snapshot;
            long contentVersion;
            _lock.EnterReadLock();
            try
            {
                if (HasShortQueryStructuresReady())
                {
                    return;
                }

                snapshot = SortedArray ?? Array.Empty<FileRecord>();
                contentVersion = ContentVersion;
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
                if (ContentVersion != contentVersion || !ReferenceEquals(snapshot, SortedArray))
                {
                    return;
                }

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

        private bool HasShortQueryStructuresReady()
        {
            var recordCount = SortedArray?.Length ?? 0;
            var index = Volatile.Read(ref _nonAsciiShortQueryIndex) ?? NonAsciiShortQueryIndex.Empty;
            return recordCount == 0
                   || (Volatile.Read(ref _singleCharAsciiCounts) != null
                       && Volatile.Read(ref _singleCharAsciiRecordCount) == recordCount
                       && Volatile.Read(ref _bigramAsciiCounts) != null
                       && Volatile.Read(ref _bigramAsciiRecordCount) == recordCount
                       && index.RecordCount == recordCount);
        }

        private bool TryShortNonAsciiSearch(
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
            if (string.IsNullOrEmpty(query) || query.Length > 2)
            {
                return false;
            }

            if (query.Length == 1 && query[0] < SingleCharAsciiLimit)
            {
                return false;
            }

            if (query.Length == 2 && query[0] < SingleCharAsciiLimit && query[1] < SingleCharAsciiLimit)
            {
                return false;
            }

            var activeOverlay = overlay ?? ContainsOverlay.Empty;
            FileRecord[] records;
            NonAsciiShortQueryIndex index;
            int[] postings;
            _lock.EnterReadLock();
            try
            {
                records = SortedArray;
                index = Volatile.Read(ref _nonAsciiShortQueryIndex) ?? NonAsciiShortQueryIndex.Empty;
                if (records == null || records.Length == 0 || index.RecordCount != records.Length)
                {
                    return false;
                }

                if (!index.TryGetPostings(query, out postings))
                {
                    postings = Array.Empty<int>();
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            var normalizedOffset = Math.Max(offset, 0);
            var normalizedMaxResults = Math.Max(maxResults, 0);
            var page = new List<FileRecord>(Math.Min(normalizedMaxResults <= 0 ? 0 : normalizedMaxResults, 64));
            var total = 0;
            var candidateCount = postings.Length;
            var hasDrive = driveLetter.HasValue;
            var dl = hasDrive ? char.ToUpperInvariant(driveLetter.Value) : '\0';
            var filterAll = filter == SearchTypeFilter.All;
            var stopwatch = Stopwatch.StartNew();

            for (var i = 0; i < postings.Length; i++)
            {
                if (((i + 1) & 0x7FF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                var recordId = postings[i];
                var indexId = recordId - 1;
                if ((uint)indexId >= (uint)records.Length)
                {
                    continue;
                }

                var record = records[indexId];
                if (record == null
                    || (hasDrive && char.ToUpperInvariant(record.DriveLetter) != dl)
                    || IsDeleted(record, activeOverlay)
                    || (!filterAll && !MatchesFilter(record, filter))
                    || !NameContains(record, query))
                {
                    continue;
                }

                total++;
                if (total > normalizedOffset && page.Count < normalizedMaxResults)
                {
                    page.Add(record);
                }
            }

            AddShortAsciiOverlayMatches(query, filter, hasDrive ? dl : (char?)null, normalizedOffset, normalizedMaxResults, page, ref total, ct, activeOverlay);
            stopwatch.Stop();
            result = new ContainsSearchResult
            {
                Mode = query.Length == 1
                    ? (hasDrive ? "short-index-char+drive" : "short-index-char")
                    : (hasDrive ? "short-index-bigram+drive" : "short-index-bigram"),
                CandidateCount = candidateCount,
                Total = total,
                Page = page,
                VerifyMs = stopwatch.ElapsedMilliseconds,
                IncludesLiveOverlay = activeOverlay.AddedCount > 0
            };
            return true;
        }

        private static void AddPosting(Dictionary<int, List<int>> buckets, int token, int recordId)
        {
            if (!buckets.TryGetValue(token, out var list))
            {
                list = new List<int>(4);
                buckets[token] = list;
            }

            list.Add(recordId);
        }

        private static void AddPosting(Dictionary<ulong, List<int>> buckets, ulong token, int recordId)
        {
            if (!buckets.TryGetValue(token, out var list))
            {
                list = new List<int>(4);
                buckets[token] = list;
            }

            list.Add(recordId);
        }

        private static ulong PackShortQueryBigram(char first, char second)
        {
            return ((ulong)first << 32) | second;
        }

        private static int[] CloneIntArray(int[] values, int expectedLength)
        {
            if (values == null || values.Length == 0)
            {
                return Array.Empty<int>();
            }

            var copy = new int[values.Length];
            Array.Copy(values, copy, values.Length);
            return copy;
        }

        private static int[][] CloneIntMatrix(int[][] values, int expectedRows, int expectedColumns)
        {
            if (values == null || values.Length == 0)
            {
                return Array.Empty<int[]>();
            }

            var copy = new int[values.Length][];
            for (var i = 0; i < values.Length; i++)
            {
                var row = values[i];
                if (row == null || row.Length == 0)
                {
                    copy[i] = Array.Empty<int>();
                    continue;
                }

                var rowCopy = new int[row.Length];
                Array.Copy(row, rowCopy, row.Length);
                copy[i] = rowCopy;
            }

            return copy;
        }

        private static ShortQueryPageSampleSnapshot[] CloneBigramAsciiPageSamples(FileRecord[] records, FileRecord[][] samples)
        {
            if (records == null || records.Length == 0 || samples == null || samples.Length == 0)
            {
                return Array.Empty<ShortQueryPageSampleSnapshot>();
            }

            var reverseLookup = new Dictionary<FileRecord, int>(ReferenceEqualityComparer<FileRecord>.Instance);
            for (var i = 0; i < records.Length; i++)
            {
                if (records[i] != null)
                {
                    reverseLookup[records[i]] = i + 1;
                }
            }

            var result = new List<ShortQueryPageSampleSnapshot>();
            for (var token = 0; token < samples.Length; token++)
            {
                var sample = samples[token];
                if (sample == null || sample.Length == 0)
                {
                    continue;
                }

                var ids = new int[sample.Length];
                var idCount = 0;
                for (var i = 0; i < sample.Length; i++)
                {
                    if (sample[i] != null && reverseLookup.TryGetValue(sample[i], out var recordId))
                    {
                        ids[idCount++] = recordId;
                    }
                }

                if (idCount > 0)
                {
                    if (idCount != ids.Length)
                    {
                        Array.Resize(ref ids, idCount);
                    }

                    result.Add(new ShortQueryPageSampleSnapshot(token, ids));
                }
            }

            return result.Count == 0 ? Array.Empty<ShortQueryPageSampleSnapshot>() : result.ToArray();
        }

        private static FileRecord[][] ExpandBigramAsciiPageSamples(FileRecord[] records, ShortQueryPageSampleSnapshot[] samples)
        {
            if (records == null || records.Length == 0 || samples == null || samples.Length == 0)
            {
                return Array.Empty<FileRecord[]>();
            }

            var lookup = new FileRecord[records.Length + 1];
            for (var i = 0; i < records.Length; i++)
            {
                lookup[i + 1] = records[i];
            }

            var result = new FileRecord[BigramAsciiTokenCount][];
            foreach (var sample in samples)
            {
                if (sample == null || sample.RecordIds == null || sample.RecordIds.Length == 0)
                {
                    continue;
                }

                if (sample.Token < 0 || sample.Token >= BigramAsciiTokenCount)
                {
                    continue;
                }

                var bucket = new List<FileRecord>(sample.RecordIds.Length);
                for (var i = 0; i < sample.RecordIds.Length; i++)
                {
                    var recordId = sample.RecordIds[i];
                    if ((uint)recordId >= (uint)lookup.Length)
                    {
                        continue;
                    }

                    var record = lookup[recordId];
                    if (record != null)
                    {
                        bucket.Add(record);
                    }
                }

                if (bucket.Count > 0)
                {
                    result[sample.Token] = bucket.ToArray();
                }
            }

            return result;
        }

        private sealed class NonAsciiShortQueryIndex
        {
            public static NonAsciiShortQueryIndex Empty => new NonAsciiShortQueryIndex(0, null, null, 0);

            private readonly Dictionary<int, int[]> _singlePostings;
            private readonly Dictionary<ulong, int[]> _bigramPostings;

            private NonAsciiShortQueryIndex(int recordCount, Dictionary<int, int[]> singlePostings, Dictionary<ulong, int[]> bigramPostings, ulong contentFingerprint)
            {
                RecordCount = recordCount;
                _singlePostings = singlePostings ?? new Dictionary<int, int[]>();
                _bigramPostings = bigramPostings ?? new Dictionary<ulong, int[]>();
                ContentFingerprint = contentFingerprint;
            }

            public int RecordCount { get; }
            public ulong ContentFingerprint { get; }
            public int BucketCount => _singlePostings.Count + _bigramPostings.Count;
            public int PostingCount
            {
                get
                {
                    var total = 0;
                    foreach (var postings in _singlePostings.Values)
                    {
                        total += postings?.Length ?? 0;
                    }

                    foreach (var postings in _bigramPostings.Values)
                    {
                        total += postings?.Length ?? 0;
                    }

                    return total;
                }
            }

            public static NonAsciiShortQueryIndex FromBuckets(
                int recordCount,
                Dictionary<int, List<int>> singleBuckets,
                Dictionary<ulong, List<int>> bigramBuckets,
                ulong contentFingerprint = 0)
            {
                var single = new Dictionary<int, int[]>(singleBuckets?.Count ?? 0);
                if (singleBuckets != null)
                {
                    foreach (var pair in singleBuckets)
                    {
                        if (pair.Value == null || pair.Value.Count == 0)
                        {
                            continue;
                        }

                        single[pair.Key] = pair.Value.ToArray();
                    }
                }

                var bigram = new Dictionary<ulong, int[]>(bigramBuckets?.Count ?? 0);
                if (bigramBuckets != null)
                {
                    foreach (var pair in bigramBuckets)
                    {
                        if (pair.Value == null || pair.Value.Count == 0)
                        {
                            continue;
                        }

                        bigram[pair.Key] = pair.Value.ToArray();
                    }
                }

                return new NonAsciiShortQueryIndex(recordCount, single, bigram, contentFingerprint);
            }

            public static NonAsciiShortQueryIndex FromSnapshot(ShortQuerySnapshot snapshot, ulong contentFingerprint)
            {
                if (snapshot == null)
                {
                    return Empty;
                }

                var single = new Dictionary<int, int[]>(snapshot.NonAsciiSingles?.Length ?? 0);
                if (snapshot.NonAsciiSingles != null)
                {
                    foreach (var posting in snapshot.NonAsciiSingles)
                    {
                        if (posting == null || posting.RecordIds == null)
                        {
                            continue;
                        }

                        single[(int)posting.Key] = ClonePostingIds(posting.RecordIds);
                    }
                }

                var bigram = new Dictionary<ulong, int[]>(snapshot.NonAsciiBigrams?.Length ?? 0);
                if (snapshot.NonAsciiBigrams != null)
                {
                    foreach (var posting in snapshot.NonAsciiBigrams)
                    {
                        if (posting == null || posting.RecordIds == null)
                        {
                            continue;
                        }

                        bigram[posting.Key] = ClonePostingIds(posting.RecordIds);
                    }
                }

                return new NonAsciiShortQueryIndex(snapshot.RecordCount, single, bigram, contentFingerprint);
            }

            public ShortQueryPostingSnapshot[] ExportSingles()
            {
                var postings = new List<ShortQueryPostingSnapshot>(_singlePostings.Count);
                foreach (var pair in _singlePostings)
                {
                    postings.Add(new ShortQueryPostingSnapshot((ulong)pair.Key, ClonePostingIds(pair.Value)));
                }

                return postings.Count == 0 ? Array.Empty<ShortQueryPostingSnapshot>() : postings.ToArray();
            }

            public ShortQueryPostingSnapshot[] ExportBigrams()
            {
                var postings = new List<ShortQueryPostingSnapshot>(_bigramPostings.Count);
                foreach (var pair in _bigramPostings)
                {
                    postings.Add(new ShortQueryPostingSnapshot(pair.Key, ClonePostingIds(pair.Value)));
                }

                return postings.Count == 0 ? Array.Empty<ShortQueryPostingSnapshot>() : postings.ToArray();
            }

            public bool TryGetPostings(string query, out int[] postings)
            {
                postings = Array.Empty<int>();
                if (string.IsNullOrEmpty(query))
                {
                    return false;
                }

                if (query.Length == 1)
                {
                    return query[0] >= SingleCharAsciiLimit
                           && _singlePostings.TryGetValue(query[0], out postings);
                }

                if (query.Length == 2)
                {
                    if (query[0] < SingleCharAsciiLimit && query[1] < SingleCharAsciiLimit)
                    {
                        return false;
                    }

                    return _bigramPostings.TryGetValue(PackShortQueryBigram(query[0], query[1]), out postings);
                }

                return false;
            }

            private static int[] ClonePostingIds(int[] ids)
            {
                if (ids == null || ids.Length == 0)
                {
                    return Array.Empty<int>();
                }

                var copy = new int[ids.Length];
                Array.Copy(ids, copy, ids.Length);
                return copy;
            }
        }

        private readonly struct PathMatchKey : IEquatable<PathMatchKey>
        {
            public PathMatchKey(char driveLetter, ulong parentFrn, string lowerName)
            {
                DriveLetter = char.ToUpperInvariant(driveLetter);
                ParentFrn = parentFrn;
                LowerName = lowerName ?? string.Empty;
            }

            public char DriveLetter { get; }
            public ulong ParentFrn { get; }
            public string LowerName { get; }

            public bool Equals(PathMatchKey other)
            {
                return DriveLetter == other.DriveLetter
                       && ParentFrn == other.ParentFrn
                       && string.Equals(LowerName, other.LowerName, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is PathMatchKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = (DriveLetter * 397) ^ ParentFrn.GetHashCode();
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(LowerName ?? string.Empty);
                    return hash;
                }
            }
        }

        private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
            where T : class
        {
            public static readonly ReferenceEqualityComparer<T> Instance = new ReferenceEqualityComparer<T>();

            public bool Equals(T x, T y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(T obj)
            {
                return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
