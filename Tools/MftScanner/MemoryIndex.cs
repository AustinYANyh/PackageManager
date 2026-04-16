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

        public void Build(IEnumerable<FileRecord> records)
        {
            // 如果传入的已经是 List，直接用；否则转换一次
            var list = records as List<FileRecord> ?? new List<FileRecord>(records);
            var arr  = list.ToArray();

            // 用 StringComparer.Ordinal 直接比较，避免委托调用开销
            Array.Sort(arr, (a, b) => string.CompareOrdinal(a.LowerName, b.LowerName));

            var map = new Dictionary<string, List<FileRecord>>(arr.Length);
            foreach (var r in arr)
            {
                if (!map.TryGetValue(r.LowerName, out var bucket))
                    map[r.LowerName] = bucket = new List<FileRecord>(1);
                bucket.Add(r);
            }

            _lock.EnterWriteLock();
            try { ExactHashMap = map; SortedArray = arr; }
            finally { _lock.ExitWriteLock(); }
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
    }
}
