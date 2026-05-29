using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
                ApplySnapshot(repo, RepositoryVcsSnapshot.CreateError(repo));
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
                            return new RepositoryRefreshResult(repo, RepositoryVcsSnapshot.CreateError(repo));
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
            repo.SvnRemoteUpdateCount = snapshot.SvnRemoteUpdateCount;
            repo.GitChangedFiles = new ObservableCollection<VcsChangedFile>(snapshot.GitChangedFiles.Select(file => file.Clone()));
            repo.RootSvnChangedFiles = new ObservableCollection<VcsChangedFile>(snapshot.RootSvnChangedFiles.Select(file => file.Clone()));
            repo.SubRepositories = new ObservableCollection<SubRepository>(snapshot.SubRepositories);
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
            var gitSubDirs = await Task.Run(() => FindGitSubDirectories(repo.Path), cancellationToken);
            var svnSubDirs = await Task.Run(() => FindSvnSubDirectories(repo.Path), cancellationToken);
            var hasAnyGit = hasGit || gitSubDirs.Count > 0;
            var hasAnySvn = hasSvn || svnSubDirs.Count > 0;

            if (hasAnyGit && hasAnySvn)
            {
                snapshot.VcsType = VcsType.Mixed;
            }
            else if (hasAnyGit)
            {
                snapshot.VcsType = VcsType.Git;
            }
            else if (hasAnySvn)
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

            if (gitSubDirs.Count > 0 || svnSubDirs.Count > 0)
            {
                await RefreshSubRepositoriesAsync(snapshot, repo.Path, gitSubDirs, svnSubDirs, cancellationToken);
            }

            snapshot.VcsStatus = CalculateOverallStatus(snapshot);
            snapshot.LastStatusRefresh = DateTime.Now;
            return snapshot;
        }

        private static async Task RefreshGitStatusAsync(RepositoryVcsSnapshot snapshot, string repoPath, CancellationToken ct)
        {
            var gitStatus = await ReadGitStatusAsync(repoPath, "Git 根仓库", ct);
            snapshot.GitBranch = gitStatus.Branch;
            snapshot.GitAheadCount = gitStatus.AheadCount;
            snapshot.GitBehindCount = gitStatus.BehindCount;
            snapshot.AddedCount = gitStatus.AddedCount;
            snapshot.ModifiedCount = gitStatus.ModifiedCount;
            snapshot.DeletedCount = gitStatus.DeletedCount;
            snapshot.StagedCount = gitStatus.StagedCount;
            snapshot.HasConflict = gitStatus.HasConflict;
            snapshot.GitChangedFiles.AddRange(gitStatus.ChangedFiles);
            if (gitStatus.HasError)
            {
                snapshot.VcsStatus = VcsStatus.Error;
            }
        }

        private static async Task<GitStatusInfo> ReadGitStatusAsync(string repoPath, string groupName, CancellationToken ct)
        {
            var info = new GitStatusInfo();
            var branchResult = await RunCommandAsync("git", "branch --show-current", repoPath, ct);
            if (branchResult.ExitCode == 0)
            {
                info.Branch = branchResult.Output.Trim();
                if (string.IsNullOrWhiteSpace(info.Branch))
                {
                    var headResult = await RunCommandAsync("git", "rev-parse --short HEAD", repoPath, ct);
                    info.Branch = headResult.ExitCode == 0 ? $"({headResult.Output.Trim()})" : "(detached)";
                }
            }
            else
            {
                var fallbackBranchResult = await RunCommandAsync("git", "rev-parse --abbrev-ref HEAD", repoPath, ct);
                if (fallbackBranchResult.ExitCode == 0)
                {
                    var branch = fallbackBranchResult.Output.Trim();
                    info.Branch = string.Equals(branch, "HEAD", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : branch;
                }
            }

            await RefreshGitRemoteTrackingAsync(repoPath, ct);

            var abResult = await RunCommandAsync("git", "rev-list --left-right --count HEAD...@{upstream}", repoPath, ct);
            if (abResult.ExitCode == 0)
            {
                var parts = abResult.Output.Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    int.TryParse(parts[0], out var ahead);
                    int.TryParse(parts[1], out var behind);
                    info.AheadCount = ahead;
                    info.BehindCount = behind;
                }
            }

            var statusResult = await RunCommandAsync("git", "-c core.quotepath=false status --porcelain --untracked-files=no", repoPath, ct);
            if (statusResult.ExitCode != 0)
            {
                info.HasError = true;
                return info;
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

                if (TryCreateGitChangedFile(repoPath, groupName, line, indexStatus, workStatus, out var changedFile))
                {
                    info.ChangedFiles.Add(changedFile);
                }
            }

            info.AddedCount = added;
            info.ModifiedCount = modified;
            info.DeletedCount = deleted;
            info.StagedCount = staged;
            info.HasConflict = hasConflict;
            return info;
        }

        private static async Task RefreshGitRemoteTrackingAsync(string repoPath, CancellationToken ct)
        {
            var upstreamResult = await RunCommandAsync("git", "rev-parse --abbrev-ref --symbolic-full-name @{upstream}", repoPath, ct);
            if (upstreamResult.ExitCode != 0)
            {
                return;
            }

            await RunCommandAsync("git", "fetch --prune --quiet", repoPath, ct);
        }

        private static async Task RefreshSvnStatusAsync(RepositoryVcsSnapshot snapshot, string svnPath, CancellationToken ct)
        {
            var infoResult = await RunCommandAsync("svn", "info --show-item revision", svnPath, ct);
            if (infoResult.ExitCode == 0 && int.TryParse(infoResult.Output.Trim(), out var rev))
            {
                snapshot.SvnRevision = rev;
            }

            snapshot.SvnRemoteUpdateCount = await ReadSvnRemoteUpdateCountAsync(svnPath, ct);

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

                if (TryCreateSvnChangedFile(svnPath, svnPath, "SVN 根目录", line, out var changedFile))
                {
                    snapshot.RootSvnChangedFiles.Add(changedFile);
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

        private static List<string> FindGitSubDirectories(string rootPath)
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

                    if (HasGitMetadata(dir))
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

                            if (HasGitMetadata(subDir))
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

        private static bool HasGitMetadata(string path)
        {
            return Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));
        }

        private static async Task RefreshSubRepositoriesAsync(RepositoryVcsSnapshot snapshot, string repoPath, IEnumerable<string> gitDirs, IEnumerable<string> svnDirs, CancellationToken ct)
        {
            foreach (var gitDir in (gitDirs ?? Enumerable.Empty<string>()).OrderBy(path => GetRelativePath(repoPath, path), StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();

                var relativePath = GetRelativePath(repoPath, gitDir);
                var subRepo = new SubRepository
                {
                    RelativePath = relativePath,
                    VcsType = VcsType.Git,
                    Status = VcsStatus.Unknown,
                };

                var gitStatus = await ReadGitStatusAsync(gitDir, $"Git 子仓库/{relativePath}", ct);
                subRepo.Branch = gitStatus.Branch;
                subRepo.GitAheadCount = gitStatus.AheadCount;
                subRepo.GitBehindCount = gitStatus.BehindCount;
                subRepo.StagedCount = gitStatus.StagedCount;
                subRepo.ChangedFiles = new ObservableCollection<VcsChangedFile>(gitStatus.ChangedFiles);
                subRepo.ChangedFileCount = gitStatus.ChangedFiles.Count;
                subRepo.Status = gitStatus.HasError
                    ? VcsStatus.Error
                    : gitStatus.HasConflict
                        ? VcsStatus.Conflict
                        : gitStatus.ChangedFiles.Count == 0
                            ? VcsStatus.Clean
                            : VcsStatus.Modified;
                subRepo.StatusSummary = gitStatus.HasError ? "检测失败" : gitStatus.ChangedFiles.Count == 0 ? "干净" : $"{gitStatus.ChangedFiles.Count}项变更";
                snapshot.SubRepositories.Add(subRepo);
            }

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

                subRepo.SvnRemoteUpdateCount = await ReadSvnRemoteUpdateCountAsync(svnDir, ct);

                var statusResult = await RunCommandAsync("svn", "status", svnDir, ct);
                if (statusResult.ExitCode == 0)
                {
                    var lines = SplitLines(statusResult.Output)
                        .Where(line => line.Length > 0 && IsValidSvnChangeStatus(line[0]))
                        .ToList();
                    subRepo.ChangedFiles = new ObservableCollection<VcsChangedFile>(
                        lines.Select(line =>
                            TryCreateSvnChangedFile(repoPath, svnDir, $"SVN 子仓库/{subRepo.RelativePath}", line, out var changedFile)
                                ? changedFile
                                : null)
                            .Where(file => file != null));
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

        private static async Task<int> ReadSvnRemoteUpdateCountAsync(string svnPath, CancellationToken ct)
        {
            var statusResult = await RunCommandAsync("svn", "status -u", svnPath, ct);
            if (statusResult.ExitCode != 0)
            {
                return 0;
            }

            return SplitLines(statusResult.Output)
                .Count(HasSvnRemoteUpdateMarker);
        }

        private static bool HasSvnRemoteUpdateMarker(string statusLine)
        {
            return !string.IsNullOrEmpty(statusLine) &&
                   statusLine.Length > 8 &&
                   statusLine[8] == '*';
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

            if (snapshot.SubRepositories.Any(s => s.Status == VcsStatus.Error))
            {
                return VcsStatus.Error;
            }

            if (snapshot.HasConflict || snapshot.SubRepositories.Any(s => s.Status == VcsStatus.Conflict))
            {
                return VcsStatus.Conflict;
            }

            var hasRootChanges = snapshot.AddedCount + snapshot.ModifiedCount + snapshot.DeletedCount > 0;
            var hasSubChanges = snapshot.SubRepositories.Any(s => s.ChangedFileCount > 0);
            return hasRootChanges || hasSubChanges ? VcsStatus.Modified : VcsStatus.Clean;
        }

        private static VcsType DetectRepositoryVcsType(string repoPath)
        {
            if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            {
                return VcsType.None;
            }

            var hasGit = Directory.Exists(Path.Combine(repoPath, ".git")) || File.Exists(Path.Combine(repoPath, ".git"));
            var hasSvn = Directory.Exists(Path.Combine(repoPath, ".svn"));
            var hasGitSubDirs = FindGitSubDirectories(repoPath).Count > 0;
            var hasSvnSubDirs = FindSvnSubDirectories(repoPath).Count > 0;
            if ((hasGit || hasGitSubDirs) && (hasSvn || hasSvnSubDirs))
            {
                return VcsType.Mixed;
            }

            if (hasGit || hasGitSubDirs)
            {
                return VcsType.Git;
            }

            return hasSvn || hasSvnSubDirs ? VcsType.Svn : VcsType.None;
        }

        private static bool IsGitConflict(char indexStatus, char workStatus)
        {
            return indexStatus == 'U' ||
                   workStatus == 'U' ||
                   (indexStatus == 'A' && workStatus == 'A') ||
                   (indexStatus == 'D' && workStatus == 'D');
        }

        private static bool TryCreateGitChangedFile(string repoPath, string groupName, string statusLine, char indexStatus, char workStatus, out VcsChangedFile changedFile)
        {
            try
            {
                changedFile = CreateGitChangedFile(repoPath, groupName, statusLine, indexStatus, workStatus);
                return changedFile != null;
            }
            catch
            {
                changedFile = null;
                return false;
            }
        }

        private static VcsChangedFile CreateGitChangedFile(string repoPath, string groupName, string statusLine, char indexStatus, char workStatus)
        {
            var pathPart = statusLine.Length > 3 ? statusLine.Substring(3).Trim() : string.Empty;
            var originalPath = TryParseRenameOriginalPath(pathPart, out var currentPath);
            var statusCode = ResolveGitStatusCode(indexStatus, workStatus);
            var relativePath = NormalizeVcsPath(currentPath);
            return new VcsChangedFile
            {
                VcsType = VcsType.Git,
                StatusCode = statusCode,
                RelativePath = relativePath,
                OriginalRelativePath = NormalizeVcsPath(originalPath),
                AbsolutePath = Path.Combine(repoPath, relativePath ?? string.Empty),
                WorkingDirectory = repoPath,
                GroupName = groupName,
            };
        }

        private static bool TryCreateSvnChangedFile(string repoPath, string svnDir, string groupName, string statusLine, out VcsChangedFile changedFile)
        {
            try
            {
                changedFile = CreateSvnChangedFile(repoPath, svnDir, groupName, statusLine);
                return changedFile != null;
            }
            catch
            {
                changedFile = null;
                return false;
            }
        }

        private static VcsChangedFile CreateSvnChangedFile(string repoPath, string svnDir, string groupName, string statusLine)
        {
            var statusCode = statusLine.Length == 0 ? '?' : statusLine[0];
            var pathPart = statusLine.Length > 1 ? statusLine.Substring(1).Trim() : string.Empty;
            var absolutePath = Path.IsPathRooted(pathPart) ? pathPart : Path.Combine(svnDir, pathPart);
            var relativePath = GetRelativePath(svnDir, absolutePath);
            return new VcsChangedFile
            {
                VcsType = VcsType.Svn,
                StatusCode = statusCode,
                RelativePath = relativePath,
                AbsolutePath = absolutePath,
                WorkingDirectory = svnDir,
                GroupName = groupName,
            };
        }

        private static char ResolveGitStatusCode(char indexStatus, char workStatus)
        {
            if (IsGitConflict(indexStatus, workStatus))
            {
                return 'U';
            }

            if (indexStatus == 'D' || workStatus == 'D')
            {
                return 'D';
            }

            if (indexStatus == 'A' || workStatus == 'A' || indexStatus == '?' || workStatus == '?')
            {
                return 'A';
            }

            if (indexStatus == 'R' || workStatus == 'R')
            {
                return 'R';
            }

            return 'M';
        }

        private static string TryParseRenameOriginalPath(string pathPart, out string currentPath)
        {
            currentPath = NormalizeGitStatusPath(pathPart);
            if (string.IsNullOrWhiteSpace(pathPart))
            {
                return null;
            }

            var marker = " -> ";
            var index = pathPart.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }

            var originalPath = NormalizeGitStatusPath(pathPart.Substring(0, index).Trim());
            currentPath = NormalizeGitStatusPath(pathPart.Substring(index + marker.Length).Trim());
            return originalPath;
        }

        private static string NormalizeGitStatusPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            path = path.Trim();
            if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"')
            {
                path = path.Substring(1, path.Length - 2)
                    .Replace("\\\"", "\"")
                    .Replace("\\\\", "\\");
            }

            return path;
        }

        private static string NormalizeVcsPath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? path : path.Replace('/', Path.DirectorySeparatorChar);
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

        private class GitStatusInfo
        {
            public string Branch { get; set; }

            public int AheadCount { get; set; }

            public int BehindCount { get; set; }

            public int AddedCount { get; set; }

            public int ModifiedCount { get; set; }

            public int DeletedCount { get; set; }

            public int StagedCount { get; set; }

            public bool HasConflict { get; set; }

            public bool HasError { get; set; }

            public List<VcsChangedFile> ChangedFiles { get; } = new List<VcsChangedFile>();
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

            public int SvnRemoteUpdateCount { get; set; }

            public bool HasConflict { get; set; }

            public DateTime LastStatusRefresh { get; set; }

            public List<VcsChangedFile> GitChangedFiles { get; } = new List<VcsChangedFile>();

            public List<VcsChangedFile> RootSvnChangedFiles { get; } = new List<VcsChangedFile>();

            public List<SubRepository> SubRepositories { get; } = new List<SubRepository>();

            public static RepositoryVcsSnapshot CreateError(CodeRepository repo)
            {
                var snapshot = new RepositoryVcsSnapshot
                {
                    VcsType = repo?.VcsType != VcsType.None ? repo.VcsType : DetectRepositoryVcsType(repo?.Path),
                    VcsStatus = VcsStatus.Error,
                    GitBranch = repo?.GitBranch,
                    GitAheadCount = repo?.GitAheadCount ?? 0,
                    GitBehindCount = repo?.GitBehindCount ?? 0,
                    AddedCount = repo?.AddedCount ?? 0,
                    ModifiedCount = repo?.ModifiedCount ?? 0,
                    DeletedCount = repo?.DeletedCount ?? 0,
                    StagedCount = repo?.StagedCount ?? 0,
                    SvnRevision = repo?.SvnRevision ?? 0,
                    SvnRemoteUpdateCount = repo?.SvnRemoteUpdateCount ?? 0,
                    HasConflict = repo?.HasConflict ?? false,
                    LastStatusRefresh = DateTime.Now,
                };

                if (repo?.GitChangedFiles != null)
                {
                    snapshot.GitChangedFiles.AddRange(repo.GitChangedFiles.Select(file => file.Clone()));
                }

                if (repo?.RootSvnChangedFiles != null)
                {
                    snapshot.RootSvnChangedFiles.AddRange(repo.RootSvnChangedFiles.Select(file => file.Clone()));
                }

                if (repo?.SubRepositories != null)
                {
                    snapshot.SubRepositories.AddRange(repo.SubRepositories.Select(sub => sub.Clone()));
                }

                return snapshot;
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
