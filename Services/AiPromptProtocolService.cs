using System;
using System.Text;

namespace PackageManager.Services
{
    public static class AiPromptProtocolService
    {
        private const string CodeGraphProtocolMarker = "## CodeGraph 代码探索要求";

        public static void AppendCodeGraphProtocol(StringBuilder sb)
        {
            if (sb == null)
            {
                throw new ArgumentNullException(nameof(sb));
            }

            sb.AppendLine(CodeGraphProtocolMarker);
            sb.AppendLine("- 代码探索阶段必须先使用 CodeGraph 工具链，再决定是否需要文本搜索。不要先用 `rg` / `grep` / 手工枚举文件来找代码入口。");
            sb.AppendLine("- 如果当前仓库还没有 CodeGraph 索引，先在目标仓库执行：");
            sb.AppendLine("```powershell");
            sb.AppendLine("cd your-project");
            sb.AppendLine("codegraph init -i");
            sb.AppendLine("```");
            sb.AppendLine("- 上面的 `your-project` 替换为当前仓库根目录，然后再继续做 CodeGraph 探索。");
            sb.AppendLine("- MCP 可用时必须优先使用 MCP，尤其是 `codegraph_explore`，因为 CodeGraph CLI 当前没有等价的 `explore` 子命令。");
            sb.AppendLine("- 读取、理解或验证代码文件内容时，优先使用 `codegraph_explore` / `codegraph_node` 返回的 on-disk source，不要直接用 `Get-Content` / `cat` 读取代码文件；只有 CodeGraph 不可用、文件未被索引、需要查看非代码配置文件，或需要精确全文/编码检查时才允许读取文件，并且要先说明原因。");
            sb.AppendLine("- 首次探索实现入口、模块边界、业务链路或 bug 根因时，先调用 `codegraph_explore`，并基于返回的调用关系继续分析。");
            sb.AppendLine("- 已知符号查位置用 `codegraph_search` / `codegraph_node`；查调用者、被调用者、影响范围用 `codegraph_callers` / `codegraph_callees` / `codegraph_impact`。");
            sb.AppendLine("- 长任务中每进入新的代码区域、模块、文件族或符号链路，都必须重新使用对应的 CodeGraph 工具；不能只在开头调用一次后完全回退。");
            sb.AppendLine("- MCP server 默认有文件 watcher，通常会自动同步索引；但如果查询结果出现旧类、旧方法、旧属性、旧文件或调用边明显缺失，不要继续信任当前索引。");
            sb.AppendLine("- 如果 MCP 返回 `Transport closed`、超时或无响应，不要立刻退回大范围 `rg`。先尝试恢复 MCP：");
            sb.AppendLine("  1. 在仓库根目录运行 `codegraph status .`，确认索引状态。");
            sb.AppendLine("  2. 运行 `codegraph sync .`，同步当前磁盘变更。");
            sb.AppendLine("  3. 如果查询结果仍与磁盘文本不一致，即使 status 显示 up to date，也运行 `codegraph index --force .`。");
            sb.AppendLine("  4. 仍异常时重新运行 `codegraph init -i .`。");
            sb.AppendLine("  5. 如怀疑 MCP 配置损坏，运行 `codegraph install --print-config codex` 检查配置。");
            sb.AppendLine("  6. 必要时运行 `codegraph install -t codex -l global -y` 重装 Codex MCP 配置。");
            sb.AppendLine("  7. 可用 `codegraph serve --mcp --path <repo>` 验证 MCP server 能否启动。");
            sb.AppendLine("- 如果 MCP 短时间无法恢复，再用 CodeGraph CLI 的 `query`、`files`、`callers`、`callees`、`impact` 做结构化兜底。");
            sb.AppendLine("- 使用 CodeGraph CLI 查询前，先在仓库根目录运行 `codegraph sync .`；不要先用可能陈旧的 CLI 索引查询。");
            sb.AppendLine("- 如果 CLI 查询结果与当前磁盘文本不一致，例如仍显示已删除的类、方法、属性或文件，必须视为索引失真。先运行 `codegraph sync .`；如果同步后仍不一致，再运行 `codegraph index --force .` 或重新 `codegraph init -i .`。不要因为索引文件会被写入而跳过同步，`.codegraph/` 是允许维护的工作索引。");
            sb.AppendLine("- 只有 CodeGraph 无索引、无结果、目标不是代码、内容未被索引、或需要精确文本/配置匹配时，才回退到 `rg` / 文件读取 / 普通搜索；回退前必须说明具体原因。");
            sb.AppendLine("- 如果已经回退到 `rg` 找到候选代码符号，随后仍要用 `codegraph_node` / `codegraph_callers` / `codegraph_callees` 补齐结构和影响范围。");
            sb.AppendLine();
        }

        public static string EnsureCodeGraphProtocol(string prompt)
        {
            var content = prompt ?? string.Empty;
            if (content.IndexOf(CodeGraphProtocolMarker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return content;
            }

            var sb = new StringBuilder();
            AppendCodeGraphProtocol(sb);
            if (!string.IsNullOrWhiteSpace(content))
            {
                sb.AppendLine();
                sb.Append(content);
            }

            return sb.ToString();
        }
    }
}
