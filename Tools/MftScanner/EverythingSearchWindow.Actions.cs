using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MftScanner
{
    public partial class EverythingSearchWindow
    {
        private void EverythingSearchWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L)
            {
                e.Handled = true;
                FocusSearchBoxAndSelectAll();
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.R)
            {
                e.Handled = true;
                _ = StartIndexingAsync(true);
                return;
            }

            if (e.Key == Key.F5)
            {
                e.Handled = true;
                _ = ApplyFilterAsync(SearchBox.Text, false);
            }
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
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
            else if (e.Key == Key.Down && _displayedResults.Count > 0)
            {
                e.Handled = true;
                ResultsGrid.SelectedItem = _displayedResults[0];
                ResultsGrid.Focus();
            }
            else if (e.Key == Key.Up && _displayedResults.Count > 0)
            {
                e.Handled = true;
                ResultsGrid.SelectedItem = _displayedResults[_displayedResults.Count - 1];
                ResultsGrid.Focus();
            }
        }

        private void ResultsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var item = ResultsGrid.SelectedItem as EverythingSearchResultItem;
            if (item == null)
                return;

            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; OpenItem(item, false); }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; OpenContainingFolder(item.FullPath); }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift) { e.Handled = true; OpenItem(item, true); }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; CopyToClipboard(item.FullPath); StatusText.Text = "路径已复制"; }
            else if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { e.Handled = true; CopyToClipboard(item.FileName); StatusText.Text = "文件名已复制"; }
            else if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; OpenTerminal(item); }
            else if (e.Key == Key.F2) { e.Handled = true; RenameItem(item); }
            else if (e.Key == Key.Delete) { e.Handled = true; DeleteItem(item); }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; ShowProperties(item); }
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
        private void RunAsAdminButton_Click(object sender, RoutedEventArgs e) { ExecuteForSelected(OpenItem, true); }
        private void PropertiesButton_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) ShowProperties(item); }
        private void OpenTerminalButton_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) OpenTerminal(item); }
        private void RenameButton_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) RenameItem(item); }
        private void DeleteButton_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) DeleteItem(item); }
        private void ClearSearchButton_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }
        private void SyntaxHelpButton_Click(object sender, RoutedEventArgs e) { MessageBox.Show("支持普通包含、^前缀、后缀$、/正则/、* 与 ? 通配符。", "语法提示", MessageBoxButton.OK, MessageBoxImage.Information); }
        private void OpenMenuItem_Click(object sender, RoutedEventArgs e) { ExecuteForSelected(OpenItem, false); }
        private void OpenContainingFolder_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) OpenContainingFolder(item.FullPath); }
        private void CopyPath_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) { CopyToClipboard(item.FullPath); StatusText.Text = "路径已复制"; } }
        private void CopyNameMenuItem_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) { CopyToClipboard(item.FileName); StatusText.Text = "文件名已复制"; } }
        private void CopyParentPathMenuItem_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) { CopyToClipboard(item.DirectoryPath); StatusText.Text = "父目录路径已复制"; } }
        private void RunAsAdminMenuItem_Click(object sender, RoutedEventArgs e) { ExecuteForSelected(OpenItem, true); }
        private void OpenTerminalMenuItem_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) OpenTerminal(item); }
        private void RenameMenuItem_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) RenameItem(item); }
        private void DeleteMenuItem_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) DeleteItem(item); }
        private void PropertiesMenuItem_Click(object sender, RoutedEventArgs e) { var item = ResultsGrid.SelectedItem as EverythingSearchResultItem; if (item != null) ShowProperties(item); }

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
                Process.Start(new ProcessStartInfo("cmd.exe", "/K cd /d \"" + targetDirectory + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开终端：" + ex.Message, "文件搜索", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ShowProperties(EverythingSearchResultItem item)
        {
            try
            {
                Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true, Verb = "properties" });
            }
            catch (Exception ex)
            {
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
                _ = ApplyFilterAsync(SearchBox.Text, false);
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
            SearchBox.Text = entry.Query;
            FocusSearchBoxAndSelectAll();
        }

        private void RecentSearchList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var entry = RecentSearchList.SelectedItem as SearchHistoryEntry;
            if (entry == null) return;
            SearchBox.Text = entry.Query;
            _ = ApplyFilterAsync(entry.Query, false);
        }

        private static void CancelToken(ref CancellationTokenSource cts)
        {
            if (cts == null) return;
            try { cts.Cancel(); cts.Dispose(); } catch { } finally { cts = null; }
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
    }
}
