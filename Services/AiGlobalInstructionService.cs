using System;
using System.IO;
using System.Text;

namespace PackageManager.Services
{
    public static class AiGlobalInstructionService
    {
        private static readonly object SyncRoot = new object();
        private const string BeginMarker = "<!-- PackageManager CodeGraph Instructions: Begin -->";
        private const string EndMarker = "<!-- PackageManager CodeGraph Instructions: End -->";

        public static AiGlobalInstructionSyncResult EnsureCodeGraphInstructions()
        {
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

                return new AiGlobalInstructionSyncResult(codexPath, claudePath, codexChanged, claudeChanged);
            }
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
