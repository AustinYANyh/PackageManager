using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IOPath = System.IO.Path;

namespace PackageManager.Services.RevitCleanup
{
    internal sealed class EverythingIndexProvider
    {
        private readonly object gate = new object();
        private string esExecutablePath;
        private bool availabilityChecked;

        public bool IsAvailable()
        {
            lock (gate)
            {
                if (availabilityChecked)
                {
                    return !string.IsNullOrWhiteSpace(esExecutablePath);
                }

                availabilityChecked = true;
                esExecutablePath = ResolveEsExecutablePath();
                return !string.IsNullOrWhiteSpace(esExecutablePath);
            }
        }

        public Task<RevitFileQueryResult> QueryAsync(RevitFileQueryOptions options, IProgress<RevitFileQueryProgress> progress, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                if (!IsAvailable())
                {
                    return null;
                }

                var files = new List<RevitIndexedFileInfo>();
                var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var root in options.Roots)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new RevitFileQueryProgress
                    {
                        Message = $"正在查询 Everything 索引... {root.DisplayName}：{root.RootPath}"
                    });

                    foreach (var item in QueryRoot(root, options.Extensions, cancellationToken))
                    {
                        if (dedup.Add(item.FullPath))
                        {
                            files.Add(item);
                        }
                    }
                }

                return new RevitFileQueryResult
                {
                    SourceKind = RevitFileQuerySourceKind.EverythingIndex,
                    ProviderDisplayText = "Everything索引",
                    Files = files.OrderByDescending(item => item.ModifiedTimeUtc).ToList()
                };
            }, cancellationToken);
        }

        private IEnumerable<RevitIndexedFileInfo> QueryRoot(RevitFileQueryRoot root, IEnumerable<string> extensions, CancellationToken cancellationToken)
        {
            var searchText = string.Join("|", (extensions ?? Array.Empty<string>())
                .Select(extension => "*" + RevitCleanupPathUtility.NormalizeExtension(extension)));

            var arguments = string.Join(" ", new[]
            {
                "-path",
                Quote(root.RootPath),
                "-full-path-and-name",
                "-size",
                "-dm",
                "-csv",
                "-no-header",
                "-date-format",
                "3",
                Quote(searchText)
            });

            var startInfo = new ProcessStartInfo
            {
                FileName = esExecutablePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    yield break;
                }

                var standardOutput = process.StandardOutput.ReadToEnd();
                var standardError = process.StandardError.ReadToEnd();
                process.WaitForExit();
                cancellationToken.ThrowIfCancellationRequested();

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Everything 查询失败，ExitCode={process.ExitCode}，Error={standardError}");
                }

                using (var reader = new StringReader(standardOutput))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        var fields = ParseCsvLine(line);
                        if (fields.Count < 3)
                        {
                            continue;
                        }

                        var fullPath = RevitCleanupPathUtility.NormalizePath(fields[0]);
                        if (string.IsNullOrWhiteSpace(fullPath))
                        {
                            continue;
                        }

                        if (!fullPath.StartsWith(root.RootPath + IOPath.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(fullPath, root.RootPath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!RevitCleanupPathUtility.IsIndexedExtension(fullPath, extensions))
                        {
                            continue;
                        }

                        long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sizeBytes);
                        DateTime.TryParse(fields[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var modifiedTimeUtc);

                        yield return new RevitIndexedFileInfo
                        {
                            FullPath = fullPath,
                            FileName = IOPath.GetFileName(fullPath),
                            SizeBytes = sizeBytes,
                            ModifiedTimeUtc = modifiedTimeUtc,
                            RootPath = root.RootPath,
                            RootDisplayName = root.DisplayName,
                            SourceKind = RevitFileQuerySourceKind.EverythingIndex
                        };
                    }
                }
            }
        }

        private string ResolveEsExecutablePath()
        {
            var candidates = new List<string>();
            AddExecutableSearchDirectories(candidates, Environment.GetEnvironmentVariable("PATH"));
            AddIfExists(candidates, IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", "es.exe"));
            AddIfExists(candidates, IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything", "es.exe"));
            AddIfExists(candidates, IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Everything", "es.exe"));

            return candidates.FirstOrDefault(File.Exists);
        }

        private void AddExecutableSearchDirectories(ICollection<string> candidates, string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return;
            }

            foreach (var path in rawPath.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                AddIfExists(candidates, IOPath.Combine(path.Trim(), "es.exe"));
            }
        }

        private void AddIfExists(ICollection<string> candidates, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    candidates.Add(path);
                }
            }
            catch
            {
            }
        }

        private string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        private List<string> ParseCsvLine(string line)
        {
            var results = new List<string>();
            var builder = new StringBuilder();
            var inQuotes = false;

            for (var index = 0; index < line.Length; index++)
            {
                var current = line[index];
                if (current == '"')
                {
                    if (inQuotes && ((index + 1) < line.Length) && line[index + 1] == '"')
                    {
                        builder.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }

                    continue;
                }

                if (!inQuotes && current == ',')
                {
                    results.Add(builder.ToString());
                    builder.Clear();
                    continue;
                }

                builder.Append(current);
            }

            results.Add(builder.ToString());
            return results;
        }
    }
}
