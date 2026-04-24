using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace MftScanner.Services
{
    internal static class LoggingService
    {
        private static readonly object Gate = new object();
        private static readonly string BaseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PackageManager",
            "logs");
        private static readonly string InfoDirectory = BaseDirectory;
        private static readonly string DebugDirectory = Path.Combine(BaseDirectory, "debug");
        private static readonly string ErrorDirectory = Path.Combine(BaseDirectory, "errors");
        private static readonly string IndexPerfDirectory = Path.Combine(BaseDirectory, "index-service-diagnostics");
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PackageManager",
            "settings.json");
        private static readonly object PerfSettingsGate = new object();
        private static readonly TimeSpan PerfSettingsRefreshInterval = TimeSpan.FromSeconds(2);

        private static DateTime _lastPerfSettingsCheckUtc = DateTime.MinValue;
        private static DateTime _perfSettingsLastWriteUtc = DateTime.MinValue;
        private static bool _indexPerfEnabled;

        public static void LogInfo(string message) => Write(InfoDirectory, "INFO", message);

        public static void LogDebug(string message) => Write(DebugDirectory, "DEBUG", message);

        public static void LogWarning(string message) => Write(InfoDirectory, "WARN", message);

        public static void LogIndexPerf(string category, string message)
        {
            if (!IsIndexPerfEnabled())
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(IndexPerfDirectory);
                var filePath = Path.Combine(IndexPerfDirectory, DateTime.Now.ToString("yyyyMMdd") + ".log");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] {message}{Environment.NewLine}";
                lock (Gate)
                {
                    File.AppendAllText(filePath, line, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }

        public static string FormatPerfValue(string value, int maxLength = 160)
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

        public static void LogError(Exception exception, string message)
        {
            var details = exception == null ? message : message + Environment.NewLine + exception;
            Write(ErrorDirectory, "ERROR", details);
        }

        private static void Write(string directory, string level, string message)
        {
            try
            {
                var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
                lock (Gate)
                {
                    Directory.CreateDirectory(directory);
                    var logFilePath = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd") + ".log");
                    File.AppendAllText(logFilePath, text + Environment.NewLine);
                }

                Debug.WriteLine(text);
            }
            catch
            {
            }
        }

        private static bool IsIndexPerfEnabled()
        {
            var utcNow = DateTime.UtcNow;
            lock (PerfSettingsGate)
            {
                if (utcNow - _lastPerfSettingsCheckUtc < PerfSettingsRefreshInterval)
                {
                    return _indexPerfEnabled;
                }

                _lastPerfSettingsCheckUtc = utcNow;
                try
                {
                    if (!File.Exists(SettingsFilePath))
                    {
                        _indexPerfEnabled = false;
                        _perfSettingsLastWriteUtc = DateTime.MinValue;
                        return _indexPerfEnabled;
                    }

                    var lastWriteUtc = File.GetLastWriteTimeUtc(SettingsFilePath);
                    if (lastWriteUtc == _perfSettingsLastWriteUtc)
                    {
                        return _indexPerfEnabled;
                    }

                    var json = File.ReadAllText(SettingsFilePath, Encoding.UTF8);
                    var root = JObject.Parse(json);
                    _indexPerfEnabled = root.Value<bool?>("EnableIndexServicePerformanceAnalysis") ?? false;
                    _perfSettingsLastWriteUtc = lastWriteUtc;
                }
                catch
                {
                    _indexPerfEnabled = false;
                }

                return _indexPerfEnabled;
            }
        }
    }
}
