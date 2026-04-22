using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace MftScanner
{
    internal static class IndexPerfLog
    {
        private static readonly object WriteLock = new object();
        private static readonly object SettingsLock = new object();
        private static readonly TimeSpan SettingsRefreshInterval = TimeSpan.FromSeconds(2);
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PackageManager",
            "settings.json");
        private static readonly string LogDirectoryPathValue = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PackageManager",
            "logs",
            "index-service-diagnostics");

        private static DateTime _lastSettingsCheckUtc = DateTime.MinValue;
        private static DateTime _settingsLastWriteUtc = DateTime.MinValue;
        private static bool _enabled;

        public static string LogDirectoryPath => LogDirectoryPathValue;

        public static bool IsEnabled => RefreshSettingsIfNeeded();

        public static void Write(string category, string message)
        {
            if (!RefreshSettingsIfNeeded())
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(LogDirectoryPathValue);
                var filePath = Path.Combine(LogDirectoryPathValue, DateTime.Now.ToString("yyyyMMdd") + ".log");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] {message}{Environment.NewLine}";
                lock (WriteLock)
                {
                    File.AppendAllText(filePath, line, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }

        public static string FormatValue(string value, int maxLength = 160)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "<empty>";
            }

            var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (normalized.Length <= maxLength)
            {
                return normalized;
            }

            return normalized.Substring(0, maxLength) + "...";
        }

        private static bool RefreshSettingsIfNeeded()
        {
            var utcNow = DateTime.UtcNow;
            lock (SettingsLock)
            {
                if (utcNow - _lastSettingsCheckUtc < SettingsRefreshInterval)
                {
                    return _enabled;
                }

                _lastSettingsCheckUtc = utcNow;
                try
                {
                    if (!File.Exists(SettingsFilePath))
                    {
                        _enabled = false;
                        _settingsLastWriteUtc = DateTime.MinValue;
                        return _enabled;
                    }

                    var lastWriteUtc = File.GetLastWriteTimeUtc(SettingsFilePath);
                    if (lastWriteUtc == _settingsLastWriteUtc)
                    {
                        return _enabled;
                    }

                    var json = File.ReadAllText(SettingsFilePath, Encoding.UTF8);
                    var root = JObject.Parse(json);
                    _enabled = root.Value<bool?>("EnableIndexServicePerformanceAnalysis") ?? false;
                    _settingsLastWriteUtc = lastWriteUtc;
                }
                catch
                {
                    _enabled = false;
                }

                return _enabled;
            }
        }
    }
}