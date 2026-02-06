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
            if (_items.Count == 0)
            {
                ToastService.ShowToast("解除占用", "请先添加目标", "Warning");
                return;
            }
            foreach (var i in _items)
            {
                i.Status = "处理中";
                i.Message = string.Empty;
            }
            var targets = _items.Select(i => i.FilePath).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            try
            {
                var list = await _updateService.ListLockingProcessesForTargetsAsync(targets, _cts.Token);
                if (list == null || list.Count == 0)
                {
                    foreach (var i in _items)
                    {
                        i.Status = "完成";
                        i.Message = "未发现占用";
                    }
                    ToastService.ShowToast("解除占用", "未发现占用", "Info");
                    return;
                }
                var pids = list.Select(p => p.Id).Where(id => id > 0).Distinct().ToArray();
                var resultPath = await AdminElevationService.RunElevatedUnlockUiWithResultAsync(targets, pids);
                foreach (var i in _items)
                {
                    i.Status = string.IsNullOrEmpty(resultPath) ? "失败" : "已启动解除占用程序";
                    i.Message = string.IsNullOrEmpty(resultPath) ? "无法启动解除占用程序" : "请在解除占用窗口中终止占用进程";
                }
                if (!string.IsNullOrEmpty(resultPath))
                {
                    StartResultWatcher(resultPath);
                }
            }
            catch (Exception ex)
            {
                foreach (var i in _items)
                {
                    i.Status = "失败";
                    i.Message = ex.Message;
                }
            }
        }
        
        private void StartResultWatcher(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    var tries = 0;
                    while (!_cts.IsCancellationRequested && !File.Exists(path) && tries < 50)
                    {
                        await Task.Delay(200);
                        tries++;
                    }
                    if (!File.Exists(path)) return;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fs))
                    {
                        string line;
                        while (!_cts.IsCancellationRequested)
                        {
                            line = reader.ReadLine();
                            if (line == null)
                            {
                                await Task.Delay(200);
                                continue;
                            }
                            try
                            {
                                var obj = Newtonsoft.Json.Linq.JObject.Parse(line);
                                var pid = (int?)obj["pid"];
                                var success = (bool?)obj["success"];
                                var message = (string)obj["message"];
                                var completed = (bool?)obj["completed"];
                                if (pid.HasValue)
                                {
                                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        foreach (var i in _items)
                                        {
                                            i.Message = success == true
                                                ? $"已终止 PID {pid.Value}"
                                                : string.IsNullOrWhiteSpace(message) ? $"终止失败 PID {pid.Value}" : message;
                                        }
                                    }));
                                }
                                if (completed == true)
                                {
                                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        foreach (var i in _items) i.Status = "完成";
                                    }));
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch
                {
                }
            });
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
