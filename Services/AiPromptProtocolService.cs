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
            sb.AppendLine("- 做代码搜索、符号定位、架构理解、调用关系、影响范围分析时，必须先使用 CodeGraph 工具链。");
            sb.AppendLine("- 如果当前仓库还没有 CodeGraph 索引，先在目标仓库执行：");
            sb.AppendLine("```powershell");
            sb.AppendLine("cd your-project");
            sb.AppendLine("codegraph init -i");
            sb.AppendLine("```");
            sb.AppendLine("- 上面的 `your-project` 替换为当前仓库根目录，然后再继续做 CodeGraph 探索。");
            sb.AppendLine("- 首次探索实现入口、模块边界或业务链路时，先调用 `codegraph_explore`。");
            sb.AppendLine("- 已知符号查位置用 `codegraph_search` / `codegraph_node`；查调用者、被调用者、影响范围用 `codegraph_callers` / `codegraph_callees` / `codegraph_impact`。");
            sb.AppendLine("- 长任务中每进入新的代码区域、模块或符号链路，都重新优先使用 CodeGraph，不能只在开头调用一次后完全回退。");
            sb.AppendLine("- 只有 CodeGraph 无索引、无结果、目标不是代码、或内容未被索引时，才回退到 `rg` / 文件读取 / 普通搜索；回退时必须说明原因。");
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
