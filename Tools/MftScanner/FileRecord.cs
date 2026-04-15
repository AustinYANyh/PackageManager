namespace MftScanner
{
    /// <summary>
    /// 索引中存储的最小文件记录单元，仅包含文件名、完整路径和是否为目录标志。
    /// 不含文件大小和修改时间（按需加载）。
    /// </summary>
    public sealed class FileRecord
    {
        public FileRecord(string lowerName, string originalName, string fullPath, bool isDirectory)
        {
            LowerName = lowerName;
            OriginalName = originalName;
            FullPath = fullPath;
            IsDirectory = isDirectory;
        }

        /// <summary>文件名小写，用于索引键和比较。</summary>
        public string LowerName { get; }

        /// <summary>原始大小写文件名，用于显示。</summary>
        public string OriginalName { get; }

        /// <summary>完整路径（含盘符）。</summary>
        public string FullPath { get; }

        /// <summary>是否为目录。</summary>
        public bool IsDirectory { get; }
    }
}
