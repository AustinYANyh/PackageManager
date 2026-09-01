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

        public async Task RefreshRepositoryStatusAsync(CodeRepository repo, CancellationToken cancellationToken = default, bool forceRefresh = false, bool includeRemoteStatus = false)
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
                var snapshot = await BuildSnapshotAsync(repo, cancellationToken, includeRemoteStatus, forceRefresh);
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

        public async Task RefreshAllAsync(IEnumerable<CodeRepository> repositories, CancellationToken cancellationToken = default, bool forceRefresh = false, bool includeRemoteStatus = false)
        {
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
                            var snapshot = await BuildSnapshotAsync(repo, token, includeRemoteStatus, forceRefresh);
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
            ApplyChangedFiles(repo.GitChangedFiles, snapshot.GitChangedFiles, out var gitChanged);
            repo.GitChangedFiles = gitChanged;
            ApplyChangedFiles(repo.RootSvnChangedFiles, snapshot.RootSvnChangedFiles, out var svnChanged);
            repo.RootSvnChangedFiles = svnChanged;
            ApplySubRepositories(repo, snapshot.SubRepositories);
            repo.VcsType = snapshot.VcsType;
            repo.VcsStatus = snapshot.VcsStatus;
            repo.LastStatusRefresh = snapshot.LastStatusRefresh;
        }

        /// <summary>
        /// 变更文件集合按签名（状态码+路径序列）对比后更新：内容未变时保留现有集合，
        /// 避免 60 秒轮询对干净仓库反复触发集合 Reset 重绑。
        /// </summary>
        private static void ApplyChangedFiles(ObservableCollection<VcsChangedFile> current, IEnumerable<VcsChangedFile> fresh, out ObservableCollection<VcsChangedFile> applied)
        {
            var freshList = fresh?.ToList() ?? new List<VcsChangedFile>();
            applied = ComputeChangedFilesSignature(current) == ComputeChangedFilesSignature(freshList)
                ? current
                : new ObservableCollection<VcsChangedFile>(freshList.Select(file => file.Clone()));
        }

        private static string ComputeChangedFilesSignature(IEnumerable<VcsChangedFile> files)
        {
            if (files == null)
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder();
            foreach (var file in files)
            {
                builder.Append(file?.StatusCode ?? '\0').Append('|').Append(file?.RelativePath ?? string.Empty).Append(';');
            }

            return builder.ToString();
        }

        /// <summary>
        /// 子仓库列表差分更新：布局（数量与顺序）一致时原地更新现有实例的字段，
        /// 保留列表容器与 UI 虚拟化状态；布局变化时整体替换。
        /// </summary>
        private static void ApplySubRepositories(CodeRepository repo, List<SubRepository> fresh)
        {
            var freshList = fresh ?? new List<SubRepository>();
            var current = repo.SubRepositories;
            if (current == null || current.Count == 0)
            {
                repo.SubRepositories = new ObservableCollection<SubRepository>(freshList);
                return;
            }

            var sameLayout = current.Count == freshList.Count;
            if (sameLayout)
            {
                for (var i = 0; i < freshList.Count; i++)
                {
                    if (current[i].VcsType != freshList[i].VcsType ||
                        !string.Equals(current[i].RelativePath, freshList[i].RelativePath, StringComparison.OrdinalIgnoreCase))
                    {
                        sameLayout = false;
                        break;
                    }
                }
            }

            if (!sameLayout)
            {
                repo.SubRepositories = new ObservableCollection<SubRepository>(freshList);
                return;
            }

            for (var i = 0; i < freshList.Count; i++)
            {
                UpdateSubRepository(current[i], freshList[i]);
            }

            // 差分路径无集合 setter，显式触发派生属性重算（脏检查基于新字段值放行）
            repo.RaiseVcsSummaryChanged();
        }

        private static void UpdateSubRepository(SubRepository target, SubRepository source)
        {
            target.Branch = source.Branch;
            target.Revision = source.Revision;
            target.Status = source.Status;
            target.ChangedFileCount = source.ChangedFileCount;
            target.GitAheadCount = source.GitAheadCount;
            target.GitBehindCount = source.GitBehindCount;
            target.SvnRemoteUpdateCount = source.SvnRemoteUpdateCount;
            target.StagedCount = source.StagedCount;
            target.StatusSummary = source.StatusSummary;
            target.ChangedFiles = source.ChangedFiles;
        }

        private bool ShouldSkipRefresh(CodeRepository repo, bool forceRefresh)
        {
            return !forceRefresh &&
                   repo != null &&
                   !string.IsNullOrWhiteSpace(repo.Path) &&
                   _lastRefreshTimes.TryGetValue(repo.Path, out var lastTime) &&
                   DateTime.Now - lastTime < MinRefreshInterval;
        }

        /// <summary>子仓库目录扫描缓存条目：一趟遍历的结果与时间戳。</summary>
        private sealed class SubRepoScanCache
        {
            public DateTime Timestamp { get; set; }

            public List<string> GitDirs { get; set; }

            public List<string> SvnDirs { get; set; }
        }

        private static readonly TimeSpan SubRepoScanCacheTtl = TimeSpan.FromMinutes(1);

        /// <summary>跨仓库共享的子仓库进程限流：避免根仓库 4 并发 × 子仓库并发造成进程风暴。</summary>
        private static readonly SemaphoreSlim SubRepoProcessGate = new SemaphoreSlim(8);

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SubRepoScanCache> _subRepoScanCache =
            new System.Collections.Concurrent.ConcurrentDictionary<string, SubRepoScanCache>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 获取子仓库目录列表：优先读缓存（1 分钟 TTL），过期或强制刷新时重扫。
        /// </summary>
        /// <param name="repoPath">仓库根路径。</param>
        /// <param name="forceRefresh">是否强制绕过缓存。</param>
        /// <returns>Git 与 SVN 子仓库目录。</returns>
        private (List<string> GitDirs, List<string> SvnDirs) GetSubRepositories(string repoPath, bool forceRefresh)
        {
            var normalized = repoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!forceRefresh &&
                _subRepoScanCache.TryGetValue(normalized, out var cached) &&
                DateTime.Now - cached.Timestamp < SubRepoScanCacheTtl)
            {
                return (cached.GitDirs, cached.SvnDirs);
            }

            var (gitDirs, svnDirs) = DiscoverSubRepositories(repoPath);
            _subRepoScanCache[normalized] = new SubRepoScanCache
            {
                Timestamp = DateTime.Now,
                GitDirs = gitDirs,
                SvnDirs = svnDirs,
            };
            return (gitDirs, svnDirs);
        }

        /// <summary>
        /// 单趟 2 层目录遍历同时发现 Git 与 SVN 子仓库（原先两趟独立遍历，IO 直接减半）。
        /// </summary>
        /// <param name="rootPath">仓库根路径。</param>
        /// <returns>Git 与 SVN 子仓库目录列表。</returns>
        private static (List<string> GitDirs, List<string> SvnDirs) DiscoverSubRepositories(string rootPath)
        {
            var gitDirs = new List<string>();
            var svnDirs = new List<string>();
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
                        gitDirs.Add(dir);
                        continue;
                    }

                    if (Directory.Exists(Path.Combine(dir, ".svn")))
                    {
                        svnDirs.Add(dir);
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
                                gitDirs.Add(subDir);
                            }
                            else if (Directory.Exists(Path.Combine(subDir, ".svn")))
                            {
                                svnDirs.Add(subDir);
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

            return (gitDirs, svnDirs);
        }

        private async Task<RepositoryVcsSnapshot> BuildSnapshotAsync(CodeRepository repo, CancellationToken cancellationToken, bool includeRemoteStatus, bool forceRefresh)
        {
            var snapshot = new RepositoryVcsSnapshot
            {
                GitAheadCount = repo?.GitAheadCount ?? 0,
                GitBehindCount = repo?.GitBehindCount ?? 0,
                SvnRemoteUpdateCount = repo?.SvnRemoteUpdateCount ?? 0,
            };
            var hasGit = Directory.Exists(Path.Combine(repo.Path, ".git")) || File.Exists(Path.Combine(repo.Path, ".git"));
            var hasSvn = Directory.Exists(Path.Combine(repo.Path, ".svn"));
            var (gitSubDirs, svnSubDirs) = await Task.Run(() => GetSubRepositories(repo.Path, forceRefresh), cancellationToken);
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
                await RefreshGitStatusAsync(snapshot, repo.Path, cancellationToken, includeRemoteStatus);
            }

            if (hasSvn && !hasGit)
            {
                await RefreshSvnStatusAsync(snapshot, repo.Path, cancellationToken, includeRemoteStatus);
            }

            if (gitSubDirs.Count > 0 || svnSubDirs.Count > 0)
            {
                await RefreshSubRepositoriesAsync(snapshot, repo.Path, gitSubDirs, svnSubDirs, repo.SubRepositories, cancellationToken, includeRemoteStatus);
            }

            snapshot.VcsStatus = CalculateOverallStatus(snapshot);
            snapshot.LastStatusRefresh = DateTime.Now;
            return snapshot;
        }

        private static async Task RefreshGitStatusAsync(RepositoryVcsSnapshot snapshot, string repoPath, CancellationToken ct, bool includeRemoteStatus)
        {
            var gitStatus = await ReadGitStatusAsync(repoPath, "Git 根仓库", ct, includeRemoteStatus);
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

        private static async Task<GitStatusInfo> ReadGitStatusAsync(string repoPath, string groupName, CancellationToken ct, bool includeRemoteStatus)
        {
            var info = new GitStatusInfo();

            if (includeRemoteStatus)
            {
                // 先 fetch 使 ahead/behind 反映最新远端；fetch 与后续解析保持原有先后依赖
                await RunCommandAsync("git", "fetch --prune --quiet", repoPath, ct);
            }

            // 单命令合并：分支 + ahead/behind + 变更清单一次取回
            // （原实现为 branch/rev-list/status 3-4 个串行进程，每轮轮询每仓库都要重复付出进程启动开销）
            var statusResult = await RunCommandAsync(
                "git",
                "-c core.quotepath=false status --porcelain --branch --untracked-files=no",
                repoPath,
                ct);
            if (statusResult.ExitCode != 0)
            {
                info.HasError = true;
                return info;
            }

            var lines = SplitLines(statusResult.Output);
            var firstLine = lines.FirstOrDefault() ?? string.Empty;
            await ParsePorcelainBranchHeaderAsync(firstLine, repoPath, info, ct);

            var added = 0;
            var modified = 0;
            var deleted = 0;
            var staged = 0;
            var hasConflict = false;

            foreach (var line in lines.Skip(1))
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

        /// <summary>
        /// 解析 `status --porcelain --branch` 首行，填充分支名与 ahead/behind。
        /// 形态：`## main`（无 upstream）、`## main...origin/main`、`## main...origin/main [ahead 2, behind 1]`、`## HEAD (no branch)`（detached）。
        /// 解析失败（如异常输出）回退为单独探分支，语义与旧实现等价。
        /// </summary>
        private static async Task ParsePorcelainBranchHeaderAsync(string firstLine, string repoPath, GitStatusInfo info, CancellationToken ct)
        {
            if (!firstLine.StartsWith("## ", StringComparison.Ordinal))
            {
                var branchResult = await RunCommandAsync("git", "branch --show-current", repoPath, ct);
                if (branchResult.ExitCode == 0)
                {
                    info.Branch = branchResult.Output.Trim();
                }

                return;
            }

            var rest = firstLine.Substring(3);
            var bracket = rest.IndexOf('[');
            var tracking = (bracket >= 0 ? rest.Substring(0, bracket) : rest).Trim();

            if (bracket >= 0)
            {
                // `[ahead 2, behind 1]` / `[ahead 2]` / `[behind 1]` / `[gone]`：逐段解析数字
                var markers = rest.Substring(bracket).Trim('[', ']').Split(',');
                foreach (var marker in markers)
                {
                    var m = marker.Trim();
                    if (m.StartsWith("ahead ", StringComparison.OrdinalIgnoreCase) && int.TryParse(m.Substring(6), out var ahead))
                    {
                        info.AheadCount = ahead;
                    }
                    else if (m.StartsWith("behind ", StringComparison.OrdinalIgnoreCase) && int.TryParse(m.Substring(7), out var behind))
                    {
                        info.BehindCount = behind;
                    }
                }
            }

            var localName = tracking;
            var dots = tracking.IndexOf("...", StringComparison.Ordinal);
            if (dots >= 0)
            {
                localName = tracking.Substring(0, dots);
            }

            if (string.Equals(localName, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                // detached：保持旧实现的 `(短哈希)` 展示行为
                var headResult = await RunCommandAsync("git", "rev-parse --short HEAD", repoPath, ct);
                info.Branch = headResult.ExitCode == 0 ? $"({headResult.Output.Trim()})" : "(detached)";
            }
            else
            {
                info.Branch = localName;
            }
        }

        private static async Task RefreshSvnStatusAsync(RepositoryVcsSnapshot snapshot, string svnPath, CancellationToken ct, bool includeRemoteStatus)
        {
            var infoResult = await RunCommandAsync("svn", "info --show-item revision", svnPath, ct);
            if (infoResult.ExitCode == 0 && int.TryParse(infoResult.Output.Trim(), out var rev))
            {
                snapshot.SvnRevision = rev;
            }

            if (includeRemoteStatus)
            {
                snapshot.SvnRemoteUpdateCount = await ReadSvnRemoteUpdateCountAsync(svnPath, ct);
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

        private static bool HasGitMetadata(string path)
        {
            return Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));
        }

        private static async Task RefreshSubRepositoriesAsync(
            RepositoryVcsSnapshot snapshot,
            string repoPath,
            IEnumerable<string> gitDirs,
            IEnumerable<string> svnDirs,
            IEnumerable<SubRepository> cachedSubRepositories,
            CancellationToken ct,
            bool includeRemoteStatus)
        {
            var cachedSubRepositoryMap = (cachedSubRepositories ?? Enumerable.Empty<SubRepository>())
                .Where(sub => sub != null && !string.IsNullOrWhiteSpace(sub.RelativePath))
                .GroupBy(sub => $"{sub.VcsType}:{sub.RelativePath}", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            // Git 子仓库并行采集（共享限流 8，避免根仓库并发 × 子仓库并发叠加成进程风暴）
            var orderedGitDirs = (gitDirs ?? Enumerable.Empty<string>())
                .OrderBy(path => GetRelativePath(repoPath, path), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var gitResults = await Task.WhenAll(orderedGitDirs.Select(async gitDir =>
            {
                ct.ThrowIfCancellationRequested();

                await SubRepoProcessGate.WaitAsync(ct);
                try
                {
                    var relativePath = GetRelativePath(repoPath, gitDir);
                    var cachedSubRepository = FindCachedSubRepository(cachedSubRepositoryMap, VcsType.Git, relativePath);
                    var subRepo = new SubRepository
                    {
                        RelativePath = relativePath,
                        VcsType = VcsType.Git,
                        Status = VcsStatus.Unknown,
                        GitAheadCount = cachedSubRepository?.GitAheadCount ?? 0,
                        GitBehindCount = cachedSubRepository?.GitBehindCount ?? 0,
                    };

                    var gitStatus = await ReadGitStatusAsync(gitDir, $"Git 子仓库/{relativePath}", ct, includeRemoteStatus);
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
                    return subRepo;
                }
                finally
                {
                    SubRepoProcessGate.Release();
                }
            }));

            foreach (var subRepo in gitResults.Where(sub => sub != null))
            {
                snapshot.SubRepositories.Add(subRepo);
            }

            // SVN 子仓库并行采集
            var svnResults = await Task.WhenAll((svnDirs ?? Enumerable.Empty<string>()).Select(async svnDir =>
            {
                ct.ThrowIfCancellationRequested();

                await SubRepoProcessGate.WaitAsync(ct);
                try
                {
                    var subRepo = new SubRepository
                    {
                        RelativePath = GetRelativePath(repoPath, svnDir),
                        VcsType = VcsType.Svn,
                        Status = VcsStatus.Unknown,
                    };
                    var cachedSubRepository = FindCachedSubRepository(cachedSubRepositoryMap, VcsType.Svn, subRepo.RelativePath);
                    subRepo.SvnRemoteUpdateCount = cachedSubRepository?.SvnRemoteUpdateCount ?? 0;

                    var infoResult = await RunCommandAsync("svn", "info --show-item revision", svnDir, ct);
                    if (infoResult.ExitCode == 0 && int.TryParse(infoResult.Output.Trim(), out var rev))
                    {
                        subRepo.Revision = rev;
                    }

                    if (includeRemoteStatus)
                    {
                        subRepo.SvnRemoteUpdateCount = await ReadSvnRemoteUpdateCountAsync(svnDir, ct);
                    }

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

                    return subRepo;
                }
                finally
                {
                    SubRepoProcessGate.Release();
                }
            }));

            foreach (var subRepo in svnResults.Where(sub => sub != null))
            {
                snapshot.SubRepositories.Add(subRepo);
            }
        }

        private static SubRepository FindCachedSubRepository(IDictionary<string, SubRepository> cachedSubRepositories, VcsType vcsType, string relativePath)
        {
            if (cachedSubRepositories == null || string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            cachedSubRepositories.TryGetValue($"{vcsType}:{relativePath}", out var subRepository);
            return subRepository;
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
            var (gitSubDirs, svnSubDirs) = DiscoverSubRepositories(repoPath);
            var hasGitSubDirs = gitSubDirs.Count > 0;
            var hasSvnSubDirs = svnSubDirs.Count > 0;
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
