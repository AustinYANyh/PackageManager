using System;
using System.IO;
using System.Text;

namespace PackageManager.Services
{
    public static class AiGlobalBehaviorRuleService
    {
        private static readonly object SyncRoot = new object();

        private const string BeginMarker = "<!-- PackageManager Global Behavior Rules: Begin -->";
        private const string EndMarker = "<!-- PackageManager Global Behavior Rules: End -->";

        public static void EnsureGlobalBehaviorRules()
        {
            lock (SyncRoot)
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(userProfile))
                {
                    throw new DirectoryNotFoundException("无法定位用户目录，不能同步全局行为规则。");
                }

                EnsureClaudeGlobalRules(Path.Combine(userProfile, ".claude", "CLAUDE.md"));
                CleanupObsoleteMemoryDirectory(Path.Combine(userProfile, ".claude", "memory"));
            }
        }

        private static void EnsureClaudeGlobalRules(string claudeMemoryPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(claudeMemoryPath));

            var existing = File.Exists(claudeMemoryPath) ? File.ReadAllText(claudeMemoryPath, Encoding.UTF8) : string.Empty;
            var next = UpsertManagedBlock(existing, BeginMarker, EndMarker, BuildGlobalBehaviorRules());
            if (string.Equals(existing, next, StringComparison.Ordinal))
            {
                return;
            }

            WriteWithBackup(claudeMemoryPath, next, File.Exists(claudeMemoryPath));
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

        private static void CleanupObsoleteMemoryDirectory(string memoryDirectory)
        {
            if (!Directory.Exists(memoryDirectory))
            {
                return;
            }

            foreach (var fileName in new[] { "strictly-follow-skill-constraints.md", "diagnose-before-acting.md", "MEMORY.md" })
            {
                try
                {
                    var path = Path.Combine(memoryDirectory, fileName);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                }
            }

            try
            {
                if (Directory.Exists(memoryDirectory) && Directory.GetFileSystemEntries(memoryDirectory).Length == 0)
                {
                    Directory.Delete(memoryDirectory);
                }
            }
            catch
            {
            }
        }

        private static string BuildGlobalBehaviorRules()
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
            sb.AppendLine("**Why:** 用户精心编写的 skill 约束每一条都有原因，目的是确保流程可控、可审计、可复现。自行变通会破坏这些目标，也违背了用户编写 skill 的初衷。");
            sb.AppendLine();
            sb.AppendLine("**How to apply:** 遇到 skill/prompt 中的约束时，先逐条理解每条限制的含义，然后在约束范围内寻找解决方案。如果当前工具能力确实无法满足，应该向用户说明困难并请求指导，而不是自行绕过。永远不要\"我行我素\"。");
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
            sb.AppendLine("**Why:** 控制台/命令行模式下没有 IDE 的 ask 模式来分离分析与执行。弱模型容易在每次对话结束时自动动代码，浪费 token 做无效编译，还可能改错代码。先分析再确认再执行，是保证效率和正确性的基本原则。");
            sb.AppendLine();
            sb.AppendLine("**How to apply:** 每次收到问题时，强制自己走完上述步骤后再回复。回复时以\"分析结果\"开头，不要以代码修改开头。用户说\"确认\"\"执行\"\"改吧\"之类的话之后才动代码。");
            sb.AppendLine();
            sb.AppendLine("## 图片理解能力（无视觉模型兜底）");
            sb.AppendLine();
            sb.AppendLine("当执行需要读取或理解图片内容的功能（分析截图、UI 设计稿、架构图、图表、识别图中文字等）时，按以下顺序判断：");
            sb.AppendLine();
            sb.AppendLine("1. **优先判断自身是否具备原生图片理解能力** — 若为多模态模型、本身支持视觉输入，直接读取图片即可。");
            sb.AppendLine("2. **模型本身无视觉能力时，优先调用已配置的 MCP 工具弥补** — 先确认当前环境是否配置了图片/视觉类 MCP（图片分析、OCR 文字识别、图表解析、UI 截图比对等 server）；若有，调用对应 MCP 工具完成识图。");
            sb.AppendLine("3. **既无原生视觉能力，也没有任何可用的图片分析 MCP** — 明确告知用户「无法识图」，不得对图片内容进行臆测或编造；直接基于已有信息（文件名、上下文、用户已给出的文字等）继续完成任务，不再额外向用户索要图片描述。");
            sb.AppendLine();
            sb.AppendLine("**Why:** 不同模型视觉能力差异很大，弱模型拿到图片可能凭空编造内容，造成误导。用 MCP 兜底可以在不换模型的情况下补齐识图能力；都没有时如实告知并复用已有信息，才能避免幻觉和反复打扰用户。");
            sb.AppendLine();
            sb.Append("**How to apply:** 能看图就直接看 → 看不了就用 MCP 看 → 两者都没有就如实告知无法识图、基于已有文字信息继续完成，绝不编造图片内容。");
            return sb.ToString().TrimEnd('\r', '\n');
        }
    }
}
