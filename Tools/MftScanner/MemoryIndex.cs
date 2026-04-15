using System;
using System.Collections.Generic;
using System.Threading;

namespace MftScanner
{
    /// <summary>
    /// 纯内存索引容器，封装两种数据结构：ExactHashMap 和 SortedArray。
    /// Trie 已移除——前缀匹配改由 SortedArray 二分查找实现，内存占用大幅降低。
    /// 读操作无锁，写操作通过 ReaderWriterLockSlim 保护。
    /// </summary>
    public sealed class MemoryIndex
    {
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        /// <summary>精确匹配哈希表：文件名小写 → FileRecord 列表。</summary>
        public Dictionary<string, List<FileRecord>> ExactHashMap { get; private set; }
            = new Dictionary<string, List<FileRecord>>();

        /// <summary>有序数组：按 LowerName 字典序排列，用于二分查找、前缀/包含/后缀/正则匹配。</summary>
        public FileRecord[] SortedArray { get; private set; } = Array.Empty<FileRecord>();

        /// <summary>索引中 FileRecord 的总数。</summary>
        public int TotalCount => SortedArray.Length;

        private static readonly IComparer<FileRecord> ByLowerName =
            Comparer<FileRecord>.Create((a, b) => string.CompareOrdinal(a.LowerName, b.LowerName));

        /// <summary>
        /// 批量构建索引：一次性填充 ExactHashMap 和 SortedArray。
        /// </summary>
        public void Build(IEnumerable<FileRecord> records)
        {
            var list = new List<FileRecord>(records);
            var map = new Dictionary<string, List<FileRecord>>(list.Count);

            foreach (var r in list)
            {
                if (!map.TryGetValue(r.LowerName, out var bucket))
                    map[r.LowerName] = bucket = new List<FileRecord>();
                bucket.Add(r);
            }

            var arr = list.ToArray();
            Array.Sort(arr, ByLowerName);

            _lock.EnterWriteLock();
            try
            {
                ExactHashMap = map;
                SortedArray  = arr;
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>增量插入一条 FileRecord。</summary>
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

        /// <summary>从索引中移除指定记录。</summary>
        public void Remove(string lowerName, string fullPath)
        {
            _lock.EnterWriteLock();
            try
            {
                if (ExactHashMap.TryGetValue(lowerName, out var bucket))
                {
                    bucket.RemoveAll(r => r.FullPath == fullPath);
                    if (bucket.Count == 0) ExactHashMap.Remove(lowerName);
                }

                var arr = SortedArray;
                for (var i = 0; i < arr.Length; i++)
                {
                    if (arr[i].LowerName == lowerName && arr[i].FullPath == fullPath)
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

        /// <summary>重命名：单次写锁内先移除旧记录再插入新记录。</summary>
        public void Rename(string oldLowerName, string oldFullPath, FileRecord newRecord)
        {
            _lock.EnterWriteLock();
            try
            {
                // remove old from map
                if (ExactHashMap.TryGetValue(oldLowerName, out var bucket))
                {
                    bucket.RemoveAll(r => r.FullPath == oldFullPath);
                    if (bucket.Count == 0) ExactHashMap.Remove(oldLowerName);
                }

                // remove old from array
                var arr = SortedArray;
                for (var i = 0; i < arr.Length; i++)
                {
                    if (arr[i].LowerName == oldLowerName && arr[i].FullPath == oldFullPath)
                    {
                        var tmp = new FileRecord[arr.Length - 1];
                        Array.Copy(arr, 0, tmp, 0, i);
                        Array.Copy(arr, i + 1, tmp, i, arr.Length - i - 1);
                        arr = tmp;
                        break;
                    }
                }

                // insert new into map
                if (!ExactHashMap.TryGetValue(newRecord.LowerName, out var nb))
                    ExactHashMap[newRecord.LowerName] = nb = new List<FileRecord>();
                nb.Add(newRecord);

                // insert new into array
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
