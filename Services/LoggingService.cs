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
        private static string debugDir;
        private static string errorDir;

        /// <summary>
        /// 初始化日志目录结构。若未指定自定义目录，则使用 %LocalAppData%\PackageManager\logs。
        /// 若自定义目录创建失败，回退到临时目录。
        /// </summary>
        /// <param name="customBaseDir">自定义日志根目录路径，为 null 时使用默认路径。</param>
        public static void Initialize(string customBaseDir = null)
        {
            try
            {
                baseDir = string.IsNullOrWhiteSpace(customBaseDir)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackageManager", "logs")
                    : customBaseDir;

                infoDir = baseDir;
                debugDir = Path.Combine(baseDir, "debug");
                errorDir = Path.Combine(baseDir, "errors");

                Directory.CreateDirectory(infoDir);
                Directory.CreateDirectory(debugDir);
                Directory.CreateDirectory(errorDir);
            }
            catch
            {
                baseDir = Path.Combine(Path.GetTempPath(), "PackageManager", "logs");
                infoDir = baseDir;
                debugDir = Path.Combine(baseDir, "debug");
                errorDir = Path.Combine(baseDir, "errors");
                try
                {
                    Directory.CreateDirectory(infoDir);
                    Directory.CreateDirectory(debugDir);
                    Directory.CreateDirectory(errorDir);
                }
                catch { }
            }
        }

        /// <summary>
        /// 记录信息级别日志。
        /// </summary>
        /// <param name="message">日志消息内容。</param>
        public static void LogInfo(string message) => Append(infoDir, "INFO", message, null);

        /// <summary>
        /// 记录调试级别日志。
        /// </summary>
        /// <param name="message">调试消息内容。</param>
        public static void LogDebug(string message) => Append(debugDir, "DEBUG", message, null);

        /// <summary>
        /// 记录警告级别日志。
        /// </summary>
        /// <param name="message">警告消息内容。</param>
        public static void LogWarning(string message) => Append(infoDir, "WARN", message, null);

        /// <summary>
        /// 记录错误级别日志，包含异常详细信息。
        /// </summary>
        /// <param name="ex">异常对象。</param>
        /// <param name="message">附加描述消息，可为 null。</param>
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
