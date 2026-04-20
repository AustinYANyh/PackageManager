using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PackageManager.Services;

public class DataPersistenceService
{
    private const string CommonStartupItemsPropertyName = "CommonStartupItems";
    private const string CommonStartupGroupsPropertyName = "CommonStartupGroups";

    private readonly string _settingsFilePath;
    private readonly JsonSerializerSettings _jsonSettings;

    public DataPersistenceService()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appFolder = Path.Combine(appDataPath, "PackageManager");
        Directory.CreateDirectory(appFolder);

        _settingsFilePath = Path.Combine(appFolder, "settings.json");
        _jsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };
    }

    public bool SaveSettings(AppSettings settings)
    {
        try
        {
            var serializer = JsonSerializer.Create(_jsonSettings);
            var document = LoadSettingsDocument();
            var effectiveSettings = settings ?? new AppSettings();

            document[CommonStartupItemsPropertyName] =
                JToken.FromObject(effectiveSettings.CommonStartupItems ?? new List<CommonStartupItem>(), serializer);
            document[CommonStartupGroupsPropertyName] =
                JToken.FromObject(effectiveSettings.CommonStartupGroups ?? new List<CommonStartupGroup>(), serializer);

            BackupSettingsFileIfNeeded();
            File.WriteAllText(_settingsFilePath, document.ToString(Formatting.Indented));
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
                return new AppSettings();
            }

            var serializer = JsonSerializer.Create(_jsonSettings);
            var document = LoadSettingsDocument();
            var settings = new AppSettings();

            if (document.TryGetValue(CommonStartupItemsPropertyName, StringComparison.OrdinalIgnoreCase, out var itemsToken))
            {
                settings.CommonStartupItems = itemsToken.Type == JTokenType.Null
                    ? new List<CommonStartupItem>()
                    : itemsToken.ToObject<List<CommonStartupItem>>(serializer) ?? new List<CommonStartupItem>();
            }

            if (document.TryGetValue(CommonStartupGroupsPropertyName, StringComparison.OrdinalIgnoreCase, out var groupsToken))
            {
                settings.CommonStartupGroups = groupsToken.Type == JTokenType.Null
                    ? new List<CommonStartupGroup>()
                    : groupsToken.ToObject<List<CommonStartupGroup>>(serializer) ?? new List<CommonStartupGroup>();
            }

            return settings;
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
        if (!File.Exists(_settingsFilePath))
        {
            return;
        }

        File.Copy(_settingsFilePath, _settingsFilePath + ".bak", true);
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
