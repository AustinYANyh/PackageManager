using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PackageManager.Features.SubmitDefect.Models;
using PackageManager.Services;
using PackageManager.Services.PingCode;

namespace PackageManager.Features.SubmitDefect.Services
{
    /// <summary>
    /// PingCode 工作项（缺陷/故事）创建编排服务：创建工作项 → 上传图片并关联 → 写入示意图字段。
    /// </summary>
    /// <remarks>
    /// 写入示意图走 PATCH 的 properties.shiyitu（实测 fields.shiyitu 被静默忽略、properties.shiyitu 生效）；
    /// 图片必须先有 work_item.id 才能关联上传，故采用「先创建→再传图→再 PATCH」三步时序。
    /// </remarks>
    public class PingCodeWorkItemCreatorService
    {
        private const string DefaultDescription = "<p>-</p>";

        /// <summary>
        /// 工作项类型名称到 PingCode 系统类型标识的映射（系统类型 id 稳定，无需查端点）。
        /// </summary>
        private static readonly Dictionary<string, string> WorkItemTypeIds =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "缺陷", "bug" },
                { "故事", "story" },
                { "bug", "bug" },
                { "story", "story" },
            };

        private readonly PingCodeApiService api;

        /// <summary>
        /// 初始化 <see cref="PingCodeWorkItemCreatorService"/> 的新实例。
        /// </summary>
        public PingCodeWorkItemCreatorService()
        {
            api = new PingCodeApiService();
        }

        /// <summary>
        /// 初始化 <see cref="PingCodeWorkItemCreatorService"/> 并复用指定的 API 实例。
        /// </summary>
        /// <param name="api">已有的 PingCode API 实例，为 null 时新建。</param>
        public PingCodeWorkItemCreatorService(PingCodeApiService api)
        {
            this.api = api ?? new PingCodeApiService();
        }

        /// <summary>
        /// 执行创建编排。
        /// </summary>
        /// <param name="options">提交入参。</param>
        /// <param name="progress">进度回调（可选，用于驱动 UI 状态文案）。</param>
        /// <returns>创建结果，含编号、链接、各步骤明细。工作项创建成功即视为 Success（示意图写失败不影响）。</returns>
        public async Task<CreateResult> CreateAsync(SubmitDefectOptions options, IProgress<string> progress = null)
        {
            var res = new CreateResult();
            void Report(string msg) => progress?.Report(msg);

            try
            {
                if (string.IsNullOrWhiteSpace(options?.ProjectId))
                {
                    throw new InvalidOperationException("未选择项目");
                }

                if (string.IsNullOrWhiteSpace(options.IterationId))
                {
                    throw new InvalidOperationException("当前项目无可用迭代");
                }

                if (!WorkItemTypeIds.TryGetValue(options.WorkItemType ?? "缺陷", out var typeId))
                {
                    typeId = "bug";
                }

                Report("正在创建工作项…");
                var body = new JObject
                {
                    ["project_id"] = options.ProjectId,
                    ["title"] = string.IsNullOrWhiteSpace(options.Title) ? "（无标题）" : options.Title,
                    ["type_id"] = typeId,
                    ["description"] = string.IsNullOrWhiteSpace(options.DescriptionHtml) ? DefaultDescription : options.DescriptionHtml,
                    ["story_points"] = 0.1,
                    ["sprint_id"] = options.IterationId,
                };

                var created = await api.CreateWorkItemAsync(body);
                res.WorkItemId = created?.Value<string>("id");
                res.Identifier = created?.Value<string>("identifier");
                res.HtmlUrl = created?.Value<string>("html_url");
                if (string.IsNullOrWhiteSpace(res.WorkItemId))
                {
                    throw new InvalidOperationException("创建工作项返回无 id");
                }

                res.Steps.Add($"已创建 {res.Identifier}");
                Report($"已创建 {res.Identifier}");

                var images = options.Images ?? new List<PastedImage>();
                var uploaded = 0;
                for (var i = 0; i < images.Count; i++)
                {
                    var img = images[i];
                    img.UploadStatus = UploadStatus.Uploading;
                    Report($"上传图片 {i + 1}/{images.Count}…");
                    try
                    {
                        var resp = await api.UploadAttachmentViaApiAsync(img.Data, img.FileName, img.ContentType, res.WorkItemId, null);
                        if (resp == null)
                        {
                            img.UploadStatus = UploadStatus.Failed;
                            img.Error = "上传请求失败";
                            res.Steps.Add($"图片 {img.FileName} 上传失败");
                        }
                        else
                        {
                            var url = ExtractPublicUrl(resp);
                            if (!string.IsNullOrWhiteSpace(url))
                            {
                                img.PublicUrl = url;
                                img.UploadStatus = UploadStatus.Done;
                                uploaded++;
                            }
                            else
                            {
                                img.UploadStatus = UploadStatus.Failed;
                                img.Error = "未取到公开地址";
                                var preview = resp.ToString();
                                if (preview.Length > 220)
                                {
                                    preview = preview.Substring(0, 220) + "…";
                                }
                                res.Steps.Add($"图片 {img.FileName} 已上传但未取到公开地址（已作为附件关联），响应：{preview}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        img.UploadStatus = UploadStatus.Failed;
                        img.Error = ex.Message;
                        res.Steps.Add($"图片 {img.FileName} 上传异常：{ex.Message}");
                    }
                }

                var withUrl = images.Where(x => !string.IsNullOrWhiteSpace(x.PublicUrl)).ToList();
                var shiyitu = BuildShiyituHtml(withUrl);
                if (!string.IsNullOrWhiteSpace(shiyitu))
                {
                    Report("正在写入示意图…");
                    try
                    {
                        await api.UpdateWorkItemAsync(res.WorkItemId,
                            new JObject { ["properties"] = new JObject { ["shiyitu"] = shiyitu } });
                        res.ShiyituWritten = true;
                        res.Steps.Add("示意图已写入");
                        if (withUrl.Any(i =>
                        {
                            var u = (i.PublicUrl ?? string.Empty).ToLowerInvariant();
                            return u.Contains("q-sign-time") || u.Contains("cos.ap-") || u.Contains("response-content-disposition");
                        }))
                        {
                            res.Steps.Add("提示：示意图图片为附件临时签名地址（非永久公开链接），在 PingCode 网页端示意图区的长期显示请验证；图片已永久关联到工作项附件区作为保底");
                        }
                    }
                    catch (Exception ex)
                    {
                        res.ShiyituWritten = false;
                        res.Steps.Add($"示意图写入失败（{uploaded} 张图已作为附件关联到工作项）：{ex.Message}");
                    }
                }
                else if (images.Count > 0)
                {
                    res.Steps.Add(uploaded == 0
                        ? "无图片取到公开地址，示意图未写入（图片附件已关联）"
                        : "示意图无需写入");
                }

                res.Success = true;
                Report($"完成：{res.Identifier}");
                return res;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "提交工作项编排失败");
                res.Steps.Add($"失败：{ex.Message}");
                res.Success = false;
                return res;
            }
        }

        /// <summary>
        /// 从附件上传响应中提取公开图片地址（多字段兜底，实际字段名待首次实测确认）。
        /// </summary>
        /// <param name="resp">上传响应。</param>
        /// <returns>公开地址；取不到返回 null。</returns>
        private static string ExtractPublicUrl(JObject resp)
        {
            if (resp == null)
            {
                return null;
            }

            var direct = FirstNonEmpty(
                resp.Value<string>("public_url"),
                resp.Value<string>("download_url"),
                resp.Value<string>("url"),
                resp.Value<string>("raw_url"),
                resp.Value<string>("file_url"),
                resp.Value<string>("permalink_url"));
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }

            var file = resp["file"];
            if (file != null)
            {
                return FirstNonEmpty(
                    file.Value<string>("public_url"),
                    file.Value<string>("download_url"),
                    file.Value<string>("url"),
                    file.Value<string>("raw_url"));
            }

            return null;
        }

        private static string BuildShiyituHtml(IList<PastedImage> images)
        {
            if ((images == null) || (images.Count == 0))
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var img in images)
            {
                var url = img.PublicUrl;
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var encodedUrl = WebUtility.HtmlEncode(url);
                var encodedAlt = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(img.FileName) ? "示意图" : img.FileName);
                sb.Append($"<div class=\"sketch-image\"><img src=\"{encodedUrl}\" alt=\"{encodedAlt}\"/></div>");
            }

            return sb.Length == 0 ? null : sb.ToString();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return null;
            }

            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                {
                    return v;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// 工作项创建结果。
    /// </summary>
    public class CreateResult
    {
        /// <summary>是否整体成功（工作项已创建即视为成功，示意图写失败不影响）。</summary>
        public bool Success { get; set; }

        /// <summary>工作项编号，如 JD_GROUP-7067。</summary>
        public string Identifier { get; set; }

        /// <summary>工作项 Web 地址。</summary>
        public string HtmlUrl { get; set; }

        /// <summary>工作项标识。</summary>
        public string WorkItemId { get; set; }

        /// <summary>示意图字段是否成功写入。</summary>
        public bool ShiyituWritten { get; set; }

        /// <summary>各步骤明细（含失败项），供 UI 汇总展示。</summary>
        public List<string> Steps { get; set; } = new List<string>();
    }
}
