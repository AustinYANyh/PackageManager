using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MftScanner;
using PackageManager.Features.CodeWorkspace.Models;

namespace PackageManager.Features.CodeWorkspace.Services
{
    public class VcsExternalDiffToolService
    {
        private static readonly object CacheLock = new object();
        private static string _tortoiseGitProcPath;
        private static string _tortoiseSvnProcPath;

        public async Task<string> ResolveToolPathAsync(VcsType vcsType, CancellationToken cancellationToken = default)
        {
            var exeName = vcsType == VcsType.Git
                ? "TortoiseGitProc.exe"
                : vcsType == VcsType.Svn
                    ? "TortoiseProc.exe"
                    : null;
            if (string.IsNullOrWhiteSpace(exeName))
            {
                return null;
            }

            var cached = GetCachedPath(vcsType);
            if (File.Exists(cached))
            {
                return cached;
            }

            var resolved = ResolveFromKnownLocations(vcsType, exeName)
                           ?? await ResolveFromIndexAsync(vcsType, exeName, cancellationToken).ConfigureAwait(false);
            if (File.Exists(resolved))
            {
                SetCachedPath(vcsType, resolved);
                return resolved;
            }

            SetCachedPath(vcsType, null);
            return null;
        }

        private static string GetCachedPath(VcsType vcsType)
        {
            lock (CacheLock)
            {
                return vcsType == VcsType.Git ? _tortoiseGitProcPath : _tortoiseSvnProcPath;
            }
        }

        private static void SetCachedPath(VcsType vcsType, string path)
        {
            lock (CacheLock)
            {
                if (vcsType == VcsType.Git)
                {
                    _tortoiseGitProcPath = path;
                }
                else if (vcsType == VcsType.Svn)
                {
                    _tortoiseSvnProcPath = path;
                }
            }
        }

        private static string ResolveFromKnownLocations(VcsType vcsType, string exeName)
        {
            var candidates = new List<string>();
            var folderName = vcsType == VcsType.Git ? "TortoiseGit" : "TortoiseSVN";
            AddCandidate(candidates, Path.Combine(@"C:\Program Files", folderName, "bin", exeName));
            AddCandidate(candidates, Path.Combine(@"C:\Program Files (x86)", folderName, "bin", exeName));
            AddCandidate(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), folderName, "bin", exeName));
            AddCandidate(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), folderName, "bin", exeName));

            foreach (var pathDirectory in SplitPathEnvironment())
            {
                AddCandidate(candidates, Path.Combine(pathDirectory, exeName));
            }

            return SelectPreferred(vcsType, candidates);
        }

        private static async Task<string> ResolveFromIndexAsync(VcsType vcsType, string exeName, CancellationToken cancellationToken)
        {
            ISharedIndexService indexService = null;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    indexService = SharedIndexServiceFactory.Create("CodeWorkspace.VcsDiffTool");
                    var result = await indexService.SearchAsync(
                        exeName,
                        30,
                        0,
                        SearchTypeFilter.Launchable,
                        null,
                        cts.Token).ConfigureAwait(false);

                    var candidates = result?.Results?
                        .Where(item => item != null &&
                                       !item.IsDirectory &&
                                       string.Equals(item.FileName, exeName, StringComparison.OrdinalIgnoreCase))
                        .Select(item => item.FullPath)
                        .ToList() ?? new List<string>();

                    return SelectPreferred(vcsType, candidates);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    indexService?.Shutdown();
                }
            }
        }

        private static string SelectPreferred(VcsType vcsType, IEnumerable<string> candidates)
        {
            var existing = (candidates ?? Enumerable.Empty<string>())
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (existing.Count == 0)
            {
                return null;
            }

            var preferredSegment = vcsType == VcsType.Git
                ? @"\TortoiseGit\bin\TortoiseGitProc.exe"
                : @"\TortoiseSVN\bin\TortoiseProc.exe";
            return existing
                .OrderByDescending(path => path.EndsWith(preferredSegment, StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => path.Length)
                .FirstOrDefault();
        }

        private static IEnumerable<string> SplitPathEnvironment()
        {
            return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Trim().Trim('"'))
                .Where(Directory.Exists);
        }

        private static void AddCandidate(ICollection<string> candidates, string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                candidates.Add(path);
            }
        }
    }
}
