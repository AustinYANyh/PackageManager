using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace MftScanner
{
    public sealed partial class MemoryIndex
    {
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
        private long _contentVersion;

        /// <summary>精确匹配：文件名小写 → FileRecord 列表。</summary>
        public Dictionary<string, List<FileRecord>> ExactHashMap { get; private set; }
            = new Dictionary<string, List<FileRecord>>();

        /// <summary>扩展名匹配：扩展名小写（如 ".log"）→ FileRecord 列表。</summary>
        public Dictionary<string, List<FileRecord>> ExtensionHashMap { get; private set; }
            = new Dictionary<string, List<FileRecord>>();

        /// <summary>有序数组：按 LowerName 字典序排列。</summary>
        public FileRecord[] SortedArray { get; private set; } = Array.Empty<FileRecord>();
        public FileRecord[] DirectorySortedArray { get; private set; } = Array.Empty<FileRecord>();
        public FileRecord[] LaunchableSortedArray { get; private set; } = Array.Empty<FileRecord>();
        public FileRecord[] ScriptSortedArray { get; private set; } = Array.Empty<FileRecord>();
        public FileRecord[] LogSortedArray { get; private set; } = Array.Empty<FileRecord>();
        public FileRecord[] ConfigSortedArray { get; private set; } = Array.Empty<FileRecord>();
        private ContainsAccelerator _containsAccelerator = ContainsAccelerator.Empty;
        private ContainsOverlay _containsOverlay = ContainsOverlay.Empty;
        private bool _containsAcceleratorReady = true;
        private long _containsAcceleratorEpoch;
        private readonly List<PendingContainsMutation> _pendingContainsMutations = new List<PendingContainsMutation>();
        private bool _pendingContainsMutationsOverflowed;
        private const int MaxPendingContainsMutations = 65536;

        public int TotalCount => SortedArray.Length;

        public bool HasContainsAccelerator
        {
            get
            {
                var accelerator = Volatile.Read(ref _containsAccelerator);
                var overlay = Volatile.Read(ref _containsOverlay) ?? ContainsOverlay.Empty;
                return Volatile.Read(ref _containsAcceleratorReady)
                       && !overlay.IsOverflowed
                       && accelerator != null
                       && !accelerator.IsEmpty;
            }
        }

        public sealed class ContainsSearchResult
        {
            public List<FileRecord> Page { get; set; } = new List<FileRecord>();
            public int Total { get; set; }
            public string Mode { get; set; } = "fallback";
            public int CandidateCount { get; set; }
            public long IntersectMs { get; set; }
            public long VerifyMs { get; set; }
        }

        public enum ContainsWarmupScope
        {
            TrigramOnly,
            Full
        }

        private static readonly IComparer<FileRecord> ByLowerName =
            Comparer<FileRecord>.Create((a, b) => string.CompareOrdinal(a.LowerName, b.LowerName));

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
                || overlay.IsOverflowed
                || accelerator == null
                || accelerator.IsEmpty)
            {
                if (overlay.IsOverflowed)
                {
                    IndexPerfLog.Write("INDEX",
                        $"[CONTAINS ACCELERATOR] outcome=overlay-overflow fallback=true query={IndexPerfLog.FormatValue(query)}");
                }
                return false;
            }

            if (!accelerator.Supports(query))
            {
                return false;
            }

            result = accelerator.Search(query, filter, offset, maxResults, ct, overlay);
            return true;
        }

        public void LoadSortedRecords(IReadOnlyList<FileRecord> sortedRecords, bool buildContainsAccelerator = true)
        {
            var arr = CopyRecords(sortedRecords);
            BuildDerivedStructures(
                arr,
                out var exactMap,
                out var extensionMap,
                out var directoryArr,
                out var launchableArr,
                out var scriptArr,
                out var logArr,
                out var configArr);
            Publish(
                arr,
                exactMap,
                extensionMap,
                directoryArr,
                launchableArr,
                scriptArr,
                logArr,
                configArr,
                buildContainsAccelerator ? ContainsAccelerator.Build(arr, ContainsAcceleratorBucketKinds.All) : null,
                buildContainsAccelerator);
        }

        public void Build(IReadOnlyList<FileRecord> records, bool buildContainsAccelerator = true)
        {
            if (records == null || records.Count == 0)
            {
                Publish(
                    Array.Empty<FileRecord>(),
                    new Dictionary<string, List<FileRecord>>(),
                    new Dictionary<string, List<FileRecord>>(),
                    Array.Empty<FileRecord>(),
                    Array.Empty<FileRecord>(),
                    Array.Empty<FileRecord>(),
                    Array.Empty<FileRecord>(),
                    Array.Empty<FileRecord>(),
                    ContainsAccelerator.Empty,
                    true);
                return;
            }

            var arr = CopyRecords(records);
            Array.Sort(arr, ByLowerName);
            BuildDerivedStructures(
                arr,
                out var exactMap,
                out var extensionMap,
                out var directoryArr,
                out var launchableArr,
                out var scriptArr,
                out var logArr,
                out var configArr);
            Publish(
                arr,
                exactMap,
                extensionMap,
                directoryArr,
                launchableArr,
                scriptArr,
                logArr,
                configArr,
                buildContainsAccelerator ? ContainsAccelerator.Build(arr, ContainsAcceleratorBucketKinds.All) : null,
                buildContainsAccelerator);
        }

        public void Insert(FileRecord record)
        {
            _lock.EnterWriteLock();
            try
            {
                if (!ExactHashMap.TryGetValue(record.LowerName, out var bucket))
                    ExactHashMap[record.LowerName] = bucket = new List<FileRecord>();
                bucket.Add(record);

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
                _contentVersion++;
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
                if (ExactHashMap.TryGetValue(lowerName, out var bucket))
                {
                    if (frn != 0)
                        bucket.RemoveAll(r => r.Frn == frn && r.DriveLetter == driveLetter);
                    else if (parentFrn != 0)
                        bucket.RemoveAll(r => r.ParentFrn == parentFrn && r.DriveLetter == driveLetter);
                    else
                        bucket.RemoveAll(r => r.DriveLetter == driveLetter);
                    if (bucket.Count == 0) ExactHashMap.Remove(lowerName);
                }

                if (TryGetIndexedExtension(lowerName, out var extension)
                    && ExtensionHashMap.TryGetValue(extension, out var extensionBucket))
                {
                    RemoveFromBucket(extensionBucket, frn, lowerName, parentFrn, driveLetter);
                    if (extensionBucket.Count == 0) ExtensionHashMap.Remove(extension);
                }

                RemoveFromFilterBuckets(frn, lowerName, parentFrn, driveLetter);
                if (_containsAcceleratorReady)
                {
                    AppendContainsOverlay(PendingContainsMutation.ForRemove(new RecordKey(frn, lowerName, parentFrn, driveLetter)));
                }
                else
                {
                    EnqueuePendingContainsRemove(new RecordKey(frn, lowerName, parentFrn, driveLetter));
                }

                var arr = SortedArray;
                for (var i = 0; i < arr.Length; i++)
                {
                    var match = frn != 0
                        ? arr[i].Frn == frn && arr[i].DriveLetter == driveLetter
                        : arr[i].LowerName == lowerName && arr[i].DriveLetter == driveLetter
                          && (parentFrn == 0 || arr[i].ParentFrn == parentFrn);
                    if (match)
                    {
                        var newArr = new FileRecord[arr.Length - 1];
                        Array.Copy(arr, 0, newArr, 0, i);
                        Array.Copy(arr, i + 1, newArr, i, arr.Length - i - 1);
                        SortedArray = newArr;
                        _contentVersion++;
                        break;
                    }
                }
            }
            finally { _lock.ExitWriteLock(); }
        }

        public void Rename(ulong frn, string oldLowerName, ulong oldParentFrn, char driveLetter, FileRecord newRecord)
        {
            _lock.EnterWriteLock();
            try
            {
                if (ExactHashMap.TryGetValue(oldLowerName, out var bucket))
                {
                    if (frn != 0)
                        bucket.RemoveAll(r => r.Frn == frn && r.DriveLetter == driveLetter);
                    else
                        bucket.RemoveAll(r => r.ParentFrn == oldParentFrn && r.DriveLetter == driveLetter);
                    if (bucket.Count == 0) ExactHashMap.Remove(oldLowerName);
                }

                if (TryGetIndexedExtension(oldLowerName, out var oldExtension)
                    && ExtensionHashMap.TryGetValue(oldExtension, out var oldExtensionBucket))
                {
                    RemoveFromBucket(oldExtensionBucket, frn, oldLowerName, oldParentFrn, driveLetter);
                    if (oldExtensionBucket.Count == 0) ExtensionHashMap.Remove(oldExtension);
                }

                RemoveFromFilterBuckets(frn, oldLowerName, oldParentFrn, driveLetter);

                var arr = SortedArray;
                for (var i = 0; i < arr.Length; i++)
                {
                    var match = frn != 0
                        ? arr[i].Frn == frn && arr[i].DriveLetter == driveLetter
                        : arr[i].LowerName == oldLowerName &&
                          arr[i].ParentFrn == oldParentFrn &&
                          arr[i].DriveLetter == driveLetter;
                    if (match)
                    {
                        var tmp = new FileRecord[arr.Length - 1];
                        Array.Copy(arr, 0, tmp, 0, i);
                        Array.Copy(arr, i + 1, tmp, i, arr.Length - i - 1);
                        arr = tmp;
                        break;
                    }
                }

                if (!ExactHashMap.TryGetValue(newRecord.LowerName, out var nb))
                    ExactHashMap[newRecord.LowerName] = nb = new List<FileRecord>();
                nb.Add(newRecord);

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
                _contentVersion++;
            }
            finally { _lock.ExitWriteLock(); }
        }

        private static FileRecord[] CopyRecords(IReadOnlyList<FileRecord> records)
        {
            if (records == null || records.Count == 0)
                return Array.Empty<FileRecord>();

            var arr = records as FileRecord[];
            if (arr != null)
                return arr;

            arr = new FileRecord[records.Count];
            for (var i = 0; i < records.Count; i++)
                arr[i] = records[i];
            return arr;
        }

        private static Dictionary<string, List<FileRecord>> BuildExactHashMap(FileRecord[] arr)
        {
            var map = new Dictionary<string, List<FileRecord>>(arr.Length);
            foreach (var r in arr)
            {
                if (!map.TryGetValue(r.LowerName, out var bucket))
                    map[r.LowerName] = bucket = new List<FileRecord>(1);
                bucket.Add(r);
            }

            return map;
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
            out Dictionary<string, List<FileRecord>> exactMap,
            out Dictionary<string, List<FileRecord>> extensionMap,
            out FileRecord[] directoryArr,
            out FileRecord[] launchableArr,
            out FileRecord[] scriptArr,
            out FileRecord[] logArr,
            out FileRecord[] configArr)
        {
            if (arr == null || arr.Length == 0)
            {
                exactMap = new Dictionary<string, List<FileRecord>>();
                extensionMap = new Dictionary<string, List<FileRecord>>();
                directoryArr = Array.Empty<FileRecord>();
                launchableArr = Array.Empty<FileRecord>();
                scriptArr = Array.Empty<FileRecord>();
                logArr = Array.Empty<FileRecord>();
                configArr = Array.Empty<FileRecord>();
                return;
            }

            exactMap = new Dictionary<string, List<FileRecord>>(arr.Length);
            extensionMap = new Dictionary<string, List<FileRecord>>();
            var directories = new List<FileRecord>();
            var launchables = new List<FileRecord>();
            var scripts = new List<FileRecord>();
            var logs = new List<FileRecord>();
            var configs = new List<FileRecord>();

            for (var i = 0; i < arr.Length; i++)
            {
                var record = arr[i];
                if (!exactMap.TryGetValue(record.LowerName, out var exactBucket))
                {
                    exactBucket = new List<FileRecord>(1);
                    exactMap[record.LowerName] = exactBucket;
                }

                exactBucket.Add(record);

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

        private void Publish(
            FileRecord[] arr,
            Dictionary<string, List<FileRecord>> exactMap,
            Dictionary<string, List<FileRecord>> extensionMap,
            FileRecord[] directoryArr,
            FileRecord[] launchableArr,
            FileRecord[] scriptArr,
            FileRecord[] logArr,
            FileRecord[] configArr,
            ContainsAccelerator containsAccelerator,
            bool containsAcceleratorReady)
        {
            _lock.EnterWriteLock();
            try
            {
                ExactHashMap = exactMap;
                ExtensionHashMap = extensionMap;
                SortedArray = arr;
                DirectorySortedArray = directoryArr;
                LaunchableSortedArray = launchableArr;
                ScriptSortedArray = scriptArr;
                LogSortedArray = logArr;
                ConfigSortedArray = configArr;
                _containsAccelerator = containsAcceleratorReady
                    ? (containsAccelerator ?? ContainsAccelerator.Empty)
                    : ContainsAccelerator.Empty;
                _containsOverlay = ContainsOverlay.Empty;
                _containsAcceleratorReady = containsAcceleratorReady;
                _containsAcceleratorEpoch++;
                _pendingContainsMutations.Clear();
                _pendingContainsMutationsOverflowed = false;
                _contentVersion++;
            }
            finally { _lock.ExitWriteLock(); }
        }

        public void ApplyBatch(IReadOnlyList<UsnChangeEntry> changes, bool rebuildContainsAccelerator = true)
        {
            if (changes == null || changes.Count == 0)
                return;

            _lock.EnterWriteLock();
            try
            {
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
                BuildDerivedStructures(
                    arr,
                    out var exactMap,
                    out var extensionMap,
                    out var directoryArr,
                    out var launchableArr,
                    out var scriptArr,
                    out var logArr,
                    out var configArr);
                ExactHashMap = exactMap;
                ExtensionHashMap = extensionMap;
                SortedArray = arr;
                DirectorySortedArray = directoryArr;
                LaunchableSortedArray = launchableArr;
                ScriptSortedArray = scriptArr;
                LogSortedArray = logArr;
                ConfigSortedArray = configArr;
                if (rebuildContainsAccelerator)
                {
                    _containsAccelerator = ContainsAccelerator.Build(arr, ContainsAcceleratorBucketKinds.All);
                    _containsOverlay = ContainsOverlay.Empty;
                    _containsAcceleratorReady = true;
                    _containsAcceleratorEpoch++;
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
                _contentVersion++;
            }
            finally { _lock.ExitWriteLock(); }
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
            var requiredBuckets = scope == ContainsWarmupScope.Full
                ? ContainsAcceleratorBucketKinds.All
                : ContainsAcceleratorBucketKinds.Trigram;

            while (!ct.IsCancellationRequested)
            {
                FileRecord[] snapshot;
                long epoch;
                _lock.EnterReadLock();
                try
                {
                    if (_containsAcceleratorReady
                        && _containsAccelerator != null
                        && _containsAccelerator.Supports(requiredBuckets))
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

                var accelerator = ContainsAccelerator.Build(snapshot, requiredBuckets, ct);

                _lock.EnterWriteLock();
                try
                {
                    if (_containsAcceleratorReady
                        && _containsAccelerator != null
                        && _containsAccelerator.Supports(requiredBuckets))
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
                    _containsAcceleratorEpoch++;
                    IndexPerfLog.Write("INDEX",
                        $"[CONTAINS PUBLISH] outcome=success scope={scope} overlayAdds={_containsOverlay.AddedCount} overlayRemoves={_containsOverlay.RemovedCount}");
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

        private static FileRecord[] InsertIntoSortedArray(FileRecord[] target, FileRecord record)
        {
            var arr = target ?? Array.Empty<FileRecord>();
            var idx = Array.BinarySearch(arr, record, ByLowerName);
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
                var match = frn != 0
                    ? arr[i].Frn == frn && arr[i].DriveLetter == driveLetter
                    : arr[i].LowerName == lowerName && arr[i].DriveLetter == driveLetter
                      && (parentFrn == 0 || arr[i].ParentFrn == parentFrn);
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
                bucket.RemoveAll(r => r.Frn == frn && r.DriveLetter == driveLetter);
                return;
            }

            bucket.RemoveAll(r => r.LowerName == lowerName
                                  && r.DriveLetter == driveLetter
                && (parentFrn == 0 || r.ParentFrn == parentFrn));
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
                _containsAcceleratorReady = false;
                EnqueuePendingContainsMutation(mutation);
                IndexPerfLog.Write("INDEX",
                    $"[CONTAINS OVERLAY] outcome=overflow adds={overlay.AddedCount} removes={overlay.RemovedCount}");
                return;
            }

            _containsAcceleratorEpoch++;
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

                accelerator.WithRemoved(Key);
            }
        }

        private enum PendingContainsMutationKind
        {
            Insert,
            Remove
        }

        private sealed class ContainsOverlay
        {
            public static readonly ContainsOverlay Empty = new ContainsOverlay(
                new Dictionary<RecordKey, FileRecord>(),
                new HashSet<RecordKey>(),
                false);

            private readonly Dictionary<RecordKey, FileRecord> _addedByKey;
            private readonly HashSet<RecordKey> _removedKeys;

            private ContainsOverlay(
                Dictionary<RecordKey, FileRecord> addedByKey,
                HashSet<RecordKey> removedKeys,
                bool isOverflowed)
            {
                _addedByKey = addedByKey ?? new Dictionary<RecordKey, FileRecord>();
                _removedKeys = removedKeys ?? new HashSet<RecordKey>();
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

            public ContainsOverlay WithMutation(PendingContainsMutation mutation, int maxMutations)
            {
                if (IsOverflowed)
                {
                    return this;
                }

                var addedByKey = new Dictionary<RecordKey, FileRecord>(_addedByKey);
                var removedKeys = new HashSet<RecordKey>(_removedKeys);
                ApplyMutation(addedByKey, removedKeys, mutation);
                if (addedByKey.Count + removedKeys.Count > maxMutations)
                {
                    return new ContainsOverlay(addedByKey, removedKeys, true);
                }

                return new ContainsOverlay(addedByKey, removedKeys, false);
            }

            public ContainsOverlay WithMutations(IReadOnlyList<PendingContainsMutation> mutations, int maxMutations)
            {
                if (mutations == null || mutations.Count == 0 || IsOverflowed)
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
                        return new ContainsOverlay(addedByKey, removedKeys, true);
                    }
                }

                return new ContainsOverlay(addedByKey, removedKeys, false);
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
                    : new ContainsOverlay(addedByKey, removedKeys, false);
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

                return new ContainsOverlay(addedByKey, removedKeys, false);
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

                addedByKey.Remove(mutation.Key);
                removedKeys.Add(mutation.Key);
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
                    return Frn == other.Frn && DriveLetter == other.DriveLetter;

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
                        return (frnHash * 397) ^ DriveLetter.GetHashCode();
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
