using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using PackageManager.Services;

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
            var launchMode = ResolveTerminalLaunchMode();
            var scriptPath = WriteCommandScript(command);
            var launchTarget = CreateLaunchTarget(launchMode, scriptPath);
            var startDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (UsesWindowsTerminal(launchMode))
            {
                var wtPath = ResolveWindowsTerminalPath();
                Process.Start(new ProcessStartInfo
                {
                    FileName = wtPath,
                    Arguments = "new-tab --title \"" + EscapeArgument(title) + "\" -d \"" + EscapeArgument(startDirectory) + "\" -- \"" + EscapeArgument(launchTarget.FileName) + "\" " + launchTarget.Arguments,
                    UseShellExecute = true,
                    WorkingDirectory = startDirectory,
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = launchTarget.FileName,
                Arguments = launchTarget.Arguments,
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
            if (TryResolvePowerShell7Path(out var candidate))
            {
                return candidate;
            }

            throw new FileNotFoundException("未找到 PowerShell 7 的 pwsh.exe。");
        }

        public static bool HasPowerShell7()
        {
            return TryResolvePowerShell7Path(out _);
        }

        public static string ResolveWindowsPowerShellPath()
        {
            var candidate = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe");
            return File.Exists(candidate) ? candidate : "powershell.exe";
        }

        public static string ResolveWindowsTerminalPath()
        {
            var candidates = new[]
            {
                Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Microsoft\WindowsApps\wt.exe"),
            };

            return candidates.FirstOrDefault(File.Exists) ?? "wt.exe";
        }

        public static bool HasVisualStudioDeveloperCommandPrompt()
        {
            return TryResolveVisualStudioToolPath("VsDevCmd.bat", out _);
        }

        public static bool HasVisualStudioDeveloperPowerShell()
        {
            return TryResolveVisualStudioToolPath("Launch-VsDevShell.ps1", out _);
        }

        private static TerminalLaunchMode ResolveTerminalLaunchMode()
        {
            try
            {
                return new DataPersistenceService().LoadSettings()?.TerminalLaunchMode
                       ?? TerminalLaunchMode.WindowsTerminalWindowsPowerShell;
            }
            catch
            {
                return TerminalLaunchMode.WindowsTerminalWindowsPowerShell;
            }
        }

        private static bool TryResolvePowerShell7Path(out string path)
        {
            var candidates = new[]
            {
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\PowerShell\7\pwsh.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\PowerShell\7\pwsh.exe"),
                Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Microsoft\PowerShell\7\pwsh.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramW6432%\PowerShell\7\pwsh.exe"),
            };

            path = candidates.FirstOrDefault(File.Exists);
            return !string.IsNullOrWhiteSpace(path);
        }

        private static string ResolveShellPath(TerminalLaunchMode launchMode)
        {
            switch (launchMode)
            {
                case TerminalLaunchMode.WindowsTerminalPowerShell7:
                case TerminalLaunchMode.PowerShell7:
                    return ResolvePowerShell7Path();
                case TerminalLaunchMode.WindowsTerminalWindowsPowerShell:
                case TerminalLaunchMode.WindowsPowerShell:
                default:
                    return ResolveWindowsPowerShellPath();
            }
        }

        private static TerminalLaunchTarget CreateLaunchTarget(TerminalLaunchMode launchMode, string scriptPath)
        {
            switch (launchMode)
            {
                case TerminalLaunchMode.WindowsTerminalVisualStudioDeveloperCommandPrompt:
                    return new TerminalLaunchTarget
                    {
                        FileName = ResolveCommandPromptPath(),
                        Arguments = CreateDeveloperCommandPromptArguments(scriptPath)
                    };
                case TerminalLaunchMode.WindowsTerminalVisualStudioDeveloperPowerShell:
                    return new TerminalLaunchTarget
                    {
                        FileName = ResolveWindowsPowerShellPath(),
                        Arguments = CreateDeveloperPowerShellArguments(scriptPath)
                    };
                case TerminalLaunchMode.WindowsTerminalPowerShell7:
                case TerminalLaunchMode.PowerShell7:
                case TerminalLaunchMode.WindowsTerminalWindowsPowerShell:
                case TerminalLaunchMode.WindowsPowerShell:
                default:
                    return new TerminalLaunchTarget
                    {
                        FileName = ResolveShellPath(launchMode),
                        Arguments = CreatePowerShellFileArguments(scriptPath)
                    };
            }
        }

        private static string CreatePowerShellFileArguments(string scriptPath)
        {
            return "-NoLogo -NoExit -ExecutionPolicy Bypass -File \"" + EscapeArgument(scriptPath) + "\"";
        }

        private static string CreateDeveloperCommandPromptArguments(string scriptPath)
        {
            return "/k \"" + EscapeArgument(WriteDeveloperCommandPromptScript(scriptPath)) + "\"";
        }

        private static string CreateDeveloperPowerShellArguments(string scriptPath)
        {
            return CreatePowerShellFileArguments(WriteDeveloperPowerShellScript(scriptPath));
        }

        private static bool UsesWindowsTerminal(TerminalLaunchMode launchMode)
        {
            return launchMode == TerminalLaunchMode.WindowsTerminalWindowsPowerShell
                   || launchMode == TerminalLaunchMode.WindowsTerminalPowerShell7
                   || launchMode == TerminalLaunchMode.WindowsTerminalVisualStudioDeveloperCommandPrompt
                   || launchMode == TerminalLaunchMode.WindowsTerminalVisualStudioDeveloperPowerShell;
        }

        private static string WriteCommandScript(string command)
        {
            return WriteTemporaryScript(".ps1", BuildPowerShellScript(command));
        }

        private static string BuildPowerShellScript(string command)
        {
            var header = @"
try {
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $OutputEncoding = [Console]::OutputEncoding
} catch {
}

$env:TERM = 'xterm-256color'
Remove-Item Env:\NO_COLOR -ErrorAction SilentlyContinue

";
            return header + (command ?? string.Empty);
        }

        private static string WriteDeveloperCommandPromptScript(string scriptPath)
        {
            var vsDevCmdPath = ResolveVisualStudioToolPath(
                "VsDevCmd.bat",
                "未找到 Visual Studio Developer Command Prompt 的 VsDevCmd.bat。");
            var content = "@echo off\r\n"
                          + "chcp 65001 >nul\r\n"
                          + "call \"" + vsDevCmdPath + "\"\r\n"
                          + "if errorlevel 1 exit /b %errorlevel%\r\n"
                          + "\"" + ResolveWindowsPowerShellPath() + "\" -NoLogo -NoExit -ExecutionPolicy Bypass -File \"" + scriptPath + "\"\r\n";
            return WriteTemporaryScript(".cmd", content);
        }

        private static string WriteDeveloperPowerShellScript(string scriptPath)
        {
            var devShellPath = ResolveVisualStudioToolPath(
                "Launch-VsDevShell.ps1",
                "未找到 Visual Studio Developer PowerShell 的 Launch-VsDevShell.ps1。");
            var content = @"
try {
    [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
    [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
    $OutputEncoding = [Console]::OutputEncoding
} catch {
}

"
                          + "& '" + EscapePowerShellSingleQuoted(devShellPath) + "' -SkipAutomaticLocation\r\n"
                          + "& '" + EscapePowerShellSingleQuoted(scriptPath) + "'\r\n";
            return WriteTemporaryScript(".ps1", content);
        }

        private static string ResolveCommandPromptPath()
        {
            var candidate = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe");
            return File.Exists(candidate) ? candidate : "cmd.exe";
        }

        private static string ResolveVisualStudioToolPath(string fileName, string errorMessage)
        {
            if (TryResolveVisualStudioToolPath(fileName, out var path))
            {
                return path;
            }

            throw new FileNotFoundException(errorMessage);
        }

        private static bool TryResolveVisualStudioToolPath(string fileName, out string path)
        {
            path = null;
            foreach (var root in GetVisualStudioRoots())
            {
                foreach (var versionDirectory in SafeEnumerateDirectories(root).OrderByDescending(Path.GetFileName))
                {
                    var legacyCandidate = Path.Combine(versionDirectory, "Common7", "Tools", fileName);
                    if (File.Exists(legacyCandidate))
                    {
                        path = legacyCandidate;
                        return true;
                    }

                    foreach (var editionDirectory in SafeEnumerateDirectories(versionDirectory).OrderBy(GetVisualStudioEditionSortKey))
                    {
                        var candidate = Path.Combine(editionDirectory, "Common7", "Tools", fileName);
                        if (File.Exists(candidate))
                        {
                            path = candidate;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static IEnumerable<string> GetVisualStudioRoots()
        {
            var programFilesRoots = new[]
            {
                Environment.ExpandEnvironmentVariables(@"%ProgramW6432%"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            return programFilesRoots
                .Where(root => !string.IsNullOrWhiteSpace(root) && !root.Contains("%"))
                .Select(root => Path.Combine(root, "Microsoft Visual Studio"))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static int GetVisualStudioEditionSortKey(string path)
        {
            var name = Path.GetFileName(path) ?? string.Empty;
            if (name.Equals("Community", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (name.Equals("Professional", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (name.Equals("Enterprise", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            if (name.Equals("BuildTools", StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            return 4;
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string path)
        {
            try
            {
                return Directory.Exists(path) ? Directory.EnumerateDirectories(path) : Enumerable.Empty<string>();
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        private static string WriteTemporaryScript(string extension, string content)
        {
            var scriptDirectory = Path.Combine(Path.GetTempPath(), "PackageManager", "TerminalScripts");
            Directory.CreateDirectory(scriptDirectory);
            CleanupOldScripts(scriptDirectory);

            var scriptPath = Path.Combine(scriptDirectory, $"terminal-command-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{extension}");
            var encoding = string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
                ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                : new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            File.WriteAllText(scriptPath, content ?? string.Empty, encoding);
            return scriptPath;
        }

        private static void CleanupOldScripts(string scriptDirectory)
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-7);
                foreach (var file in Directory.EnumerateFiles(scriptDirectory, "terminal-command-*.*"))
                {
                    try
                    {
                        if (File.GetCreationTime(file) < cutoff)
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static string EscapeArgument(string value)
        {
            return (value ?? "").Replace("\"", "\\\"");
        }

        private sealed class TerminalLaunchTarget
        {
            public string FileName { get; set; }

            public string Arguments { get; set; }
        }
    }
}
