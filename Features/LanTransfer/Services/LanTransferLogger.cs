using System;
using System.IO;
using System.Text;

namespace PackageManager.Services;

internal static class LanTransferLogger
{
    private static readonly object SyncRoot = new object();

    private static string _directory;

    /// <summary>
    /// 获取局域网传输日志目录路径。
    /// </summary>
    /// <returns>日志目录的完整路径。</returns>
    public static string GetDirectoryPath()
    {
        if (!string.IsNullOrWhiteSpace(_directory))
        {
            return _directory;
        }

        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PackageManager",
            "logs",
            "lan-transfer");

        Directory.CreateDirectory(_directory);
        return _directory;
    }

    /// <summary>
    /// 记录信息级别日志。
    /// </summary>
    /// <param name="message">日志消息。</param>
    public static void LogInfo(string message)
    {
        Append("INFO", message, null);
    }

    /// <summary>
    /// 记录警告级别日志。
    /// </summary>
    /// <param name="message">警告消息。</param>
    public static void LogWarning(string message)
    {
        Append("WARN", message, null);
    }

    /// <summary>
    /// 记录错误级别日志，包含异常详情。
    /// </summary>
    /// <param name="ex">异常对象。</param>
    /// <param name="message">附加描述消息。</param>
    public static void LogError(Exception ex, string message = null)
    {
        Append("ERROR", message, ex);
    }

    private static void Append(string level, string message, Exception ex)
    {
        try
        {
            var filePath = Path.Combine(GetDirectoryPath(), $"{DateTime.Now:yyyyMMdd}.log");
            var builder = new StringBuilder();
            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [")
                .Append(level)
                .Append("] ");

            if (!string.IsNullOrWhiteSpace(message))
            {
                builder.Append(message);
            }

            if (ex != null)
            {
                builder.AppendLine();
                builder.Append(ex.GetType().FullName)
                    .Append(": ")
                    .Append(ex.Message)
                    .AppendLine();
                builder.Append(ex.StackTrace);
            }

            lock (SyncRoot)
            {
                File.AppendAllText(filePath, builder.ToString() + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
