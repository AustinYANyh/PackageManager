using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using PackageManager.Services;

namespace PackageManager.Function.UnlockTool
{
    public partial class UnlockFilesWindow : Window
    {
        private readonly ObservableCollection<UnlockItem> _items = new ObservableCollection<UnlockItem>();
        private readonly PackageUpdateService _updateService = new PackageUpdateService();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public UnlockFilesWindow()
        {
            InitializeComponent();
            FilesListView.ItemsSource = _items;
            TargetDirTextBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        private static void UpdateDragEffects(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if ((paths != null) && paths.Any(p => Directory.Exists(p) || IsSupportedFile(p)))
                {
                    e.Effects = DragDropEffects.Copy;
                }
                else
                {
                    e.Effects = DragDropEffects.None;
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }

            e.Handled = true;
        }

        private static bool IsSupportedFile(string path)
        {
            return File.Exists(path);
        }

        private void SelectDirButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog();
            var res = dlg.ShowDialog();
            if (res == System.Windows.Forms.DialogResult.OK)
            {
                TargetDirTextBox.Text = dlg.SelectedPath;
                AddTargets(new[] { dlg.SelectedPath });
            }
        }

        private void AddFilesButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "所有文件|*.*",
                Multiselect = true,
            };
            var ok = dlg.ShowDialog() == true;
            if (!ok)
            {
                return;
            }

            AddTargets(dlg.FileNames);
        }

        private void ClearListButton_Click(object sender, RoutedEventArgs e)
        {
            _items.Clear();
        }

        private async void UnlockButton_Click(object sender, RoutedEventArgs e)
        {
            await UnlockAsync();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _cts.Cancel();
            Close();
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            UpdateDragEffects(e);
        }

        private void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            AddTargets(files);
        }

        private void FilesListView_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            AddTargets(files);
        }

        private void FilesListView_PreviewDragOver(object sender, DragEventArgs e)
        {
            UpdateDragEffects(e);
        }

        private void FilesListView_DragOver(object sender, DragEventArgs e)
        {
            UpdateDragEffects(e);
        }

        private void DropArea_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            AddTargets(files);
        }

        private void DropArea_PreviewDragOver(object sender, DragEventArgs e)
        {
            UpdateDragEffects(e);
        }

        private void DropArea_DragOver(object sender, DragEventArgs e)
        {
            UpdateDragEffects(e);
        }

        private void DropArea_DragEnter(object sender, DragEventArgs e)
        {
            UpdateDragEffects(e);
        }

        private void AddTargets(string[] paths)
        {
            if ((paths == null) || (paths.Length == 0))
            {
                return;
            }

            foreach (var p in paths)
            {
                if (!(Directory.Exists(p) || File.Exists(p)))
                {
                    continue;
                }

                if (_items.Any(i => string.Equals(i.FilePath, p, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _items.Add(new UnlockItem { FilePath = p, Status = "待处理" });
            }
        }

        private async Task UnlockAsync()
        {
            foreach (var item in _items)
            {
                item.Status = "处理中";
                item.Message = string.Empty;
            }

            var targets = _items.Select(i => i.FilePath).ToArray();
            try
            {
                var count = await _updateService.UnlockLocksForTargetsAsync(targets, _cts.Token);
                foreach (var item in _items)
                {
                    item.Status = "完成";
                    item.Message = count > 0 ? "已尝试解除占用" : "未发现占用";
                }
                ToastService.ShowToast("解除占用", count > 0 ? $"已处理 {count} 个进程" : "未发现占用", count > 0 ? "Success" : "Info");
            }
            catch (Exception ex)
            {
                foreach (var item in _items)
                {
                    item.Status = "失败";
                    item.Message = ex.Message;
                }
            }
        }

        private class UnlockItem : INotifyPropertyChanged
        {
            private string filePath;
            private string status;
            private string message;

            public string FilePath
            {
                get => filePath;
                set
                {
                    if (value == filePath) return;
                    filePath = value;
                    OnPropertyChanged();
                }
            }

            public string Status
            {
                get => status;
                set
                {
                    if (value == status) return;
                    status = value;
                    OnPropertyChanged();
                }
            }

            public string Message
            {
                get => message;
                set
                {
                    if (value == message) return;
                    message = value;
                    OnPropertyChanged();
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
