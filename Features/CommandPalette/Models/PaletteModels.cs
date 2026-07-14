using System.Collections.Generic;
using Newtonsoft.Json;

namespace PackageManager.Features.CommandPalette.Models
{
    /// <summary>
    /// 命令面板候选项，序列化为 JSON 推送给前端 JS 渲染。
    /// </summary>
    public sealed class PaletteItem
    {
        public string Id { get; set; }
        public string Type { get; set; }       // cmd | nav | pkg | file
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Pinyin { get; set; }      // 拼音（全拼+简拼，| 分隔），可空
        public string Hint { get; set; }        // 右侧快捷键提示
        public List<PaletteTag> Tags { get; set; }

        /// <summary>执行路由载荷（nav 的 key / pkg 的 ProductName / file 的全路径 / cmd 的命令键），不序列化给前端。</summary>
        [JsonIgnore]
        public string ExecuteKey { get; set; }

        public PaletteItem AddTag(string text, string kind = "st")
        {
            if (Tags == null) Tags = new List<PaletteTag>();
            Tags.Add(new PaletteTag { Text = text, Kind = kind });
            return this;
        }
    }

    public sealed class PaletteTag
    {
        public string Text { get; set; }
        public string Kind { get; set; }   // new | warn | ahead | st
    }
}
