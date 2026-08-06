using System.Collections.Generic;
using PackageManager.Features.SubmitDefect.Models;

namespace PackageManager.Features.SubmitDefect.Services
{
    /// <summary>
    /// 提交工作项的入参。
    /// </summary>
    public class SubmitDefectOptions
    {
        /// <summary>项目标识。</summary>
        public string ProjectId { get; set; }

        /// <summary>迭代（冲刺）标识。</summary>
        public string IterationId { get; set; }

        /// <summary>工作项类型名称：缺陷 或 故事。</summary>
        public string WorkItemType { get; set; }

        /// <summary>标题。</summary>
        public string Title { get; set; }

        /// <summary>描述 HTML（仅文字，不含图）。</summary>
        public string DescriptionHtml { get; set; }

        /// <summary>示意图图片列表（含动图，写入 shiyitu 字段）。</summary>
        public List<PastedImage> Images { get; set; } = new List<PastedImage>();

        /// <summary>视频附件列表（只作附件关联，不进示意图）。</summary>
        public List<PastedImage> Videos { get; set; } = new List<PastedImage>();
    }
}
