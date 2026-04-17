using System.Collections.Generic;

namespace MftScanner
{
    /// <summary>
    /// 前缀树节点，用于快速前缀匹配。
    /// </summary>
    public sealed class TrieNode
    {
        /// <summary>子节点映射：字符 → 子节点。</summary>
        public Dictionary<char, TrieNode> Children { get; } = new Dictionary<char, TrieNode>();

        /// <summary>终止于此节点的文件记录列表。</summary>
        public List<FileRecord> Records { get; } = new List<FileRecord>();
    }
}
