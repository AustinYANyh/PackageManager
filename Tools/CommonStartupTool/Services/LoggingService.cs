using System;
using System.Diagnostics;
using System.IO;

namespace PackageManager.Services;

public static class LoggingService
{
    private static readonly object Gate = new();
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PackageManager",
        "logs",
        "CommonStartupTool.log");

    public static void LogInfo(string message) => Write("INFO", message);

    public static void LogWarning(string message) => Write("WARN", message);

    public static void LogError(Exception ex, string message)
    {
        var details = ex == null ? message : $"{message}{Environment.NewLine}{ex}";
        Write("ERROR", details);
    }

    private static void Write(string level, string message)
    {
        try
        {
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath));
                File.AppendAllText(LogFilePath, text + Environment.NewLine);
            }

            Debug.WriteLine(text);
        }
        catch
        {
        }
    }
}
