using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IOPath = System.IO.Path;

namespace PackageManager.Services.RevitCleanup
{
    internal sealed class LocalIndexProvider
    {
        private static readonly TimeSpan RebuildIntervalWithoutWatcher = TimeSpan.FromHours(12);

        private readonly RevitLocalIndexStore store = new RevitLocalIndexStore();
        private readonly ConcurrentDictionary<string, RootWatcherState> watchers = new ConcurrentDictionary<string, RootWatcherState>(StringComparer.OrdinalIgnoreCase);

        public async Task EnsureIndexReadyAsync(RevitFileQueryOptions options, IProgress<RevitFileQueryProgress> progress, CancellationToken cancellationToken)
        {
            await QueryAsync(options, progress, cancellationToken).ConfigureAwait(false);
        }

        public Task<RevitFileQueryResult> QueryAsync(RevitFileQueryOptions options, IProgress<RevitFileQueryProgress> progress, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                var files = new List<RevitIndexedFileInfo>();
                var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var builtAnyRoot = false;

                foreach (var root in options.Roots)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!Directory.Exists(root.RootPath))
                    {
                        LoggingService.LogWarning($"Revit 本地索引跳过不存在目录：{root.RootPath}");
                        continue;
                    }

                    var watcherState = EnsureWatcher(root, options.Extensions);
                    var snapshot = store.LoadSnapshot(root.RootPath);
                    var metadata = store.GetRootMetadata(root.RootPath);

                    if (options.ForceRebuildLocalIndex)
                    {
                        progress?.Report(new RevitFileQueryProgress
                        {
                            Message = $"正在重建索引... {root.DisplayName}：{root.RootPath}"
                        });
                        LoggingService.LogInfo($"Revit 本地索引重建开始：{root.DisplayName} -> {root.RootPath}");
                        snapshot = BuildSnapshot(root, options.Extensions, cancellationToken);
                        snapshot.LastSyncUtc = snapshot.LastIndexedUtc;
                        store.SaveSnapshot(snapshot, watcherState != null && watcherState.Enabled);
                        watcherState?.ResetPendingChanges();
                        builtAnyRoot = true;
                        LoggingService.LogInfo($"Revit 本地索引重建完成：{root.DisplayName} -> {root.RootPath}，Files={snapshot.Files.Count}");
                    }
                    else if (snapshot == null)
                    {
                        progress?.Report(new RevitFileQueryProgress
                        {
                            Message = $"正在建立索引... {root.DisplayName}：{root.RootPath}"
                        });
                        LoggingService.LogInfo($"Revit 本地索引首次建库开始：{root.DisplayName} -> {root.RootPath}");
                        snapshot = BuildSnapshot(root, options.Extensions, cancellationToken);
                        snapshot.LastSyncUtc = snapshot.LastIndexedUtc;
                        store.SaveSnapshot(snapshot, watcherState != null && watcherState.Enabled);
                        watcherState?.ResetPendingChanges();
                        builtAnyRoot = true;
                        LoggingService.LogInfo($"Revit 本地索引首次建库完成：{root.DisplayName} -> {root.RootPath}，Files={snapshot.Files.Count}");
                    }
                    else
                    {
                        snapshot.RootDisplayName = root.DisplayName;
                        snapshot.RootPath = root.RootPath;
                        var updated = false;

                        if ((watcherState != null) && watcherState.TryTakeRequiresFullResync())
                        {
                            progress?.Report(new RevitFileQueryProgress
                            {
                                Message = $"正在增量更新索引... {root.DisplayName}：{root.RootPath}"
                            });
                            LoggingService.LogInfo($"Revit 本地索引整根增量更新开始：{root.DisplayName} -> {root.RootPath}");
                            snapshot = BuildSnapshot(root, options.Extensions, cancellationToken);
                            snapshot.LastSyncUtc = snapshot.LastIndexedUtc;
                            updated = true;
                            LoggingService.LogInfo($"Revit 本地索引整根增量更新完成：{root.DisplayName} -> {root.RootPath}，Files={snapshot.Files.Count}");
                        }
                        else if ((watcherState != null) && watcherState.TryDrainChanges(out var changes) && changes.Count > 0)
                        {
                            progress?.Report(new RevitFileQueryProgress
                            {
                                Message = $"正在同步索引变更... {root.DisplayName}：{changes.Count} 项"
                            });
                            LoggingService.LogInfo($"Revit 本地索引变更同步开始：{root.DisplayName} -> {root.RootPath}，Changes={changes.Count}");
                            ApplyChanges(snapshot, changes, options.Extensions, cancellationToken);
                            snapshot.LastSyncUtc = DateTime.UtcNow;
                            updated = true;
                            LoggingService.LogInfo($"Revit 本地索引变更同步完成：{root.DisplayName} -> {root.RootPath}，Files={snapshot.Files.Count}");
                        }
                        else if ((watcherState == null || !watcherState.Enabled) && ShouldRebuild(metadata))
                        {
                            progress?.Report(new RevitFileQueryProgress
                            {
                                Message = $"正在增量更新索引... {root.DisplayName}：{root.RootPath}"
                            });
                            LoggingService.LogInfo($"Revit 本地索引周期校验开始：{root.DisplayName} -> {root.RootPath}");
                            snapshot = BuildSnapshot(root, options.Extensions, cancellationToken);
                            snapshot.LastSyncUtc = snapshot.LastIndexedUtc;
                            updated = true;
                            LoggingService.LogInfo($"Revit 本地索引周期校验完成：{root.DisplayName} -> {root.RootPath}，Files={snapshot.Files.Count}");
                        }

                        if (updated)
                        {
                            store.SaveSnapshot(snapshot, watcherState != null && watcherState.Enabled);
                        }
                    }

                    var liveRecords = MaterializeLiveRecords(snapshot, root, options.Extensions);
                    if (NeedPersistValidatedSnapshot(snapshot.Files, liveRecords))
                    {
                        snapshot.Files = liveRecords;
                        snapshot.LastSyncUtc = DateTime.UtcNow;
                        store.SaveSnapshot(snapshot, watcherState != null && watcherState.Enabled);
                    }

                    foreach (var record in liveRecords.OrderByDescending(item => item.ModifiedTimeUtc))
                    {
                        var normalizedPath = RevitCleanupPathUtility.NormalizePath(record.FullPath);
                        if (!dedup.Add(normalizedPath))
                        {
                            continue;
                        }

                        files.Add(new RevitIndexedFileInfo
                        {
                            FullPath = normalizedPath,
                            FileName = record.FileName,
                            SizeBytes = record.SizeBytes,
                            ModifiedTimeUtc = record.ModifiedTimeUtc,
                            RootPath = record.RootPath,
                            RootDisplayName = record.RootDisplayName,
                            SourceKind = builtAnyRoot ? RevitFileQuerySourceKind.FirstBuild : RevitFileQuerySourceKind.LocalIndex
                        });
                    }
                }

                return new RevitFileQueryResult
                {
                    SourceKind = builtAnyRoot ? RevitFileQuerySourceKind.FirstBuild : RevitFileQuerySourceKind.LocalIndex,
                    ProviderDisplayText = builtAnyRoot ? "首次建库" : "本地索引",
                    Files = files.OrderByDescending(item => item.ModifiedTimeUtc).ToList()
                };
            }, cancellationToken);
        }

        public Task RemoveFilesFromIndexAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                store.RemoveFiles(filePaths);
            }, cancellationToken);
        }

        private bool ShouldRebuild(RevitIndexedRootMetadata metadata)
        {
            if (metadata == null)
            {
                return true;
            }

            var referenceTime = metadata.LastSyncUtc == DateTime.MinValue ? metadata.LastIndexedUtc : metadata.LastSyncUtc;
            if (referenceTime == DateTime.MinValue)
            {
                return true;
            }

            return (DateTime.UtcNow - referenceTime) >= RebuildIntervalWithoutWatcher;
        }

        private List<RevitIndexedFileRecord> MaterializeLiveRecords(RevitRootIndexSnapshot snapshot, RevitFileQueryRoot root, IEnumerable<string> extensions)
        {
            var records = new List<RevitIndexedFileRecord>();
            foreach (var record in snapshot.Files ?? Enumerable.Empty<RevitIndexedFileRecord>())
            {
                var normalizedPath = RevitCleanupPathUtility.NormalizePath(record.FullPath);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    continue;
                }

                if (!RevitCleanupPathUtility.IsPathUnderRoot(normalizedPath, root.RootPath))
                {
                    continue;
                }

                if (!RevitCleanupPathUtility.IsIndexedExtension(normalizedPath, extensions))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(normalizedPath);
                    if (!info.Exists)
                    {
                        continue;
                    }

                    records.Add(new RevitIndexedFileRecord
                    {
                        RootPath = root.RootPath,
                        RootDisplayName = root.DisplayName,
                        FullPath = normalizedPath,
                        FileName = info.Name,
                        Extension = RevitCleanupPathUtility.NormalizeExtension(info.Extension),
                        SizeBytes = info.Length,
                        ModifiedTimeUtc = info.LastWriteTimeUtc
                    });
                }
                catch
                {
                }
            }

            return records.OrderByDescending(item => item.ModifiedTimeUtc).ToList();
        }

        private bool AreSameRecord(RevitIndexedFileRecord left, RevitIndexedFileRecord right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }

            return string.Equals(RevitCleanupPathUtility.NormalizePath(left.FullPath), RevitCleanupPathUtility.NormalizePath(right.FullPath), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.RootPath, right.RootPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.RootDisplayName, right.RootDisplayName, StringComparison.Ordinal) &&
                   left.SizeBytes == right.SizeBytes &&
                   left.ModifiedTimeUtc == right.ModifiedTimeUtc;
        }

        private bool NeedPersistValidatedSnapshot(IReadOnlyList<RevitIndexedFileRecord> oldRecords, IReadOnlyList<RevitIndexedFileRecord> newRecords)
        {
            oldRecords = oldRecords ?? Array.Empty<RevitIndexedFileRecord>();
            newRecords = newRecords ?? Array.Empty<RevitIndexedFileRecord>();

            if (oldRecords.Count != newRecords.Count)
            {
                return true;
            }

            for (var index = 0; index < oldRecords.Count; index++)
            {
                if (!AreSameRecord(oldRecords[index], newRecords[index]))
                {
                    return true;
                }
            }

            return false;
        }

        private RootWatcherState EnsureWatcher(RevitFileQueryRoot root, IEnumerable<string> extensions)
        {
            return watchers.GetOrAdd(root.RootPath, _ =>
            {
                try
                {
                    var watcher = new FileSystemWatcher(root.RootPath)
                    {
                        IncludeSubdirectories = true,
                        Filter = "*.*",
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                        EnableRaisingEvents = true,
                        InternalBufferSize = 64 * 1024
                    };

                    var state = new RootWatcherState(watcher, root.RootPath, extensions);
                    watcher.Created += state.OnCreated;
                    watcher.Changed += state.OnChanged;
                    watcher.Deleted += state.OnDeleted;
                    watcher.Renamed += state.OnRenamed;
                    watcher.Error += state.OnError;
                    return state;
                }
                catch (Exception ex)
                {
                    LoggingService.LogWarning($"Revit 本地索引无法启用目录监听，已退化为周期校验：{root.RootPath}，{ex.Message}");
                    return new RootWatcherState(null, root.RootPath, extensions);
                }
            });
        }

        private RevitRootIndexSnapshot BuildSnapshot(RevitFileQueryRoot root, IEnumerable<string> extensions, CancellationToken cancellationToken)
        {
            var files = new List<RevitIndexedFileRecord>();
            var directories = new Stack<string>();
            directories.Push(root.RootPath);

            while (directories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentDirectory = directories.Pop();
                foreach (var file in EnumerateFilesSafe(currentDirectory, extensions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var descriptor = CreateRecord(file, root);
                    if (descriptor != null)
                    {
                        files.Add(descriptor);
                    }
                }

                foreach (var subDirectory in EnumerateDirectoriesSafe(currentDirectory))
                {
                    directories.Push(subDirectory);
                }
            }

            return new RevitRootIndexSnapshot
            {
                RootPath = root.RootPath,
                RootDisplayName = root.DisplayName,
                LastIndexedUtc = DateTime.UtcNow,
                Files = files.OrderByDescending(item => item.ModifiedTimeUtc).ToList()
            };
        }

        private void ApplyChanges(RevitRootIndexSnapshot snapshot, IReadOnlyCollection<RevitIndexChange> changes, IEnumerable<string> extensions, CancellationToken cancellationToken)
        {
            var map = snapshot.Files.ToDictionary(item => RevitCleanupPathUtility.NormalizePath(item.FullPath), StringComparer.OrdinalIgnoreCase);

            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (change.IsDirectory)
                {
                    RemoveSubtreeEntries(map, RevitCleanupPathUtility.NormalizePath(change.PreviousPath));
                    var currentDirectoryPath = RevitCleanupPathUtility.NormalizePath(change.FilePath);
                    RemoveSubtreeEntries(map, currentDirectoryPath);

                    if (!string.IsNullOrWhiteSpace(currentDirectoryPath) && Directory.Exists(currentDirectoryPath))
                    {
                        var directoryRoot = new RevitFileQueryRoot
                        {
                            RootPath = snapshot.RootPath,
                            DisplayName = snapshot.RootDisplayName
                        };

                        foreach (var subtreeRecord in BuildRecordsForDirectory(directoryRoot, currentDirectoryPath, extensions, cancellationToken))
                        {
                            map[RevitCleanupPathUtility.NormalizePath(subtreeRecord.FullPath)] = subtreeRecord;
                        }
                    }

                    continue;
                }

                var normalizedOldPath = RevitCleanupPathUtility.NormalizePath(change.PreviousPath);
                if (!string.IsNullOrWhiteSpace(normalizedOldPath))
                {
                    map.Remove(normalizedOldPath);
                }

                var normalizedPath = RevitCleanupPathUtility.NormalizePath(change.FilePath);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    continue;
                }

                if (!RevitCleanupPathUtility.IsIndexedExtension(normalizedPath, extensions))
                {
                    map.Remove(normalizedPath);
                    continue;
                }

                if (!File.Exists(normalizedPath))
                {
                    map.Remove(normalizedPath);
                    continue;
                }

                var root = new RevitFileQueryRoot
                {
                    RootPath = snapshot.RootPath,
                    DisplayName = snapshot.RootDisplayName
                };
                var record = CreateRecord(normalizedPath, root);
                if (record != null)
                {
                    map[normalizedPath] = record;
                }
            }

            snapshot.Files = map.Values.OrderByDescending(item => item.ModifiedTimeUtc).ToList();
        }

        private IEnumerable<string> EnumerateFilesSafe(string directory, IEnumerable<string> extensions)
        {
            foreach (var extension in (extensions ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                IEnumerable<string> files = Array.Empty<string>();
                try
                {
                    files = Directory.EnumerateFiles(directory, "*" + extension, SearchOption.TopDirectoryOnly);
                }
                catch
                {
                }

                foreach (var file in files)
                {
                    yield return file;
                }
            }
        }

        private IEnumerable<string> EnumerateDirectoriesSafe(string directory)
        {
            try
            {
                return Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private RevitIndexedFileRecord CreateRecord(string filePath, RevitFileQueryRoot root)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists)
                {
                    return null;
                }

                return new RevitIndexedFileRecord
                {
                    RootPath = root.RootPath,
                    RootDisplayName = root.DisplayName,
                    FullPath = RevitCleanupPathUtility.NormalizePath(info.FullName),
                    FileName = info.Name,
                    Extension = RevitCleanupPathUtility.NormalizeExtension(info.Extension),
                    SizeBytes = info.Length,
                    ModifiedTimeUtc = info.LastWriteTimeUtc
                };
            }
            catch
            {
                return null;
            }
        }

        private IEnumerable<RevitIndexedFileRecord> BuildRecordsForDirectory(RevitFileQueryRoot root, string directoryPath, IEnumerable<string> extensions, CancellationToken cancellationToken)
        {
            var directories = new Stack<string>();
            directories.Push(directoryPath);

            while (directories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentDirectory = directories.Pop();

                foreach (var file in EnumerateFilesSafe(currentDirectory, extensions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var record = CreateRecord(file, root);
                    if (record != null)
                    {
                        yield return record;
                    }
                }

                foreach (var subDirectory in EnumerateDirectoriesSafe(currentDirectory))
                {
                    directories.Push(subDirectory);
                }
            }
        }

        private void RemoveSubtreeEntries(IDictionary<string, RevitIndexedFileRecord> map, string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return;
            }

            foreach (var existingPath in map.Keys.Where(path =>
                         RevitCleanupPathUtility.IsPathUnderRoot(path, directoryPath))
                     .ToList())
            {
                map.Remove(existingPath);
            }
        }

        private sealed class RootWatcherState
        {
            private readonly ConcurrentQueue<RevitIndexChange> pendingChanges = new ConcurrentQueue<RevitIndexChange>();
            private readonly HashSet<string> extensions;
            private int requiresFullResync;

            public RootWatcherState(FileSystemWatcher watcher, string rootPath, IEnumerable<string> extensions)
            {
                Watcher = watcher;
                RootPath = rootPath;
                this.extensions = new HashSet<string>((extensions ?? Array.Empty<string>()).Select(RevitCleanupPathUtility.NormalizeExtension), StringComparer.OrdinalIgnoreCase);
            }

            public FileSystemWatcher Watcher { get; }

            public string RootPath { get; }

            public bool Enabled => Watcher != null;

            public bool TryTakeRequiresFullResync()
            {
                return Interlocked.Exchange(ref requiresFullResync, 0) == 1;
            }

            public bool TryDrainChanges(out List<RevitIndexChange> changes)
            {
                changes = new List<RevitIndexChange>();
                while (pendingChanges.TryDequeue(out var change))
                {
                    changes.Add(change);
                }

                return changes.Count > 0;
            }

            public void ResetPendingChanges()
            {
                while (pendingChanges.TryDequeue(out _))
                {
                }

                Interlocked.Exchange(ref requiresFullResync, 0);
            }

            public void OnCreated(object sender, FileSystemEventArgs e)
            {
                EnqueueFileEvent(e.ChangeType, e.FullPath);
            }

            public void OnChanged(object sender, FileSystemEventArgs e)
            {
                EnqueueFileEvent(e.ChangeType, e.FullPath);
            }

            public void OnDeleted(object sender, FileSystemEventArgs e)
            {
                EnqueueFileEvent(e.ChangeType, e.FullPath);
            }

            public void OnRenamed(object sender, RenamedEventArgs e)
            {
                EnqueueChange(new RevitIndexChange
                {
                    ChangeType = WatcherChangeTypes.Renamed,
                    FilePath = e.FullPath,
                    PreviousPath = e.OldFullPath,
                    IsDirectory = Directory.Exists(e.FullPath) || Directory.Exists(e.OldFullPath)
                });
            }

            public void OnError(object sender, ErrorEventArgs e)
            {
                Interlocked.Exchange(ref requiresFullResync, 1);
                LoggingService.LogWarning($"Revit 本地索引目录监听发生异常，后续将重建当前根目录索引：{RootPath}，{e.GetException()?.Message}");
            }

            private void EnqueueFileEvent(WatcherChangeTypes changeType, string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                var normalizedPath = RevitCleanupPathUtility.NormalizePath(path);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    return;
                }

                var normalizedExtension = RevitCleanupPathUtility.NormalizeExtension(IOPath.GetExtension(normalizedPath));
                if (!extensions.Contains(normalizedExtension))
                {
                    return;
                }

                EnqueueChange(new RevitIndexChange
                {
                    ChangeType = changeType,
                    FilePath = normalizedPath,
                    IsDirectory = false
                });
            }

            private void EnqueueChange(RevitIndexChange change)
            {
                pendingChanges.Enqueue(change);
            }
        }

        private sealed class RevitIndexChange
        {
            public WatcherChangeTypes ChangeType { get; set; }

            public string FilePath { get; set; }

            public string PreviousPath { get; set; }

            public bool IsDirectory { get; set; }
        }
    }
}
