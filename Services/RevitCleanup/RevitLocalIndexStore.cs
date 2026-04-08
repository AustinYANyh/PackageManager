using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using IOPath = System.IO.Path;

namespace PackageManager.Services.RevitCleanup
{
    internal sealed class RevitLocalIndexStore
    {
        private readonly object gate = new object();
        private readonly string indexDirectoryPath;
        private readonly string manifestFilePath;
        private readonly JsonSerializerSettings jsonSettings;
        private RevitLocalIndexManifest manifest;

        public RevitLocalIndexStore()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            indexDirectoryPath = IOPath.Combine(appDataPath, "PackageManager", "RevitCleanupIndex");
            manifestFilePath = IOPath.Combine(indexDirectoryPath, "manifest.json");
            jsonSettings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore
            };
        }

        public RevitIndexedRootMetadata GetRootMetadata(string rootPath)
        {
            var normalizedRootPath = RevitCleanupPathUtility.NormalizePath(rootPath);
            if (string.IsNullOrWhiteSpace(normalizedRootPath))
            {
                return null;
            }

            lock (gate)
            {
                EnsureLoaded();
                manifest.Roots.TryGetValue(normalizedRootPath, out var metadata);
                return metadata;
            }
        }

        public RevitRootIndexSnapshot LoadSnapshot(string rootPath)
        {
            var normalizedRootPath = RevitCleanupPathUtility.NormalizePath(rootPath);
            if (string.IsNullOrWhiteSpace(normalizedRootPath))
            {
                return null;
            }

            lock (gate)
            {
                EnsureLoaded();
                if (!manifest.Roots.TryGetValue(normalizedRootPath, out var metadata))
                {
                    return null;
                }

                var snapshotPath = GetSnapshotFilePath(metadata.StorageKey);
                if (!File.Exists(snapshotPath))
                {
                    return null;
                }

                try
                {
                    var json = File.ReadAllText(snapshotPath, Encoding.UTF8);
                    return JsonConvert.DeserializeObject<RevitRootIndexSnapshot>(json, jsonSettings);
                }
                catch (Exception ex)
                {
                    LoggingService.LogError(ex, $"加载 Revit 本地索引快照失败：{snapshotPath}");
                    return null;
                }
            }
        }

        public void SaveSnapshot(RevitRootIndexSnapshot snapshot, bool watcherEnabled)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.RootPath))
            {
                return;
            }

            lock (gate)
            {
                EnsureLoaded();
                Directory.CreateDirectory(indexDirectoryPath);

                var normalizedRootPath = RevitCleanupPathUtility.NormalizePath(snapshot.RootPath);
                var storageKey = manifest.Roots.TryGetValue(normalizedRootPath, out var existingMetadata) && !string.IsNullOrWhiteSpace(existingMetadata.StorageKey)
                    ? existingMetadata.StorageKey
                    : CreateStorageKey(normalizedRootPath);

                snapshot.RootPath = normalizedRootPath;
                snapshot.Files = (snapshot.Files ?? new List<RevitIndexedFileRecord>())
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.FullPath))
                    .GroupBy(item => RevitCleanupPathUtility.NormalizePath(item.FullPath), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(item => item.ModifiedTimeUtc).First())
                    .OrderByDescending(item => item.ModifiedTimeUtc)
                    .ToList();

                var snapshotPath = GetSnapshotFilePath(storageKey);
                WriteJsonAtomic(snapshotPath, snapshot);

                manifest.Roots[normalizedRootPath] = new RevitIndexedRootMetadata
                {
                    RootPath = normalizedRootPath,
                    RootDisplayName = snapshot.RootDisplayName,
                    StorageKey = storageKey,
                    LastIndexedUtc = snapshot.LastIndexedUtc,
                    LastSyncUtc = snapshot.LastSyncUtc,
                    WatcherEnabled = watcherEnabled
                };

                SaveManifest();
            }
        }

        public void RemoveFiles(IEnumerable<string> filePaths)
        {
            var normalizedPaths = (filePaths ?? Array.Empty<string>())
                .Select(RevitCleanupPathUtility.NormalizePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalizedPaths.Count == 0)
            {
                return;
            }

            lock (gate)
            {
                EnsureLoaded();
                var groups = normalizedPaths.GroupBy(GetRootPathForFile, StringComparer.OrdinalIgnoreCase)
                                            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                                            .ToList();
                foreach (var group in groups)
                {
                    if (!manifest.Roots.TryGetValue(group.Key, out var metadata))
                    {
                        continue;
                    }

                    var snapshotPath = GetSnapshotFilePath(metadata.StorageKey);
                    if (!File.Exists(snapshotPath))
                    {
                        continue;
                    }

                    RevitRootIndexSnapshot snapshot;
                    try
                    {
                        snapshot = JsonConvert.DeserializeObject<RevitRootIndexSnapshot>(File.ReadAllText(snapshotPath, Encoding.UTF8), jsonSettings);
                    }
                    catch (Exception ex)
                    {
                        LoggingService.LogError(ex, $"更新 Revit 本地索引失败：{snapshotPath}");
                        continue;
                    }

                    if (snapshot?.Files == null)
                    {
                        continue;
                    }

                    var removed = new HashSet<string>(group, StringComparer.OrdinalIgnoreCase);
                    snapshot.Files = snapshot.Files.Where(item => !removed.Contains(RevitCleanupPathUtility.NormalizePath(item.FullPath))).ToList();
                    snapshot.LastSyncUtc = DateTime.UtcNow;
                    WriteJsonAtomic(snapshotPath, snapshot);

                    metadata.LastSyncUtc = snapshot.LastSyncUtc;
                    metadata.LastIndexedUtc = snapshot.LastIndexedUtc;
                    metadata.RootDisplayName = snapshot.RootDisplayName;
                }

                SaveManifest();
            }
        }

        private string GetRootPathForFile(string filePath)
        {
            foreach (var rootPath in manifest.Roots.Keys.OrderByDescending(item => item.Length))
            {
                if (RevitCleanupPathUtility.IsPathUnderRoot(filePath, rootPath))
                {
                    return rootPath;
                }
            }

            return null;
        }

        private void EnsureLoaded()
        {
            if (manifest != null)
            {
                return;
            }

            if (!File.Exists(manifestFilePath))
            {
                manifest = new RevitLocalIndexManifest();
                return;
            }

            try
            {
                var json = File.ReadAllText(manifestFilePath, Encoding.UTF8);
                manifest = JsonConvert.DeserializeObject<RevitLocalIndexManifest>(json, jsonSettings) ?? new RevitLocalIndexManifest();
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"加载 Revit 本地索引清单失败：{manifestFilePath}");
                manifest = new RevitLocalIndexManifest();
            }
        }

        private void SaveManifest()
        {
            Directory.CreateDirectory(indexDirectoryPath);
            WriteJsonAtomic(manifestFilePath, manifest ?? new RevitLocalIndexManifest());
        }

        private string GetSnapshotFilePath(string storageKey)
        {
            return IOPath.Combine(indexDirectoryPath, storageKey + ".json");
        }

        private string CreateStorageKey(string rootPath)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(rootPath);
                var hash = sha.ComputeHash(bytes);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var item in hash)
                {
                    builder.Append(item.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private void WriteJsonAtomic(string path, object value)
        {
            var tempPath = path + ".tmp";
            var json = JsonConvert.SerializeObject(value, jsonSettings);
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }
    }

    internal sealed class RevitLocalIndexManifest
    {
        public Dictionary<string, RevitIndexedRootMetadata> Roots { get; set; } = new Dictionary<string, RevitIndexedRootMetadata>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class RevitIndexedRootMetadata
    {
        public string RootPath { get; set; }

        public string RootDisplayName { get; set; }

        public string StorageKey { get; set; }

        public DateTime LastIndexedUtc { get; set; }

        public DateTime LastSyncUtc { get; set; }

        public bool WatcherEnabled { get; set; }
    }

    internal sealed class RevitRootIndexSnapshot
    {
        public string RootPath { get; set; }

        public string RootDisplayName { get; set; }

        public DateTime LastIndexedUtc { get; set; }

        public DateTime LastSyncUtc { get; set; }

        public List<RevitIndexedFileRecord> Files { get; set; } = new List<RevitIndexedFileRecord>();
    }

    internal sealed class RevitIndexedFileRecord
    {
        public string RootPath { get; set; }

        public string RootDisplayName { get; set; }

        public string FullPath { get; set; }

        public string FileName { get; set; }

        public string Extension { get; set; }

        public long SizeBytes { get; set; }

        public DateTime ModifiedTimeUtc { get; set; }
    }
}
