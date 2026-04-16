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

        /// <summary>有序数组：按 LowerName 字典序排列。</summary>
        public FileRecord[] SortedArray { get; private set; } = Array.Empty<FileRecord>();

        public int TotalCount => SortedArray.Length;

        private static readonly IComparer<FileRecord> ByLowerName =
            Comparer<FileRecord>.Create((a, b) => string.CompareOrdinal(a.LowerName, b.LowerName));

        public void LoadSortedRecords(IReadOnlyList<FileRecord> sortedRecords)
        {
            var arr = CopyRecords(sortedRecords);
            Publish(arr, BuildExactHashMap(arr));
        }

        public void Build(IReadOnlyList<FileRecord> records)
        {
            if (records == null || records.Count == 0)
            {
                Publish(Array.Empty<FileRecord>(), new Dictionary<string, List<FileRecord>>());
                return;
            }

            var arr = CopyRecords(records);
            Array.Sort(arr, ByLowerName);
            Publish(arr, BuildExactHashMap(arr));
        }

        public void Insert(FileRecord record)
        {
            _lock.EnterWriteLock();
            try
            {
                if (!ExactHashMap.TryGetValue(record.LowerName, out var bucket))
                    ExactHashMap[record.LowerName] = bucket = new List<FileRecord>();
                bucket.Add(record);

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
        /// 移除记录。优先用 (lowerName, parentFrn, driveLetter) 精确匹配；
        /// 若 parentFrn == 0（USN 删除事件有时不提供），则退化为按 (lowerName, driveLetter) 移除第一条。
        /// </summary>
        public void Remove(string lowerName, ulong parentFrn, char driveLetter)
        {
            _lock.EnterWriteLock();
            try
            {
                if (ExactHashMap.TryGetValue(lowerName, out var bucket))
                {
                    if (parentFrn != 0)
                        bucket.RemoveAll(r => r.ParentFrn == parentFrn && r.DriveLetter == driveLetter);
                    else
                        bucket.RemoveAll(r => r.DriveLetter == driveLetter);
                    if (bucket.Count == 0) ExactHashMap.Remove(lowerName);
                }

                var arr = SortedArray;
                for (var i = 0; i < arr.Length; i++)
                {
                    var match = arr[i].LowerName == lowerName && arr[i].DriveLetter == driveLetter
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

        public void Rename(string oldLowerName, ulong oldParentFrn, char driveLetter, FileRecord newRecord)
        {
            _lock.EnterWriteLock();
            try
            {
                if (ExactHashMap.TryGetValue(oldLowerName, out var bucket))
                {
                    bucket.RemoveAll(r => r.ParentFrn == oldParentFrn && r.DriveLetter == driveLetter);
                    if (bucket.Count == 0) ExactHashMap.Remove(oldLowerName);
                }

                var arr = SortedArray;
                for (var i = 0; i < arr.Length; i++)
                {
                    if (arr[i].LowerName == oldLowerName &&
                        arr[i].ParentFrn == oldParentFrn &&
                        arr[i].DriveLetter == driveLetter)
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

        private void Publish(FileRecord[] arr, Dictionary<string, List<FileRecord>> map)
        {
            _lock.EnterWriteLock();
            try
            {
                ExactHashMap = map;
                SortedArray = arr;
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
                    map[new RecordKey(record.LowerName, record.ParentFrn, record.DriveLetter)] = record;

                for (var i = 0; i < changes.Count; i++)
                {
                    var change = changes[i];
                    switch (change.Kind)
                    {
                        case UsnChangeKind.Create:
                            map[new RecordKey(change.LowerName, change.ParentFrn, change.DriveLetter)] = change.ToRecord();
                            break;
                        case UsnChangeKind.Delete:
                            map.Remove(new RecordKey(change.LowerName, change.ParentFrn, change.DriveLetter));
                            break;
                        case UsnChangeKind.Rename:
                            map.Remove(new RecordKey(change.OldLowerName, change.OldParentFrn, change.DriveLetter));
                            map[new RecordKey(change.LowerName, change.ParentFrn, change.DriveLetter)] = change.ToRecord();
                            break;
                    }
                }

                var arr = new FileRecord[map.Count];
                var index = 0;
                foreach (var record in map.Values)
                    arr[index++] = record;

                Array.Sort(arr, ByLowerName);
                ExactHashMap = BuildExactHashMap(arr);
                SortedArray = arr;
            }
            finally { _lock.ExitWriteLock(); }
        }

        private struct RecordKey : IEquatable<RecordKey>
        {
            public RecordKey(string lowerName, ulong parentFrn, char driveLetter)
            {
                LowerName = lowerName;
                ParentFrn = parentFrn;
                DriveLetter = driveLetter;
            }

            public string LowerName { get; }
            public ulong ParentFrn { get; }
            public char DriveLetter { get; }

            public bool Equals(RecordKey other)
            {
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
                    var hash = LowerName != null ? StringComparer.Ordinal.GetHashCode(LowerName) : 0;
                    hash = (hash * 397) ^ ParentFrn.GetHashCode();
                    hash = (hash * 397) ^ DriveLetter.GetHashCode();
                    return hash;
                }
            }
        }
    }
}
