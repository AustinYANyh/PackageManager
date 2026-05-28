using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PackageManager.Features.CodeWorkspace.Models;

namespace PackageManager.Features.CodeWorkspace.Services
{
    public class VcsDiffService
    {
        private const long MaxTextFileBytes = 128 * 1024;
        private const int DiffOnlyContextLines = 3;
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(8);
        private readonly VcsExternalDiffToolService _externalDiffToolService = new VcsExternalDiffToolService();

        public async Task<DiffContentResult> LoadDiffContentAsync(VcsChangedFile file)
        {
            if (file == null)
            {
                return DiffContentResult.Error("未选择变更文件。");
            }

            try
            {
                var totalStopwatch = Stopwatch.StartNew();
                var oldReadStopwatch = Stopwatch.StartNew();
                string oldText;
                string newText;
                if (file.VcsType == VcsType.Git)
                {
                    oldText = await LoadGitOldTextAsync(file);
                    oldReadStopwatch.Stop();
                    var workingReadStopwatch = Stopwatch.StartNew();
                    newText = LoadWorkingText(file, isOldSide: false);
                    workingReadStopwatch.Stop();
                    var diffStopwatch = Stopwatch.StartNew();
                    var diffOnlyText = BuildDiffOnlyText(oldText, newText);
                    diffStopwatch.Stop();
                    totalStopwatch.Stop();
                    return DiffContentResult.Ok(oldText ?? string.Empty, newText ?? string.Empty, diffOnlyText, new DiffTiming
                    {
                        OldReadMs = oldReadStopwatch.ElapsedMilliseconds,
                        WorkingReadMs = workingReadStopwatch.ElapsedMilliseconds,
                        DiffBuildMs = diffStopwatch.ElapsedMilliseconds,
                        TotalLoadMs = totalStopwatch.ElapsedMilliseconds,
                    });
                }
                else if (file.VcsType == VcsType.Svn)
                {
                    oldText = await LoadSvnOldTextAsync(file);
                    oldReadStopwatch.Stop();
                    var workingReadStopwatch = Stopwatch.StartNew();
                    newText = LoadWorkingText(file, isOldSide: false);
                    workingReadStopwatch.Stop();
                    var diffStopwatch = Stopwatch.StartNew();
                    var diffOnlyText = BuildDiffOnlyText(oldText, newText);
                    diffStopwatch.Stop();
                    totalStopwatch.Stop();
                    return DiffContentResult.Ok(oldText ?? string.Empty, newText ?? string.Empty, diffOnlyText, new DiffTiming
                    {
                        OldReadMs = oldReadStopwatch.ElapsedMilliseconds,
                        WorkingReadMs = workingReadStopwatch.ElapsedMilliseconds,
                        DiffBuildMs = diffStopwatch.ElapsedMilliseconds,
                        TotalLoadMs = totalStopwatch.ElapsedMilliseconds,
                    });
                }
                else
                {
                    return DiffContentResult.Error("不支持的版本控制类型。");
                }
            }
            catch (BinaryFileException)
            {
                return DiffContentResult.Error("该文件可能是二进制文件，无法以内嵌文本差异方式展示。");
            }
            catch (LargeFileException)
            {
                return DiffContentResult.Error($"文件超过 {MaxTextFileBytes / 1024}KB，已跳过内嵌差异展示。请使用外部工具查看。");
            }
            catch (Exception ex)
            {
                return DiffContentResult.Error(ex.Message);
            }
        }

        private static async Task<string> LoadGitOldTextAsync(VcsChangedFile file)
        {
            if (file.IsAdded)
            {
                return string.Empty;
            }

            var relativePath = (file.OriginalRelativePath ?? file.RelativePath ?? string.Empty).Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return string.Empty;
            }

            var result = await RunCommandAsync("git", $"show HEAD:{QuoteArgument(relativePath)}", file.WorkingDirectory);
            return result.ExitCode == 0 ? ValidateLoadedText(result.Output) : string.Empty;
        }

        private static async Task<string> LoadSvnOldTextAsync(VcsChangedFile file)
        {
            if (file.IsAdded)
            {
                return string.Empty;
            }

            var target = !string.IsNullOrWhiteSpace(file.AbsolutePath)
                ? file.AbsolutePath
                : Path.Combine(file.WorkingDirectory ?? string.Empty, file.RelativePath ?? string.Empty);
            var result = await RunCommandAsync("svn", $"cat -r BASE {QuoteArgument(target)}", file.WorkingDirectory);
            return result.ExitCode == 0 ? ValidateLoadedText(result.Output) : string.Empty;
        }

        private static string LoadWorkingText(VcsChangedFile file, bool isOldSide)
        {
            if (!isOldSide && file.IsDeleted)
            {
                return string.Empty;
            }

            var target = !string.IsNullOrWhiteSpace(file.AbsolutePath)
                ? file.AbsolutePath
                : Path.Combine(file.WorkingDirectory ?? string.Empty, file.RelativePath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
            {
                return string.Empty;
            }

            var info = new FileInfo(target);
            if (info.Length > MaxTextFileBytes)
            {
                throw new LargeFileException();
            }

            var bytes = File.ReadAllBytes(target);
            if (LooksBinary(bytes))
            {
                throw new BinaryFileException();
            }

            return DecodeText(bytes);
        }

        public async Task OpenExternalAsync(VcsChangedFile file)
        {
            if (file == null)
            {
                return;
            }

            if (file.VcsType == VcsType.Svn)
            {
                var tortoiseProc = await _externalDiffToolService.ResolveToolPathAsync(VcsType.Svn).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(tortoiseProc))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = tortoiseProc,
                        Arguments = $"/command:diff /path:{QuoteArgument(file.AbsolutePath)}",
                        UseShellExecute = true,
                    });
                    return;
                }
            }
            else if (file.VcsType == VcsType.Git)
            {
                var tortoiseGitProc = await _externalDiffToolService.ResolveToolPathAsync(VcsType.Git).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(tortoiseGitProc))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = tortoiseGitProc,
                        Arguments = $"/command:diff /path:{QuoteArgument(file.AbsolutePath)}",
                        UseShellExecute = true,
                    });
                    return;
                }
            }

            throw new InvalidOperationException("未找到已安装的 TortoiseGit/TortoiseSVN 图形差异工具。");
        }

        private static async Task<CommandResult> RunCommandAsync(string fileName, string arguments, string workingDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                var exited = await Task.Run(() => process.WaitForExit((int)CommandTimeout.TotalMilliseconds));
                if (!exited)
                {
                    try { process.Kill(); } catch { }
                    return new CommandResult { ExitCode = -1, Output = string.Empty, Error = "Timeout" };
                }

                return new CommandResult
                {
                    ExitCode = process.ExitCode,
                    Output = await outputTask,
                    Error = await errorTask,
                };
            }
        }

        private static bool LooksBinary(byte[] bytes)
        {
            var limit = Math.Min(bytes?.Length ?? 0, 8000);
            for (var i = 0; i < limit; i++)
            {
                if (bytes[i] == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string DecodeText(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }

            return Encoding.UTF8.GetString(bytes);
        }

        private static string ValidateLoadedText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            if (Encoding.UTF8.GetByteCount(text) > MaxTextFileBytes)
            {
                throw new LargeFileException();
            }

            return text;
        }

        private static DiffOnlyText BuildDiffOnlyText(string oldText, string newText)
        {
            oldText = oldText ?? string.Empty;
            newText = newText ?? string.Empty;

            var model = SideBySideDiffBuilder.Diff(oldText, newText, false, false);
            if (model?.OldText?.Lines == null || model.NewText?.Lines == null)
            {
                return new DiffOnlyText { OldText = oldText, NewText = newText };
            }

            if (!model.OldText.Lines.Any(line => line.Type != ChangeType.Unchanged) &&
                !model.NewText.Lines.Any(line => line.Type != ChangeType.Unchanged))
            {
                return new DiffOnlyText { OldText = string.Empty, NewText = string.Empty };
            }

            return new DiffOnlyText
            {
                OldText = BuildCondensedPane(model.OldText.Lines),
                NewText = BuildCondensedPane(model.NewText.Lines),
            };
        }

        private static string BuildCondensedPane(IReadOnlyList<DiffPiece> lines)
        {
            if (lines == null || lines.Count == 0)
            {
                return string.Empty;
            }

            var include = new bool[lines.Count];
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Type == ChangeType.Unchanged)
                {
                    continue;
                }

                var start = Math.Max(0, i - DiffOnlyContextLines);
                var end = Math.Min(lines.Count - 1, i + DiffOnlyContextLines);
                for (var j = start; j <= end; j++)
                {
                    include[j] = true;
                }
            }

            var builder = new StringBuilder();
            var lastIncluded = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                if (!include[i])
                {
                    continue;
                }

                if (lastIncluded >= 0 && i > lastIncluded + 1)
                {
                    builder.AppendLine("...");
                }

                builder.AppendLine(lines[i].Text ?? string.Empty);
                lastIncluded = i;
            }

            return builder.ToString();
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private class CommandResult
        {
            public int ExitCode { get; set; }

            public string Output { get; set; }

            public string Error { get; set; }
        }

        private class BinaryFileException : Exception
        {
        }

        private class LargeFileException : Exception
        {
        }
    }

    public class DiffContentResult
    {
        public bool Success { get; set; }

        public string OldText { get; set; }

        public string NewText { get; set; }

        public string DiffOnlyOldText { get; set; }

        public string DiffOnlyNewText { get; set; }

        public string ErrorMessage { get; set; }

        public DiffTiming Timing { get; set; }

        public static DiffContentResult Ok(string oldText, string newText, DiffOnlyText diffOnlyText, DiffTiming timing)
        {
            return new DiffContentResult
            {
                Success = true,
                OldText = oldText,
                NewText = newText,
                DiffOnlyOldText = diffOnlyText?.OldText ?? string.Empty,
                DiffOnlyNewText = diffOnlyText?.NewText ?? string.Empty,
                Timing = timing ?? new DiffTiming(),
            };
        }

        public static DiffContentResult Error(string message)
        {
            return new DiffContentResult { Success = false, ErrorMessage = message };
        }
    }

    public class DiffOnlyText
    {
        public string OldText { get; set; }

        public string NewText { get; set; }
    }

    public class DiffTiming
    {
        public long OldReadMs { get; set; }

        public long WorkingReadMs { get; set; }

        public long DiffBuildMs { get; set; }

        public long RenderBindMs { get; set; }

        public long TotalLoadMs { get; set; }

        public long ReadTotalMs => OldReadMs + WorkingReadMs;

        public bool IsSlow => ReadTotalMs > 300 || DiffBuildMs > 200 || RenderBindMs > 300 || TotalLoadMs > 800;
    }
}
