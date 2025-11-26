using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using PackageManager.Models;
using PackageManager.Services;

namespace PackageManager
{
    // 添加卡片占位类型（仅用于模板识别）
    public sealed class AddCardPlaceholder
    {
    }

    /// <summary>
    /// 用于选择并应用预设配置的窗口
    /// </summary>
    public partial class ConfigPresetWindow : Window
    {
        private readonly string _initialIniContent;

        private ConfigPreset SelectedPreset;

        public ConfigPresetWindow(string initialIniContent = null)
        {
            InitializeComponent();
            DataContext = this;
            _initialIniContent = initialIniContent;
            InitializeBuiltInPresets();
        }

        // 用于 ItemsControl 的统一数据源（内置 + 自定义 + 添加占位）
        public ObservableCollection<object> PresetItems { get; } = new ObservableCollection<object>();

        public ObservableCollection<ConfigPreset> CustomPresets { get; } = new ObservableCollection<ConfigPreset>();

        public string SelectedPresetContent { get; private set; }

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

        private static ConfigPreset ParseIni(string content)
        {
            try
            {
                string ReadQuoted(string key)
                {
                    var m = Regex.Match(content, $@"^{key}\s*=\s*""(.*?)""\s*$", RegexOptions.Multiline);
                    return m.Success ? m.Groups[1].Value : string.Empty;
                }

                int ReadInt(string key, int def = 0)
                {
                    var m = Regex.Match(content, $@"^{key}\=(\d+)\s*$", RegexOptions.Multiline);
                    return m.Success ? int.Parse(m.Groups[1].Value) : def;
                }

                var preset = new ConfigPreset
                {
                    ServerDomain = ReadQuoted("ServerDomain"),
                    CommonServerDomain = ReadQuoted("CommonServerDomain"),
                    IEProxyAvailable = ReadQuoted("IEProxyAvailable"),
                    requestTimeout = ReadInt("requestTimeout"),
                    responseTimeout = ReadInt("responseTimeout"),
                    requestRetryTimes = ReadInt("requestRetryTimes"),
                };
                return preset;
            }
            catch
            {
                return null;
            }
        }

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
            var content = new StringBuilder();
            var selected = SelectedPreset ?? PresetItems.OfType<ConfigPreset>().FirstOrDefault(p => p.IsSelected);
            if (selected == null)
            {
                MessageBox.Show("请先选择一个预设或自定义配置", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BuildIni(content,
                     selected.ServerDomain ?? string.Empty,
                     selected.CommonServerDomain ?? string.Empty,
                     selected.IEProxyAvailable ?? "yes",
                     selected.requestTimeout,
                     selected.responseTimeout,
                     selected.requestRetryTimes);

            SelectedPresetContent = content.ToString();
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
            }
        }

        private void PresetRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ConfigPreset preset)
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
                var preset = target?.DataContext as ConfigPreset;

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

                // 如果删除的是当前选中项，重置选中到第一个配置项
                if (SelectedPreset == preset)
                {
                    SelectedPreset = null;
                    var first = PresetItems.OfType<ConfigPreset>().FirstOrDefault();
                    if (first != null)
                    {
                        first.IsSelected = true;
                        SelectedPreset = first;
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
            if (sender is FrameworkElement fe && fe.DataContext is ConfigPreset preset && preset.IsBuiltIn)
            {
                // 内置预设不显示右键菜单
                e.Handled = true;
            }
        }

        private void InitializeBuiltInPresets()
        {
            
            // 默认（空域名，3000超时）
            PresetItems.Add(new ConfigPreset
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
            PresetItems.Add(new ConfigPreset
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
            PresetItems.Add(new ConfigPreset
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
                if (PresetItems[i] is ConfigPreset cp && CustomPresets.Contains(cp))
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
            if (string.IsNullOrWhiteSpace(_initialIniContent))
            {
                return; // 不再默认选中预设一
            }

            var parsed = ParseIni(_initialIniContent);
            if (parsed == null)
            {
                return;
            }

            // 在所有配置中寻找匹配项（规范化域名，先严格匹配，后域名匹配回退）
            var expectedServer = NormalizeDomain(parsed.ServerDomain);
            var expectedCommon = NormalizeDomain(parsed.CommonServerDomain);

            var match = PresetItems.OfType<ConfigPreset>().FirstOrDefault(p =>
                                                                              string.Equals(NormalizeDomain(p.ServerDomain),
                                                                                            expectedServer,
                                                                                            StringComparison.OrdinalIgnoreCase) &&
                                                                              string.Equals(NormalizeDomain(p.CommonServerDomain),
                                                                                            expectedCommon,
                                                                                            StringComparison.OrdinalIgnoreCase) &&
                                                                              string.Equals(NullToEmpty(p.IEProxyAvailable),
                                                                                            NullToEmpty(parsed.IEProxyAvailable),
                                                                                            StringComparison.OrdinalIgnoreCase) &&
                                                                              (p.requestTimeout == parsed.requestTimeout) &&
                                                                              (p.responseTimeout == parsed.responseTimeout) &&
                                                                              (p.requestRetryTimes == parsed.requestRetryTimes));

            // 回退：仅根据两个域名匹配
            if (match == null)
            {
                match = PresetItems.OfType<ConfigPreset>().FirstOrDefault(p =>
                                                                              string.Equals(NormalizeDomain(p.ServerDomain),
                                                                                            expectedServer,
                                                                                            StringComparison.OrdinalIgnoreCase) &&
                                                                              string.Equals(NormalizeDomain(p.CommonServerDomain),
                                                                                            expectedCommon,
                                                                                            StringComparison.OrdinalIgnoreCase));
            }

            if (match != null)
            {
                match.IsSelected = true;
                SelectedPreset = match;
            }
        }
    }
}