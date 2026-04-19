using System;
using System.Collections.Generic;
using System.Threading;

namespace MftScanner
{
    public sealed class MemoryIndex
    {
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

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

        public int TotalCount => SortedArray.Length;

        private static readonly IComparer<FileRecord> ByLowerName =
            Comparer<FileRecord>.Create((a, b) => string.CompareOrdinal(a.LowerName, b.LowerName));

        public void LoadSortedRecords(IReadOnlyList<FileRecord> sortedRecords)
        {
            var arr = CopyRecords(sortedRecords);
            Publish(
                arr,
                BuildExactHashMap(arr),
                BuildExtensionHashMap(arr),
                BuildFilteredArray(arr, SearchTypeFilter.Folder),
                BuildFilteredArray(arr, SearchTypeFilter.Launchable),
                BuildFilteredArray(arr, SearchTypeFilter.Script),
                BuildFilteredArray(arr, SearchTypeFilter.Log),
                BuildFilteredArray(arr, SearchTypeFilter.Config));
        }

        public void Build(IReadOnlyList<FileRecord> records)
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
                    Array.Empty<FileRecord>());
                return;
            }

            var arr = CopyRecords(records);
            Array.Sort(arr, ByLowerName);
            Publish(
                arr,
                BuildExactHashMap(arr),
                BuildExtensionHashMap(arr),
                BuildFilteredArray(arr, SearchTypeFilter.Folder),
                BuildFilteredArray(arr, SearchTypeFilter.Launchable),
                BuildFilteredArray(arr, SearchTypeFilter.Script),
                BuildFilteredArray(arr, SearchTypeFilter.Log),
                BuildFilteredArray(arr, SearchTypeFilter.Config));
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

                var arr = SortedArray;
                var idx = Array.BinarySearch(arr, record, ByLowerName);
                if (idx < 0) idx = ~idx;
                var newArr = new FileRecord[arr.Length + 1];
                Array.Copy(arr, 0, newArr, 0, idx);
                newArr[idx] = record;
                Array.Copy(arr, idx, newArr, idx + 1, arr.Length - idx);
                SortedArray = newArr;
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

                var insertIdx = Array.BinarySearch(arr, newRecord, ByLowerName);
                if (insertIdx < 0) insertIdx = ~insertIdx;
                var newArr = new FileRecord[arr.Length + 1];
                Array.Copy(arr, 0, newArr, 0, insertIdx);
                newArr[insertIdx] = newRecord;
                Array.Copy(arr, insertIdx, newArr, insertIdx + 1, arr.Length - insertIdx);
                SortedArray = newArr;
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
            FileRecord[] configArr)
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
            }
            finally { _lock.ExitWriteLock(); }
        }

        public void ApplyBatch(IReadOnlyList<UsnChangeEntry> changes)
        {
            if (changes == null || changes.Count == 0)
                return;

            _lock.EnterWriteLock();
            try
            {
                var map = new Dictionary<RecordKey, FileRecord>(SortedArray.Length + changes.Count);
                foreach (var record in SortedArray)
                    map[RecordKey.FromRecord(record)] = record;

                for (var i = 0; i < changes.Count; i++)
                {
                    var change = changes[i];
                    switch (change.Kind)
                    {
                        case UsnChangeKind.Create:
                            map[RecordKey.FromChange(change)] = change.ToRecord();
                            break;
                        case UsnChangeKind.Delete:
                            map.Remove(RecordKey.FromChange(change));
                            break;
                        case UsnChangeKind.Rename:
                            map.Remove(RecordKey.FromRenameOld(change));
                            map[RecordKey.FromChange(change)] = change.ToRecord();
                            break;
                    }
                }

                var arr = new FileRecord[map.Count];
                var index = 0;
                foreach (var record in map.Values)
                    arr[index++] = record;

                Array.Sort(arr, ByLowerName);
                ExactHashMap = BuildExactHashMap(arr);
                ExtensionHashMap = BuildExtensionHashMap(arr);
                SortedArray = arr;
                DirectorySortedArray = BuildFilteredArray(arr, SearchTypeFilter.Folder);
                LaunchableSortedArray = BuildFilteredArray(arr, SearchTypeFilter.Launchable);
                ScriptSortedArray = BuildFilteredArray(arr, SearchTypeFilter.Script);
                LogSortedArray = BuildFilteredArray(arr, SearchTypeFilter.Log);
                ConfigSortedArray = BuildFilteredArray(arr, SearchTypeFilter.Config);
            }
            finally { _lock.ExitWriteLock(); }
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
