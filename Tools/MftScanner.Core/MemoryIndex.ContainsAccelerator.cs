using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MftScanner
{
    public sealed partial class MemoryIndex
    {
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

            private const int ParallelBuildMinRecords = 32768;
            private const int CancellationStride = 0x7FF;

            private readonly FileRecord[] _records;
            private readonly BucketStore<char> _charBuckets;
            private readonly BucketStore<uint> _bigramBuckets;
            private readonly BucketStore<ulong> _trigramBuckets;
            private readonly BucketKinds _builtBuckets;

            public static ContainsAccelerator Empty => new ContainsAccelerator();

            public bool IsEmpty => _records.Length == 0;
            public bool IsComplete => _builtBuckets == BucketKinds.All;
            public bool HasCharBucket => (_builtBuckets & BucketKinds.Char) != 0;
            public bool HasBigramBucket => (_builtBuckets & BucketKinds.Bigram) != 0;
            public bool HasTrigramBucket => (_builtBuckets & BucketKinds.Trigram) != 0;

            private ContainsAccelerator()
            {
                _records = Array.Empty<FileRecord>();
                _charBuckets = BucketStore<char>.Empty;
                _bigramBuckets = BucketStore<uint>.Empty;
                _trigramBuckets = BucketStore<ulong>.Empty;
                _builtBuckets = BucketKinds.None;
            }

            private ContainsAccelerator(
                FileRecord[] records,
                BucketKinds builtBuckets,
                BucketStore<char> charBuckets,
                BucketStore<uint> bigramBuckets,
                BucketStore<ulong> trigramBuckets)
            {
                _builtBuckets = builtBuckets;
                _records = records ?? Array.Empty<FileRecord>();
                _charBuckets = charBuckets ?? BucketStore<char>.Empty;
                _bigramBuckets = bigramBuckets ?? BucketStore<uint>.Empty;
                _trigramBuckets = trigramBuckets ?? BucketStore<ulong>.Empty;
            }

            public bool Supports(string query)
            {
                if (string.IsNullOrEmpty(query))
                {
                    return false;
                }

                if (query.Length == 1)
                {
                    return !IsAsciiToken(query[0])
                           && (_builtBuckets & BucketKinds.Char) != 0;
                }

                if (query.Length == 2)
                {
                    return (!IsAsciiToken(query[0]) || !IsAsciiToken(query[1]))
                           && (_builtBuckets & BucketKinds.Bigram) != 0;
                }

                return (_builtBuckets & BucketKinds.Trigram) != 0;
            }

            public bool Supports(ContainsAcceleratorBucketKinds requiredBuckets)
            {
                var flags = ToBucketKinds(requiredBuckets);
                return (_builtBuckets & flags) == flags;
            }

            public bool ContainsRecord(RecordKey key)
            {
                if (string.IsNullOrEmpty(key.LowerName) || _records.Length == 0)
                {
                    return false;
                }

                var lo = LowerBoundByLowerName(_records, key.LowerName);
                for (var i = lo; i < _records.Length; i++)
                {
                    var record = _records[i];
                    if (record == null)
                    {
                        continue;
                    }

                    var compare = string.CompareOrdinal(record.LowerName, key.LowerName);
                    if (compare != 0)
                    {
                        break;
                    }

                    if (RecordKey.FromRecord(record).Equals(key))
                    {
                        return true;
                    }
                }

                return false;
            }

            public static ContainsAccelerator Build(
                FileRecord[] records,
                ContainsAcceleratorBucketKinds requiredBuckets,
                CancellationToken ct = default(CancellationToken))
            {
                return Build(records, requiredBuckets, null, ct);
            }

            public static ContainsAccelerator Build(
                FileRecord[] records,
                ContainsAcceleratorBucketKinds requiredBuckets,
                ContainsAccelerator existing,
                CancellationToken ct)
            {
                var totalStopwatch = Stopwatch.StartNew();
                var builtBuckets = ToBucketKinds(requiredBuckets);
                if (records == null || records.Length == 0)
                {
                    return new ContainsAccelerator(records, builtBuckets, BucketStore<char>.Empty, BucketStore<uint>.Empty, BucketStore<ulong>.Empty);
                }

                var reuseExisting = existing != null
                                    && !existing.IsEmpty
                                    && ReferenceEquals(existing._records, records);
                if (reuseExisting)
                {
                    builtBuckets |= existing._builtBuckets;
                }

                var charBuckets = (builtBuckets & BucketKinds.Char) != 0
                    ? (reuseExisting && existing.HasCharBucket
                        ? existing._charBuckets
                        : BuildBucketStore(records, "char", CountCharTokens, FillCharTokens, ct))
                    : BucketStore<char>.Empty;
                var bigramBuckets = (builtBuckets & BucketKinds.Bigram) != 0
                    ? (reuseExisting && existing.HasBigramBucket
                        ? existing._bigramBuckets
                        : BuildBucketStore(records, "bigram", CountBigramTokens, FillBigramTokens, ct))
                    : BucketStore<uint>.Empty;
                var trigramBuckets = (builtBuckets & BucketKinds.Trigram) != 0
                    ? (reuseExisting && existing.HasTrigramBucket
                        ? existing._trigramBuckets
                        : BuildBucketStore(records, "trigram", CountTrigramTokens, FillTrigramTokens, ct))
                    : BucketStore<ulong>.Empty;

                totalStopwatch.Stop();
                IndexPerfLog.Write("INDEX",
                    $"[CONTAINS BUILD] outcome=success buckets={builtBuckets} records={records.Length} elapsedMs={totalStopwatch.ElapsedMilliseconds}");
                return new ContainsAccelerator(records, builtBuckets, charBuckets, bigramBuckets, trigramBuckets);
            }

            public static ContainsAccelerator FromSnapshot(FileRecord[] records, ContainsPostingsSnapshot snapshot)
            {
                if (records == null
                    || snapshot == null
                    || snapshot.RecordCount != records.Length
                    || snapshot.Buckets == null
                    || snapshot.Buckets.Length == 0)
                {
                    return null;
                }

                var builtBuckets = BucketKinds.None;
                var charBuckets = BucketStore<char>.Empty;
                var bigramBuckets = BucketStore<uint>.Empty;
                var trigramBuckets = BucketStore<ulong>.Empty;
                for (var i = 0; i < snapshot.Buckets.Length; i++)
                {
                    var bucket = snapshot.Buckets[i];
                    if (bucket == null || !IsValidBucketSnapshot(bucket))
                    {
                        continue;
                    }

                    switch (bucket.Kind)
                    {
                        case ContainsPostingsBucketKind.Char:
                            charBuckets = BucketStore<char>.FromSnapshot(
                                bucket.Keys,
                                bucket.Offsets,
                                bucket.Counts,
                                bucket.ByteCounts,
                                bucket.Bytes,
                                key => (char)key);
                            builtBuckets |= BucketKinds.Char;
                            break;

                        case ContainsPostingsBucketKind.Bigram:
                            bigramBuckets = BucketStore<uint>.FromSnapshot(
                                bucket.Keys,
                                bucket.Offsets,
                                bucket.Counts,
                                bucket.ByteCounts,
                                bucket.Bytes,
                                key => (uint)key);
                            builtBuckets |= BucketKinds.Bigram;
                            break;

                        case ContainsPostingsBucketKind.Trigram:
                            trigramBuckets = BucketStore<ulong>.FromSnapshot(
                                bucket.Keys,
                                bucket.Offsets,
                                bucket.Counts,
                                bucket.ByteCounts,
                                bucket.Bytes,
                                key => key);
                            builtBuckets |= BucketKinds.Trigram;
                            break;
                    }
                }

                if (builtBuckets == BucketKinds.None)
                {
                    return null;
                }

                return new ContainsAccelerator(
                    records,
                    builtBuckets,
                    charBuckets,
                    bigramBuckets,
                    trigramBuckets);
            }

            private static bool IsValidBucketSnapshot(ContainsPostingsBucketSnapshot snapshot)
            {
                return snapshot.Keys != null
                       && snapshot.Offsets != null
                       && snapshot.Counts != null
                       && snapshot.ByteCounts != null
                       && snapshot.Bytes != null
                       && snapshot.Keys.Length == snapshot.Offsets.Length
                       && snapshot.Keys.Length == snapshot.Counts.Length
                       && snapshot.Keys.Length == snapshot.ByteCounts.Length;
            }

            public ContainsPostingsSnapshot ExportSnapshot()
            {
                var sections = new List<ContainsPostingsBucketSnapshot>(3);
                if (HasCharBucket)
                {
                    var snapshot = _charBuckets.ExportSnapshot(
                        ContainsPostingsBucketKind.Char,
                        key => key);
                    if (snapshot != null)
                        sections.Add(snapshot);
                }

                if (HasBigramBucket)
                {
                    var snapshot = _bigramBuckets.ExportSnapshot(
                        ContainsPostingsBucketKind.Bigram,
                        key => key);
                    if (snapshot != null)
                        sections.Add(snapshot);
                }

                if (HasTrigramBucket)
                {
                    var snapshot = _trigramBuckets.ExportSnapshot(
                        ContainsPostingsBucketKind.Trigram,
                        key => key);
                    if (snapshot != null)
                        sections.Add(snapshot);
                }

                return sections.Count == 0
                    ? null
                    : new ContainsPostingsSnapshot(_records.Length, sections.ToArray());
            }

            public ContainsAccelerator WithInserted(FileRecord record)
            {
                // Inserts after the immutable base build are represented by ContainsOverlay.
                return this;
            }

            public ContainsAccelerator WithRemoved(RecordKey key)
            {
                // Removes after the immutable base build are represented by ContainsOverlay.
                return this;
            }

            public ContainsSearchResult Search(
                string query,
                SearchTypeFilter filter,
                int offset,
                int maxResults,
                CancellationToken ct,
                ContainsOverlay overlay = null)
            {
                var normalizedOffset = Math.Max(offset, 0);
                var normalizedMaxResults = Math.Max(maxResults, 0);
                var activeOverlay = overlay ?? ContainsOverlay.Empty;
                ContainsSearchResult result;
                if (query.Length == 1)
                {
                    result = SearchSingleChar(query[0], query, filter, normalizedOffset, normalizedMaxResults, ct, activeOverlay);
                    result.IncludesLiveOverlay = true;
                    return result;
                }

                if (query.Length == 2)
                {
                    result = SearchBigram(PackBigram(query[0], query[1]), query, filter, normalizedOffset, normalizedMaxResults, ct, activeOverlay);
                    result.IncludesLiveOverlay = true;
                    return result;
                }

                result = SearchTrigram(query, filter, normalizedOffset, normalizedMaxResults, ct, activeOverlay);
                result.IncludesLiveOverlay = true;
                return result;
            }

            private ContainsSearchResult SearchSingleChar(char token, string query, SearchTypeFilter filter, int offset, int maxResults, CancellationToken ct, ContainsOverlay overlay)
            {
                BucketPosting posting;
                if (!_charBuckets.TryGetPosting(token, out posting) || posting.Count == 0)
                {
                    return BuildOverlayOnlyResult(query, filter, offset, maxResults, "char", ct, overlay);
                }

                return BuildBucketResult(posting, query, filter, offset, maxResults, "char", ct, overlay);
            }

            private ContainsSearchResult SearchBigram(uint token, string query, SearchTypeFilter filter, int offset, int maxResults, CancellationToken ct, ContainsOverlay overlay)
            {
                BucketPosting posting;
                if (!_bigramBuckets.TryGetPosting(token, out posting) || posting.Count == 0)
                {
                    return BuildOverlayOnlyResult(query, filter, offset, maxResults, "bigram", ct, overlay);
                }

                return BuildBucketResult(posting, query, filter, offset, maxResults, "bigram", ct, overlay);
            }

            private ContainsSearchResult SearchTrigram(string query, SearchTypeFilter filter, int offset, int maxResults, CancellationToken ct, ContainsOverlay overlay)
            {
                var trigramKeys = BuildUniqueTrigramKeys(query);
                if (trigramKeys.Count == 0)
                {
                    return BuildOverlayOnlyResult(query, filter, offset, maxResults, "fallback", ct, overlay);
                }

                var postingLists = new List<BucketPosting>(trigramKeys.Count);
                for (var i = 0; i < trigramKeys.Count; i++)
                {
                    BucketPosting posting;
                    if (!_trigramBuckets.TryGetPosting(trigramKeys[i], out posting) || posting.Count == 0)
                    {
                        return BuildOverlayOnlyResult(query, filter, offset, maxResults, "trigram", ct, overlay);
                    }

                    postingLists.Add(posting);
                }

                postingLists.Sort((a, b) => a.Count.CompareTo(b.Count));
                var intersectStopwatch = Stopwatch.StartNew();
                var primary = postingLists[0];
                var candidates = new List<int>(Math.Min(primary.Count, 256));
                var lastRecordId = int.MinValue;
                for (var i = 0; i < primary.Count; i++)
                {
                    if (((i + 1) & 0xFFF) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    var recordId = primary.Postings[primary.Offset + i];
                    if (recordId == lastRecordId)
                    {
                        continue;
                    }

                    lastRecordId = recordId;
                    var matched = true;
                    for (var j = 1; j < postingLists.Count; j++)
                    {
                        if (!ContainsRecordId(postingLists[j], recordId))
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

                    var record = GetRecord(candidates[i]);
                    if (record == null
                        || overlay.ContainsRemoved(RecordKey.FromRecord(record))
                        || !NameContains(record, query)
                        || !MatchesFilter(record, filter))
                    {
                        continue;
                    }

                    total++;
                    if (total > offset && page.Count < maxResults)
                    {
                        page.Add(record);
                    }
                }

                AddOverlayMatches(query, filter, offset, maxResults, page, ref total, ct, overlay);
                verifyStopwatch.Stop();
                result.Total = total;
                result.Page = page;
                result.VerifyMs = verifyStopwatch.ElapsedMilliseconds;
                return result;
            }

            private ContainsSearchResult BuildBucketResult(BucketPosting posting, string query, SearchTypeFilter filter, int offset, int maxResults, string mode, CancellationToken ct, ContainsOverlay overlay)
            {
                var page = new List<FileRecord>(Math.Min(maxResults, 64));
                var total = 0;
                var lastRecordId = int.MinValue;
                for (var i = 0; i < posting.Count; i++)
                {
                    if (((i + 1) & 0xFFF) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    var recordId = posting.Postings[posting.Offset + i];
                    if (recordId == lastRecordId)
                    {
                        continue;
                    }

                    lastRecordId = recordId;
                    var record = GetRecord(recordId);
                    if (record == null
                        || overlay.ContainsRemoved(RecordKey.FromRecord(record))
                        || !NameContains(record, query)
                        || !MatchesFilter(record, filter))
                    {
                        continue;
                    }

                    total++;
                    if (total > offset && page.Count < maxResults)
                    {
                        page.Add(record);
                    }
                }

                AddOverlayMatches(query, filter, offset, maxResults, page, ref total, ct, overlay);

                return new ContainsSearchResult
                {
                    Mode = mode,
                    CandidateCount = total,
                    Total = total,
                    Page = page
                };
            }

            private ContainsSearchResult BuildOverlayOnlyResult(
                string query,
                SearchTypeFilter filter,
                int offset,
                int maxResults,
                string mode,
                CancellationToken ct,
                ContainsOverlay overlay)
            {
                var page = new List<FileRecord>(Math.Min(maxResults, 64));
                var total = 0;
                AddOverlayMatches(query, filter, offset, maxResults, page, ref total, ct, overlay);
                return new ContainsSearchResult
                {
                    Mode = mode,
                    CandidateCount = total,
                    Total = total,
                    Page = page
                };
            }

            private static void AddOverlayMatches(
                string query,
                SearchTypeFilter filter,
                int offset,
                int maxResults,
                List<FileRecord> page,
                ref int total,
                CancellationToken ct,
                ContainsOverlay overlay)
            {
                if (overlay == null || overlay.AddedRecords.Count == 0)
                {
                    return;
                }

                for (var i = 0; i < overlay.AddedRecords.Count; i++)
                {
                    if (((i + 1) & CancellationStride) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    var record = overlay.AddedRecords[i];
                    if (record == null
                        || overlay.ContainsRemoved(RecordKey.FromRecord(record))
                        || !NameContains(record, query)
                        || !MatchesFilter(record, filter))
                    {
                        continue;
                    }

                    total++;
                    if (total > offset && page.Count < maxResults)
                    {
                        page.Add(record);
                    }
                }
            }

            private static bool NameContains(FileRecord record, string query)
            {
                return record != null
                       && !string.IsNullOrEmpty(record.LowerName)
                       && !string.IsNullOrEmpty(query)
                       && record.LowerName.IndexOf(query, StringComparison.Ordinal) >= 0;
            }

            private bool ContainsRecordId(BucketPosting posting, int recordId)
            {
                var lo = posting.Offset;
                var hi = posting.Offset + posting.Count - 1;
                while (lo <= hi)
                {
                    var mid = lo + ((hi - lo) / 2);
                    var compare = CompareRecordIds(posting.Postings[mid], recordId);
                    if (compare == 0)
                    {
                        return true;
                    }

                    if (compare < 0)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }

                return false;
            }

            private int CompareRecordIds(int leftRecordId, int rightRecordId)
            {
                return leftRecordId.CompareTo(rightRecordId);
            }

            private FileRecord GetRecord(int recordId)
            {
                var index = recordId - 1;
                return index >= 0 && index < _records.Length
                    ? _records[index]
                    : null;
            }

            private static int LowerBoundByLowerName(FileRecord[] records, string lowerName)
            {
                var lo = 0;
                var hi = records?.Length ?? 0;
                while (lo < hi)
                {
                    var mid = lo + ((hi - lo) / 2);
                    var record = records[mid];
                    var compare = string.CompareOrdinal(record?.LowerName, lowerName);
                    if (compare < 0)
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

            private static BucketStore<char> BuildBucketStore(
                FileRecord[] records,
                string bucketName,
                Action<string, Dictionary<char, int>> countTokens,
                Action<string, int, Dictionary<char, int>, int[]> fillTokens,
                CancellationToken ct)
            {
                return BuildBucketStoreCore(records, bucketName, countTokens, fillTokens, ct);
            }

            private static BucketStore<uint> BuildBucketStore(
                FileRecord[] records,
                string bucketName,
                Action<string, Dictionary<uint, int>> countTokens,
                Action<string, int, Dictionary<uint, int>, int[]> fillTokens,
                CancellationToken ct)
            {
                return BuildBucketStoreCore(records, bucketName, countTokens, fillTokens, ct);
            }

            private static BucketStore<ulong> BuildBucketStore(
                FileRecord[] records,
                string bucketName,
                Action<string, Dictionary<ulong, int>> countTokens,
                Action<string, int, Dictionary<ulong, int>, int[]> fillTokens,
                CancellationToken ct)
            {
                return BuildBucketStoreCore(records, bucketName, countTokens, fillTokens, ct);
            }

            private static BucketStore<TKey> BuildBucketStoreCore<TKey>(
                FileRecord[] records,
                string bucketName,
                Action<string, Dictionary<TKey, int>> countTokens,
                Action<string, int, Dictionary<TKey, int>, int[]> fillTokens,
                CancellationToken ct)
            {
                var totalStopwatch = Stopwatch.StartNew();
                long countMs = 0;
                long mergeMs = 0;
                long positionMs = 0;
                long fillMs = 0;
                var workerCount = 0;
                var bucketCount = 0;
                var totalPostings = 0;
                try
                {
                    if (records == null || records.Length == 0)
                    {
                        IndexPerfLog.Write("INDEX",
                            $"[CONTAINS BUILD BUCKET] outcome=success bucket={IndexPerfLog.FormatValue(bucketName)} records=0 workerCount=0 buckets=0 postings=0 totalMs=0 countMs=0 mergeMs=0 positionMs=0 fillMs=0");
                        return BucketStore<TKey>.Empty;
                    }

                    workerCount = DetermineWorkerCount(records.Length);
                    var ranges = BuildWorkerRanges(records.Length, workerCount);
                    var localCounts = new Dictionary<TKey, int>[workerCount];
                    var stageStopwatch = Stopwatch.StartNew();

                    if (workerCount == 1)
                    {
                        localCounts[0] = CountRange(records, ranges[0], countTokens, ct);
                    }
                    else
                    {
                        Parallel.For(0, workerCount, new ParallelOptions
                        {
                            CancellationToken = ct,
                            MaxDegreeOfParallelism = workerCount
                        }, i =>
                        {
                            localCounts[i] = CountRange(records, ranges[i], countTokens, ct);
                        });
                    }
                    stageStopwatch.Stop();
                    countMs = stageStopwatch.ElapsedMilliseconds;

                    stageStopwatch.Restart();
                    var globalCounts = new Dictionary<TKey, int>();
                    for (var i = 0; i < localCounts.Length; i++)
                    {
                        var counts = localCounts[i];
                        if (counts == null || counts.Count == 0)
                        {
                            continue;
                        }

                        foreach (var kv in counts)
                        {
                            int current;
                            globalCounts.TryGetValue(kv.Key, out current);
                            globalCounts[kv.Key] = current + kv.Value;
                        }
                    }
                    stageStopwatch.Stop();
                    mergeMs = stageStopwatch.ElapsedMilliseconds;
                    bucketCount = globalCounts.Count;

                    if (globalCounts.Count == 0)
                    {
                        totalStopwatch.Stop();
                        IndexPerfLog.Write("INDEX",
                            $"[CONTAINS BUILD BUCKET] outcome=success bucket={IndexPerfLog.FormatValue(bucketName)} records={records.Length} workerCount={workerCount} buckets=0 postings=0 totalMs={totalStopwatch.ElapsedMilliseconds} countMs={countMs} mergeMs={mergeMs} positionMs=0 fillMs=0");
                        return BucketStore<TKey>.Empty;
                    }

                    stageStopwatch.Restart();
                    var bucketIndexes = new Dictionary<TKey, int>(globalCounts.Count);
                    var entries = new BucketEntry[globalCounts.Count];
                    var bucketIndex = 0;
                    foreach (var kv in globalCounts)
                    {
                        bucketIndexes[kv.Key] = bucketIndex;
                        entries[bucketIndex] = new BucketEntry(totalPostings, kv.Value);
                        totalPostings += kv.Value;
                        bucketIndex++;
                    }

                    var workerPositions = new Dictionary<TKey, int>[workerCount];
                    var consumedByBucket = new int[entries.Length];
                    for (var i = 0; i < workerCount; i++)
                    {
                        var counts = localCounts[i];
                        if (counts == null || counts.Count == 0)
                        {
                            workerPositions[i] = new Dictionary<TKey, int>();
                            continue;
                        }

                        var positions = new Dictionary<TKey, int>(counts.Count);
                        foreach (var kv in counts)
                        {
                            var idx = bucketIndexes[kv.Key];
                            positions[kv.Key] = entries[idx].Offset + consumedByBucket[idx];
                            consumedByBucket[idx] += kv.Value;
                        }

                        workerPositions[i] = positions;
                    }
                    stageStopwatch.Stop();
                    positionMs = stageStopwatch.ElapsedMilliseconds;

                    var postings = new int[totalPostings];
                    stageStopwatch.Restart();
                    if (workerCount == 1)
                    {
                        FillRange(records, ranges[0], workerPositions[0], postings, fillTokens, ct);
                    }
                    else
                    {
                        Parallel.For(0, workerCount, new ParallelOptions
                        {
                            CancellationToken = ct,
                            MaxDegreeOfParallelism = workerCount
                        }, i =>
                        {
                            FillRange(records, ranges[i], workerPositions[i], postings, fillTokens, ct);
                        });
                    }
                    stageStopwatch.Stop();
                    fillMs = stageStopwatch.ElapsedMilliseconds;
                    totalStopwatch.Stop();
                    IndexPerfLog.Write("INDEX",
                        $"[CONTAINS BUILD BUCKET] outcome=success bucket={IndexPerfLog.FormatValue(bucketName)} records={records.Length} workerCount={workerCount} buckets={bucketCount} postings={totalPostings} totalMs={totalStopwatch.ElapsedMilliseconds} countMs={countMs} mergeMs={mergeMs} positionMs={positionMs} fillMs={fillMs}");

                    var compressedPostings = BucketStore<TKey>.CompressPostings(entries, postings, out var compressedEntries);
                    IndexPerfLog.Write("INDEX",
                        $"[CONTAINS BUILD COMPRESS] outcome=success bucket={IndexPerfLog.FormatValue(bucketName)} postings={totalPostings} " +
                        $"rawBytes={(long)totalPostings * sizeof(int)} compressedBytes={compressedPostings.Length}");

                    return new BucketStore<TKey>(bucketIndexes, compressedEntries, compressedPostings);
                }
                catch (OperationCanceledException)
                {
                    totalStopwatch.Stop();
                    IndexPerfLog.Write("INDEX",
                        $"[CONTAINS BUILD BUCKET] outcome=canceled bucket={IndexPerfLog.FormatValue(bucketName)} records={(records == null ? 0 : records.Length)} workerCount={workerCount} buckets={bucketCount} postings={totalPostings} totalMs={totalStopwatch.ElapsedMilliseconds} countMs={countMs} mergeMs={mergeMs} positionMs={positionMs} fillMs={fillMs}");
                    throw;
                }
            }

            private static Dictionary<TKey, int> CountRange<TKey>(
                FileRecord[] records,
                WorkerRange range,
                Action<string, Dictionary<TKey, int>> countTokens,
                CancellationToken ct)
            {
                var counts = new Dictionary<TKey, int>();
                for (var i = range.Start; i < range.End; i++)
                {
                    if ((((i - range.Start) + 1) & CancellationStride) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    var record = records[i];
                    if (record == null || string.IsNullOrEmpty(record.LowerName))
                    {
                        continue;
                    }

                    countTokens(record.LowerName, counts);
                }

                return counts;
            }

            private static void FillRange<TKey>(
                FileRecord[] records,
                WorkerRange range,
                Dictionary<TKey, int> positions,
                int[] postings,
                Action<string, int, Dictionary<TKey, int>, int[]> fillTokens,
                CancellationToken ct)
            {
                for (var i = range.Start; i < range.End; i++)
                {
                    if ((((i - range.Start) + 1) & CancellationStride) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    var record = records[i];
                    if (record == null || string.IsNullOrEmpty(record.LowerName))
                    {
                        continue;
                    }

                    fillTokens(record.LowerName, i + 1, positions, postings);
                }
            }

            private static WorkerRange[] BuildWorkerRanges(int recordCount, int workerCount)
            {
                var ranges = new WorkerRange[workerCount];
                var baseSize = recordCount / workerCount;
                var remainder = recordCount % workerCount;
                var start = 0;
                for (var i = 0; i < workerCount; i++)
                {
                    var length = baseSize + (i < remainder ? 1 : 0);
                    ranges[i] = new WorkerRange(start, start + length);
                    start += length;
                }

                return ranges;
            }

            private static int DetermineWorkerCount(int recordCount)
            {
                if (recordCount < ParallelBuildMinRecords)
                {
                    return 1;
                }

                var processorBudget = Math.Max(1, Environment.ProcessorCount - 2);
                return Math.Max(1, Math.Min(processorBudget, recordCount / 32768));
            }

            private static void CountCharTokens(string lowerName, Dictionary<char, int> counts)
            {
                for (var i = 0; i < lowerName.Length; i++)
                {
                    var token = lowerName[i];
                    if (!IsAsciiToken(token) && lowerName.IndexOf(token) == i)
                    {
                        IncrementCount(counts, token);
                    }
                }
            }

            private static void FillCharTokens(string lowerName, int recordId, Dictionary<char, int> positions, int[] postings)
            {
                for (var i = 0; i < lowerName.Length; i++)
                {
                    var token = lowerName[i];
                    if (IsAsciiToken(token) || lowerName.IndexOf(token) != i)
                    {
                        continue;
                    }

                    int position;
                    if (!positions.TryGetValue(token, out position))
                    {
                        continue;
                    }

                    postings[position] = recordId;
                    positions[token] = position + 1;
                }
            }

            private static void CountBigramTokens(string lowerName, Dictionary<uint, int> counts)
            {
                if (lowerName.Length < 2)
                {
                    return;
                }

                for (var i = 0; i < lowerName.Length - 1; i++)
                {
                    if (IsAsciiToken(lowerName[i]) && IsAsciiToken(lowerName[i + 1]))
                    {
                        continue;
                    }

                    var token = PackBigram(lowerName[i], lowerName[i + 1]);
                    if (!ContainsBigramBefore(lowerName, token, i))
                    {
                        IncrementCount(counts, token);
                    }
                }
            }

            private static void FillBigramTokens(string lowerName, int recordId, Dictionary<uint, int> positions, int[] postings)
            {
                if (lowerName.Length < 2)
                {
                    return;
                }

                for (var i = 0; i < lowerName.Length - 1; i++)
                {
                    if (IsAsciiToken(lowerName[i]) && IsAsciiToken(lowerName[i + 1]))
                    {
                        continue;
                    }

                    var token = PackBigram(lowerName[i], lowerName[i + 1]);
                    if (ContainsBigramBefore(lowerName, token, i))
                    {
                        continue;
                    }

                    int position;
                    if (!positions.TryGetValue(token, out position))
                    {
                        continue;
                    }

                    postings[position] = recordId;
                    positions[token] = position + 1;
                }
            }

            private static void CountTrigramTokens(string lowerName, Dictionary<ulong, int> counts)
            {
                if (lowerName.Length < 3)
                {
                    return;
                }

                for (var i = 0; i < lowerName.Length - 2; i++)
                {
                    var token = PackTrigram(lowerName[i], lowerName[i + 1], lowerName[i + 2]);
                    if (!ContainsTrigramBefore(lowerName, token, i))
                    {
                        IncrementCount(counts, token);
                    }
                }
            }

            private static void FillTrigramTokens(string lowerName, int recordId, Dictionary<ulong, int> positions, int[] postings)
            {
                if (lowerName.Length < 3)
                {
                    return;
                }

                for (var i = 0; i < lowerName.Length - 2; i++)
                {
                    var token = PackTrigram(lowerName[i], lowerName[i + 1], lowerName[i + 2]);
                    if (ContainsTrigramBefore(lowerName, token, i))
                    {
                        continue;
                    }

                    int position;
                    if (!positions.TryGetValue(token, out position))
                    {
                        continue;
                    }

                    postings[position] = recordId;
                    positions[token] = position + 1;
                }
            }

            private static bool IsAsciiToken(char value)
            {
                return value <= 0x7F;
            }

            private static bool ContainsBigramBefore(string lowerName, uint token, int endExclusive)
            {
                for (var i = 0; i < endExclusive; i++)
                {
                    if (PackBigram(lowerName[i], lowerName[i + 1]) == token)
                    {
                        return true;
                    }
                }

                return false;
            }

            private static bool ContainsTrigramBefore(string lowerName, ulong token, int endExclusive)
            {
                for (var i = 0; i < endExclusive; i++)
                {
                    if (PackTrigram(lowerName[i], lowerName[i + 1], lowerName[i + 2]) == token)
                    {
                        return true;
                    }
                }

                return false;
            }

            private static void IncrementCount<TKey>(Dictionary<TKey, int> counts, TKey token)
            {
                int current;
                counts.TryGetValue(token, out current);
                counts[token] = current + 1;
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

            private struct WorkerRange
            {
                public WorkerRange(int start, int end)
                {
                    Start = start;
                    End = end;
                }

                public int Start { get; private set; }
                public int End { get; private set; }
            }

            private struct BucketEntry
            {
                public BucketEntry(int offset, int count, int byteCount = 0)
                {
                    Offset = offset;
                    Count = count;
                    ByteCount = byteCount;
                }

                public int Offset { get; private set; }
                public int Count { get; private set; }
                public int ByteCount { get; private set; }
            }

            private struct BucketPosting
            {
                public BucketPosting(int[] postings, int offset, int count)
                {
                    Postings = postings;
                    Offset = offset;
                    Count = count;
                }

                public int[] Postings { get; private set; }
                public int Offset { get; private set; }
                public int Count { get; private set; }
            }

            private sealed class BucketStore<TKey>
            {
                private static readonly BucketStore<TKey> EmptyStore = new BucketStore<TKey>(
                    new Dictionary<TKey, int>(),
                    Array.Empty<BucketEntry>(),
                    Array.Empty<int>());

                private readonly Dictionary<TKey, int> _bucketIndexes;
                private readonly BucketEntry[] _entries;
                private readonly int[] _postings;
                private readonly byte[] _compressedPostings;
                private Dictionary<TKey, int[]> _overridePostings;

                public static BucketStore<TKey> Empty => EmptyStore;

                public BucketStore(Dictionary<TKey, int> bucketIndexes, BucketEntry[] entries, int[] postings)
                    : this(bucketIndexes, entries, postings, null)
                {
                }

                public BucketStore(Dictionary<TKey, int> bucketIndexes, BucketEntry[] entries, byte[] compressedPostings)
                    : this(bucketIndexes, entries, null, compressedPostings)
                {
                }

                private BucketStore(Dictionary<TKey, int> bucketIndexes, BucketEntry[] entries, int[] postings, byte[] compressedPostings)
                {
                    _bucketIndexes = bucketIndexes ?? new Dictionary<TKey, int>();
                    _entries = entries ?? Array.Empty<BucketEntry>();
                    _postings = postings ?? Array.Empty<int>();
                    _compressedPostings = compressedPostings ?? Array.Empty<byte>();
                }

                public bool TryGetPosting(TKey token, out BucketPosting posting)
                {
                    if (_overridePostings != null)
                    {
                        int[] overridePosting;
                        if (_overridePostings.TryGetValue(token, out overridePosting))
                        {
                            if (overridePosting == null || overridePosting.Length == 0)
                            {
                                posting = default(BucketPosting);
                                return false;
                            }

                            posting = new BucketPosting(overridePosting, 0, overridePosting.Length);
                            return true;
                        }
                    }

                    int bucketIndex;
                    if (!_bucketIndexes.TryGetValue(token, out bucketIndex))
                    {
                        posting = default(BucketPosting);
                        return false;
                    }

                    var entry = _entries[bucketIndex];
                    if (entry.Count == 0)
                    {
                        posting = default(BucketPosting);
                        return false;
                    }

                    if (_compressedPostings.Length > 0)
                    {
                        posting = new BucketPosting(DecodePosting(entry), 0, entry.Count);
                        return true;
                    }

                    posting = new BucketPosting(_postings, entry.Offset, entry.Count);
                    return true;
                }

                public void Insert(TKey token, int recordId, Func<int, int, int> comparer)
                {
                    var current = Materialize(token);
                    var insertIndex = FindInsertIndex(current, recordId, comparer);
                    if (insertIndex > 0 && current[insertIndex - 1] == recordId)
                    {
                        return;
                    }

                    if (insertIndex < current.Length && current[insertIndex] == recordId)
                    {
                        return;
                    }

                    var updated = new int[current.Length + 1];
                    Array.Copy(current, 0, updated, 0, insertIndex);
                    updated[insertIndex] = recordId;
                    Array.Copy(current, insertIndex, updated, insertIndex + 1, current.Length - insertIndex);
                    SetOverride(token, updated);
                }

                public void Remove(TKey token, int recordId)
                {
                    var current = Materialize(token);
                    if (current.Length == 0)
                    {
                        return;
                    }

                    var remaining = 0;
                    for (var i = 0; i < current.Length; i++)
                    {
                        if (current[i] != recordId)
                        {
                            remaining++;
                        }
                    }

                    if (remaining == current.Length)
                    {
                        return;
                    }

                    if (remaining == 0)
                    {
                        SetOverride(token, Array.Empty<int>());
                        return;
                    }

                    var updated = new int[remaining];
                    var index = 0;
                    for (var i = 0; i < current.Length; i++)
                    {
                        if (current[i] == recordId)
                        {
                            continue;
                        }

                        updated[index++] = current[i];
                    }

                    SetOverride(token, updated);
                }

                private void SetOverride(TKey token, int[] updated)
                {
                    if (_overridePostings == null)
                    {
                        _overridePostings = new Dictionary<TKey, int[]>();
                    }

                    _overridePostings[token] = updated ?? Array.Empty<int>();
                }

                private int[] Materialize(TKey token)
                {
                    if (_overridePostings != null)
                    {
                        int[] overridePosting;
                        if (_overridePostings.TryGetValue(token, out overridePosting))
                        {
                            return overridePosting ?? Array.Empty<int>();
                        }
                    }

                    int bucketIndex;
                    if (!_bucketIndexes.TryGetValue(token, out bucketIndex))
                    {
                        return Array.Empty<int>();
                    }

                    var entry = _entries[bucketIndex];
                    if (entry.Count == 0)
                    {
                        return Array.Empty<int>();
                    }

                    if (_compressedPostings.Length > 0)
                    {
                        return DecodePosting(entry);
                    }

                    var copy = new int[entry.Count];
                    Array.Copy(_postings, entry.Offset, copy, 0, entry.Count);
                    return copy;
                }

                private int[] DecodePosting(BucketEntry entry)
                {
                    if (entry.Count == 0)
                        return Array.Empty<int>();

                    var result = new int[entry.Count];
                    var index = 0;
                    var offset = entry.Offset;
                    var end = offset + entry.ByteCount;
                    var previous = 0;
                    while (offset < end && index < result.Length)
                    {
                        var delta = ReadVarUInt32(_compressedPostings, ref offset, end);
                        var value = checked(previous + (int)delta);
                        result[index++] = value;
                        previous = value;
                    }

                    return result;
                }

                private static int FindInsertIndex(int[] bucket, int recordId, Func<int, int, int> comparer)
                {
                    var lo = 0;
                    var hi = bucket.Length;
                    while (lo < hi)
                    {
                        var mid = lo + ((hi - lo) / 2);
                        var compare = comparer(bucket[mid], recordId);
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

                public static byte[] CompressPostings(BucketEntry[] entries, int[] postings, out BucketEntry[] compressedEntries)
                {
                    compressedEntries = Array.Empty<BucketEntry>();
                    if (entries == null || entries.Length == 0 || postings == null || postings.Length == 0)
                        return Array.Empty<byte>();

                    compressedEntries = new BucketEntry[entries.Length];
                    using (var stream = new MemoryStream(Math.Max(1024, postings.Length * 2)))
                    {
                        for (var i = 0; i < entries.Length; i++)
                        {
                            var entry = entries[i];
                            var byteOffset = checked((int)stream.Position);
                            var previous = 0;
                            for (var j = 0; j < entry.Count; j++)
                            {
                                var value = postings[entry.Offset + j];
                                var delta = checked((uint)(value - previous));
                                WriteVarUInt32(stream, delta);
                                previous = value;
                            }

                            var byteCount = checked((int)stream.Position - byteOffset);
                            compressedEntries[i] = new BucketEntry(byteOffset, entry.Count, byteCount);
                        }

                        return stream.ToArray();
                    }
                }

                public static BucketStore<TKey> FromSnapshot(
                    ulong[] keys,
                    int[] offsets,
                    int[] counts,
                    int[] byteCounts,
                    byte[] bytes,
                    Func<ulong, TKey> keyConverter)
                {
                    if (keys == null || offsets == null || counts == null || byteCounts == null || bytes == null || keyConverter == null)
                        return Empty;

                    var bucketIndexes = new Dictionary<TKey, int>(keys.Length);
                    var entries = new BucketEntry[keys.Length];
                    for (var i = 0; i < keys.Length; i++)
                    {
                        bucketIndexes[keyConverter(keys[i])] = i;
                        entries[i] = new BucketEntry(offsets[i], counts[i], byteCounts[i]);
                    }

                    return new BucketStore<TKey>(bucketIndexes, entries, bytes);
                }

                public ContainsPostingsBucketSnapshot ExportSnapshot(
                    ContainsPostingsBucketKind kind,
                    Func<TKey, ulong> keyConverter)
                {
                    if (keyConverter == null
                        || _bucketIndexes == null
                        || _bucketIndexes.Count == 0
                        || _entries == null
                        || _compressedPostings == null
                        || _compressedPostings.Length == 0)
                    {
                        return null;
                    }

                    var keys = new ulong[_bucketIndexes.Count];
                    var offsets = new int[_bucketIndexes.Count];
                    var counts = new int[_bucketIndexes.Count];
                    var byteCounts = new int[_bucketIndexes.Count];
                    foreach (var kv in _bucketIndexes)
                    {
                        var index = kv.Value;
                        keys[index] = keyConverter(kv.Key);
                        offsets[index] = _entries[index].Offset;
                        counts[index] = _entries[index].Count;
                        byteCounts[index] = _entries[index].ByteCount;
                    }

                    var bytes = new byte[_compressedPostings.Length];
                    Array.Copy(_compressedPostings, bytes, bytes.Length);
                    return new ContainsPostingsBucketSnapshot(kind, keys, offsets, counts, byteCounts, bytes);
                }

                private static void WriteVarUInt32(Stream stream, uint value)
                {
                    while (value >= 0x80)
                    {
                        stream.WriteByte((byte)(value | 0x80));
                        value >>= 7;
                    }

                    stream.WriteByte((byte)value);
                }

                private static uint ReadVarUInt32(byte[] buffer, ref int offset, int end)
                {
                    uint result = 0;
                    var shift = 0;
                    while (offset < end)
                    {
                        var b = buffer[offset++];
                        result |= (uint)(b & 0x7F) << shift;
                        if ((b & 0x80) == 0)
                            return result;

                        shift += 7;
                        if (shift > 28)
                            break;
                    }

                    return result;
                }
            }
        }
    }
}
