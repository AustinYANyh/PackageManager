using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using PackageManager.Models;

namespace PackageManager.Services
{
    /// <summary>
    /// 配置预设存储服务，负责加载和保存用户配置预设
    /// </summary>
    public static class ConfigPresetStore
    {
        private static string PresetsFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PackageManager", "UserPresets.json");

        /// <summary>
        /// 从本地文件加载所有配置预设
        /// </summary>
        /// <returns>配置预设列表；如果文件不存在或加载失败则返回空列表</returns>
        public static List<ConfigPreset> Load()
        {
            try
            {
                var path = PresetsFilePath;
                if (!File.Exists(path)) return new List<ConfigPreset>();
                var json = File.ReadAllText(path);
                var list = JsonConvert.DeserializeObject<List<ConfigPreset>>(json) ?? new List<ConfigPreset>();
                return list;
            }
            catch
            {
                return new List<ConfigPreset>();
            }
        }

        /// <summary>
        /// 将配置预设列表保存到本地文件
        /// </summary>
        /// <param name="presets">要保存的配置预设集合</param>
        public static void Save(IEnumerable<ConfigPreset> presets)
        {
            var list = presets == null ? new List<ConfigPreset>() : new List<ConfigPreset>(presets);
            var dir = Path.GetDirectoryName(PresetsFilePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonConvert.SerializeObject(list, Formatting.Indented);
            File.WriteAllText(PresetsFilePath, json);
        }
    }
}