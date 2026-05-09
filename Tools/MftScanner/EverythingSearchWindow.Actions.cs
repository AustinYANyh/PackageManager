using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MftScanner.Services;

namespace MftScanner
{
    public partial class EverythingSearchWindow
    {
        private static Key GetEffectiveKey(KeyEventArgs e)
        {
            return e.Key == Key.System ? e.SystemKey : e.Key;
        }

        private void EverythingSearchWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_isKeyboardScopeSelectionActive || (ScopeComboBox != null && ScopeComboBox.IsDropDownOpen))
            {
                return;
            }

            var key = GetEffectiveKey(e);
            if (_isTypeFilterKeyboardMode)
            {
                if (Keyboard.Modifiers == ModifierKeys.None && key == Key.Left)
                {
                    e.Handled = true;
                    MoveTypeFilterKeyboardSelection(-1);
                    return;
                }

                if (Keyboard.Modifiers == ModifierKeys.None && key == Key.Right)
                {
                    e.Handled = true;
                    MoveTypeFilterKeyboardSelection(1);
                    return;
                }

                if (Keyboard.Modifiers == ModifierKeys.None && key == Key.Home)
                {
                    e.Handled = true;
                    JumpTypeFilterKeyboardSelection(false);
                    return;
                }

                if (Keyboard.Modifiers == ModifierKeys.None && key == Key.End)
                {
                    e.Handled = true;
                    JumpTypeFilterKeyboardSelection(true);
                    return;
                }

                if (Keyboard.Modifiers == ModifierKeys.None && (key == Key.Enter || key == Key.Space))
                {
                    e.Handled = true;
                    CommitTypeFilterKeyboardMode();
                    return;
                }

                if (Keyboard.Modifiers == ModifierKeys.None && key == Key.Escape)
                {
                    e.Handled = true;
                    CancelTypeFilterKeyboardMode(restoreSearchInput: true);
                    return;
                }

                if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    CancelTypeFilterKeyboardMode(restoreSearchInput: false);
                }
            }

            FileSearchTypeFilter hotkeyFilter;
            if (Keyboard.Modifiers == ModifierKeys.Alt && TryMapTypeFilterHotkey(key, out hotkeyFilter))
            {
                e.Handled = true;
                CaptureSearchBoxInputState();
                CancelTypeFilterKeyboardMode(restoreSearchInput: false);
                ApplyTypeFilter(hotkeyFilter, restoreSearchInput: true);
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.F)
            {
                e.Handled = true;
                BeginTypeFilterKeyboardMode();
                return;
            }

            if (key == Key.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.L)
            {
                e.Handled = true;
                FocusSearchBoxAndSelectAll();
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.R)
            {
                e.Handled = true;
                _ = StartIndexingAsync(true);
                return;
            }

            if (key == Key.F5)
            {
                e.Handled = true;
                _ = ApplyFilterAsync(SearchBox.Text, false);
            }
        }

        private static bool TryMapTypeFilterHotkey(Key key, out FileSearchTypeFilter filter)
        {
            switch (key)
            {
                case Key.D1:
                case Key.NumPad1:
                    filter = FileSearchTypeFilter.All;
                    return true;
                case Key.D2:
                case Key.NumPad2:
                    filter = FileSearchTypeFilter.Launchable;
                    return true;
                case Key.D3:
                case Key.NumPad3:
                    filter = FileSearchTypeFilter.Folder;
                    return true;
                case Key.D4:
                case Key.NumPad4:
                    filter = FileSearchTypeFilter.Script;
                    return true;
                case Key.D5:
                case Key.NumPad5:
                    filter = FileSearchTypeFilter.Log;
                    return true;
                case Key.D6:
                case Key.NumPad6:
                    filter = FileSearchTypeFilter.Config;
                    return true;
                default:
                    filter = FileSearchTypeFilter.All;
                    return false;
            }
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            var key = GetEffectiveKey(e);
            if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.Down)
            {
                e.Handled = true;
                BeginKeyboardScopeSelection();
                return;
            }

            if (TryHandleResultShortcutKey(e, allowFirstResultFallback: true))
            {
                return;
            }

            if (key == Key.Enter)
            {
                e.Handled = true;
                var selected = ResultsGrid.SelectedItem as EverythingSearchResultItem;
                if (selected != null)
                {
                    OpenItem(selected, false);
                }
                else if (_displayedResults.Count > 0)
                {
                    ResultsGrid.SelectedItem = _displayedResults[0];
                    OpenItem(_displayedResults[0], false);
                }
            }
            else if (key == Key.Down && _displayedResults.Count > 0)
            {
                e.Handled = true;
                MoveSearchResultSelection(1);
            }
            else if (key == Key.Up && _displayedResults.Count > 0)
            {
                e.Handled = true;
                MoveSearchResultSelection(-1);
            }
            else if (key == Key.PageDown && _displayedResults.Count > 0)
            {
                e.Handled = true;
                ExecuteResultsGridNavigationKeyFromSearchBox(e);
            }
            else if (key == Key.PageUp && _displayedResults.Count > 0)
            {
                e.Handled = true;
                ExecuteResultsGridNavigationKeyFromSearchBox(e);
            }
            else if (key == Key.Home && _displayedResults.Count > 0)
            {
                e.Handled = true;
                ExecuteResultsGridNavigationKeyFromSearchBox(e);
            }
            else if (key == Key.End && _displayedResults.Count > 0)
            {
                e.Handled = true;
                ExecuteResultsGridNavigationKeyFromSearchBox(e);
            }
        }

        private void MoveSearchResultSelection(int delta)
        {
            if (_displayedResults.Count == 0)
                return;

            var currentItem = ResultsGrid.SelectedItem as EverythingSearchResultItem;
            var currentIndex = currentItem == null ? -1 : _displayedResults.IndexOf(currentItem);
            var nextIndex = currentIndex < 0
                ? (delta >= 0 ? 0 : _displayedResults.Count - 1)
                : Math.Max(0, Math.Min(_displayedResults.Count - 1, currentIndex + delta));
            var nextItem = _displayedResults[nextIndex];

            ResultsGrid.SelectedItem = nextItem;
            EnsureCurrentCellSelection(nextItem);
            ResultsGrid.ScrollIntoView(nextItem);
        }

        private void ExecuteResultsGridNavigationKeyFromSearchBox(KeyEventArgs originalArgs)
        {
            if (_displayedResults.Count == 0)
                return;

            var selectedItem = ResultsGrid.SelectedItem as EverythingSearchResultItem;
            if (selectedItem == null)
            {
                selectedItem = _displayedResults[0];
                ResultsGrid.SelectedItem = selectedItem;
                EnsureCurrentCellSelection(selectedItem);
            }

            var selectionStart = SearchBox.SelectionStart;
            var selectionLength = SearchBox.SelectionLength;
            var caretIndex = SearchBox.CaretIndex;

            ResultsGrid.Focus();
            EnsureCurrentCellSelection(ResultsGrid.SelectedItem as EverythingSearchResultItem);

            var presentationSource = PresentationSource.FromVisual(ResultsGrid) ?? PresentationSource.FromVisual(this);
            if (presentationSource != null)
            {
                var previewArgs = new KeyEventArgs(Keyboard.PrimaryDevice, presentationSource, Environment.TickCount, originalArgs.Key)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };
                ResultsGrid.RaiseEvent(previewArgs);

                if (!previewArgs.Handled)
                {
                    var keyArgs = new KeyEventArgs(Keyboard.PrimaryDevice, presentationSource, Environment.TickCount, originalArgs.Key)
                    {
                        RoutedEvent = Keyboard.KeyDownEvent
                    };
                    ResultsGrid.RaiseEvent(keyArgs);
                }
            }

            Dispatcher.BeginInvoke(new Action(delegate
            {
                SearchBox.Focus();
                SearchBox.Select(selectionStart, selectionLength);
                if (selectionLength == 0)
                    SearchBox.CaretIndex = caretIndex;
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void ResultsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            TryHandleResultShortcutKey(e, allowFirstResultFallback: false);
        }

        private bool TryHandleResultShortcutKey(KeyEventArgs e, bool allowFirstResultFallback)
        {
            var item = ResolveShortcutTarget(allowFirstResultFallback);
            if (item == null)
                return false;

            var key = GetEffectiveKey(e);
            if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                OpenItem(item, false);
                return true;
            }

            if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                OpenContainingFolder(item.FullPath);
                return true;
            }

            if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift)
            {
                e.Handled = true;
                OpenItem(item, true);
                return true;
            }

            if (key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                CopyToClipboard(item.FullPath);
                StatusText.Text = "路径已复制";
                return true;
            }

            if (key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                e.Handled = true;
                CopyToClipboard(item.FileName);
                StatusText.Text = "文件名已复制";
                return true;
            }

            if (key == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                e.Handled = true;
                AddToStartup(item, GetSelectedStartupGroupName());
                return true;
            }

            if (key == Key.T && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                OpenTerminal(item);
                return true;
            }

            if (key == Key.F2)
            {
                e.Handled = true;
                RenameItem(item);
                return true;
            }

            if (key == Key.Delete)
            {
                e.Handled = true;
                DeleteItem(item);
                return true;
            }

            if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                e.Handled = true;
                ShowProperties(item);
                return true;
            }

            return false;
        }

        private EverythingSearchResultItem ResolveShortcutTarget(bool allowFirstResultFallback)
        {
            var item = ResultsGrid.SelectedItem as EverythingSearchResultItem;
            if (item != null)
                return item;

            if (!allowFirstResultFallback || _displayedResults.Count == 0)
                return null;

            var firstItem = _displayedResults[0];
            ResultsGrid.SelectedItem = firstItem;
            EnsureCurrentCellSelection(firstItem);
            ResultsGrid.ScrollIntoView(firstItem);
            return firstItem;
        }

        private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var item = ResultsGrid.SelectedItem as EverythingSearchResultItem;
            if (item != null)
                OpenItem(item, false);
        }

        private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectedItemDetails(ResultsGrid.SelectedItem as EverythingSearchResultItem);
            UpdateActionButtons();
        }

        private async Task EnsureMetadataLoadedAsync(EverythingSearchResultItem item)
        {
            if (item == null || item.MetadataLoaded)
                return;

            FileMetadata metadata;
            if (!_metadataCache.TryGetValue(item.FullPath, out metadata))
            {
                metadata = await Task.Run(delegate { return ReadMetadata(item.FullPath, item.IsDirectory); }).ConfigureAwait(true);
                _metadataCache[item.FullPath] = metadata;
            }

            item.ApplyMetadata(metadata);
            if (ReferenceEquals(ResultsGrid.SelectedItem, item))
                UpdateSelectedItemDetails(item);
        }

        private static FileMetadata ReadMetadata(string fullPath, bool isDirectory)
        {
            try
            {
                if (isDirectory)
                {
                    var directory = new DirectoryInfo(fullPath);
                    return new FileMetadata { Exists = directory.Exists, SizeBytes = 0, ModifiedTime = directory.Exists ? directory.LastWriteTime : DateTime.MinValue };
                }

                var file = new FileInfo(fullPath);
                return new FileMetadata { Exists = file.Exists, SizeBytes = file.Exists ? file.Length : 0, ModifiedTime = file.Exists ? file.LastWriteTime : DateTime.MinValue };
            }
            catch
            {
                return new FileMetadata { Exists = false, SizeBytes = 0, ModifiedTime = DateTime.MinValue };
            }
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e) { ExecuteForSelected(OpenItem, false); }
        private void ForceRescanButton_Click(object sender, RoutedEventArgs e) { _ = StartIndexingAsync(true); }
        private void OpenFolderButton_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) OpenContainingFolder(item.FullPath); }
        private void CopyPathButton_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) { CopyToClipboard(item.FullPath); StatusText.Text = "路径已复制"; } }
        private void CopyNameButton_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) { CopyToClipboard(item.FileName); StatusText.Text = "文件名已复制"; } }
        private void AddToStartupButton_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) AddToStartup(item, GetSelectedStartupGroupName()); }
        private void RunAsAdminButton_Click(object sender, RoutedEventArgs e) { ExecuteForSelected(OpenItem, true); }
        private void PropertiesButton_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) ShowProperties(item); }
        private void OpenTerminalButton_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) OpenTerminal(item); }
        private void RenameButton_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) RenameItem(item); }
        private void DeleteButton_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) DeleteItem(item); }
        private void ClearSearchButton_Click(object sender, RoutedEventArgs e) { SelectScopeOption(string.Empty); SearchBox.Clear(); SearchBox.Focus(); }
        private void ClearScopeButton_Click(object sender, RoutedEventArgs e) { SelectScopeOption(string.Empty); SearchBox.Focus(); }
        private void SyntaxHelpButton_Click(object sender, RoutedEventArgs e) { MessageBox.Show("支持普通包含、^前缀、后缀$、/正则/、* 与 ? 通配符。\n路径限定通过下拉选择范围，不修改索引服务层；输入框可按 Alt+Down 打开。\n类型限定可用 Alt+1..6 直接切换，或 Alt+F 进入筛选模式。", "语法提示", MessageBoxButton.OK, MessageBoxImage.Information); }
        private void OpenMenuItem_Click(object sender, RoutedEventArgs e) { ExecuteForSelected(OpenItem, false); }
        private void OpenContainingFolder_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) OpenContainingFolder(item.FullPath); }
        private void CopyPath_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) { CopyToClipboard(item.FullPath); StatusText.Text = "路径已复制"; } }
        private void CopyNameMenuItem_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) { CopyToClipboard(item.FileName); StatusText.Text = "文件名已复制"; } }
        private void CopyParentPathMenuItem_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) { CopyToClipboard(item.DirectoryPath); StatusText.Text = "父目录路径已复制"; } }
        private void AddToStartupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var groupName = menuItem == null ? GetSelectedStartupGroupName() : menuItem.Tag as string;
            var item = ResultsGrid.SelectedItem as EverythingSearchResultItem;
            if (item != null)
                AddToStartup(item, groupName);
        }
        private void RunAsAdminMenuItem_Click(object sender, RoutedEventArgs e) { ExecuteForSelected(OpenItem, true); }
        private void OpenTerminalMenuItem_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) OpenTerminal(item); }
        private void RenameMenuItem_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) RenameItem(item); }
        private void DeleteMenuItem_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) DeleteItem(item); }
        private void PropertiesMenuItem_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) ShowProperties(item); }

        private void StartupGroupComboBox_DropDownOpened(object sender, EventArgs e)
        {
            RefreshStartupGroupOptions(GetSelectedStartupGroupName());
        }

        private void StartupGroupComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StartupGroupComboBox.SelectedItem is ComboOption option && !string.IsNullOrWhiteSpace(option.Key))
            {
                _selectedStartupGroupName = option.Key;
            }
        }

        private void ResultsContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            RefreshStartupGroupOptions(GetSelectedStartupGroupName());
            RefreshAddToStartupContextMenu();
        }

        private void ExecuteForSelected(Action<EverythingSearchResultItem, bool> action, bool elevated)
        {
            var item = ResultsGrid.SelectedItem as EverythingSearchResultItem;
            if (item != null)
                action(item, elevated);
        }

        private static void OpenItem(EverythingSearchResultItem item, bool elevated)
        {
            if (item == null)
                return;

            try
            {
                if (item.IsDirectory)
                {
                    if (!Directory.Exists(item.FullPath))
                    {
                        MessageBox.Show("文件夹不存在。", "文件搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                else if (!File.Exists(item.FullPath))
                {
                    MessageBox.Show("文件不存在。", "文件搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var startInfo = new ProcessStartInfo(item.FullPath) { UseShellExecute = true };
                if (elevated && !item.IsDirectory)
                    startInfo.Verb = "runas";
                Process.Start(startInfo);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开项目：" + ex.Message, "文件搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void OpenContainingFolder(string fullPath)
        {
            try
            {
                if (File.Exists(fullPath) || Directory.Exists(fullPath))
                    Process.Start("explorer.exe", "/select,\"" + fullPath + "\"");
                else
                    MessageBox.Show("项目不存在。", "文件搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开文件位置：" + ex.Message, "文件搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OpenTerminal(EverythingSearchResultItem item)
        {
            var targetDirectory = item.IsDirectory ? item.FullPath : item.DirectoryPath;
            if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
            {
                MessageBox.Show("目录不存在。", "文件搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                StartPowerShellTerminal(targetDirectory);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开终端：" + ex.Message, "文件搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void AddToStartup(EverythingSearchResultItem item, string groupName)
        {
            if (item == null)
                return;

            try
            {
                var result = _startupSettingsWriter.AddItem(item.FullPath, item.IsDirectory, groupName);
                if (!result.Success)
                {
                    MessageBox.Show(result.ErrorMessage ?? "加入启动项失败。", "文件搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SelectStartupGroup(result.GroupName);
                if (result.AlreadyExists)
                {
                    StatusText.Text = "已存在于启动项：" + result.ItemName + "（" + result.GroupName + "）";
                    return;
                }

                StatusText.Text = "已加入启动项：" + result.ItemName + "（" + result.GroupName + "）";
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "加入启动项失败");
                MessageBox.Show("加入启动项失败：" + ex.Message, "文件搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RefreshAddToStartupContextMenu()
        {
            if (AddToStartupMenuItem == null)
                return;

            AddToStartupMenuItem.Items.Clear();
            foreach (var option in _startupGroupOptions)
            {
                var menuItem = new MenuItem
                {
                    Header = option.DisplayName,
                    Tag = option.Key,
                    IsCheckable = true,
                    IsChecked = string.Equals(option.Key, GetSelectedStartupGroupName(), StringComparison.OrdinalIgnoreCase)
                };
                menuItem.Click += AddToStartupMenuItem_Click;
                AddToStartupMenuItem.Items.Add(menuItem);
            }
        }

        private static void StartPowerShellTerminal(string targetDirectory)
        {
            var powerShellPath = ResolvePowerShell7Path();
            var terminalPath = ResolveWindowsTerminalPath();
            var terminalTitle = GetTerminalTitle(targetDirectory);

            if (!string.IsNullOrWhiteSpace(terminalPath))
            {
                var arguments = "new-tab --title \"" + EscapeCommandLineArgument(terminalTitle) + "\" -d \"" + EscapeCommandLineArgument(targetDirectory) + "\" \"" + EscapeCommandLineArgument(powerShellPath) + "\" -NoLogo -NoExit";
                Process.Start(new ProcessStartInfo(terminalPath, arguments)
                {
                    UseShellExecute = true,
                    WorkingDirectory = targetDirectory,
                });
                return;
            }

            var argumentsFallback = "-NoLogo -NoExit";
            Process.Start(new ProcessStartInfo(powerShellPath, argumentsFallback)
            {
                UseShellExecute = true,
                WorkingDirectory = targetDirectory,
            });
        }

        private static string ResolvePowerShell7Path()
        {
            var candidates = new[]
            {
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\PowerShell\7\pwsh.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\PowerShell\7\pwsh.exe"),
                Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Microsoft\PowerShell\7\pwsh.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramW6432%\PowerShell\7\pwsh.exe"),
            };

            var candidate = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }

            throw new FileNotFoundException("未找到 PowerShell 7 的 pwsh.exe。");
        }

        private static string EscapePowerShellSingleQuotedString(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        private static string ResolveWindowsTerminalPath()
        {
            var candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
            return File.Exists(candidate) ? candidate : "wt.exe";
        }

        private static string GetTerminalTitle(string targetDirectory)
        {
            var trimmed = (targetDirectory ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var title = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(title) ? targetDirectory : title;
        }

        private static string EscapeCommandLineArgument(string value)
        {
            return (value ?? string.Empty).Replace("\"", "\\\"");
        }

        private void ShowProperties(EverythingSearchResultItem item)
        {
            try
            {
                if (item == null || string.IsNullOrWhiteSpace(item.FullPath))
                    return;

                if (item.IsDirectory)
                {
                    if (!Directory.Exists(item.FullPath))
                    {
                        MessageBox.Show("文件夹不存在。", "文件搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                else if (!File.Exists(item.FullPath))
                {
                    MessageBox.Show("文件不存在。", "文件搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!ShellPropertiesDialog.Show(item.FullPath, out var errorMessage))
                {
                    LoggingService.LogWarning("打开属性页失败：" + errorMessage + "，Path=" + item.FullPath);
                    MessageBox.Show("无法打开属性：" + errorMessage, "文件搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, "打开属性页失败");
                MessageBox.Show("无法打开属性：" + ex.Message, "文件搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RenameItem(EverythingSearchResultItem item)
        {
            var currentName = item.IsDirectory ? item.FileName : Path.GetFileName(item.FullPath);
            var nextName = ShowTextInputDialog("重命名", "请输入新名称：", currentName);
            if (string.IsNullOrWhiteSpace(nextName) || string.Equals(nextName, currentName, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var newPath = Path.Combine(item.DirectoryPath, nextName);
                if (item.IsDirectory) Directory.Move(item.FullPath, newPath); else File.Move(item.FullPath, newPath);
                _ = ApplyFilterAsync(SearchBox.Text, false);
                StatusText.Text = "已重命名：" + nextName;
            }
            catch (Exception ex)
            {
                MessageBox.Show("重命名失败：" + ex.Message, "文件搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void DeleteItem(EverythingSearchResultItem item)
        {
            var result = MessageBox.Show("确认永久删除“" + item.FileName + "”？", "文件搜索", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                if (item.IsDirectory) Directory.Delete(item.FullPath, true); else File.Delete(item.FullPath);
                ApplyLocalDeletedItem(item);
                _ = NotifyIndexDeletedAsync(item.FullPath, item.IsDirectory);
                StatusText.Text = "已删除：" + item.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show("删除失败：" + ex.Message, "文件搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CopyToClipboard(string text)
        {
            try { Clipboard.SetText(text ?? string.Empty); } catch { }
        }

        private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            _recentSearches.Clear();
            SaveWindowState();
        }

        private void RecentSearchList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var entry = RecentSearchList.SelectedItem as SearchHistoryEntry;
            if (entry == null) return;
            PopulateQueryInputs(entry.Query);
            FocusSearchBoxAndSelectAll();
        }

        private void RecentSearchList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var entry = RecentSearchList.SelectedItem as SearchHistoryEntry;
            if (entry == null) return;
            PopulateQueryInputs(entry.Query);
            _ = ApplyFilterAsync(SearchBox.Text, false);
        }

        private static void CancelToken(ref CancellationTokenSource cts)
        {
            if (cts == null) return;
            try { cts.Cancel(); cts.Dispose(); } catch { } finally { cts = null; }
        }

        private void ApplyLocalDeletedItem(EverythingSearchResultItem item)
        {
            if (item == null)
                return;

            if (!string.IsNullOrWhiteSpace(item.FullPath))
                _locallyDeletedPathSet.Add(item.FullPath);

            var lowerName = (item.FileName ?? Path.GetFileName(item.FullPath) ?? string.Empty).ToLowerInvariant();
            if (MatchesCurrentQueryAndType(lowerName, item.FullPath, item.IsDirectory) && _totalMatchedCount > 0)
                _totalMatchedCount--;

            if (RemoveDisplayedResult(item.FullPath, lowerName))
            {
                ApplyCurrentSort((ResultsGrid.SelectedItem as EverythingSearchResultItem)?.FullPath, false);
                _loadedResultCount = _displayedResults.Count;
            }

            UpdateSummaryStatus();
            UpdateEmptyState();
        }

        private async Task NotifyIndexDeletedAsync(string fullPath, bool isDirectory)
        {
            try
            {
                await _indexService.NotifyDeletedAsync(fullPath, isDirectory, CancellationToken.None).ConfigureAwait(true);
            }
            catch
            {
            }
        }

        private void CancelSearchToken()
        {
            var cts = _searchCts;
            if (cts == null) return;
            try { cts.Cancel(); } catch { } finally { _searchCts = null; }
        }

        private static string ShowTextInputDialog(string title, string prompt, string initialValue)
        {
            var window = new Window { Title = title, Width = 420, Height = 170, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
            var panel = new Grid { Margin = new Thickness(16) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var promptBlock = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 10) };
            var textBox = new TextBox { Text = initialValue ?? string.Empty, Margin = new Thickness(0, 0, 0, 12) };
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okButton = new Button { Content = "确定", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancelButton = new Button { Content = "取消", Width = 80, IsCancel = true };
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);

            Grid.SetRow(promptBlock, 0);
            Grid.SetRow(textBox, 1);
            Grid.SetRow(buttonPanel, 2);
            panel.Children.Add(promptBlock);
            panel.Children.Add(textBox);
            panel.Children.Add(buttonPanel);
            window.Content = panel;

            okButton.Click += delegate { window.DialogResult = true; window.Close(); };
            cancelButton.Click += delegate { window.DialogResult = false; window.Close(); };
            textBox.Focus();
            textBox.SelectAll();
            return window.ShowDialog() == true ? textBox.Text : null;
        }

        private static class ShellPropertiesDialog
        {
            private const int SwShow = 5;
            private const uint SeeMaskInvokeIdList = 0x0000000C;
            private const uint SeeMaskUnicode = 0x00004000;

            public static bool Show(string fullPath, out string errorMessage)
            {
                var info = new ShellExecuteInfo
                {
                    cbSize = Marshal.SizeOf(typeof(ShellExecuteInfo)),
                    fMask = SeeMaskInvokeIdList | SeeMaskUnicode,
                    lpVerb = "properties",
                    lpFile = fullPath,
                    nShow = SwShow
                };

                if (ShellExecuteEx(ref info))
                {
                    errorMessage = null;
                    return true;
                }

                var win32Error = Marshal.GetLastWin32Error();
                errorMessage = win32Error == 0
                    ? "ShellExecuteEx 返回失败。"
                    : new Win32Exception(win32Error).Message + " (Win32=" + win32Error + ")";
                return false;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct ShellExecuteInfo
            {
                public int cbSize;
                public uint fMask;
                public IntPtr hwnd;
                public string lpVerb;
                public string lpFile;
                public string lpParameters;
                public string lpDirectory;
                public int nShow;
                public IntPtr hInstApp;
                public IntPtr lpIDList;
                public string lpClass;
                public IntPtr hkeyClass;
                public uint dwHotKey;
                public IntPtr hIcon;
                public IntPtr hProcess;
            }

            [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool ShellExecuteEx(ref ShellExecuteInfo lpExecInfo);
        }
    }
}
