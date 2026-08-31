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
    /// PingCode 工作项（缺陷/故事/任务）创建编排服务：创建工作项 → 上传图片并关联 → 缺陷/故事写示意图字段、任务把图片追加到描述。
    /// </summary>
    /// <remarks>
    /// 图片以 base64 编码嵌入 shiyitu（img 的 data URL），永久显示、不依赖外部 URL（实测 PingCode 网页端正常渲染）；
    /// 视频/任意文件仍走 /v1/attachments 附件关联。流程：创建工作项 → 写 shiyitu（图片 base64）→ 上传附件（视频/文件）。
    /// 任务类型没有示意图（shiyitu）字段，图片 atlas 上传后以 HTML 追加到描述末尾。
    /// </remarks>
    public class PingCodeWorkItemCreatorService
    {
        private const string DefaultDescription = "<p>-</p>";

        /// <summary>
        /// 工作项类型名称到 PingCode 系统类型标识的映射。
        /// 缺陷/故事用系统类型 id（稳定，无需查端点）；任务的 type_id 不能用 "task" 短名，
        /// 须在 CreateAsync 里查项目内真实类型 ID（见 <see cref="PingCodeApiService.GetTaskTypeIdAsync"/>）。
        /// </summary>
        private static readonly Dictionary<string, string> WorkItemTypeIds =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "缺陷", "bug" },
                { "故事", "story" },
                { "任务", "task" },
                { "bug", "bug" },
                { "story", "story" },
                { "task", "task" },
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

                var isTask = string.Equals(typeId, "task", StringComparison.OrdinalIgnoreCase);

                // 任务类型的 type_id 不能用 "task" 短名（AI 拆解实测 PingCode 不接受），
                // 须查项目内「任务」类型的真实 ID；查询失败/未命中时回退 "task" 并在步骤明细中提示
                if (isTask)
                {
                    try
                    {
                        var taskTypeId = await api.GetTaskTypeIdAsync(options.ProjectId);
                        if (!string.IsNullOrWhiteSpace(taskTypeId))
                        {
                            typeId = taskTypeId;
                        }
                        else
                        {
                            res.Steps.Add("项目内未查到「任务」类型，回退 type_id=task（可能创建失败）");
                        }
                    }
                    catch (Exception ex)
                    {
                        res.Steps.Add($"查询「任务」类型失败，回退 type_id=task：{ex.Message}");
                    }
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
                var sessionRefreshed = false; // 整次提交只静默续期一次（避免每张图重复起 WebView2）

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

                            // 上传失败可能是 cookie 过期：静默续期（探测+隐藏 WebView2）后自动重试一次，用户无感
                            if (string.IsNullOrWhiteSpace(atlasUrl) && !sessionRefreshed)
                            {
                                sessionRefreshed = true;
                                Report("示意图上传未成功，正在自动续期 PingCode 会话…");
                                var fresh = await new PingCodeSessionService().EnsureFreshCookieAsync(force: true);
                                if (!string.IsNullOrWhiteSpace(fresh))
                                {
                                    atlasUploader = new PingCodeAtlasUploader(fresh);
                                    atlasUrl = await atlasUploader.UploadAsync(img.Data, img.FileName, img.ContentType);
                                    if (!string.IsNullOrWhiteSpace(atlasUrl))
                                    {
                                        res.Steps.Add("Cookie 已自动续期，重试上传成功");
                                    }
                                }
                            }

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

                var imageHtml = isTask ? BuildDescriptionImageHtml(images) : BuildShiyituHtml(images);
                if (!string.IsNullOrWhiteSpace(imageHtml))
                {
                    Report(isTask ? "正在把图片追加到描述…" : "正在写入示意图…");
                    try
                    {
                        if (isTask)
                        {
                            // 任务无示意图字段（shiyitu），图片 HTML 追加到描述末尾
                            var baseDesc = string.IsNullOrWhiteSpace(options.DescriptionHtml) ? DefaultDescription : options.DescriptionHtml;
                            await api.UpdateWorkItemAsync(res.WorkItemId,
                                new JObject { ["description"] = baseDesc + imageHtml });
                            res.ShiyituWritten = true;
                            res.Steps.Add("图片已追加到任务描述（atlas 永久 URL）");
                        }
                        else
                        {
                            await api.UpdateWorkItemAsync(res.WorkItemId,
                                new JObject { ["properties"] = new JObject { ["shiyitu"] = imageHtml } });
                            res.ShiyituWritten = true;
                            res.Steps.Add(atlasUploader.IsReady
                                ? "示意图已写入（atlas 永久 URL）"
                                : "示意图已写入（base64 内嵌，未登录 PingCode；大图可能超字段上限，请在提交工作项页登录 PingCode 走 atlas）");
                        }
                    }
                    catch (Exception ex)
                    {
                        res.ShiyituWritten = false;
                        res.Steps.Add(isTask ? $"图片追加到描述失败：{ex.Message}" : $"示意图写入失败：{ex.Message}");
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

        /// <summary>
        /// 构建任务描述用的图片 HTML：1:1 复刻 PingCode 网页编辑器插图的标签结构——
        /// src + originUrl（{url}/origin-url 原图端点）+ alt + size + style，无外层包装。
        /// 缺 originUrl/size 或带 div 包装时，网页端点开预览会得到透明占位图，
        /// 富文本编辑器解析失败还可能在交互保存时把描述整个抹空（实测 JD_GROUP-7216）。
        /// </summary>
        private static string BuildDescriptionImageHtml(IList<PastedImage> images)
        {
            if ((images == null) || (images.Count == 0))
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var img in images)
            {
                if ((img == null) || string.IsNullOrWhiteSpace(img.PublicUrl))
                {
                    continue;
                }

                var encodedUrl = WebUtility.HtmlEncode(img.PublicUrl);
                var encodedAlt = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(img.FileName) ? "示意图" : img.FileName);
                var size = (img.Data == null) ? 0 : img.Data.Length;
                sb.Append($"<img src=\"{encodedUrl}\" originUrl=\"{encodedUrl}/origin-url\" alt=\"{encodedAlt}\" size=\"{size}\" style=\"text-align: center;\" />");
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
