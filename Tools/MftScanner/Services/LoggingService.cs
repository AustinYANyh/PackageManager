using System;
using System.Diagnostics;
using System.IO;

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

        public static void LogInfo(string message) => Write(InfoDirectory, "INFO", message);

        public static void LogDebug(string message) => Write(DebugDirectory, "DEBUG", message);

        public static void LogWarning(string message) => Write(InfoDirectory, "WARN", message);

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
    }
}
