using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using PackageManager.Services;

namespace PackageManager.Function.SlnTool
{
    public partial class SlnUpdateWindow : Window
    {
        private readonly ObservableCollection<SlnItem> _items = new ObservableCollection<SlnItem>();

        public SlnUpdateWindow()
        {
            InitializeComponent();
            FilesListView.ItemsSource = _items;
        }

        private static void UpdateDragEffects(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if ((e.Data.GetData(DataFormats.FileDrop) is string[] paths) && paths.Any(p => Directory.Exists(p) || System.IO.Path.GetExtension(p).Equals(".sln", StringComparison.OrdinalIgnoreCase)))
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

        private void AddFilesButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "解决方案文件|*.sln|所有文件|*.*",
                Multiselect = true,
            };
            var ok = dlg.ShowDialog() == true;
            if (!ok) return;
            AddPaths(dlg.FileNames);
        }

        private void ClearListButton_Click(object sender, RoutedEventArgs e)
        {
            _items.Clear();
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            await ProcessAllAsync();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            UpdateDragEffects(e);
        }

        private void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            AddPaths(files);
        }

        private void FilesListView_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            AddPaths(files);
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
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            AddPaths(files);
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

        private void AddPaths(IEnumerable<string> paths)
        {
            if (paths == null) return;
            var slns = new List<string>();
            foreach (var p in paths)
            {
                try
                {
                    if (Directory.Exists(p))
                    {
                        slns.AddRange(Directory.EnumerateFiles(p, "*.sln", SearchOption.AllDirectories));
                    }
                    else if (File.Exists(p) && System.IO.Path.GetExtension(p).Equals(".sln", StringComparison.OrdinalIgnoreCase))
                    {
                        slns.Add(p);
                    }
                }
                catch { }
            }
            foreach (var f in slns.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_items.Any(i => string.Equals(i.FilePath, f, StringComparison.OrdinalIgnoreCase))) continue;
                _items.Add(new SlnItem { FilePath = f, Status = "待处理" });
            }
        }

        private async Task ProcessAllAsync()
        {
            foreach (var item in _items) item.Status = "处理中";
            await Task.Run(() =>
            {
                foreach (var item in _items)
                {
                    try
                    {
                        var updater = new Updater();
                        var changed = updater.UpdateDependencies(item.FilePath);
                        item.Status = "完成";
                        item.Message = changed ? "已更新依赖" : "无改动";
                    }
                    catch (Exception ex)
                    {
                        item.Status = "失败";
                        item.Message = ex.Message;
                    }
                }
            });

            var okCount = _items.Count(i => i.Status == "完成");
            var failCount = _items.Count(i => i.Status == "失败");
            var info = $"成功 {okCount}，失败 {failCount}";
            ToastService.ShowToast("编译顺序更新", info, failCount == 0 ? "Success" : "Warning");
        }

        private class SlnItem : INotifyPropertyChanged
        {
            private string filePath;
            private string status;
            private string message;

            public string FilePath
            {
                get => filePath;
                set { if (value == filePath) return; filePath = value; OnPropertyChanged(); }
            }

            public string Status
            {
                get => status;
                set { if (value == status) return; status = value; OnPropertyChanged(); }
            }

            public string Message
            {
                get => message;
                set { if (value == message) return; message = value; OnPropertyChanged(); }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
