using System;
using System.IO;
using System.Text;

namespace PackageManager.Services
{
    /// <summary>
    /// 管理 AI CLI 项目级记忆文件。
    /// - Claude Code：~/.claude/projects/{project-slug}/memory/*.md
    /// - Codex：{项目根目录}/AGENTS.md
    /// 启动时幂等调用：文件已存在则跳过，不存在则创建。
    /// 同时清理旧版全局 CLAUDE.md 中的受管块（已迁移到项目级 memory）。
    /// </summary>
    public static class AiProjectMemoryService
    {
        private static readonly object SyncRoot = new object();

        private const string GlobalBlockBegin = "<!-- PackageManager Global Behavior Rules: Begin -->";
        private const string GlobalBlockEnd = "<!-- PackageManager Global Behavior Rules: End -->";
        private const string CodexAgentsBegin = "<!-- PackageManager Behavior Rules: Begin -->";
        private const string CodexAgentsEnd = "<!-- PackageManager Behavior Rules: End -->";

        /// <summary>
        /// 确保当前项目的 Claude Code 和 Codex 项目级记忆文件存在。
        /// 幂等：已存在则跳过。
        /// </summary>
        /// <param name="projectRoot">
        /// 项目根目录（由调用方传入，即启动 AI CLI 时的工作目录，如 E:\PackageManager）。
        /// 传入时创建 Claude Code memory + Codex AGENTS.md；为 null 时跳过。
        /// </param>
        public static void EnsureProjectMemory(string projectRoot = null)
        {
            lock (SyncRoot)
            {
                if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
                {
                    return;
                }

                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(userProfile))
                {
                    throw new DirectoryNotFoundException("无法定位用户目录，不能同步项目级记忆。");
                }

                // E:\PackageManager → E--PackageManager
                var projectSlug = projectRoot.Replace('\\', '-').Replace('/', '-').Replace(':', '-');

                // Claude Code 项目级记忆：~/.claude/projects/{slug}/memory/
                var memoryDir = Path.Combine(userProfile, ".claude", "projects", projectSlug, "memory");
                EnsureMemoryFileCreated(Path.Combine(memoryDir, "strictly-follow-skill-constraints.md"), BuildStrictlyFollowContent);
                EnsureMemoryFileCreated(Path.Combine(memoryDir, "diagnose-before-acting.md"), BuildDiagnoseBeforeActingContent);
                EnsureMemoryFileCreated(Path.Combine(memoryDir, "image-understanding-fallback.md"), BuildImageUnderstandingContent);
                EnsureMemoryFileCreated(Path.Combine(memoryDir, "MEMORY.md"), BuildIndexContent);

                // Codex 项目级指令：{项目根目录}/AGENTS.md
                EnsureCodexProjectAgentsMd(Path.Combine(projectRoot, "AGENTS.md"));

                CleanupObsoleteGlobalBlock(Path.Combine(userProfile, ".claude", "CLAUDE.md"));
            }
        }

        private static void EnsureCodexProjectAgentsMd(string agentsPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(agentsPath));

            var existing = File.Exists(agentsPath) ? File.ReadAllText(agentsPath, Encoding.UTF8) : string.Empty;
            var next = UpsertManagedBlock(existing, CodexAgentsBegin, CodexAgentsEnd, BuildCodexBehaviorRulesBlock());
            if (string.Equals(existing, next, StringComparison.Ordinal))
            {
                return;
            }

            WriteWithBackup(agentsPath, next, File.Exists(agentsPath));
        }

        private static string UpsertManagedBlock(string existing, string beginMarker, string endMarker, string blockContent)
        {
            var block = beginMarker + Environment.NewLine + blockContent + Environment.NewLine + endMarker;
            if (string.IsNullOrWhiteSpace(existing))
            {
                return block + Environment.NewLine;
            }

            var begin = existing.IndexOf(beginMarker, StringComparison.Ordinal);
            var end = existing.IndexOf(endMarker, StringComparison.Ordinal);
            if (begin >= 0 && end > begin)
            {
                end += endMarker.Length;
                return existing.Substring(0, begin) + block + existing.Substring(end);
            }

            var separator = existing.EndsWith(Environment.NewLine, StringComparison.Ordinal)
                ? Environment.NewLine
                : Environment.NewLine + Environment.NewLine;
            return existing + separator + block + Environment.NewLine;
        }

        private static string BuildCodexBehaviorRulesBlock()
        {
            var sb = new StringBuilder();
            sb.AppendLine("## 严格遵守 skill 约束和执行流程");
            sb.AppendLine();
            sb.AppendLine("执行 skill 或 prompt 时，其中明确禁止的事情一律不允许做，明确要求的执行流程必须严格按步骤来。不得因为技术上有困难就自己找变通办法绕过约束，不得因为\"方便\"就跳过或替换流程步骤。");
            sb.AppendLine();
            sb.AppendLine("具体包括但不限于：");
            sb.AppendLine("- 禁止创建临时文件时，必须在内存中解决，不得写 .py/.ps1/.txt 等任何文件");
            sb.AppendLine("- 要求用特定脚本完成的操作，不得自己写替代脚本或手动执行等效命令");
            sb.AppendLine("- 指定了状态目录/文件路径的，必须使用指定路径，不得回退到默认路径");
            sb.AppendLine("- 有明确步骤顺序的，必须按顺序执行，不得合并、跳过或重新排列");
            sb.AppendLine();
            sb.AppendLine("## 先分析再执行");
            sb.AppendLine();
            sb.AppendLine("收到问题或反馈时，严禁跳过分析直接改代码。必须先做以下步骤：");
            sb.AppendLine();
            sb.AppendLine("1. **读取日志和错误信息** — 先看控制台输出、日志文件、崩溃报告，提取关键错误信息");
            sb.AppendLine("2. **结合现有代码分析原因** — 根据错误信息找到相关代码，理解上下文，分析根因");
            sb.AppendLine("3. **给出结论** — 向用户说明问题原因和建议的修复方案");
            sb.AppendLine("4. **等待确认** — 在用户确认分析正确之前，不要修改任何代码文件");
            sb.AppendLine();
            sb.AppendLine("以下行为严禁：");
            sb.AppendLine("- 未经确认就直接修改代码（包括 Edit、Write 等工具）");
            sb.AppendLine("- 未经要求就主动尝试编译构建（很多项目未必能编译通过，编译会浪费大量 token）");
            sb.AppendLine("- 分析完成后自作主张执行修复，而不是先报告结论");
            sb.AppendLine("- 日志位置不明确时自行搜索探查（探查耗时且浪费 token）");
            sb.AppendLine();
            sb.AppendLine("当需要读取日志但位置不明确时，必须第一时间向用户给出选项让用户选择，而不是开放式提问让用户自己输入。给出两个选项：① 自行搜索（结合代码结构定位日志路径，而非暴力全盘搜索）② 用户输入（用户提供的路径或关键词，据此查找）。用户点击选项即可，不要让用户手动打字回复。");
            sb.AppendLine();
            sb.AppendLine("## 图片理解能力（无视觉模型兜底）");
            sb.AppendLine();
            sb.AppendLine("当执行需要读取或理解图片内容的功能（分析截图、UI 设计稿、架构图、图表、识别图中文字等）时，按以下顺序判断：");
            sb.AppendLine();
            sb.AppendLine("1. **优先判断自身是否具备原生图片理解能力** — 若为多模态模型、本身支持视觉输入，直接读取图片即可。");
            sb.AppendLine("2. **模型本身无视觉能力时，优先调用已配置的 MCP 工具弥补** — 先确认当前环境是否配置了图片/视觉类 MCP（图片分析、OCR 文字识别、图表解析、UI 截图比对等 server）；若有，调用对应 MCP 工具完成识图。");
            sb.AppendLine("3. **既无原生视觉能力，也没有任何可用的图片分析 MCP** — 明确告知用户「无法识图」，不得对图片内容进行臆测或编造；直接基于已有信息（文件名、上下文、用户已给出的文字等）继续完成任务，不再额外向用户索要图片描述。");
            sb.AppendLine();
            sb.Append("能看图就直接看 → 看不了就用 MCP 看 → 两者都没有就如实告知无法识图、基于已有文字信息继续完成，绝不编造图片内容。");
            return sb.ToString();
        }

        private static void EnsureMemoryFileCreated(string targetPath, Func<string> contentBuilder)
        {
            if (File.Exists(targetPath))
            {
                return;
            }

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(targetPath, contentBuilder(), Encoding.UTF8);
        }

        private static void WriteWithBackup(string path, string content, bool createBackup)
        {
            var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tempPath, content, Encoding.UTF8);

            try
            {
                if (createBackup)
                {
                    var backupPath = path + ".bak." + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                    File.Replace(tempPath, path, backupPath);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }
            }
        }

        private static void CleanupObsoleteGlobalBlock(string claudeMdPath)
        {
            if (!File.Exists(claudeMdPath))
            {
                return;
            }

            var content = File.ReadAllText(claudeMdPath, Encoding.UTF8);
            var beginIdx = content.IndexOf(GlobalBlockBegin, StringComparison.Ordinal);
            var endIdx = content.IndexOf(GlobalBlockEnd, StringComparison.Ordinal);

            if (beginIdx < 0 || endIdx <= beginIdx)
            {
                return;
            }

            // 移除受管块及其前后空行
            var blockEnd = endIdx + GlobalBlockEnd.Length;
            var trimmed = content.Substring(0, beginIdx).TrimEnd()
                        + Environment.NewLine
                        + content.Substring(blockEnd).TrimStart();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                File.Delete(claudeMdPath);
            }
            else
            {
                File.WriteAllText(claudeMdPath, trimmed, Encoding.UTF8);
            }
        }

        private static string BuildStrictlyFollowContent()
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("name: strictly-follow-skill-constraints");
            sb.AppendLine("description: 执行任何 skill/prompt 时，必须逐条遵守其中所有约束和执行流程，不得自行变通或绕过");
            sb.AppendLine("metadata:");
            sb.AppendLine("  type: feedback");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("执行 skill 或 prompt 时，其中明确禁止的事情一律不允许做，明确要求的执行流程必须严格按步骤来。不得因为技术上有困难就自己找变通办法绕过约束，不得因为\"方便\"就跳过或替换流程步骤。");
            sb.AppendLine();
            sb.AppendLine("具体包括但不限于：");
            sb.AppendLine("- 禁止创建临时文件时，必须在内存中解决，不得写 .py/.ps1/.txt 等任何文件");
            sb.AppendLine("- 要求用特定脚本完成的操作，不得自己写替代脚本或手动执行等效命令");
            sb.AppendLine("- 指定了状态目录/文件路径的，必须使用指定路径，不得回退到默认路径");
            sb.AppendLine("- 有明确步骤顺序的，必须按顺序执行，不得合并、跳过或重新排列");
            sb.AppendLine();
            sb.AppendLine("**Why:** 用户精心编写的 skill 约束每一条都有原因，目的是确保流程可控、可审计、可复现。自行变通会破坏这些目标，也违背了用户编写 skill 的初衷。");
            sb.AppendLine();
            sb.AppendLine("**How to apply:** 遇到 skill/prompt 中的约束时，先逐条理解每条限制的含义，然后在约束范围内寻找解决方案。如果当前工具能力确实无法满足，应该向用户说明困难并请求指导，而不是自行绕过。永远不要\"我行我素\"。");
            return sb.ToString().TrimEnd('\r', '\n');
        }

        private static string BuildDiagnoseBeforeActingContent()
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("name: diagnose-before-acting");
            sb.AppendLine("description: 先分析排查再给结论，确认后才执行，不要主动编译或改代码");
            sb.AppendLine("metadata:");
            sb.AppendLine("  type: feedback");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("收到问题或反馈时，严禁跳过分析直接改代码。必须先做以下步骤：");
            sb.AppendLine();
            sb.AppendLine("1. **读取日志和错误信息** — 先看控制台输出、日志文件、崩溃报告，提取关键错误信息");
            sb.AppendLine("2. **结合现有代码分析原因** — 根据错误信息找到相关代码，理解上下文，分析根因");
            sb.AppendLine("3. **给出结论** — 向用户说明问题原因和建议的修复方案");
            sb.AppendLine("4. **等待确认** — 在用户确认分析正确之前，不要修改任何代码文件");
            sb.AppendLine();
            sb.AppendLine("以下行为严禁：");
            sb.AppendLine("- 未经确认就直接修改代码（包括 Edit、Write 等工具）");
            sb.AppendLine("- 未经要求就主动尝试编译构建（很多项目未必能编译通过，编译会浪费大量 token）");
            sb.AppendLine("- 分析完成后自作主张执行修复，而不是先报告结论");
            sb.AppendLine("- 日志位置不明确时自行搜索探查（探查耗时且浪费 token）");
            sb.AppendLine();
            sb.AppendLine("当需要读取日志但位置不明确时，必须第一时间向用户给出选项让用户选择，而不是开放式提问让用户自己输入。给出两个选项：① 自行搜索（结合代码结构定位日志路径，而非暴力全盘搜索）② 用户输入（用户提供的路径或关键词，据此查找）。用户点击选项即可，不要让用户手动打字回复。");
            sb.AppendLine();
            sb.AppendLine("**Why:** 控制台/命令行模式下没有 IDE 的 ask 模式来分离分析与执行。弱模型容易在每次对话结束时自动动代码，浪费 token 做无效编译，还可能改错代码。先分析再确认再执行，是保证效率和正确性的基本原则。");
            sb.AppendLine();
            sb.AppendLine("**How to apply:** 每次收到问题时，强制自己走完上述步骤后再回复。回复时以\"分析结果\"开头，不要以代码修改开头。用户说\"确认\"\"执行\"\"改吧\"之类的话之后才动代码。");
            return sb.ToString().TrimEnd('\r', '\n');
        }

        private static string BuildImageUnderstandingContent()
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("name: image-understanding-fallback");
            sb.AppendLine("description: 图片理解按原生能力→MCP兜底→如实告知三级优先级处理");
            sb.AppendLine("metadata:");
            sb.AppendLine("  type: feedback");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("当执行需要读取或理解图片内容的功能（分析截图、UI 设计稿、架构图、图表、识别图中文字等）时，按以下顺序判断：");
            sb.AppendLine();
            sb.AppendLine("1. **优先判断自身是否具备原生图片理解能力** — 若为多模态模型、本身支持视觉输入，直接读取图片即可。");
            sb.AppendLine("2. **模型本身无视觉能力时，优先调用已配置的 MCP 工具弥补** — 先确认当前环境是否配置了图片/视觉类 MCP（图片分析、OCR 文字识别、图表解析、UI 截图比对等 server）；若有，调用对应 MCP 工具完成识图。");
            sb.AppendLine("3. **既无原生视觉能力，也没有任何可用的图片分析 MCP** — 明确告知用户「无法识图」，不得对图片内容进行臆测或编造；直接基于已有信息（文件名、上下文、用户已给出的文字等）继续完成任务，不再额外向用户索要图片描述。");
            sb.AppendLine();
            sb.AppendLine("**Why:** 不同模型视觉能力差异很大，弱模型拿到图片可能凭空编造内容，造成误导。用 MCP 兜底可以在不换模型的情况下补齐识图能力；都没有时如实告知并复用已有信息，才能避免幻觉和反复打扰用户。");
            sb.AppendLine();
            sb.AppendLine("**How to apply:** 能看图就直接看 → 看不了就用 MCP 看 → 两者都没有就如实告知无法识图、基于已有文字信息继续完成，绝不编造图片内容。");
            return sb.ToString().TrimEnd('\r', '\n');
        }

        private static string BuildIndexContent()
        {
            var sb = new StringBuilder();
            sb.AppendLine("- [严格遵守 skill 约束和执行流程](strictly-follow-skill-constraints.md) — skill 中禁止的事一律不做，要求的流程必须按步骤来");
            sb.AppendLine("- [先分析再执行](diagnose-before-acting.md) — 读日志排查原因后给结论，确认后再改代码，不主动编译");
            sb.AppendLine("- [图片理解兜底](image-understanding-fallback.md) — 原生视觉→MCP兜底→如实告知，不编造图片内容");
            return sb.ToString().TrimEnd('\r', '\n');
        }
    }
}
