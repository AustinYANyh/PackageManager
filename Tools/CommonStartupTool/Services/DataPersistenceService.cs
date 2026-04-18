using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace PackageManager.Services;

public class DataPersistenceService
{
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
            var json = JsonConvert.SerializeObject(settings ?? new AppSettings(), _jsonSettings);
            File.WriteAllText(_settingsFilePath, json);
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

            var json = File.ReadAllText(_settingsFilePath);
            var settings = JsonConvert.DeserializeObject<AppSettings>(json, _jsonSettings);
            return settings ?? new AppSettings();
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "加载启动项设置失败");
            return new AppSettings();
        }
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
