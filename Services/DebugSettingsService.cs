using System;
using System.IO;
using System.Text.RegularExpressions;

namespace PackageManager.Services
{
    /// <summary>
    /// 负责读取/写入调试模式配置（config/DebugSetting.json）
    /// </summary>
    public static class DebugSettingsService
    {
        /// <summary>
        /// 读取指定路径的调试模式配置。
        /// </summary>
        /// <param name="localPath">本地包路径。</param>
        /// <param name="defaultValue">默认值，默认为 false。</param>
        /// <returns>是否启用调试模式。</returns>
        public static bool ReadIsDebugMode(string localPath, bool defaultValue = false)
        {
            try
            {
                if (string.IsNullOrEmpty(localPath)) return defaultValue;
                string debugSettingPath = Path.Combine(localPath, "config", "DebugSetting.json");
                if (!File.Exists(debugSettingPath)) return defaultValue;

                string json = File.ReadAllText(debugSettingPath);
                var match = Regex.Match(json, "\"IsDebugMode\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return string.Equals(match.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
                }
                return defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// 写入调试模式配置到指定路径的 DebugSetting.json 文件。
        /// </summary>
        /// <param name="localPath">本地包路径。</param>
        /// <param name="enable">是否启用调试模式。</param>
        public static void WriteIsDebugMode(string localPath, bool enable)
        {
            try
            {
                if (string.IsNullOrEmpty(localPath)) return;

                string configDir = Path.Combine(localPath, "config");
                string debugSettingPath = Path.Combine(configDir, "DebugSetting.json");
                Directory.CreateDirectory(configDir);

                string newValue = enable ? "true" : "false";
                string content = File.Exists(debugSettingPath) ? File.ReadAllText(debugSettingPath) : "{\n  \"IsDebugMode\": false\n}";

                if (Regex.IsMatch(content, "\"IsDebugMode\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase))
                {
                    content = Regex.Replace(content, "(\"IsDebugMode\"\\s*:\\s*)(true|false)", $"$1{newValue}", RegexOptions.IgnoreCase);
                }
                else
                {
                    content = "{\n  \"IsDebugMode\": " + newValue + "\n}";
                }

                File.WriteAllText(debugSettingPath, content);
            }
            catch
            {
                // 忽略写入异常
            }
        }
    }
}