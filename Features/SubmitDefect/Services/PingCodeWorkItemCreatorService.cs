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
    /// 图片以 base64 编码嵌入 shiyitu（img 的 data URL），永久显示、不依赖外部 URL（实测 PingCode 网页端正常渲染）；
    /// 视频/任意文件仍走 /v1/attachments 附件关联。流程：创建工作项 → 写 shiyitu（图片 base64）→ 上传附件（视频/文件）。
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

                // 图片：优先走 atlas 内部链路（cookie→upload-token→file/upload→atlas URL，永久显示，无字段限制）
                // 未登录（无 cookie）时 fallback base64 嵌入（字段大小受限）
                var images = options.Images ?? new List<PastedImage>();
                var cookieManager = new PingCodeCookieManager();
                var cookie = await cookieManager.LoadCookiesAsync();
                var atlasUploader = new PingCodeAtlasUploader(cookie);

                if (atlasUploader.IsReady)
                {
                    for (var i = 0; i < images.Count; i++)
                    {
                        var img = images[i];
                        img.UploadStatus = UploadStatus.Uploading;
                        Report($"上传示意图 {i + 1}/{images.Count}…");
                        try
                        {
                            var atlasUrl = await atlasUploader.UploadAsync(img.Data, img.FileName, img.ContentType);
                            if (!string.IsNullOrWhiteSpace(atlasUrl))
                            {
                                img.PublicUrl = atlasUrl;
                                img.UploadStatus = UploadStatus.Done;
                            }
                            else
                            {
                                img.UploadStatus = UploadStatus.Failed;
                                img.Error = "atlas 上传失败";
                                res.Steps.Add($"图片 {img.FileName} atlas 上传失败（Cookie 可能过期，请在提交工作项页重新登录 PingCode）");
                            }
                        }
                        catch (Exception ex)
                        {
                            img.UploadStatus = UploadStatus.Failed;
                            img.Error = ex.Message;
                            res.Steps.Add($"图片 {img.FileName} atlas 上传异常：{ex.Message}");
                        }
                    }
                }

                var shiyitu = BuildShiyituHtml(images);
                if (!string.IsNullOrWhiteSpace(shiyitu))
                {
                    Report("正在写入示意图…");
                    try
                    {
                        await api.UpdateWorkItemAsync(res.WorkItemId,
                            new JObject { ["properties"] = new JObject { ["shiyitu"] = shiyitu } });
                        res.ShiyituWritten = true;
                        res.Steps.Add(atlasUploader.IsReady
                            ? "示意图已写入（atlas 永久 URL）"
                            : "示意图已写入（base64 内嵌，未登录 PingCode；大图可能超字段上限，请在提交工作项页登录 PingCode 走 atlas）");
                    }
                    catch (Exception ex)
                    {
                        res.ShiyituWritten = false;
                        res.Steps.Add($"示意图写入失败：{ex.Message}");
                    }
                }

                // 附件（视频/文件）：只作附件关联，不进示意图（shiyitu 是 <img>，视频/文件不能进）
                var attachments = options.Attachments ?? new List<PastedImage>();
                for (var i = 0; i < attachments.Count; i++)
                {
                    var a = attachments[i];
                    a.UploadStatus = UploadStatus.Uploading;
                    Report($"上传附件 {i + 1}/{attachments.Count}…");
                    try
                    {
                        var resp = await api.UploadAttachmentViaApiAsync(a.Data, a.FileName, a.ContentType, res.WorkItemId, null);
                        if (resp == null)
                        {
                            a.UploadStatus = UploadStatus.Failed;
                            a.Error = "上传请求失败";
                            res.Steps.Add($"附件 {a.FileName} 上传失败");
                        }
                        else
                        {
                            a.UploadStatus = UploadStatus.Done;
                        }
                    }
                    catch (Exception ex)
                    {
                        a.UploadStatus = UploadStatus.Failed;
                        a.Error = ex.Message;
                        res.Steps.Add($"附件 {a.FileName} 上传异常：{ex.Message}");
                    }
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

        private static string BuildShiyituHtml(IList<PastedImage> images)
        {
            if ((images == null) || (images.Count == 0))
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var img in images)
            {
                if (img == null)
                {
                    continue;
                }

                var encodedAlt = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(img.FileName) ? "示意图" : img.FileName);

                // 只走 atlas URL（关掉 base64 fallback，让 atlas 失败直接暴露，不悄悄兜底）
                if (!string.IsNullOrWhiteSpace(img.PublicUrl))
                {
                    var encodedUrl = WebUtility.HtmlEncode(img.PublicUrl);
                    sb.Append($"<div class=\"sketch-image\"><img src=\"{encodedUrl}\" alt=\"{encodedAlt}\"/></div>");
                }
                // base64 fallback 暂时关闭（保留备用，以后可能重新启用兜底）
                //else if ((img.Data != null) && (img.Data.Length > 0))
                //{
                //    var mime = string.IsNullOrWhiteSpace(img.ContentType) ? "image/png" : img.ContentType;
                //    var b64 = Convert.ToBase64String(img.Data);
                //    sb.Append($"<div class=\"sketch-image\"><img src=\"data:{mime};base64,{b64}\" alt=\"{encodedAlt}\""/></div>");
                //}
            }

            return sb.Length == 0 ? null : sb.ToString();
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
