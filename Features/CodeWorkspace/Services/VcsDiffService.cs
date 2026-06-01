using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using PackageManager.Features.CodeWorkspace.Models;

namespace PackageManager.Features.CodeWorkspace.Services
{
    public class VcsDiffService
    {
        private const long MaxTextFileBytes = 1024 * 1024;
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
                    var diffRows = BuildDiffRows(oldText, newText);
                    diffStopwatch.Stop();
                    totalStopwatch.Stop();
                    return DiffContentResult.Ok(oldText ?? string.Empty, newText ?? string.Empty, diffRows, new DiffTiming
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
                    var diffRows = BuildDiffRows(oldText, newText);
                    diffStopwatch.Stop();
                    totalStopwatch.Stop();
                    return DiffContentResult.Ok(oldText ?? string.Empty, newText ?? string.Empty, diffRows, new DiffTiming
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
                StartExternalDiffTool(tortoiseProc, file);
                return;
            }
            else if (file.VcsType == VcsType.Git)
            {
                var tortoiseGitProc = await _externalDiffToolService.ResolveToolPathAsync(VcsType.Git).ConfigureAwait(false);
                StartExternalDiffTool(tortoiseGitProc, file);
                return;
            }

            throw new InvalidOperationException("不支持的版本控制类型，无法打开外部差异工具。");
        }

        private static void StartExternalDiffTool(string toolPath, VcsChangedFile file)
        {
            if (string.IsNullOrWhiteSpace(toolPath) || !File.Exists(toolPath))
            {
                throw new InvalidOperationException("未找到已安装的 TortoiseGit/TortoiseSVN 图形差异工具。");
            }

            var arguments = BuildExternalDiffArguments(file);
            Process.Start(new ProcessStartInfo
            {
                FileName = toolPath,
                Arguments = arguments,
                UseShellExecute = true,
            });
        }

        private static string BuildExternalDiffArguments(VcsChangedFile file)
        {
            var target = GetWorkingPath(file);
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new InvalidOperationException("变更文件路径为空，无法打开外部差异工具。");
            }

            if (file.IsAdded)
            {
                if (!File.Exists(target))
                {
                    throw new FileNotFoundException("新增文件在工作区中不存在，无法打开外部差异工具。", target);
                }

                var emptyBaseFile = CreateEmptyComparisonFile(target);
                return $"/command:diff /path:{QuoteArgument(emptyBaseFile)} /path2:{QuoteArgument(target)}";
            }

            return $"/command:diff /path:{QuoteArgument(target)}";
        }

        private static string GetWorkingPath(VcsChangedFile file)
        {
            return !string.IsNullOrWhiteSpace(file?.AbsolutePath)
                ? file.AbsolutePath
                : Path.Combine(file?.WorkingDirectory ?? string.Empty, file?.RelativePath ?? string.Empty);
        }

        private static string CreateEmptyComparisonFile(string targetPath)
        {
            var extension = Path.GetExtension(targetPath);
            if (string.IsNullOrWhiteSpace(extension) || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                extension = ".tmp";
            }

            var tempPath = Path.Combine(Path.GetTempPath(), "PackageManagerDiffEmpty_" + Guid.NewGuid().ToString("N") + extension);
            using (File.Create(tempPath))
            {
            }

            return tempPath;
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

        private static DiffRowSet BuildDiffRows(string oldText, string newText)
        {
            oldText = oldText ?? string.Empty;
            newText = newText ?? string.Empty;

            var model = SideBySideDiffBuilder.Diff(oldText, newText, false, false);
            if (model?.OldText?.Lines == null || model.NewText?.Lines == null)
            {
                return DiffRowSet.FromRows(BuildFallbackRows(oldText, newText));
            }

            var rows = BuildRowsFromModel(model.OldText.Lines, model.NewText.Lines);
            AddFinalNewlineNote(rows, oldText, newText);
            return DiffRowSet.FromRows(rows);
        }

        private static List<DiffLineRow> BuildFallbackRows(string oldText, string newText)
        {
            var oldLines = SplitTextLines(oldText);
            var newLines = SplitTextLines(newText);
            var max = Math.Max(oldLines.Count, newLines.Count);
            var rows = new List<DiffLineRow>(max);
            for (var i = 0; i < max; i++)
            {
                var oldExists = i < oldLines.Count;
                var newExists = i < newLines.Count;
                rows.Add(CreateDiffLineRow(
                    oldExists ? oldLines[i] : null,
                    newExists ? newLines[i] : null,
                    oldExists ? i + 1 : (int?)null,
                    newExists ? i + 1 : (int?)null,
                    oldExists ? ChangeType.Unchanged : ChangeType.Imaginary,
                    newExists ? ChangeType.Unchanged : ChangeType.Imaginary));
            }

            return rows;
        }

        private static List<DiffLineRow> BuildRowsFromModel(IReadOnlyList<DiffPiece> oldLines, IReadOnlyList<DiffPiece> newLines)
        {
            var count = Math.Max(oldLines?.Count ?? 0, newLines?.Count ?? 0);
            var rows = new List<DiffLineRow>(count);
            for (var i = 0; i < count; i++)
            {
                var oldPiece = oldLines != null && i < oldLines.Count ? oldLines[i] : null;
                var newPiece = newLines != null && i < newLines.Count ? newLines[i] : null;
                rows.Add(CreateDiffLineRow(
                    oldPiece?.Text,
                    newPiece?.Text,
                    oldPiece?.Position,
                    newPiece?.Position,
                    oldPiece?.Type ?? ChangeType.Imaginary,
                    newPiece?.Type ?? ChangeType.Imaginary,
                    oldPiece?.SubPieces,
                    newPiece?.SubPieces));
            }

            return rows;
        }

        private static DiffLineRow CreateDiffLineRow(
            string oldText,
            string newText,
            int? oldLineNumber,
            int? newLineNumber,
            ChangeType oldChangeType,
            ChangeType newChangeType,
            IReadOnlyList<DiffPiece> oldSubPieces = null,
            IReadOnlyList<DiffPiece> newSubPieces = null)
        {
            var row = new DiffLineRow
            {
                OldLineNumber = oldLineNumber,
                NewLineNumber = newLineNumber,
                OldText = oldText ?? string.Empty,
                NewText = newText ?? string.Empty,
                OldChangeType = oldChangeType,
                NewChangeType = newChangeType,
            };

            row.HasWhitespaceOnlyChange = row.IsChanged &&
                                          string.Equals(RemoveWhitespace(row.OldText), RemoveWhitespace(row.NewText), StringComparison.Ordinal) &&
                                          !string.Equals(row.OldText, row.NewText, StringComparison.Ordinal);
            row.OldTextRuns = BuildTextRuns(row.OldText, oldSubPieces, row.IsOldChanged, isNewSide: false);
            row.NewTextRuns = BuildTextRuns(row.NewText, newSubPieces, row.IsNewChanged, isNewSide: true);
            return row;
        }

        private static string BuildDisplayText(string text, bool visualizeWhitespace)
        {
            if (text == null)
            {
                return string.Empty;
            }

            if (!visualizeWhitespace || text.Length == 0)
            {
                return text;
            }

            return text.Replace(" ", "·").Replace("\t", "→   ");
        }

        private static List<DiffTextRun> BuildTextRuns(string text, IReadOnlyList<DiffPiece> subPieces, bool fallbackChanged, bool isNewSide)
        {
            text = text ?? string.Empty;
            if (subPieces == null || subPieces.Count == 0)
            {
                return new List<DiffTextRun>
                {
                    new DiffTextRun(BuildDisplayText(text, fallbackChanged), fallbackChanged ? DiffTextRunKind.Changed : DiffTextRunKind.Normal, isNewSide)
                };
            }

            var subPieceText = string.Concat(subPieces.Select(piece => piece?.Text ?? string.Empty));
            if (!string.Equals(subPieceText, text, StringComparison.Ordinal))
            {
                return new List<DiffTextRun>
                {
                    new DiffTextRun(BuildDisplayText(text, fallbackChanged), fallbackChanged ? DiffTextRunKind.Changed : DiffTextRunKind.Normal, isNewSide)
                };
            }

            return subPieces
                .Select(piece => new DiffTextRun(
                    BuildDisplayText(piece?.Text ?? string.Empty, piece != null && piece.Type != ChangeType.Unchanged),
                    piece != null && piece.Type != ChangeType.Unchanged ? DiffTextRunKind.Changed : DiffTextRunKind.Normal,
                    isNewSide))
                .ToList();
        }

        private static void AddFinalNewlineNote(ICollection<DiffLineRow> rows, string oldText, string newText)
        {
            var oldHasFinalNewline = HasFinalNewline(oldText);
            var newHasFinalNewline = HasFinalNewline(newText);
            if (oldHasFinalNewline == newHasFinalNewline)
            {
                return;
            }

            rows.Add(DiffLineRow.CreateNote(
                oldHasFinalNewline ? string.Empty : "\\ No newline at end of BASE",
                newHasFinalNewline ? string.Empty : "\\ No newline at end of working copy"));
        }

        private static bool HasFinalNewline(string text)
        {
            return !string.IsNullOrEmpty(text) && (text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal));
        }

        private static List<string> SplitTextLines(string text)
        {
            return (text ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split(new[] { '\n' }, StringSplitOptions.None)
                .ToList();
        }

        private static string RemoveWhitespace(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var ch in value)
            {
                if (!char.IsWhiteSpace(ch))
                {
                    builder.Append(ch);
                }
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

        public IReadOnlyList<DiffLineRow> FullRows { get; set; }

        public IReadOnlyList<DiffLineRow> DiffOnlyRows { get; set; }

        public int FirstChangedRowIndex { get; set; } = -1;

        public string ErrorMessage { get; set; }

        public DiffTiming Timing { get; set; }

        public static DiffContentResult Ok(string oldText, string newText, DiffRowSet diffRows, DiffTiming timing)
        {
            return new DiffContentResult
            {
                Success = true,
                OldText = oldText,
                NewText = newText,
                DiffOnlyOldText = string.Empty,
                DiffOnlyNewText = string.Empty,
                FullRows = diffRows?.FullRows ?? new List<DiffLineRow>(),
                DiffOnlyRows = diffRows?.DiffOnlyRows ?? new List<DiffLineRow>(),
                FirstChangedRowIndex = diffRows?.FirstChangedRowIndex ?? -1,
                Timing = timing ?? new DiffTiming(),
            };
        }

        public static DiffContentResult Error(string message)
        {
            return new DiffContentResult { Success = false, ErrorMessage = message };
        }
    }

    public class DiffRowSet
    {
        public IReadOnlyList<DiffLineRow> FullRows { get; private set; }

        public IReadOnlyList<DiffLineRow> DiffOnlyRows { get; private set; }

        public int FirstChangedRowIndex { get; private set; }

        public static DiffRowSet FromRows(IReadOnlyList<DiffLineRow> rows)
        {
            rows = rows ?? new List<DiffLineRow>();
            return new DiffRowSet
            {
                FullRows = rows,
                DiffOnlyRows = BuildDiffOnlyRows(rows),
                FirstChangedRowIndex = FindFirstChangedRowIndex(rows),
            };
        }

        private static IReadOnlyList<DiffLineRow> BuildDiffOnlyRows(IReadOnlyList<DiffLineRow> rows)
        {
            if (rows.Count == 0 || rows.All(row => !row.IsChanged))
            {
                return new List<DiffLineRow>();
            }

            var include = new bool[rows.Count];
            for (var i = 0; i < rows.Count; i++)
            {
                if (!rows[i].IsChanged)
                {
                    continue;
                }

                var start = Math.Max(0, i - 3);
                var end = Math.Min(rows.Count - 1, i + 3);
                for (var j = start; j <= end; j++)
                {
                    include[j] = true;
                }
            }

            var result = new List<DiffLineRow>();
            var lastIncluded = -1;
            for (var i = 0; i < rows.Count; i++)
            {
                if (!include[i])
                {
                    continue;
                }

                if (lastIncluded >= 0 && i > lastIncluded + 1)
                {
                    result.Add(DiffLineRow.CreateSeparator());
                }

                result.Add(rows[i]);
                lastIncluded = i;
            }

            return result;
        }

        private static int FindFirstChangedRowIndex(IReadOnlyList<DiffLineRow> rows)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].IsChanged)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    public class DiffLineRow
    {
        private static readonly Brush UnchangedBrush = CreateBrush(0xFF, 0xFF, 0xFF);
        private static readonly Brush InsertedBrush = CreateBrush(0xE7, 0xF6, 0xEA);
        private static readonly Brush DeletedBrush = CreateBrush(0xFC, 0xE8, 0xE6);
        private static readonly Brush ModifiedBrush = CreateBrush(0xFF, 0xF4, 0xD8);
        private static readonly Brush ImaginaryBrush = CreateBrush(0xF3, 0xF5, 0xF8);
        private static readonly Brush SeparatorBrush = CreateBrush(0xFA, 0xFB, 0xFC);
        private static readonly Brush LineNumberBrushValue = CreateBrush(0x6B, 0x72, 0x80);
        private static readonly Brush MutedTextBrushValue = CreateBrush(0x94, 0xA3, 0xB8);

        public int? OldLineNumber { get; set; }

        public int? NewLineNumber { get; set; }

        public string OldText { get; set; }

        public string NewText { get; set; }

        public ChangeType OldChangeType { get; set; }

        public ChangeType NewChangeType { get; set; }

        public bool IsSeparator { get; set; }

        public bool IsNote { get; set; }

        public bool HasWhitespaceOnlyChange { get; set; }

        public IReadOnlyList<DiffTextRun> OldTextRuns { get; set; }

        public IReadOnlyList<DiffTextRun> NewTextRuns { get; set; }

        public bool IsChanged => IsSeparator || IsNote || OldChangeType != ChangeType.Unchanged || NewChangeType != ChangeType.Unchanged;

        public bool IsOldChanged => OldChangeType != ChangeType.Unchanged && OldChangeType != ChangeType.Imaginary;

        public bool IsNewChanged => NewChangeType != ChangeType.Unchanged && NewChangeType != ChangeType.Imaginary;

        public string OldLineNumberText => OldLineNumber.HasValue ? OldLineNumber.Value.ToString() : string.Empty;

        public string NewLineNumberText => NewLineNumber.HasValue ? NewLineNumber.Value.ToString() : string.Empty;

        public string OldChangeMarker => IsSeparator ? string.Empty : GetMarker(OldChangeType);

        public string NewChangeMarker => IsSeparator ? string.Empty : GetMarker(NewChangeType);

        public Brush OldBackground => IsSeparator || IsNote ? SeparatorBrush : GetBackground(OldChangeType);

        public Brush NewBackground => IsSeparator || IsNote ? SeparatorBrush : GetBackground(NewChangeType);

        public Brush OldLineNumberForeground => LineNumberBrushValue;

        public Brush NewLineNumberForeground => LineNumberBrushValue;

        public string SeparatorText => IsSeparator ? "..." : string.Empty;

        public Visibility SeparatorVisibility => IsSeparator ? Visibility.Visible : Visibility.Collapsed;

        public static DiffLineRow CreateSeparator()
        {
            return new DiffLineRow
            {
                IsSeparator = true,
                OldChangeType = ChangeType.Imaginary,
                NewChangeType = ChangeType.Imaginary,
                OldTextRuns = new List<DiffTextRun>(),
                NewTextRuns = new List<DiffTextRun>(),
            };
        }

        public static DiffLineRow CreateNote(string oldNote, string newNote)
        {
            return new DiffLineRow
            {
                IsNote = true,
                OldChangeType = ChangeType.Imaginary,
                NewChangeType = ChangeType.Imaginary,
                OldTextRuns = new List<DiffTextRun> { new DiffTextRun(oldNote ?? string.Empty, DiffTextRunKind.Normal, isNewSide: false) },
                NewTextRuns = new List<DiffTextRun> { new DiffTextRun(newNote ?? string.Empty, DiffTextRunKind.Normal, isNewSide: true) },
            };
        }

        private static string GetMarker(ChangeType type)
        {
            switch (type)
            {
                case ChangeType.Inserted:
                    return "+";
                case ChangeType.Deleted:
                    return "-";
                case ChangeType.Modified:
                    return "~";
                default:
                    return string.Empty;
            }
        }

        private static Brush GetBackground(ChangeType type)
        {
            switch (type)
            {
                case ChangeType.Inserted:
                    return InsertedBrush;
                case ChangeType.Deleted:
                    return DeletedBrush;
                case ChangeType.Modified:
                    return ModifiedBrush;
                case ChangeType.Imaginary:
                    return ImaginaryBrush;
                default:
                    return UnchangedBrush;
            }
        }

        private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    public class DiffTextRun
    {
        private static readonly Brush TransparentBrush = CreateBrush(0x00, 0x00, 0x00, 0x00);
        private static readonly Brush OldChangedBrush = CreateBrush(0xFF, 0xF8, 0xC7, 0xC2);
        private static readonly Brush NewChangedBrush = CreateBrush(0xFF, 0xC8, 0xEA, 0xD0);

        public DiffTextRun(string text, DiffTextRunKind kind, bool isNewSide)
        {
            Text = text ?? string.Empty;
            Kind = kind;
            Background = kind == DiffTextRunKind.Changed
                ? isNewSide ? NewChangedBrush : OldChangedBrush
                : TransparentBrush;
        }

        public string Text { get; }

        public DiffTextRunKind Kind { get; }

        public Brush Background { get; }

        public bool IsChanged => Kind == DiffTextRunKind.Changed;

        private static SolidColorBrush CreateBrush(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    public enum DiffTextRunKind
    {
        Normal,
        Changed,
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
