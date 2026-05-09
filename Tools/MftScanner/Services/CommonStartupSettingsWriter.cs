using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MftScanner.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MftScanner.Services
{
    internal sealed class CommonStartupSettingsWriter
    {
        public const string DefaultGroupName = "项目入口";

        private static readonly string[] PresetGroupNames =
        {
            "项目入口",
            "开发工具",
            "运维脚本",
            "目录快捷方式",
            "临时工具"
        };

        private static readonly object Gate = new object();

        private readonly string _settingsFilePath;
        private readonly string _commonStartupSettingsFilePath;
        private readonly string _commonStartupSettingsBackupFolderPath;
        private readonly JsonSerializerSettings _jsonSettings;

        public CommonStartupSettingsWriter()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appFolder = Path.Combine(appDataPath, "PackageManager");
            Directory.CreateDirectory(appFolder);

            _settingsFilePath = Path.Combine(appFolder, "settings.json");
            _commonStartupSettingsFilePath = Path.Combine(appFolder, "common_startup_settings.json");
            _commonStartupSettingsBackupFolderPath = Path.Combine(appFolder, "common_startup_settings_history");
            _jsonSettings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore
            };
        }

        public IList<string> LoadGroupNames()
        {
            lock (Gate)
            {
                var settings = NormalizeSettings(LoadSettings());
                return settings.CommonStartupGroups
                    .OrderBy(group => group.Order <= 0 ? int.MaxValue : group.Order)
                    .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();
            }
        }

        public AddStartupItemResult AddItem(string fullPath, bool isDirectory, string groupName)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return AddStartupItemResult.Failed("路径为空。");
            }

            if (isDirectory)
            {
                if (!Directory.Exists(fullPath))
                {
                    return AddStartupItemResult.Failed("文件夹不存在。");
                }
            }
            else if (!File.Exists(fullPath))
            {
                return AddStartupItemResult.Failed("文件不存在。");
            }

            lock (Gate)
            {
                var settings = NormalizeSettings(LoadSettings());
                var normalizedGroupName = NormalizeGroupName(groupName);
                EnsureGroupExists(settings, normalizedGroupName);

                var existing = settings.CommonStartupItems.FirstOrDefault(item =>
                    item != null
                    && !string.IsNullOrWhiteSpace(item.FullPath)
                    && string.Equals(item.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    return AddStartupItemResult.Existing(
                        string.IsNullOrWhiteSpace(existing.Name) ? ResolveItemName(fullPath, isDirectory) : existing.Name,
                        NormalizeGroupName(existing.GroupName));
                }

                var nextOrder = settings.CommonStartupItems
                    .Where(item => item != null && string.Equals(NormalizeGroupName(item.GroupName), normalizedGroupName, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Order)
                    .DefaultIfEmpty(0)
                    .Max() + 1;

                var itemToAdd = new CommonStartupItem
                {
                    Name = ResolveItemName(fullPath, isDirectory),
                    FullPath = fullPath,
                    Arguments = string.Empty,
                    Note = string.Empty,
                    GroupName = normalizedGroupName,
                    IsFavorite = false,
                    Order = nextOrder
                };

                settings.CommonStartupItems.Add(itemToAdd);
                SaveSettings(settings);
                return AddStartupItemResult.Added(itemToAdd.Name, normalizedGroupName);
            }
        }

        private CommonStartupSettings LoadSettings()
        {
            try
            {
                if (File.Exists(_commonStartupSettingsFilePath))
                {
                    var json = File.ReadAllText(_commonStartupSettingsFilePath);
                    return JsonConvert.DeserializeObject<CommonStartupSettings>(json, _jsonSettings) ?? new CommonStartupSettings();
                }

                if (!File.Exists(_settingsFilePath))
                {
                    return new CommonStartupSettings();
                }

                var token = JToken.Parse(File.ReadAllText(_settingsFilePath));
                if (!(token is JObject document))
                {
                    return new CommonStartupSettings();
                }

                var serializer = JsonSerializer.Create(_jsonSettings);
                return new CommonStartupSettings
                {
                    CommonStartupItems = document.TryGetValue("CommonStartupItems", StringComparison.OrdinalIgnoreCase, out var itemsToken)
                        ? itemsToken.ToObject<List<CommonStartupItem>>(serializer) ?? new List<CommonStartupItem>()
                        : new List<CommonStartupItem>(),
                    CommonStartupGroups = document.TryGetValue("CommonStartupGroups", StringComparison.OrdinalIgnoreCase, out var groupsToken)
                        ? groupsToken.ToObject<List<CommonStartupGroup>>(serializer) ?? new List<CommonStartupGroup>()
                        : new List<CommonStartupGroup>()
                };
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "加载启动项配置失败");
                return new CommonStartupSettings();
            }
        }

        private void SaveSettings(CommonStartupSettings settings)
        {
            BackupSettingsFileIfNeeded();
            var json = JsonConvert.SerializeObject(settings, _jsonSettings);
            File.WriteAllText(_commonStartupSettingsFilePath, json);
        }

        private void BackupSettingsFileIfNeeded()
        {
            if (!File.Exists(_commonStartupSettingsFilePath))
            {
                return;
            }

            File.Copy(_commonStartupSettingsFilePath, _commonStartupSettingsFilePath + ".bak", true);
            Directory.CreateDirectory(_commonStartupSettingsBackupFolderPath);
            var stampedBackupPath = Path.Combine(
                _commonStartupSettingsBackupFolderPath,
                "common_startup_settings_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".json");
            File.Copy(_commonStartupSettingsFilePath, stampedBackupPath, true);

            foreach (var staleBackup in new DirectoryInfo(_commonStartupSettingsBackupFolderPath)
                         .GetFiles("common_startup_settings_*.json")
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(20))
            {
                staleBackup.Delete();
            }
        }

        private static CommonStartupSettings NormalizeSettings(CommonStartupSettings settings)
        {
            settings = settings ?? new CommonStartupSettings();
            settings.CommonStartupItems = settings.CommonStartupItems ?? new List<CommonStartupItem>();
            settings.CommonStartupGroups = settings.CommonStartupGroups ?? new List<CommonStartupGroup>();

            var groups = new List<CommonStartupGroup>();
            var knownGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var preset in PresetGroupNames)
            {
                knownGroups.Add(preset);
                groups.Add(new CommonStartupGroup { Name = preset });
            }

            foreach (var group in settings.CommonStartupGroups
                         .Where(group => group != null && !string.IsNullOrWhiteSpace(group.Name))
                         .OrderBy(group => group.Order <= 0 ? int.MaxValue : group.Order)
                         .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase))
            {
                var name = group.Name.Trim();
                if (knownGroups.Add(name))
                {
                    groups.Add(new CommonStartupGroup { Name = name });
                }
            }

            settings.CommonStartupItems = settings.CommonStartupItems
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.FullPath))
                .ToList();
            foreach (var item in settings.CommonStartupItems)
            {
                item.Name = string.IsNullOrWhiteSpace(item.Name) ? ResolveItemName(item.FullPath, Directory.Exists(item.FullPath)) : item.Name.Trim();
                item.Arguments = item.Arguments ?? string.Empty;
                item.Note = item.Note ?? string.Empty;
                item.GroupName = NormalizeGroupName(item.GroupName);
                if (knownGroups.Add(item.GroupName))
                {
                    groups.Add(new CommonStartupGroup { Name = item.GroupName });
                }
            }

            for (var i = 0; i < groups.Count; i++)
            {
                groups[i].Order = i + 1;
            }

            settings.CommonStartupGroups = groups;
            return settings;
        }

        private static void EnsureGroupExists(CommonStartupSettings settings, string groupName)
        {
            if (settings.CommonStartupGroups.Any(group => string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            settings.CommonStartupGroups.Add(new CommonStartupGroup
            {
                Name = groupName,
                Order = settings.CommonStartupGroups.Count + 1
            });
        }

        private static string NormalizeGroupName(string groupName)
        {
            return string.IsNullOrWhiteSpace(groupName) ? DefaultGroupName : groupName.Trim();
        }

        private static string ResolveItemName(string fullPath, bool isDirectory)
        {
            if (isDirectory)
            {
                var trimmedPath = (fullPath ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var directoryName = Path.GetFileName(trimmedPath);
                return string.IsNullOrWhiteSpace(directoryName) ? fullPath : directoryName;
            }

            var fileName = Path.GetFileNameWithoutExtension(fullPath);
            return string.IsNullOrWhiteSpace(fileName) ? fullPath : fileName;
        }

        private sealed class CommonStartupSettings
        {
            public List<CommonStartupItem> CommonStartupItems { get; set; } = new List<CommonStartupItem>();
            public List<CommonStartupGroup> CommonStartupGroups { get; set; } = new List<CommonStartupGroup>();
        }

        private sealed class CommonStartupItem
        {
            public string Name { get; set; }
            public string FullPath { get; set; }
            public string Arguments { get; set; } = string.Empty;
            public string Note { get; set; } = string.Empty;
            public string GroupName { get; set; } = string.Empty;
            public bool IsFavorite { get; set; }
            public int Order { get; set; }
            public DateTime? LastLaunchedAt { get; set; }
            public int LaunchCount { get; set; }
        }

        private sealed class CommonStartupGroup
        {
            public string Name { get; set; } = string.Empty;
            public int Order { get; set; }
        }
    }

    internal sealed class AddStartupItemResult
    {
        private AddStartupItemResult(bool success, bool alreadyExists, string itemName, string groupName, string errorMessage)
        {
            Success = success;
            AlreadyExists = alreadyExists;
            ItemName = itemName;
            GroupName = groupName;
            ErrorMessage = errorMessage;
        }

        public bool Success { get; private set; }
        public bool AlreadyExists { get; private set; }
        public string ItemName { get; private set; }
        public string GroupName { get; private set; }
        public string ErrorMessage { get; private set; }

        public static AddStartupItemResult Added(string itemName, string groupName)
        {
            return new AddStartupItemResult(true, false, itemName, groupName, null);
        }

        public static AddStartupItemResult Existing(string itemName, string groupName)
        {
            return new AddStartupItemResult(true, true, itemName, groupName, null);
        }

        public static AddStartupItemResult Failed(string errorMessage)
        {
            return new AddStartupItemResult(false, false, null, null, errorMessage);
        }
    }
}
