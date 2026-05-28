using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PackageManager.Features.CodeWorkspace.Models;

namespace PackageManager.Features.CodeWorkspace.Services
{
    public class VcsStatusService
    {
        private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

        private readonly ConcurrentDictionary<string, DateTime> _lastRefreshTimes = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _refreshCts;

        public async Task RefreshRepositoryStatusAsync(CodeRepository repo, CancellationToken cancellationToken = default, bool forceRefresh = false)
        {
            if (repo == null || string.IsNullOrWhiteSpace(repo.Path) || !Directory.Exists(repo.Path))
            {
                return;
            }

            if (ShouldSkipRefresh(repo, forceRefresh))
            {
                return;
            }

            repo.IsRefreshing = true;

            try
            {
                var snapshot = await BuildSnapshotAsync(repo, cancellationToken);
                ApplySnapshot(repo, snapshot);
                _lastRefreshTimes[repo.Path] = DateTime.Now;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                repo.VcsStatus = VcsStatus.Error;
            }
            finally
            {
                repo.IsRefreshing = false;
            }
        }

        public async Task RefreshAllAsync(IEnumerable<CodeRepository> repositories, CancellationToken cancellationToken = default, bool forceRefresh = false)
        {
            _refreshCts?.Cancel();
            _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _refreshCts.Token;
            var repositoryList = repositories?
                .Where(repo => repo != null && !string.IsNullOrWhiteSpace(repo.Path) && Directory.Exists(repo.Path))
                .Where(repo => !ShouldSkipRefresh(repo, forceRefresh))
                .ToList() ?? new List<CodeRepository>();

            if (repositoryList.Count == 0)
            {
                return;
            }

            foreach (var repo in repositoryList)
            {
                repo.IsRefreshing = true;
            }

            try
            {
                using (var semaphore = new SemaphoreSlim(4))
                {
                    var tasks = repositoryList.Select(async repo =>
                    {
                        await semaphore.WaitAsync(token);
                        try
                        {
                            var snapshot = await BuildSnapshotAsync(repo, token);
                            return new RepositoryRefreshResult(repo, snapshot);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            return new RepositoryRefreshResult(repo, RepositoryVcsSnapshot.CreateError());
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });

                    var results = await Task.WhenAll(tasks);

                    foreach (var result in results)
                    {
                        ApplySnapshot(result.Repository, result.Snapshot);
                        _lastRefreshTimes[result.Repository.Path] = DateTime.Now;
                    }
                }
            }
            finally
            {
                foreach (var repo in repositoryList)
                {
                    repo.IsRefreshing = false;
                }
            }
        }

        public void CancelRefresh()
        {
            _refreshCts?.Cancel();
        }

        private static void ApplySnapshot(CodeRepository repo, RepositoryVcsSnapshot snapshot)
        {
            repo.HasConflict = snapshot.HasConflict;
            repo.GitBranch = snapshot.GitBranch;
            repo.GitAheadCount = snapshot.GitAheadCount;
            repo.GitBehindCount = snapshot.GitBehindCount;
            repo.AddedCount = snapshot.AddedCount;
            repo.ModifiedCount = snapshot.ModifiedCount;
            repo.DeletedCount = snapshot.DeletedCount;
            repo.StagedCount = snapshot.StagedCount;
            repo.SvnRevision = snapshot.SvnRevision;
            repo.SubRepositories = new System.Collections.ObjectModel.ObservableCollection<SubRepository>(snapshot.SubRepositories);
            repo.VcsType = snapshot.VcsType;
            repo.VcsStatus = snapshot.VcsStatus;
            repo.LastStatusRefresh = snapshot.LastStatusRefresh;
        }

        private bool ShouldSkipRefresh(CodeRepository repo, bool forceRefresh)
        {
            return !forceRefresh &&
                   repo != null &&
                   !string.IsNullOrWhiteSpace(repo.Path) &&
                   _lastRefreshTimes.TryGetValue(repo.Path, out var lastTime) &&
                   DateTime.Now - lastTime < MinRefreshInterval;
        }

        private static async Task<RepositoryVcsSnapshot> BuildSnapshotAsync(CodeRepository repo, CancellationToken cancellationToken)
        {
            var snapshot = new RepositoryVcsSnapshot();
            var hasGit = Directory.Exists(Path.Combine(repo.Path, ".git")) || File.Exists(Path.Combine(repo.Path, ".git"));
            var hasSvn = Directory.Exists(Path.Combine(repo.Path, ".svn"));
            var svnSubDirs = await Task.Run(() => FindSvnSubDirectories(repo.Path), cancellationToken);

            if (hasGit && svnSubDirs.Count > 0)
            {
                snapshot.VcsType = VcsType.Mixed;
            }
            else if (hasGit)
            {
                snapshot.VcsType = VcsType.Git;
            }
            else if (hasSvn)
            {
                snapshot.VcsType = VcsType.Svn;
            }
            else
            {
                snapshot.VcsType = VcsType.None;
            }

            if (hasGit)
            {
                await RefreshGitStatusAsync(snapshot, repo.Path, cancellationToken);
            }

            if (hasSvn && !hasGit)
            {
                await RefreshSvnStatusAsync(snapshot, repo.Path, cancellationToken);
            }

            if (svnSubDirs.Count > 0)
            {
                await RefreshSubRepositoriesAsync(snapshot, repo.Path, svnSubDirs, cancellationToken);
            }

            snapshot.VcsStatus = CalculateOverallStatus(snapshot);
            snapshot.LastStatusRefresh = DateTime.Now;
            return snapshot;
        }

        private static async Task RefreshGitStatusAsync(RepositoryVcsSnapshot snapshot, string repoPath, CancellationToken ct)
        {
            var branchResult = await RunCommandAsync("git", "branch --show-current", repoPath, ct);
            if (branchResult.ExitCode == 0)
            {
                snapshot.GitBranch = branchResult.Output.Trim();
                if (string.IsNullOrWhiteSpace(snapshot.GitBranch))
                {
                    var headResult = await RunCommandAsync("git", "rev-parse --short HEAD", repoPath, ct);
                    snapshot.GitBranch = headResult.ExitCode == 0 ? $"({headResult.Output.Trim()})" : "(detached)";
                }
            }

            var abResult = await RunCommandAsync("git", "rev-list --left-right --count HEAD...@{upstream}", repoPath, ct);
            if (abResult.ExitCode == 0)
            {
                var parts = abResult.Output.Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    int.TryParse(parts[0], out var ahead);
                    int.TryParse(parts[1], out var behind);
                    snapshot.GitAheadCount = ahead;
                    snapshot.GitBehindCount = behind;
                }
            }

            var statusResult = await RunCommandAsync("git", "status --porcelain", repoPath, ct);
            if (statusResult.ExitCode != 0)
            {
                snapshot.VcsStatus = VcsStatus.Error;
                return;
            }

            var lines = SplitLines(statusResult.Output);
            var added = 0;
            var modified = 0;
            var deleted = 0;
            var staged = 0;
            var hasConflict = false;

            foreach (var line in lines)
            {
                if (line.Length < 2)
                {
                    continue;
                }

                var indexStatus = line[0];
                var workStatus = line[1];
                if (indexStatus != ' ' && indexStatus != '?')
                {
                    staged++;
                }

                if (IsGitConflict(indexStatus, workStatus))
                {
                    hasConflict = true;
                    modified++;
                }
                else if (indexStatus == '?' || workStatus == '?' || indexStatus == 'A' || workStatus == 'A')
                {
                    added++;
                }
                else if (indexStatus == 'D' || workStatus == 'D')
                {
                    deleted++;
                }
                else
                {
                    modified++;
                }
            }

            snapshot.AddedCount = added;
            snapshot.ModifiedCount = modified;
            snapshot.DeletedCount = deleted;
            snapshot.StagedCount = staged;
            snapshot.HasConflict = hasConflict;
        }

        private static async Task RefreshSvnStatusAsync(RepositoryVcsSnapshot snapshot, string svnPath, CancellationToken ct)
        {
            var infoResult = await RunCommandAsync("svn", "info --show-item revision", svnPath, ct);
            if (infoResult.ExitCode == 0 && int.TryParse(infoResult.Output.Trim(), out var rev))
            {
                snapshot.SvnRevision = rev;
            }

            var statusResult = await RunCommandAsync("svn", "status", svnPath, ct);
            if (statusResult.ExitCode != 0)
            {
                snapshot.VcsStatus = VcsStatus.Error;
                return;
            }

            var added = 0;
            var modified = 0;
            var deleted = 0;
            var hasConflict = false;

            foreach (var line in SplitLines(statusResult.Output).Where(line => line.Length > 0 && IsValidSvnChangeStatus(line[0])))
            {
                switch (line[0])
                {
                    case 'A':
                        added++;
                        break;
                    case 'D':
                        deleted++;
                        break;
                    case 'C':
                        hasConflict = true;
                        modified++;
                        break;
                    default:
                        modified++;
                        break;
                }
            }

            snapshot.AddedCount = added;
            snapshot.ModifiedCount = modified;
            snapshot.DeletedCount = deleted;
            snapshot.HasConflict = hasConflict;
        }

        private static List<string> FindSvnSubDirectories(string rootPath)
        {
            var result = new List<string>();
            try
            {
                foreach (var dir in Directory.GetDirectories(rootPath))
                {
                    var dirName = Path.GetFileName(dir);
                    if (ShouldSkipDirectory(dirName))
                    {
                        continue;
                    }

                    if (Directory.Exists(Path.Combine(dir, ".svn")))
                    {
                        result.Add(dir);
                        continue;
                    }

                    try
                    {
                        foreach (var subDir in Directory.GetDirectories(dir))
                        {
                            var subDirName = Path.GetFileName(subDir);
                            if (ShouldSkipDirectory(subDirName))
                            {
                                continue;
                            }

                            if (Directory.Exists(Path.Combine(subDir, ".svn")))
                            {
                                result.Add(subDir);
                            }
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }

            return result;
        }

        private static async Task RefreshSubRepositoriesAsync(RepositoryVcsSnapshot snapshot, string repoPath, IEnumerable<string> svnDirs, CancellationToken ct)
        {
            foreach (var svnDir in svnDirs)
            {
                ct.ThrowIfCancellationRequested();

                var subRepo = new SubRepository
                {
                    RelativePath = GetRelativePath(repoPath, svnDir),
                    VcsType = VcsType.Svn,
                    Status = VcsStatus.Unknown,
                };

                var infoResult = await RunCommandAsync("svn", "info --show-item revision", svnDir, ct);
                if (infoResult.ExitCode == 0 && int.TryParse(infoResult.Output.Trim(), out var rev))
                {
                    subRepo.Revision = rev;
                }

                var statusResult = await RunCommandAsync("svn", "status", svnDir, ct);
                if (statusResult.ExitCode == 0)
                {
                    var lines = SplitLines(statusResult.Output)
                        .Where(line => line.Length > 0 && IsValidSvnChangeStatus(line[0]))
                        .ToList();
                    subRepo.ChangedFileCount = lines.Count;
                    subRepo.Status = lines.Count == 0
                        ? VcsStatus.Clean
                        : lines.Any(line => line.Length > 0 && line[0] == 'C')
                            ? VcsStatus.Conflict
                            : VcsStatus.Modified;
                    subRepo.StatusSummary = lines.Count == 0 ? "干净" : $"{lines.Count}项变更";
                }
                else
                {
                    subRepo.Status = VcsStatus.Error;
                    subRepo.StatusSummary = "检测失败";
                }

                snapshot.SubRepositories.Add(subRepo);
            }
        }

        private static VcsStatus CalculateOverallStatus(RepositoryVcsSnapshot snapshot)
        {
            if (snapshot.VcsStatus == VcsStatus.Error)
            {
                return VcsStatus.Error;
            }

            if (snapshot.VcsType == VcsType.None)
            {
                return VcsStatus.Unknown;
            }

            if (snapshot.HasConflict || snapshot.SubRepositories.Any(s => s.Status == VcsStatus.Conflict))
            {
                return VcsStatus.Conflict;
            }

            if (snapshot.SubRepositories.Any(s => s.Status == VcsStatus.Error))
            {
                return VcsStatus.Error;
            }

            var hasRootChanges = snapshot.AddedCount + snapshot.ModifiedCount + snapshot.DeletedCount > 0;
            var hasSubChanges = snapshot.SubRepositories.Any(s => s.ChangedFileCount > 0);
            return hasRootChanges || hasSubChanges ? VcsStatus.Modified : VcsStatus.Clean;
        }

        private static bool IsGitConflict(char indexStatus, char workStatus)
        {
            return indexStatus == 'U' ||
                   workStatus == 'U' ||
                   (indexStatus == 'A' && workStatus == 'A') ||
                   (indexStatus == 'D' && workStatus == 'D');
        }

        private static bool IsValidSvnChangeStatus(char statusCode)
        {
            return statusCode == 'A' ||
                   statusCode == 'M' ||
                   statusCode == 'D' ||
                   statusCode == 'C' ||
                   statusCode == '!' ||
                   statusCode == '~' ||
                   statusCode == 'R';
        }

        private static bool ShouldSkipDirectory(string dirName)
        {
            return string.IsNullOrWhiteSpace(dirName) ||
                   dirName.StartsWith(".", StringComparison.Ordinal) ||
                   string.Equals(dirName, "node_modules", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "bin", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "obj", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "packages", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> SplitLines(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd());
        }

        private static string GetRelativePath(string basePath, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(fullPath))
            {
                return fullPath;
            }

            if (!basePath.EndsWith("\\", StringComparison.Ordinal))
            {
                basePath += "\\";
            }

            var baseUri = new Uri(basePath);
            var fullUri = new Uri(fullPath);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString().Replace('/', '\\'));
        }

        private static async Task<CommandResult> RunCommandAsync(string fileName, string arguments, string workingDirectory, CancellationToken ct)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    process.Start();
                    using (ct.Register(() => TryKill(process)))
                    {
                        var outputTask = process.StandardOutput.ReadToEndAsync();
                        var errorTask = process.StandardError.ReadToEndAsync();
                        var exited = await Task.Run(() => process.WaitForExit((int)CommandTimeout.TotalMilliseconds), ct);

                        if (!exited)
                        {
                            TryKill(process);
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
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new CommandResult { ExitCode = -1, Output = string.Empty, Error = ex.Message };
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
            }
        }

        private class CommandResult
        {
            public int ExitCode { get; set; }

            public string Output { get; set; }

            public string Error { get; set; }
        }

        private class RepositoryVcsSnapshot
        {
            public VcsType VcsType { get; set; } = VcsType.None;

            public VcsStatus VcsStatus { get; set; } = VcsStatus.Unknown;

            public string GitBranch { get; set; }

            public int GitAheadCount { get; set; }

            public int GitBehindCount { get; set; }

            public int AddedCount { get; set; }

            public int ModifiedCount { get; set; }

            public int DeletedCount { get; set; }

            public int StagedCount { get; set; }

            public int SvnRevision { get; set; }

            public bool HasConflict { get; set; }

            public DateTime LastStatusRefresh { get; set; }

            public List<SubRepository> SubRepositories { get; } = new List<SubRepository>();

            public static RepositoryVcsSnapshot CreateError()
            {
                return new RepositoryVcsSnapshot
                {
                    VcsStatus = VcsStatus.Error,
                    LastStatusRefresh = DateTime.Now,
                };
            }
        }

        private class RepositoryRefreshResult
        {
            public RepositoryRefreshResult(CodeRepository repository, RepositoryVcsSnapshot snapshot)
            {
                Repository = repository;
                Snapshot = snapshot;
            }

            public CodeRepository Repository { get; }

            public RepositoryVcsSnapshot Snapshot { get; }
        }
    }
}
