namespace MftScanner
{
    /// <summary>
    /// 索引中存储的最小文件记录单元。
    /// 不存完整路径——路径在搜索结果展示时通过 FRN 链按需解析，大幅降低内存占用。
    /// </summary>
    public sealed class FileRecord
    {
        public FileRecord(string lowerName, string originalName,
                          ulong parentFrn, char driveLetter, bool isDirectory, ulong frn = 0)
        {
            LowerName   = lowerName;
            OriginalName = originalName;
            ParentFrn   = parentFrn;
            DriveLetter = driveLetter;
            IsDirectory = isDirectory;
            Frn = frn;
        }

        /// <summary>文件名小写，用于索引键和比较。</summary>
        public string LowerName { get; }

        /// <summary>原始大小写文件名，用于显示。</summary>
        public string OriginalName { get; }

        /// <summary>父目录的 FRN，用于按需解析完整路径。</summary>
        public ulong ParentFrn { get; }

        /// <summary>所在卷盘符（'C'、'D' 等）。</summary>
        public char DriveLetter { get; }

        /// <summary>是否为目录。</summary>
        public bool IsDirectory { get; }

        /// <summary>文件自身 FRN；优先用于 USN 增量删除/重命名精确命中。</summary>
        public ulong Frn { get; }
    }
}
