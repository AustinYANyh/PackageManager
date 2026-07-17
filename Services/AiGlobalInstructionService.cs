using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PackageManager.Services
{
    public static class AiGlobalInstructionService
    {
        private static readonly object SyncRoot = new object();
        private const string BeginMarker = "<!-- PackageManager CodeGraph Instructions: Begin -->";
        private const string EndMarker = "<!-- PackageManager CodeGraph Instructions: End -->";
        private const string CodeGraphIdleTimeoutMs = "36000000";
        private const string BehaviorBeginMarker = "<!-- PackageManager Behavior Rules: Begin -->";
        private const string BehaviorEndMarker = "<!-- PackageManager Behavior Rules: End -->";

        public static void EnsureBehaviorRules()
        {
            lock (SyncRoot)
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(userProfile))
                {
                    return;
                }

                var agentsPath = Path.Combine(userProfile, ".codex", "AGENTS.md");
                Directory.CreateDirectory(Path.GetDirectoryName(agentsPath));

                var existing = File.Exists(agentsPath) ? File.ReadAllText(agentsPath, Encoding.UTF8) : string.Empty;
                var next = UpsertBehaviorRulesBlock(existing, BehaviorBeginMarker, BehaviorEndMarker, BuildBehaviorRulesBlock());
                if (string.Equals(existing, next, StringComparison.Ordinal))
                {
                    return;
                }

                WriteWithBackup(agentsPath, next, File.Exists(agentsPath));
            }
        }

        private static string UpsertBehaviorRulesBlock(string existing, string beginMarker, string endMarker, string blockContent)
        {
            var block = blockContent;
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

        private static string BuildBehaviorRulesBlock()
        {
            var sb = new StringBuilder();
            sb.AppendLine(BehaviorBeginMarker);
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
            sb.AppendLine("能看图就直接看 → 看不了就用 MCP 看 → 两者都没有就如实告知无法识图、基于已有文字信息继续完成，绝不编造图片内容。");
            sb.AppendLine();
            sb.Append(BehaviorEndMarker);
            return sb.ToString();
        }

        public static AiGlobalInstructionSyncResult EnsureCodeGraphInstructions()
        {
            return new AiGlobalInstructionSyncResult("", "", false, false);
            lock (SyncRoot)
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(userProfile))
                {
                    throw new DirectoryNotFoundException("无法定位用户目录，不能同步 AI 全局 CodeGraph 规则。");
                }

                var codexPath = Path.Combine(userProfile, ".codex", "AGENTS.md");
                var claudePath = Path.Combine(userProfile, ".claude", "CLAUDE.md");
                var codexChanged = EnsureManagedBlock(codexPath);
                var claudeChanged = EnsureManagedBlock(claudePath);
                EnsureCodeGraphMcpConfiguration(userProfile);

                return new AiGlobalInstructionSyncResult(codexPath, claudePath, codexChanged, claudeChanged);
            }
        }

        private static void EnsureCodeGraphMcpConfiguration(string userProfile)
        {
            EnsureCodexCodeGraphMcpConfiguration(Path.Combine(userProfile, ".codex", "config.toml"));
            EnsureClaudeCodeGraphMcpConfiguration(Path.Combine(userProfile, ".claude.json"));
        }

        private static void EnsureCodexCodeGraphMcpConfiguration(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
            var next = UpsertTomlSection(existing, "[mcp_servers.codegraph]", BuildCodexCodeGraphMcpSection());
            if (string.Equals(existing, next, StringComparison.Ordinal))
            {
                return;
            }

            WriteWithBackup(path, next, File.Exists(path));
        }

        private static string BuildCodexCodeGraphMcpSection()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[mcp_servers.codegraph]");
            sb.AppendLine("command = \"codegraph\"");
            sb.AppendLine("args = [\"serve\", \"--mcp\"]");
            sb.AppendLine("env = { CODEGRAPH_DAEMON_IDLE_TIMEOUT_MS = \"" + CodeGraphIdleTimeoutMs + "\" }");
            return sb.ToString().TrimEnd('\r', '\n');
        }

        private static string UpsertTomlSection(string existing, string sectionHeader, string sectionContent)
        {
            if (string.IsNullOrWhiteSpace(existing))
            {
                return sectionContent + Environment.NewLine;
            }

            var normalized = existing.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n').ToList();
            var start = lines.FindIndex(line => string.Equals(line.Trim(), sectionHeader, StringComparison.Ordinal));
            if (start < 0)
            {
                var separator = normalized.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "\n\n";
                return normalized + separator + sectionContent + "\n";
            }

            var end = start + 1;
            while (end < lines.Count)
            {
                var trimmed = lines[end].Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    break;
                }

                end++;
            }

            var replacement = sectionContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            lines.RemoveRange(start, end - start);
            lines.InsertRange(start, replacement);
            return string.Join("\n", lines);
        }

        private static void EnsureClaudeCodeGraphMcpConfiguration(string path)
        {
            var root = LoadJsonObject(path);
            var mcpServers = EnsureObject(root, "mcpServers");
            var codegraph = EnsureObject(mcpServers, "codegraph");

            codegraph["type"] = "stdio";
            codegraph["command"] = "codegraph";
            codegraph["args"] = new JArray("serve", "--mcp");
            var env = EnsureObject(codegraph, "env");
            env["CODEGRAPH_DAEMON_IDLE_TIMEOUT_MS"] = CodeGraphIdleTimeoutMs;

            var next = root.ToString(Formatting.Indented) + Environment.NewLine;
            var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
            if (string.Equals(existing, next, StringComparison.Ordinal))
            {
                return;
            }

            WriteWithBackup(path, next, File.Exists(path));
        }

        private static JObject LoadJsonObject(string path)
        {
            if (!File.Exists(path))
            {
                return new JObject();
            }

            var content = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(content))
            {
                return new JObject();
            }

            var token = JToken.Parse(content);
            if (token is JObject obj)
            {
                return obj;
            }

            throw new InvalidDataException(path + " 必须是 JSON object，不能同步 Claude CodeGraph MCP 配置。");
        }

        private static JObject EnsureObject(JObject parent, string propertyName)
        {
            if (parent[propertyName] is JObject obj)
            {
                return obj;
            }

            obj = new JObject();
            parent[propertyName] = obj;
            return obj;
        }

        private static bool EnsureManagedBlock(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
            var next = UpsertManagedBlock(existing);
            if (string.Equals(existing, next, StringComparison.Ordinal))
            {
                return false;
            }

            WriteWithBackup(path, next, File.Exists(path));
            return true;
        }

        private static string UpsertManagedBlock(string existing)
        {
            var block = BuildManagedBlock();
            if (string.IsNullOrWhiteSpace(existing))
            {
                return block + Environment.NewLine;
            }

            var begin = existing.IndexOf(BeginMarker, StringComparison.Ordinal);
            var end = existing.IndexOf(EndMarker, StringComparison.Ordinal);
            if (begin >= 0 && end > begin)
            {
                end += EndMarker.Length;
                return existing.Substring(0, begin) + block + existing.Substring(end);
            }

            var separator = existing.EndsWith(Environment.NewLine, StringComparison.Ordinal)
                ? Environment.NewLine
                : Environment.NewLine + Environment.NewLine;
            return existing + separator + block + Environment.NewLine;
        }

        private static string BuildManagedBlock()
        {
            var sb = new StringBuilder();
            sb.AppendLine(BeginMarker);
            AiPromptProtocolService.AppendCodeGraphProtocol(sb);
            sb.Append(EndMarker);
            return sb.ToString();
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
    }

    public sealed class AiGlobalInstructionSyncResult
    {
        public AiGlobalInstructionSyncResult(string codexPath, string claudePath, bool codexChanged, bool claudeChanged)
        {
            CodexPath = codexPath;
            ClaudePath = claudePath;
            CodexChanged = codexChanged;
            ClaudeChanged = claudeChanged;
        }

        public string CodexPath { get; }

        public string ClaudePath { get; }

        public bool CodexChanged { get; }

        public bool ClaudeChanged { get; }
    }
}
