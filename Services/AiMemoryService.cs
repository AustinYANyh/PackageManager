using System;
using System.IO;
using System.Text;

namespace PackageManager.Services
{
    public static class AiMemoryService
    {
        private static readonly object SyncRoot = new object();

        private const string GlobalIndexBeginMarker = "<!-- PackageManager Memory Global: Begin -->";
        private const string GlobalIndexEndMarker = "<!-- PackageManager Memory Global: End -->";

        public static void EnsureMemoryAvailable()
        {
            lock (SyncRoot)
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(userProfile))
                {
                    throw new DirectoryNotFoundException("无法定位用户目录，不能同步 AI 记忆文件。");
                }

                EnsureGlobalMemory(Path.Combine(userProfile, ".claude", "memory"));
            }
        }

        private static void EnsureGlobalMemory(string globalMemoryDir)
        {
            Directory.CreateDirectory(globalMemoryDir);

            EnsureMemoryFileCreated(
                Path.Combine(globalMemoryDir, "strictly-follow-skill-constraints.md"),
                BuildStrictlyFollowContent);

            EnsureMemoryFileCreated(
                Path.Combine(globalMemoryDir, "diagnose-before-acting.md"),
                BuildDiagnoseBeforeActingContent);

            EnsureIndexBlockAppended(
                Path.Combine(globalMemoryDir, "MEMORY.md"),
                GlobalIndexBeginMarker,
                GlobalIndexEndMarker,
                BuildGlobalIndexContent);
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

        private static void EnsureIndexBlockAppended(string indexPath, string beginMarker, string endMarker, Func<string> contentBuilder)
        {
            var existing = File.Exists(indexPath) ? File.ReadAllText(indexPath, Encoding.UTF8) : string.Empty;

            if (existing.IndexOf(beginMarker, StringComparison.Ordinal) >= 0
                && existing.IndexOf(endMarker, StringComparison.Ordinal) >= 0)
            {
                return;
            }

            var block = beginMarker + Environment.NewLine + contentBuilder() + Environment.NewLine + endMarker;
            string next;
            if (string.IsNullOrWhiteSpace(existing))
            {
                next = block + Environment.NewLine;
            }
            else
            {
                var separator = existing.EndsWith(Environment.NewLine, StringComparison.Ordinal)
                    ? Environment.NewLine
                    : Environment.NewLine + Environment.NewLine;
                next = existing + separator + block + Environment.NewLine;
            }

            var directory = Path.GetDirectoryName(indexPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(indexPath, next, Encoding.UTF8);
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
            sb.AppendLine("执行 skill 或 prompt 时，其中明确禁止的事情一律不允许做，明确要求的执行流程必须严格按步骤来。不得因为技术上有困难就自己找变通办法绕过约束，不得因为“方便”就跳过或替换流程步骤。");
            sb.AppendLine();
            sb.AppendLine("具体包括但不限于：");
            sb.AppendLine("- 禁止创建临时文件时，必须在内存中解决，不得写 .py/.ps1/.txt 等任何文件");
            sb.AppendLine("- 要求用特定脚本完成的操作，不得自己写替代脚本或手动执行等效命令");
            sb.AppendLine("- 指定了状态目录/文件路径的，必须使用指定路径，不得回退到默认路径");
            sb.AppendLine("- 有明确步骤顺序的，必须按顺序执行，不得合并、跳过或重新排列");
            sb.AppendLine();
            sb.AppendLine("**Why:** 用户精心编写的 skill 约束每一条都有原因，目的是确保流程可控、可审计、可复现。自行变通会破坏这些目标，也违背了用户编写 skill 的初衷。");
            sb.AppendLine();
            sb.AppendLine("**How to apply:** 遇到 skill/prompt 中的约束时，先逐条理解每条限制的含义，然后在约束范围内寻找解决方案。如果当前工具能力确实无法满足，应该向用户说明困难并请求指导，而不是自行绕过。永远不要“我行我素”。");
            return sb.ToString();
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
            sb.AppendLine("**How to apply:** 每次收到问题时，强制自己走完上述个步骤后再回复。回复时以“分析结果”开头，不要以代码修改开头。用户说“确认”“执行”“改吧”之类的话之后才动代码。");
            return sb.ToString();
        }

        private static string BuildGlobalIndexContent()
        {
            var sb = new StringBuilder();
            sb.AppendLine("- [严格遵守 skill 约束和执行流程](strictly-follow-skill-constraints.md) — skill 中禁止的事一律不做，要求的流程必须按步骤来");
            sb.AppendLine("- [先分析再执行](diagnose-before-acting.md) — 读日志排查原因后给结论，确认后再改代码，不主动编译");
            return sb.ToString().TrimEnd('\r', '\n');
        }
    }
}
