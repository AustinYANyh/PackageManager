using System;
using System.Collections.Generic;
using Microsoft.International.Converters.PinYinConverter;
using PackageManager.Function.StartupTool;

namespace PackageManager.Services
{
    /// <summary>单个启动项名称的拼音索引条目。</summary>
    internal sealed class PinyinEntry
    {
        /// <summary>
        /// 全拼字符串列表。多音字会产生多个候选全拼。
        /// 例："重庆" → ["zhongqing", "chongqing"]
        /// </summary>
        public List<string> FullPinyins { get; } = new List<string>();

        /// <summary>
        /// 简拼字符串列表。多音字会产生多个候选简拼。
        /// 例："重庆" → ["zq", "cq"]
        /// </summary>
        public List<string> AbbrPinyins { get; } = new List<string>();
    }

    /// <summary>
    /// 负责预计算启动项名称的拼音索引，并执行拼音匹配。
    /// 线程安全：索引构建和查询均通过锁保护。
    /// </summary>
    public class PinyinMatcher
    {
        // key: StartupItemVm 的 Name（原始字符串）
        // value: 该名称对应的拼音索引条目
        private Dictionary<string, PinyinEntry> _index
            = new Dictionary<string, PinyinEntry>(StringComparer.Ordinal);
        private readonly object _lock = new object();

        /// <summary>全量构建索引，替换现有所有条目。</summary>
        public void BuildIndex(IEnumerable<StartupItemVm> items)
        {
            var newIndex = new Dictionary<string, PinyinEntry>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Name))
                    continue;
                newIndex[item.Name] = ComputeEntry(item.Name);
            }
            lock (_lock)
            {
                _index = newIndex;
            }
        }

        /// <summary>增量更新单个条目（新增或编辑后调用）。</summary>
        public void UpdateEntry(StartupItemVm item)
        {
            if (string.IsNullOrEmpty(item.Name))
                return;
            var entry = ComputeEntry(item.Name);
            lock (_lock)
            {
                _index[item.Name] = entry;
            }
        }

        /// <summary>移除单个条目（删除后调用）。</summary>
        public void RemoveEntry(string name)
        {
            if (string.IsNullOrEmpty(name))
                return;
            lock (_lock)
            {
                _index.Remove(name);
            }
        }

        /// <summary>
        /// 判断给定启动项是否与关键词拼音匹配。
        /// 调用方应确保 keyword 为纯英文（不含中文字符）。
        /// </summary>
        public bool IsMatch(StartupItemVm item, string keyword)
        {
            if (string.IsNullOrEmpty(item.Name))
                return false;

            PinyinEntry entry;
            lock (_lock)
            {
                if (!_index.TryGetValue(item.Name, out entry))
                    return false;
            }

            foreach (var full in entry.FullPinyins)
            {
                if (full.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            foreach (var abbr in entry.AbbrPinyins)
            {
                if (abbr.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 计算给定名称的拼音索引条目（核心拼音计算逻辑）。
        /// </summary>
        private static PinyinEntry ComputeEntry(string name)
        {
            // 将 name 分割为汉字段和非汉字段
            var segments = SplitSegments(name);

            // 统计汉字总数，用于确定最大候选数
            int totalCjk = 0;
            foreach (var seg in segments)
            {
                if (seg.IsCjk)
                    totalCjk += seg.Text.Length;
            }

            int maxCandidates;
            if (totalCjk <= 4)
                maxCandidates = 16;
            else if (totalCjk <= 8)
                maxCandidates = 8;
            else
                maxCandidates = 4;

            // 构建全拼候选列表和简拼候选列表
            // 每个段产生一组候选（字符串列表），最终做跨段笛卡尔积
            var fullPinyinSegCandidates = new List<List<string>>();
            var abbrPinyinSegCandidates = new List<List<string>>();

            foreach (var seg in segments)
            {
                if (!seg.IsCjk)
                {
                    // 非汉字段：直接保留原样（小写）
                    var literal = seg.Text.ToLower();
                    fullPinyinSegCandidates.Add(new List<string> { literal });
                    abbrPinyinSegCandidates.Add(new List<string> { literal });
                }
                else
                {
                    // 汉字段：对每个汉字获取候选拼音，做笛卡尔积
                    var charCandidates = new List<List<string>>();
                    foreach (char c in seg.Text)
                    {
                        var candidates = new List<string>();
                        if (ChineseChar.IsValidChar(c))
                        {
                            var cc = new ChineseChar(c);
                            int take = Math.Min((int)cc.PinyinCount, 2);
                            for (int i = 0; i < take; i++)
                            {
                                var py = cc.Pinyins[i];
                                if (!string.IsNullOrEmpty(py))
                                    // 去掉末尾声调数字，转小写
                                    candidates.Add(py.Substring(0, py.Length - 1).ToLower());
                            }
                        }
                        if (candidates.Count == 0)
                            candidates.Add(c.ToString().ToLower()); // fallback
                        charCandidates.Add(candidates);
                    }

                    // 笛卡尔积生成全拼候选（限制数量）
                    var fullCombos = CartesianProduct(charCandidates, maxCandidates);
                    fullPinyinSegCandidates.Add(fullCombos);

                    // 简拼候选：取每个汉字拼音首字母
                    var abbrCharCandidates = new List<List<string>>();
                    foreach (var charPinyins in charCandidates)
                    {
                        var abbrLetters = new List<string>();
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var py in charPinyins)
                        {
                            if (!string.IsNullOrEmpty(py))
                            {
                                var letter = py[0].ToString();
                                if (seen.Add(letter))
                                    abbrLetters.Add(letter);
                            }
                        }
                        if (abbrLetters.Count == 0)
                            abbrLetters.Add("?");
                        abbrCharCandidates.Add(abbrLetters);
                    }
                    var abbrCombos = CartesianProduct(abbrCharCandidates, maxCandidates);
                    abbrPinyinSegCandidates.Add(abbrCombos);
                }
            }

            // 跨段笛卡尔积，拼接各段候选
            var finalFull = CrossSegmentProduct(fullPinyinSegCandidates, maxCandidates);
            var finalAbbr = CrossSegmentProduct(abbrPinyinSegCandidates, maxCandidates);

            var entry = new PinyinEntry();
            entry.FullPinyins.AddRange(finalFull);
            entry.AbbrPinyins.AddRange(finalAbbr);
            return entry;
        }

        /// <summary>将名称分割为汉字段和非汉字段。</summary>
        private static List<Segment> SplitSegments(string name)
        {
            var segments = new List<Segment>();
            if (string.IsNullOrEmpty(name))
                return segments;

            int i = 0;
            while (i < name.Length)
            {
                bool isCjk = IsCjkChar(name[i]);
                int start = i;
                while (i < name.Length && IsCjkChar(name[i]) == isCjk)
                    i++;
                segments.Add(new Segment { Text = name.Substring(start, i - start), IsCjk = isCjk });
            }
            return segments;
        }

        private static bool IsCjkChar(char c) => c >= '\u4E00' && c <= '\u9FFF';

        /// <summary>对单个汉字段内的字符候选做笛卡尔积，生成拼接字符串列表。</summary>
        private static List<string> CartesianProduct(List<List<string>> charCandidates, int maxCount)
        {
            var result = new List<string> { "" };
            foreach (var candidates in charCandidates)
            {
                var next = new List<string>();
                foreach (var existing in result)
                {
                    foreach (var candidate in candidates)
                    {
                        next.Add(existing + candidate);
                        if (next.Count >= maxCount)
                            goto done;
                    }
                }
                done:
                result = next;
            }
            return result;
        }

        /// <summary>跨段笛卡尔积，将各段候选拼接为最终候选列表。</summary>
        private static List<string> CrossSegmentProduct(List<List<string>> segCandidates, int maxCount)
        {
            var result = new List<string> { "" };
            foreach (var segList in segCandidates)
            {
                var next = new List<string>();
                foreach (var existing in result)
                {
                    foreach (var seg in segList)
                    {
                        next.Add(existing + seg);
                        if (next.Count >= maxCount)
                            goto done;
                    }
                }
                done:
                result = next;
            }
            return result;
        }

        private struct Segment
        {
            public string Text;
            public bool IsCjk;
        }
    }
}
