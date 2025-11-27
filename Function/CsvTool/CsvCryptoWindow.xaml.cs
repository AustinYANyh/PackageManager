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
using Endelib;
using Microsoft.Win32;
using PackageManager.Services;

namespace PackageManager.Function.CsvTool
{
    public partial class CsvCryptoWindow : Window
    {
        private readonly ObservableCollection<CsvItem> _items = new ObservableCollection<CsvItem>();

        private Mode _mode = Mode.Decrypt;

        private readonly EncryptingAndDecryptingTxtTool encryptingAndDecryptingTxtTool;

        public CsvCryptoWindow()
        {
            InitializeComponent();
            FilesListView.ItemsSource = _items;
            encryptingAndDecryptingTxtTool = new EncryptingAndDecryptingTxtTool();
            OutputDirTextBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        private static void UpdateDragEffects(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if ((files != null) && files.Any(f => System.IO.Path.GetExtension(f).Equals(".csv", StringComparison.OrdinalIgnoreCase)))
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

        private void DecryptRadio_Checked(object sender, RoutedEventArgs e)
        {
            _mode = Mode.Decrypt;
        }

        private void EncryptRadio_Checked(object sender, RoutedEventArgs e)
        {
            _mode = Mode.Encrypt;
        }

        private void SelectOutputDirButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog();
            var res = dlg.ShowDialog();
            if (res == System.Windows.Forms.DialogResult.OK)
            {
                OutputDirTextBox.Text = dlg.SelectedPath;
            }
        }

        private void AddFilesButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "CSV文件|*.csv|所有文件|*.*",
                Multiselect = true,
            };
            var ok = dlg.ShowDialog() == true;
            if (!ok)
            {
                return;
            }

            AddFiles(dlg.FileNames);
        }

        private void ClearListButton_Click(object sender, RoutedEventArgs e)
        {
            _items.Clear();
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ProcessAllAsync();
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
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            AddFiles(files);
        }

        private void FilesListView_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            AddFiles(files);
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
            AddFiles(files);
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

        private void AddFiles(string[] files)
        {
            if ((files == null) || (files.Length == 0))
            {
                return;
            }

            foreach (var f in files.Where(f => System.IO.Path.GetExtension(f).Equals(".csv", StringComparison.OrdinalIgnoreCase)))
            {
                if (_items.Any(i => i.FilePath.Equals(f, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _items.Add(new CsvItem { FilePath = f, Status = "待处理" });
            }
        }

        private async Task ProcessAllAsync()
        {
            var outDir = OutputDirTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(outDir) || !Directory.Exists(outDir))
            {
                ToastService.ShowToast("输出目录不存在", outDir ?? string.Empty, "Error");
                return;
            }

            foreach (var item in _items)
            {
                item.Status = "处理中";
            }

            await Task.Run(() =>
            {
                foreach (var item in _items)
                {
                    try
                    {
                        var inputText = File.ReadAllText(item.FilePath, Encoding.GetEncoding("GB2312"));
                        var name = System.IO.Path.GetFileNameWithoutExtension(item.FilePath);
                        var outName = _mode == Mode.Encrypt ? name + ".encrypted.csv" : name + ".decrypted.csv";
                        var outPath = System.IO.Path.Combine(outDir, outName);

                        if (_mode == Mode.Decrypt)
                        {
                            bool isEncrypted = encryptingAndDecryptingTxtTool.IsEncrypted(item.FilePath);
                            if (!isEncrypted)
                            {
                                File.WriteAllText(outPath, inputText, Encoding.GetEncoding("GB2312"));
                                item.Status = "完成";
                                item.Message = "未加密，原文已输出";
                                continue;
                            }

                            try
                            {
                                using (var dst = encryptingAndDecryptingTxtTool.DecryptToStream(item.FilePath))
                                using (var reader = new StreamReader(dst, Encoding.GetEncoding("GB2312")))
                                {
                                    dst.Position = 0;
                                    string output = reader.ReadToEnd();
                                    File.WriteAllText(outPath, output, Encoding.GetEncoding("GB2312"));
                                    item.Status = "完成";
                                    item.Message = "解密成功";
                                    item.OutputPath = outPath;
                                }
                            }
                            catch
                            {
                                File.WriteAllText(outPath, inputText, Encoding.GetEncoding("GB2312"));
                                item.Status = "完成";
                                item.Message = "未加密或解密失败，原文已输出";
                                item.OutputPath = outPath;
                            }
                        }
                        else
                        {
                            encryptingAndDecryptingTxtTool.Encrypt(item.FilePath);
                            item.Status = "完成";
                            item.Message = "加密成功";
                            item.OutputPath = outPath;
                        }
                    }
                    catch (Exception ex)
                    {
                        item.Status = "失败";
                        item.Message = ex.Message;
                    }
                }
            });

            ToastService.ShowToast("处理完成", $"共 {_items.Count} 个", "Success");
        }

        private class CsvItem : INotifyPropertyChanged
        {
            private string filePath;

            private string status;

            private string message;

            private string outputPath;

            public string FilePath
            {
                get => filePath;

                set
                {
                    if (value == filePath)
                    {
                        return;
                    }

                    filePath = value;
                    OnPropertyChanged();
                }
            }

            public string Status
            {
                get => status;

                set
                {
                    if (value == status)
                    {
                        return;
                    }

                    status = value;
                    OnPropertyChanged();
                }
            }

            public string Message
            {
                get => message;

                set
                {
                    if (value == message)
                    {
                        return;
                    }

                    message = value;
                    OnPropertyChanged();
                }
            }

            public string OutputPath
            {
                get => outputPath;

                set
                {
                    if (value == outputPath)
                    {
                        return;
                    }

                    outputPath = value;
                    OnPropertyChanged();
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }

            protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
            {
                if (EqualityComparer<T>.Default.Equals(field, value)) return false;
                field = value;
                OnPropertyChanged(propertyName);
                return true;
            }
        }

        private enum Mode
        {
            Decrypt,

            Encrypt,
        }
    }
}
