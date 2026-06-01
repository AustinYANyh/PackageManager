using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace PackageManager.Features.CodeWorkspace.Services
{
    /// <summary>
    /// 代码工作区终端启动辅助。
    /// </summary>
    public static class TerminalHelper
    {
        public static void LaunchTerminalWithCommand(string command, string title)
        {
            LaunchTerminalWithCommand(null, command, title);
        }

        public static void LaunchTerminalWithCommand(string workingDirectory, string command, string title)
        {
            var psPath = ResolvePowerShell7Path();
            var wtPath = ResolveWindowsTerminalPath();
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command ?? ""));
            var psArgs = $"-NoLogo -NoExit -EncodedCommand {encoded}";
            var startDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (!string.IsNullOrWhiteSpace(wtPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = wtPath,
                    Arguments = "new-tab --title \"" + EscapeArgument(title) + "\" -d \"" + EscapeArgument(startDirectory) + "\" \"" + EscapeArgument(psPath) + "\" " + psArgs,
                    UseShellExecute = true,
                    WorkingDirectory = startDirectory,
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = psPath,
                Arguments = psArgs,
                UseShellExecute = true,
                WorkingDirectory = startDirectory,
            });
        }

        public static string EscapePowerShellSingleQuoted(string value)
        {
            return (value ?? "").Replace("'", "''");
        }

        public static string ResolvePowerShell7Path()
        {
            var candidates = new[]
            {
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\PowerShell\7\pwsh.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\PowerShell\7\pwsh.exe"),
                Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Microsoft\PowerShell\7\pwsh.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramW6432%\PowerShell\7\pwsh.exe"),
            };

            var candidate = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }

            throw new FileNotFoundException("未找到 PowerShell 7 的 pwsh.exe。");
        }

        public static string ResolveWindowsTerminalPath()
        {
            var candidates = new[]
            {
                Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Microsoft\WindowsApps\wt.exe"),
            };

            return candidates.FirstOrDefault(File.Exists) ?? "wt.exe";
        }

        private static string EscapeArgument(string value)
        {
            return (value ?? "").Replace("\"", "\\\"");
        }
    }
}
