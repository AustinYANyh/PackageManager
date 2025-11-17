using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using PackageManager.Models;
using PackageManager.Services;

namespace PackageManager
{
    public partial class LogViewerWindow : Window
    {
        private string infoDir;

        private string errorDir;

        public LogViewerWindow()
        {
            InitializeComponent();
            InitializeDirs();
            LoadAvailableDates();
            HookEvents();
            RefreshLogs();
        }

        private void InitializeDirs()
        {
            infoDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PackageManager", "logs");
            errorDir = Path.Combine(infoDir, "errors");
            try
            {
                Directory.CreateDirectory(infoDir);
                Directory.CreateDirectory(errorDir);
            }
            catch
            {
            }
        }

        private void HookEvents()
        {
            LogTypeCombo.SelectionChanged += (s, e) => RefreshLogs();
            DateCombo.SelectionChanged += (s, e) => RefreshLogs();
            LevelCombo.SelectionChanged += (s, e) => RefreshLogs();
            SearchTextBox.TextChanged += (s, e) => RefreshLogs();
        }

        private void LoadAvailableDates()
        {
            try
            {
                var files = new List<string>();
                files.AddRange(Directory.Exists(infoDir)
                                   ? Directory.GetFiles(infoDir, "*.log", SearchOption.TopDirectoryOnly)
                                   : Array.Empty<string>());
                files.AddRange(Directory.Exists(errorDir)
                                   ? Directory.GetFiles(errorDir, "*.log", SearchOption.TopDirectoryOnly)
                                   : Array.Empty<string>());
                var dates = files
                            .Select(f => Path.GetFileNameWithoutExtension(f))
                            .Where(name => Regex.IsMatch(name, "^\\d{8}$"))
                            .Distinct()
                            .Select(s => DateTime.ParseExact(s, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture))
                            .OrderByDescending(d => d)
                            .Select(d => d.ToString("yyyy-MM-dd"))
                            .ToList();

                if (dates.Count == 0)
                {
                    dates.Add(DateTime.Now.ToString("yyyy-MM-dd"));
                }

                DateCombo.ItemsSource = dates;
                DateCombo.SelectedIndex = 0;
            }
            catch
            {
                DateCombo.ItemsSource = new[] { DateTime.Now.ToString("yyyy-MM-dd") };
                DateCombo.SelectedIndex = 0;
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshLogs();
        }

        private void RefreshLogs()
        {
            try
            {
                var type = ((ComboBoxItem)LogTypeCombo.SelectedItem)?.Content?.ToString() ?? "常规日志";
                var level = ((ComboBoxItem)LevelCombo.SelectedItem)?.Content?.ToString() ?? "全部";
                var dateStr = DateCombo.SelectedItem?.ToString() ?? DateTime.Now.ToString("yyyy-MM-dd");
                var search = SearchTextBox.Text?.Trim();

                var dateFile = DateTime.ParseExact(dateStr, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).ToString("yyyyMMdd") +
                               ".log";
                var dir = type == "错误日志" ? errorDir : infoDir;
                var path = Path.Combine(dir, dateFile);

                var entries = ReadEntries(path);

                if (!string.Equals(level, "全部", StringComparison.OrdinalIgnoreCase))
                {
                    entries = entries.Where(e => string.Equals(e.Level, level, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                if (!string.IsNullOrWhiteSpace(search))
                {
                    entries = entries.Where(e => ((e.Message?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0) ||
                                                 ((e.Details?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)).ToList();
                }

                LogGrid.ItemsSource = entries;

                // 默认选中并滚动到最后一条
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        LogGrid.UpdateLayout();
                        if (entries != null && entries.Count > 0)
                        {
                            var last = entries[entries.Count - 1];
                            LogGrid.SelectedItem = last;
                            try { LogGrid.ScrollIntoView(last); } catch { }
                        }
                    }
                    catch { }
                }));
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "刷新日志失败");
                MessageBox.Show($"刷新日志失败：{ex.Message}", "日志", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private List<LogEntry> ReadEntries(string path)
        {
            var result = new List<LogEntry>();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return result;
            }

            var content = File.ReadAllText(path);
            var blocks = Regex.Split(content, "\r?\n\r?\n");
            var headerPattern = new Regex("^(?<ts>\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2}\\.\\d{3}) \\[(?<level>INFO|WARN|ERROR)\\] (?<msg>.*)$",
                                          RegexOptions.Multiline);

            foreach (var block in blocks)
            {
                if (string.IsNullOrWhiteSpace(block))
                {
                    continue;
                }

                var lines = block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                var first = lines.FirstOrDefault() ?? string.Empty;
                var m = headerPattern.Match(first);
                if (m.Success)
                {
                    var ts = m.Groups["ts"].Value;
                    var lvl = m.Groups["level"].Value;
                    var msg = m.Groups["msg"].Value;
                    var details = string.Join(Environment.NewLine, lines.Skip(1));
                    result.Add(new LogEntry
                    {
                        Timestamp = ts,
                        Level = lvl,
                        Message = msg,
                        Details = details,
                    });
                }
                else
                {
                    result.Add(new LogEntry
                    {
                        Timestamp = "", Level = "INFO", Message = first, Details = string.Join(Environment.NewLine, lines.Skip(1)),
                    });
                }
            }

            return result;
        }

        private void OpenDirectoryButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var type = ((ComboBoxItem)LogTypeCombo.SelectedItem)?.Content?.ToString() ?? "常规日志";
                var dir = type == "错误日志" ? errorDir : infoDir;

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true,
                    });
                }
                catch
                {
                    System.Diagnostics.Process.Start("explorer.exe", dir);
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "打开日志目录失败");
                MessageBox.Show($"打开日志目录失败：{ex.Message}", "日志", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}