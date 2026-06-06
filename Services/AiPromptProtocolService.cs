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
            sb.AppendLine("- 做代码搜索、符号定位、调用关系、影响范围分析时，必须优先使用 CodeGraph 工具链，而不是默认的 Grep/Glob/Explore agent。");
            sb.AppendLine("- 如果当前仓库还没有 CodeGraph 索引，先在目标仓库执行：");
            sb.AppendLine("```powershell");
            sb.AppendLine("cd your-project");
            sb.AppendLine("codegraph init -i");
            sb.AppendLine("```");
            sb.AppendLine("- 上面的 `your-project` 替换为当前仓库根目录，然后再继续做 CodeGraph 探索。");
            sb.AppendLine("- 探索实现/模块入口时先用 `codegraph_explore`；已知符号查位置用 `codegraph_search`；查调用者、被调用者、影响范围用 `codegraph_callers` / `codegraph_callees` / `codegraph_impact`。");
            sb.AppendLine("- 只有纯文本搜索、非代码文件、未索引内容，或 CodeGraph 未覆盖/无结果时，才回退到 Grep/Glob/Explore。");
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
