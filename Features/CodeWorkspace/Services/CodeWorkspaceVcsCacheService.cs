using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PackageManager.Features.CodeWorkspace.Models;
using PackageManager.Services;

namespace PackageManager.Features.CodeWorkspace.Services
{
    public class CodeWorkspaceVcsCacheService
    {
        private readonly DataPersistenceService _dataPersistenceService;
        private readonly VcsStatusService _vcsStatusService;
        private readonly object _syncRoot = new object();
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, CodeRepository> _statusCache = new Dictionary<string, CodeRepository>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _warmupCts;
        private bool _warmupStarted;

        public CodeWorkspaceVcsCacheService(DataPersistenceService dataPersistenceService, VcsStatusService vcsStatusService)
        {
            _dataPersistenceService = dataPersistenceService ?? throw new ArgumentNullException(nameof(dataPersistenceService));
            _vcsStatusService = vcsStatusService ?? throw new ArgumentNullException(nameof(vcsStatusService));
        }

        public event EventHandler CacheUpdated;

        public bool IsRefreshRunning { get; private set; }

        public void StartWarmup()
        {
            lock (_syncRoot)
            {
                if (_warmupStarted)
                {
                    return;
                }

                _warmupStarted = true;
                _warmupCts = new CancellationTokenSource();
            }

            Task.Run(() => WarmupConfiguredRepositoriesAsync(_warmupCts.Token));
        }

        public void Cancel()
        {
            _warmupCts?.Cancel();
            _vcsStatusService.CancelRefresh();
        }

        public bool ApplyCachedStatus(CodeRepository repository)
        {
            if (repository == null)
            {
                return false;
            }

            var key = NormalizePath(repository.Path);
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            CodeRepository cached;
            lock (_syncRoot)
            {
                if (!_statusCache.TryGetValue(key, out cached))
                {
                    return false;
                }

                cached = CloneStatusOnly(cached);
            }

            repository.ApplyVcsStatusFrom(cached);
            return true;
        }

        public void ApplyCachedStatuses(IEnumerable<CodeRepository> repositories)
        {
            if (repositories == null)
            {
                return;
            }

            foreach (var repository in repositories)
            {
                ApplyCachedStatus(repository);
            }
        }

        public void UpdateCache(CodeRepository repository)
        {
            if (repository == null)
            {
                return;
            }

            var key = NormalizePath(repository.Path);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            lock (_syncRoot)
            {
                _statusCache[key] = CloneStatusOnly(repository);
            }

            RaiseCacheUpdated();
        }

        public void UpdateCache(IEnumerable<CodeRepository> repositories)
        {
            if (repositories == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                foreach (var repository in repositories)
                {
                    var key = NormalizePath(repository?.Path);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        _statusCache[key] = CloneStatusOnly(repository);
                    }
                }
            }

            RaiseCacheUpdated();
        }

        public async Task RefreshRepositoriesAsync(IEnumerable<CodeRepository> repositories, bool forceRefresh, CancellationToken cancellationToken = default, bool includeRemoteStatus = false)
        {
            var repositoryList = repositories?
                .Where(repo => repo != null && !string.IsNullOrWhiteSpace(repo.Path) && Directory.Exists(repo.Path))
                .ToList() ?? new List<CodeRepository>();

            if (repositoryList.Count == 0)
            {
                return;
            }

            await _refreshGate.WaitAsync(cancellationToken);
            IsRefreshRunning = true;
            try
            {
                await _vcsStatusService.RefreshAllAsync(repositoryList, cancellationToken, forceRefresh, includeRemoteStatus);
                UpdateCache(repositoryList);
            }
            finally
            {
                IsRefreshRunning = false;
                _refreshGate.Release();
            }
        }

        private async Task WarmupConfiguredRepositoriesAsync(CancellationToken cancellationToken)
        {
            await RefreshConfiguredRepositoriesAsync(forceRefresh: true, cancellationToken: cancellationToken, includeRemoteStatus: false);
            await RefreshConfiguredRepositoriesAsync(forceRefresh: true, cancellationToken: cancellationToken, includeRemoteStatus: true);
        }

        private async Task RefreshConfiguredRepositoriesAsync(bool forceRefresh, CancellationToken cancellationToken, bool includeRemoteStatus)
        {
            try
            {
                var settings = _dataPersistenceService.LoadSettings();
                var repositories = (settings.CodeRepositories ?? new List<CodeRepository>())
                    .Where(repo => repo != null && !string.IsNullOrWhiteSpace(repo.Path) && Directory.Exists(repo.Path))
                    .Select(repo => repo.Clone())
                    .ToList();

                ApplyCachedStatuses(repositories);
                await RefreshRepositoriesAsync(repositories, forceRefresh, cancellationToken, includeRemoteStatus);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "代码工作区 VCS 后台预热失败");
            }
        }

        private static CodeRepository CloneStatusOnly(CodeRepository source)
        {
            var clone = new CodeRepository
            {
                Name = source.Name,
                Path = source.Path,
                LastUsed = source.LastUsed,
                UsageCount = source.UsageCount,
                Note = source.Note,
                ProjectFiles = source.ProjectFiles == null ? new List<string>() : new List<string>(source.ProjectFiles),
            };
            clone.ApplyVcsStatusFrom(source);
            return clone;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim();
            }
        }

        private void RaiseCacheUpdated()
        {
            CacheUpdated?.Invoke(this, EventArgs.Empty);
        }
    }
}
