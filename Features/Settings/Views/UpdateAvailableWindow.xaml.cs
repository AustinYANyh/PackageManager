using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PackageManager.Services;

namespace PackageManager.Features.Settings.Views
{
    /// <summary>
    /// 自定义更新提示窗口：发现新版本、下载进度与失败重试三态共用一窗。
    /// </summary>
    public partial class UpdateAvailableWindow : Window
    {
        private readonly AppUpdateInfo update;
        private readonly AppUpdateService updateService;
        private readonly Progress<UpdateDownloadProgress> progressReporter;
        private bool downloading;

        /// <summary>
        /// 初始化 <see cref="UpdateAvailableWindow"/> 并构建版本徽章与更新点时间线。
        /// </summary>
        /// <param name="update">更新信息。</param>
        /// <param name="updateService">更新服务，用于执行更新、跳过版本与稍后提醒。</param>
        public UpdateAvailableWindow(AppUpdateInfo update, AppUpdateService updateService)
        {
            InitializeComponent();
            this.update = update ?? throw new ArgumentNullException(nameof(update));
            this.updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
            progressReporter = new Progress<UpdateDownloadProgress>(OnDownloadProgress);

            CurrentVersionText.Text = $"v{update.Current}";
            LatestVersionText.Text = $"v{update.Latest}";
            ReleaseNoteText.Text = "新版本已发布";
            BuildChangeGroups();
        }

        private void BuildChangeGroups()
        {
            ChangeGroupsPanel.Children.Clear();
            var groups = update.ChangeGroups ?? new List<KeyValuePair<string, List<string>>>();
            if (groups.Count == 0)
            {
                ChangeGroupsPanel.Children.Add(new TextBlock
                {
                    Text = "本次更新包含若干稳定性与体验改进。",
                    FontSize = 13,
                    Foreground = ParseBrush("#374151"),
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            for (var i = 0; i < groups.Count; i++)
            {
                var isActive = i == groups.Count - 1;
                var group = groups[i];
                ChangeGroupsPanel.Children.Add(BuildGroupNode(group.Key, isActive));
                ChangeGroupsPanel.Children.Add(BuildGroupLine(group.Value, isLast: isActive));
            }
        }

        private static Border BuildGroupNode(string version, bool isActive)
        {
            var dot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = ParseBrush(isActive ? "#2B7FFF" : "#CBD5E1"),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var label = new TextBlock
            {
                Text = $"v{version}",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = ParseBrush("#111827"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(dot);
            panel.Children.Add(label);
            return new Border { Child = panel, Margin = new Thickness(0, 0, 0, 8) };
        }

        private static Border BuildGroupLine(List<string> items, bool isLast)
        {
            var line = new StackPanel();
            foreach (var item in items)
            {
                line.Children.Add(BuildChangeItem(item));
            }

            return new Border
            {
                BorderBrush = ParseBrush("#E5E7EB"),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Margin = new Thickness(3, 0, 0, isLast ? 0 : 16),
                Padding = new Thickness(13, 0, 0, 0),
                Child = line,
            };
        }

        private static StackPanel BuildChangeItem(string text)
        {
            var (tagText, tagForeground, tagBackground) = InferTag(text);
            var tag = new Border
            {
                Background = ParseBrush(tagBackground),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 1, 7, 1),
                Margin = new Thickness(0, 2, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = tagText,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ParseBrush(tagForeground),
                },
            };

            var content = new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = ParseBrush("#374151"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
            panel.Children.Add(tag);
            panel.Children.Add(content);
            return panel;
        }

        private static (string Text, string Foreground, string Background) InferTag(string text)
        {
            var value = text ?? string.Empty;
            if (value.Contains("新增") || value.Contains("添加"))
            {
                return ("新增", "#10B981", "#ECFDF5");
            }

            if (value.Contains("修复"))
            {
                return ("修复", "#D97706", "#FFFBEB");
            }

            return ("优化", "#3B82F6", "#EFF6FF");
        }

        private static Brush ParseBrush(string hex)
        {
            return (Brush)new BrushConverter().ConvertFromString(hex);
        }

        private void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            StartDownload();
        }

        private async void StartDownload()
        {
            if (downloading)
            {
                return;
            }

            downloading = true;
            ErrorBar.Visibility = Visibility.Collapsed;
            ActionsPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressNote.Text = "正在下载新版本…";
            DownloadProgress.Value = 0;
            CloseButton.Visibility = Visibility.Collapsed;
            UpdateButton.Content = "重试更新";

            try
            {
                await updateService.ExecuteUpdateAsync(update, progressReporter);
                ProgressNote.Text = "下载完成，应用即将自动重启…";
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "下载或切换新版本失败");
                downloading = false;
                ErrorText.Text = $"下载失败：{ex.Message} 请检查网络后重试，详情见错误日志。";
                ErrorBar.Visibility = Visibility.Visible;
                ProgressPanel.Visibility = Visibility.Collapsed;
                ActionsPanel.Visibility = Visibility.Visible;
                CloseButton.Visibility = Visibility.Visible;
            }
        }

        private void OnDownloadProgress(UpdateDownloadProgress value)
        {
            if (value == null)
            {
                return;
            }

            DownloadProgress.Value = Math.Max(0, Math.Min(100, value.Percent));
            PercentText.Text = $"{Math.Round(value.Percent)}%";
            SpeedText.Text = FormatSpeed(value.Speed);
            EtaText.Text = FormatRemaining(value.RemainingSeconds);
            if (value.Percent >= 100)
            {
                ProgressNote.Text = "下载完成，正在准备切换版本…";
            }
        }

        private static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0)
            {
                return "--";
            }

            if (bytesPerSecond > 1024 * 1024)
            {
                return $"{bytesPerSecond / 1024 / 1024:0.0} MB/s";
            }

            return $"{bytesPerSecond / 1024:0} KB/s";
        }

        private static string FormatRemaining(double seconds)
        {
            if (seconds < 0)
            {
                return "--:--";
            }

            if (seconds > 3600)
            {
                return $"{(int)seconds / 3600}:{(int)seconds % 3600 / 60:00}:{(int)seconds % 60:00}";
            }

            return $"{(int)seconds / 60:00}:{(int)seconds % 60:00}";
        }

        private void LaterButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                updateService.NotifyForLater(update);
            }
            catch
            {
                // 通知失败不阻塞关闭
            }

            Close();
        }

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            updateService.SkipVersion(update.Latest);
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed && !downloading)
            {
                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                    // 忽略非可拖动状态下的拖动异常
                }
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !downloading)
            {
                Close();
                e.Handled = true;
            }
        }
    }
}
