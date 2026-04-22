using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PackageManager.Services;

public class DataPersistenceService
{
    private const string CommonStartupItemsPropertyName = "CommonStartupItems";
    private const string CommonStartupGroupsPropertyName = "CommonStartupGroups";

    private readonly string _settingsFilePath;
    private readonly string _commonStartupSettingsFilePath;
    private readonly string _settingsBackupFolderPath;
    private readonly string _commonStartupSettingsBackupFolderPath;
    private readonly JsonSerializerSettings _jsonSettings;

    public DataPersistenceService()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appDataPath, "PackageManager");
        Directory.CreateDirectory(appFolder);

        _settingsFilePath = Path.Combine(appFolder, "settings.json");
        _commonStartupSettingsFilePath = Path.Combine(appFolder, "common_startup_settings.json");
        _settingsBackupFolderPath = Path.Combine(appFolder, "settings_history");
        _commonStartupSettingsBackupFolderPath = Path.Combine(appFolder, "common_startup_settings_history");
        _jsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };
    }

    public bool SaveSettings(AppSettings settings, bool preserveExistingCommonStartupData = true)
    {
        try
        {
            var effectiveSettings = settings ?? new AppSettings();

            if (preserveExistingCommonStartupData)
            {
                var currentSettings = LoadSettings();
                if ((effectiveSettings.CommonStartupItems == null || effectiveSettings.CommonStartupItems.Count == 0) &&
                    currentSettings.CommonStartupItems.Count > 0)
                {
                    effectiveSettings.CommonStartupItems = currentSettings.CommonStartupItems
                        .Select(CloneCommonStartupItem)
                        .ToList();
                }

                if ((effectiveSettings.CommonStartupGroups == null || effectiveSettings.CommonStartupGroups.Count == 0) &&
                    currentSettings.CommonStartupGroups.Count > 0)
                {
                    effectiveSettings.CommonStartupGroups = currentSettings.CommonStartupGroups
                        .Select(CloneCommonStartupGroup)
                        .ToList();
                }
            }

            BackupSettingsFileIfNeeded();
            var json = JsonConvert.SerializeObject(effectiveSettings, _jsonSettings);
            File.WriteAllText(_commonStartupSettingsFilePath, json);
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "保存启动项设置失败");
            return false;
        }
    }

    public AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                if (!File.Exists(_commonStartupSettingsFilePath))
                {
                    return new AppSettings();
                }
            }

            if (File.Exists(_commonStartupSettingsFilePath))
            {
                var json = File.ReadAllText(_commonStartupSettingsFilePath);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json, _jsonSettings) ?? new AppSettings();
                settings.CommonStartupItems ??= new List<CommonStartupItem>();
                settings.CommonStartupGroups ??= new List<CommonStartupGroup>();
                return settings;
            }

            var serializer = JsonSerializer.Create(_jsonSettings);
            var document = LoadSettingsDocument();
            var legacySettings = new AppSettings();

            if (document.TryGetValue(CommonStartupItemsPropertyName, StringComparison.OrdinalIgnoreCase, out var itemsToken))
            {
                legacySettings.CommonStartupItems = itemsToken.Type == JTokenType.Null
                    ? new List<CommonStartupItem>()
                    : itemsToken.ToObject<List<CommonStartupItem>>(serializer) ?? new List<CommonStartupItem>();
            }

            if (document.TryGetValue(CommonStartupGroupsPropertyName, StringComparison.OrdinalIgnoreCase, out var groupsToken))
            {
                legacySettings.CommonStartupGroups = groupsToken.Type == JTokenType.Null
                    ? new List<CommonStartupGroup>()
                    : groupsToken.ToObject<List<CommonStartupGroup>>(serializer) ?? new List<CommonStartupGroup>();
            }

            return legacySettings;
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "加载启动项设置失败");
            return new AppSettings();
        }
    }

    private JObject LoadSettingsDocument()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return new JObject();
        }

        var json = File.ReadAllText(_settingsFilePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JObject();
        }

        var token = JToken.Parse(json);
        if (token is JObject document)
        {
            return document;
        }

        throw new JsonException("settings.json root token must be an object.");
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
            $"common_startup_settings_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json");
        File.Copy(_commonStartupSettingsFilePath, stampedBackupPath, true);

        foreach (var staleBackup in new DirectoryInfo(_commonStartupSettingsBackupFolderPath)
                     .GetFiles("common_startup_settings_*.json")
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Skip(20))
        {
            staleBackup.Delete();
        }
    }

    private static CommonStartupItem CloneCommonStartupItem(CommonStartupItem item)
    {
        if (item == null)
        {
            return null;
        }

        return new CommonStartupItem
        {
            Name = item.Name,
            FullPath = item.FullPath,
            Arguments = item.Arguments,
            Note = item.Note,
            GroupName = item.GroupName,
            IsFavorite = item.IsFavorite,
            Order = item.Order,
            LastLaunchedAt = item.LastLaunchedAt,
            LaunchCount = item.LaunchCount
        };
    }

    private static CommonStartupGroup CloneCommonStartupGroup(CommonStartupGroup group)
    {
        if (group == null)
        {
            return null;
        }

        return new CommonStartupGroup
        {
            Name = group.Name,
            Order = group.Order
        };
    }
}

public class AppSettings
{
    public List<CommonStartupItem> CommonStartupItems { get; set; } = new List<CommonStartupItem>();
    public List<CommonStartupGroup> CommonStartupGroups { get; set; } = new List<CommonStartupGroup>();
}

public class CommonStartupItem
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

public class CommonStartupGroup
{
    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
}
