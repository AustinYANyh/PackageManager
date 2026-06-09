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
            sb.AppendLine("- 首次探索实现入口、模块边界、业务链路或 bug 根因时，先调用 `codegraph_explore`，并基于返回的调用关系继续分析。");
            sb.AppendLine("- 已知符号查位置用 `codegraph_search` / `codegraph_node`；查调用者、被调用者、影响范围用 `codegraph_callers` / `codegraph_callees` / `codegraph_impact`。");
            sb.AppendLine("- 长任务中每进入新的代码区域、模块、文件族或符号链路，都必须重新使用对应的 CodeGraph 工具；不能只在开头调用一次后完全回退。");
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
