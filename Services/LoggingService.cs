using System;
using System.IO;
using System.Text;

namespace PackageManager.Services
{
    public static class LoggingService
    {
        private static readonly object Sync = new object();
        private static string baseDir;
        private static string infoDir;
        private static string errorDir;

        public static void Initialize(string customBaseDir = null)
        {
            try
            {
                baseDir = string.IsNullOrWhiteSpace(customBaseDir)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackageManager", "logs")
                    : customBaseDir;

                infoDir = baseDir;
                errorDir = Path.Combine(baseDir, "errors");

                Directory.CreateDirectory(infoDir);
                Directory.CreateDirectory(errorDir);
            }
            catch
            {
                baseDir = Path.Combine(Path.GetTempPath(), "PackageManager", "logs");
                infoDir = baseDir;
                errorDir = Path.Combine(baseDir, "errors");
                try
                {
                    Directory.CreateDirectory(infoDir);
                    Directory.CreateDirectory(errorDir);
                }
                catch { }
            }
        }

        public static void LogInfo(string message) => Append(infoDir, "INFO", message, null);

        public static void LogWarning(string message) => Append(infoDir, "WARN", message, null);

        public static void LogError(Exception ex, string message = null) => Append(errorDir, "ERROR", message, ex);

        private static void Append(string dir, string level, string message, Exception ex)
        {
            var file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd") + ".log");
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(level).Append("] ");
            if (!string.IsNullOrWhiteSpace(message)) sb.Append(message);
            if (ex != null)
            {
                sb.AppendLine();
                sb.Append(ex.GetType().FullName).Append(": ").Append(ex.Message).AppendLine();
                sb.Append(ex.StackTrace);
                if (ex.InnerException != null)
                {
                    sb.AppendLine();
                    sb.Append("Inner: ").Append(ex.InnerException.GetType().FullName).Append(": ").Append(ex.InnerException.Message).AppendLine();
                    sb.Append(ex.InnerException.StackTrace);
                }
            }

            lock (Sync)
            {
                try
                {
                    File.AppendAllText(file, sb.ToString() + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}