using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using PackageManager.Services;

namespace PackageManager.Function.ConfigPreset
{
    // 添加卡片占位类型（仅用于模板识别）
    public sealed class AddCardPlaceholder
    {
    }

    /// <summary>
    /// 用于选择并应用预设配置的窗口
    /// </summary>
    public partial class ConfigPresetWindow : Window, INotifyPropertyChanged
    {
        private readonly string _initialIniContent;

        private Models.ConfigPreset SelectedPreset;

        public ConfigPresetWindow(string initialIniContent = null)
        {
            InitializeComponent();
            DataContext = this;
            _initialIniContent = initialIniContent;
            InitializeBuiltInPresets();
        }

        // 用于 ItemsControl 的统一数据源（内置 + 自定义 + 添加占位）
        public ObservableCollection<object> PresetItems { get; } = new ObservableCollection<object>();

        public ObservableCollection<Models.ConfigPreset> CustomPresets { get; } = new ObservableCollection<Models.ConfigPreset>();

        public string SelectedPresetContent { get; private set; }

        private double _cardHeight = 220;
        public double CardHeight
        {
            get => _cardHeight;
            private set
            {
                if (Math.Abs(_cardHeight - value) > 0.5)
                {
                    _cardHeight = value;
                    OnPropertyChanged(nameof(CardHeight));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private static string NormalizeDomain(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return string.Empty;
            }

            s = s.Trim();
            while (s.EndsWith("/"))
            {
                s = s.Substring(0, s.Length - 1);
            }

            return s;
        }

        private static string NullToEmpty(string s)
        {
            return s ?? string.Empty;
        }

        // 已移除INI解析逻辑，键不固定

        private static void BuildIni(StringBuilder content,
                                     string serverDomain,
                                     string commonServerDomain,
                                     string ieProxyAvailable,
                                     int requestTimeout,
                                     int responseTimeout,
                                     int retryTimes)
        {
            content.Clear();
            content.AppendLine("[ServerInfo]");
            content.AppendLine($"ServerDomain=\"{serverDomain}\"");
            content.AppendLine($"CommonServerDomain=\"{commonServerDomain}\"");
            content.AppendLine($"IEProxyAvailable=\"{ieProxyAvailable}\"");
            content.AppendLine("[LoginSetting]");
            content.AppendLine($"requestTimeout={requestTimeout}");
            content.AppendLine($"responseTimeout={responseTimeout}");
            content.AppendLine($"requestRetryTimes={retryTimes}");
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedPreset ?? PresetItems.OfType<Models.ConfigPreset>().FirstOrDefault(p => p.IsSelected);
            if (selected == null)
            {
                MessageBox.Show("请先选择一个预设或自定义配置", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 若该预设包含原始 INI 文本，则直接使用
            if (!string.IsNullOrWhiteSpace(selected.RawIniContent))
            {
                SelectedPresetContent = selected.RawIniContent;
            }
            else
            {
                var content = new StringBuilder();
                BuildIni(content,
                         selected.ServerDomain ?? string.Empty,
                         selected.CommonServerDomain ?? string.Empty,
                         selected.IEProxyAvailable ?? "yes",
                         selected.requestTimeout,
                         selected.responseTimeout,
                         selected.requestRetryTimes);
                SelectedPresetContent = content.ToString();
            }
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var loaded = ConfigPresetStore.Load();
                CustomPresets.Clear();
                foreach (var p in loaded)
                {
                    CustomPresets.Add(p);
                }

                RebuildPresetItems();
                UpdateCardHeight();
                TrySelectInitialPreset();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载自定义配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AddPresetCard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var win = new AddPresetWindow { Owner = this };
            if ((win.ShowDialog() == true) && (win.ResultPreset != null))
            {
                CustomPresets.Add(win.ResultPreset);

                // 保持“添加”卡片始终在最前
                PresetItems.Add(win.ResultPreset);
                try
                {
                    ConfigPresetStore.Save(CustomPresets);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"保存自定义配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                UpdateCardHeight();
            }
        }

        private void PresetRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is Models.ConfigPreset preset)
            {
                SelectedPreset = preset;
            }
        }

        private void DeletePresetMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var menuItem = sender as System.Windows.Controls.MenuItem;
                var ctx = menuItem?.Parent as System.Windows.Controls.ContextMenu;
                var target = ctx?.PlacementTarget as FrameworkElement;
                var preset = target?.DataContext as Models.ConfigPreset;

                if (preset == null)
                {
                    return;
                }

                // 仅允许删除自定义配置
                if (!CustomPresets.Contains(preset))
                {
                    MessageBox.Show("内置预设不可删除", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var confirm = MessageBox.Show($"确认删除自定义配置：{preset.Name}?",
                                              "确认删除",
                                              MessageBoxButton.YesNo,
                                              MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }

                // 从集合移除并保存
                CustomPresets.Remove(preset);
                PresetItems.Remove(preset);
                ConfigPresetStore.Save(CustomPresets);

                // 确保“添加”卡片在最前
                RebuildPresetItems();
                UpdateCardHeight();

                // 如果删除的是当前选中项，重置选中到“默认”预设；若不存在则选中第一个配置项
                if (SelectedPreset == preset)
                {
                    SelectedPreset = null;
                    var defaultPreset = PresetItems
                        .OfType<Models.ConfigPreset>()
                        .FirstOrDefault(p => p.IsBuiltIn && string.Equals(p.Name, "默认", StringComparison.OrdinalIgnoreCase))
                        ?? PresetItems.OfType<Models.ConfigPreset>().FirstOrDefault();

                    if (defaultPreset != null)
                    {
                        defaultPreset.IsSelected = true;
                        SelectedPreset = defaultPreset;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"删除配置失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PresetCard_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Models.ConfigPreset preset && preset.IsBuiltIn)
            {
                // 内置预设不显示右键菜单
                e.Handled = true;
            }
        }

        private void InitializeBuiltInPresets()
        {
            
            // 默认（空域名，3000超时）
            PresetItems.Add(new Models.ConfigPreset
            {
                Name = "默认",
                ServerDomain = string.Empty,
                CommonServerDomain = string.Empty,
                IEProxyAvailable = "yes",
                requestTimeout = 3000,
                responseTimeout = 3000,
                requestRetryTimes = 0,
                IsBuiltIn = true,
            });
            
            // 136
            PresetItems.Add(new Models.ConfigPreset
            {
                Name = "136",
                ServerDomain = "http://192.168.0.136:8171/HWBuildMasterPlus/",
                CommonServerDomain = "http://192.168.0.136:8171/HWBIMCommon/",
                IEProxyAvailable = "yes",
                requestTimeout = 5000,
                responseTimeout = 5000,
                requestRetryTimes = 0,
                IsBuiltIn = true,
            });

            // 137
            PresetItems.Add(new Models.ConfigPreset
            {
                Name = "137",
                ServerDomain = "http://192.168.0.137:8171/HWBuildMasterPlus/",
                CommonServerDomain = "http://192.168.0.137:8171/HWBIMCommon/",
                IEProxyAvailable = "yes",
                requestTimeout = 5000,
                responseTimeout = 5000,
                requestRetryTimes = 0,
                IsBuiltIn = true,
            });
        }

        private void RebuildPresetItems()
        {
            // 先移除已有的自定义项与添加占位
            for (int i = PresetItems.Count - 1; i >= 0; i--)
            {
                if (PresetItems[i] is Models.ConfigPreset cp && CustomPresets.Contains(cp))
                {
                    PresetItems.RemoveAt(i);
                }
                else if (PresetItems[i] is AddCardPlaceholder)
                {
                    PresetItems.RemoveAt(i);
                }
            }

            // 追加自定义项
            foreach (var p in CustomPresets)
            {
                PresetItems.Add(p);
            }

            // 最前追加卡片占位
            PresetItems.Insert(0,new AddCardPlaceholder());
        }

        private void TrySelectInitialPreset()
        {
            if (string.IsNullOrEmpty(_initialIniContent))
            {
                return;
            }

            string Normalize(string s)
            {
                if (string.IsNullOrEmpty(s)) return string.Empty;
                var t = s.Replace("\r\n", "\n").Replace("\r", "\n");
                return t.TrimEnd();
            }

            var expected = Normalize(_initialIniContent);
            var match = PresetItems
                .OfType<Models.ConfigPreset>()
                .FirstOrDefault(p => Normalize(string.IsNullOrWhiteSpace(p.RawIniContent) ? BuildIniForDisplay(p) : p.RawIniContent) == expected);

            if (match != null)
            {
                match.IsSelected = true;
                SelectedPreset = match;
            }
        }

        private void UpdateCardHeight()
        {
            try
            {
                int maxLines = 1;
                foreach (var p in PresetItems.OfType<Models.ConfigPreset>())
                {
                    var text = string.IsNullOrWhiteSpace(p.RawIniContent)
                        ? BuildIniForDisplay(p)
                        : p.RawIniContent;

                    var lines = CountLines(text);
                    if (lines > maxLines) maxLines = lines;
                }

                // 估算高度：单行约18像素 + 顶部单选按钮约28 + 内边距
                double estimated = 28 + (maxLines * 18) + 20; // radio + text + padding
                CardHeight = Math.Max(220, estimated);
            }
            catch
            {
                CardHeight = 220;
            }
        }

        private static int CountLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return 1;
            int count = 1;
            foreach (var ch in s)
            {
                if (ch == '\n') count++;
            }
            return count;
        }

        private static string BuildIniForDisplay(Models.ConfigPreset p)
        {
            var sb = new StringBuilder();
            BuildIni(sb,
                     p.ServerDomain ?? string.Empty,
                     p.CommonServerDomain ?? string.Empty,
                     string.IsNullOrEmpty(p.IEProxyAvailable) ? "yes" : p.IEProxyAvailable,
                     p.requestTimeout,
                     p.responseTimeout,
                     p.requestRetryTimes);
            return sb.ToString();
        }
    }
}
