using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace PackageManager.Views
{
    public partial class ChangelogPage : Page, ICentralPage
    {
        public event Action RequestExit;

        public ChangelogPage()
        {
            InitializeComponent();
            Loaded += (s, e) => LoadChangelog();
        }

        private void LoadChangelog()
        {
            try
            {
                // 1) 优先从内嵌资源读取
                var md = ReadEmbeddedResource("CHANGELOG.md");
                if (string.IsNullOrWhiteSpace(md))
                {
                    // 2) 备用：从固定路径读取
                    var primaryPath = @"e:\\PackageManager\\CHANGELOG.md";
                    if (File.Exists(primaryPath))
                    {
                        md = File.ReadAllText(primaryPath);
                    }
                    else
                    {
                        // 3) 继续向上查找
                        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                        var probe = FindFileUpwards(baseDir, "CHANGELOG.md", maxDepth: 5);
                        if (!string.IsNullOrWhiteSpace(probe) && File.Exists(probe))
                        {
                            md = File.ReadAllText(probe);
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(md))
                {
                    md = "# 未找到 CHANGELOG.md\n\n请检查内嵌资源或 e:/PackageManager/CHANGELOG.md 是否存在。";
                }

                var html = ConvertMarkdownToHtml(md);
                Browser.NavigateToString(html);
            }
            catch (Exception ex)
            {
                var errorHtml = $"<!DOCTYPE html><html><head><meta charset='utf-8'/><title>错误</title></head><body><h2>读取更新日志失败</h2><p>{EncodeHtml(ex.Message)}</p></body></html>";
                Browser.NavigateToString(errorHtml);
            }
        }

        private static string FindFileUpwards(string startDir, string fileName, int maxDepth)
        {
            try
            {
                var current = new DirectoryInfo(startDir);
                for (int i = 0; i < maxDepth && current != null; i++)
                {
                    var candidate = Path.Combine(current.FullName, fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                    current = current.Parent;
                }
            }
            catch { }
            return null;
        }

        private static string ReadEmbeddedResource(string resourceFileName)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var name = asm.GetManifestResourceNames()
                              .FirstOrDefault(n => n.EndsWith(resourceFileName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(name))
                {
                    using (var s = asm.GetManifestResourceStream(name))
                    using (var reader = new StreamReader(s, Encoding.UTF8, true))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch { }
            return null;
        }

        private static string EncodeHtml(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static string ConvertMarkdownToHtml(string md)
        {
            // 简易 Markdown 转 HTML（标题、列表、代码块、强调、链接、段落）
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'/>\n");
            sb.Append("<title>更新日志</title>\n");
            sb.Append("<style>body{font-family:Segoe UI,Arial,Helvetica,sans-serif;line-height:1.6;padding:16px;color:#222;background:#fff;}h1,h2,h3{margin-top:1.2em;border-bottom:1px solid #eee;padding-bottom:.3em;}pre{background:#f7f7f7;padding:12px;border:1px solid #eee;overflow:auto;}code{background:#f3f3f3;padding:2px 4px;border-radius:3px;}ul,ol{margin-left:24px;}blockquote{border-left:4px solid #ddd;padding-left:12px;color:#555;margin:8px 0;}table{border-collapse:collapse}th,td{border:1px solid #ddd;padding:6px;}a{color:#0366d6;text-decoration:none}a:hover{text-decoration:underline}</style>\n");
            sb.Append("</head><body>\n");

            var lines = md.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            bool inCode = false;
            bool inUl = false;
            bool inOl = false;
            var codeBuffer = new StringBuilder();

            Func<string, string> inline = (text) =>
            {
                var t = EncodeHtml(text);
                // 链接 [text](url)
                t = Regex.Replace(t, "\\[(.*?)\\]\\((.*?)\\)", m => $"<a href='{EncodeHtml(m.Groups[2].Value)}' target='_blank'>{m.Groups[1].Value}</a>");
                // 粗体 **text**
                t = Regex.Replace(t, "\\*\\*(.*?)\\*\\*", "<strong>$1</strong>");
                // 斜体 *text* 或 _text_
                t = Regex.Replace(t, "(?<!\\*)\\*(?!\\s)(.+?)(?<!\\s)\\*(?!\\*)", "<em>$1</em>");
                t = Regex.Replace(t, "_(.+?)_", "<em>$1</em>");
                // 行内代码 `code`
                t = Regex.Replace(t, "`([^`]+)`", m => $"<code>{EncodeHtml(m.Groups[1].Value)}</code>");
                return t;
            };

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // 代码块切换 ```
                if (line.TrimStart().StartsWith("```") || line.Trim() == "~~~")
                {
                    if (!inCode)
                    {
                        inCode = true;
                        codeBuffer.Clear();
                    }
                    else
                    {
                        inCode = false;
                        sb.Append("<pre><code>").Append(EncodeHtml(codeBuffer.ToString())).Append("</code></pre>\n");
                    }
                    continue;
                }

                if (inCode)
                {
                    codeBuffer.AppendLine(line);
                    continue;
                }

                // 无序/有序列表关闭条件：遇到空行或非列表行
                Action closeLists = () =>
                {
                    if (inUl) { sb.Append("</ul>\n"); inUl = false; }
                    if (inOl) { sb.Append("</ol>\n"); inOl = false; }
                };

                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    closeLists();
                    sb.Append("\n");
                    continue;
                }

                // 标题
                var m = Regex.Match(trimmed, "^(#{1,6})\\s+(.*)$");
                if (m.Success)
                {
                    closeLists();
                    var level = m.Groups[1].Value.Length;
                    var content = inline(m.Groups[2].Value);
                    sb.Append($"<h{level}>{content}</h{level}>\n");
                    continue;
                }

                // 引用块
                if (trimmed.StartsWith(">"))
                {
                    closeLists();
                    var content = inline(trimmed.TrimStart('>').Trim());
                    sb.Append($"<blockquote>{content}</blockquote>\n");
                    continue;
                }

                // 有序列表项 1. item
                if (Regex.IsMatch(trimmed, "^\\d+\\.\\s+"))
                {
                    if (!inOl) { closeLists(); sb.Append("<ol>\n"); inOl = true; }
                    var content = inline(Regex.Replace(trimmed, "^\\d+\\.\\s+", ""));
                    sb.Append($"<li>{content}</li>\n");
                    continue;
                }

                // 无序列表项 - item / * item
                if (Regex.IsMatch(trimmed, "^[-*]\\s+"))
                {
                    if (!inUl) { closeLists(); sb.Append("<ul>\n"); inUl = true; }
                    var content = inline(Regex.Replace(trimmed, "^[-*]\\s+", ""));
                    sb.Append($"<li>{content}</li>\n");
                    continue;
                }

                // 段落
                closeLists();
                sb.Append("<p>").Append(inline(line)).Append("</p>\n");
            }

            // 收尾
            if (inUl) sb.Append("</ul>\n");
            if (inOl) sb.Append("</ol>\n");

            sb.Append("</body></html>");
            return sb.ToString();
        }
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadChangelog();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            RequestExit?.Invoke();
        }

    }
}
