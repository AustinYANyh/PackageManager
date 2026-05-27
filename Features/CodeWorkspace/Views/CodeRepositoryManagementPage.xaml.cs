using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using PackageManager.Features.CodeWorkspace.Models;
using PackageManager.Services;
using PackageManager.Views;

namespace PackageManager.Features.CodeWorkspace.Views
{
    public partial class CodeRepositoryManagementPage : Page, INotifyPropertyChanged, ICentralPage
    {
        private readonly DataPersistenceService _dataPersistenceService;
        private CodeRepository _selectedRepository;
        private string _statusText;

        public CodeRepositoryManagementPage()
        {
            InitializeComponent();
            _dataPersistenceService = ServiceLocator.Resolve<DataPersistenceService>() ?? new DataPersistenceService();
            DataContext = this;
            LoadRepositories();
        }

        public event Action RequestExit;

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<CodeRepository> Repositories { get; } = new ObservableCollection<CodeRepository>();

        public CodeRepository SelectedRepository
        {
            get => _selectedRepository;
            set => SetProperty(ref _selectedRepository, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private void LoadRepositories()
        {
            Repositories.Clear();
            var settings = _dataPersistenceService.LoadSettings();
            foreach (var repo in (settings.CodeRepositories ?? new System.Collections.Generic.List<CodeRepository>())
                         .Where(IsValidRepository)
                         .OrderByDescending(r => r.LastUsed)
                         .ThenBy(r => r.Name))
            {
                Repositories.Add(repo.Clone());
            }

            StatusText = $"已加载 {Repositories.Count} 个仓库。可拖放文件夹到页面中添加。";
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var path = FolderPickerService.PickFolder("选择代码仓库根目录");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            AddRepository(path);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRepository == null)
            {
                MessageBox.Show("请先选择要删除的仓库。", "代码仓库管理", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Repositories.Remove(SelectedRepository);
            SelectedRepository = null;
            StatusText = "已删除仓库，点击保存后生效。";
        }

        private void RefreshProjectFilesButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var repo in Repositories)
            {
                RefreshProjectFiles(repo);
            }

            StatusText = "项目文件已刷新，点击保存后生效。";
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveRepositories();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            RequestExit?.Invoke();
        }

        private void Page_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Page_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            var added = 0;
            foreach (var path in paths.Where(Directory.Exists))
            {
                if (AddRepository(path, saveImmediately: false))
                {
                    added++;
                }
            }

            StatusText = added > 0 ? $"已添加 {added} 个仓库，点击保存后生效。" : "未添加新仓库。";
        }

        private bool AddRepository(string path, bool saveImmediately = false)
        {
            path = NormalizePath(path);
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                MessageBox.Show("仓库路径不存在。", "代码仓库管理", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (Repositories.Any(r => string.Equals(NormalizePath(r.Path), path, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText = "仓库已存在。";
                return false;
            }

            var repo = new CodeRepository
            {
                Name = new DirectoryInfo(path).Name,
                Path = path,
                LastUsed = DateTime.MinValue,
                UsageCount = 0,
            };
            RefreshProjectFiles(repo);
            Repositories.Add(repo);
            SelectedRepository = repo;

            if (saveImmediately)
            {
                SaveRepositories();
            }
            else
            {
                StatusText = "已添加仓库，点击保存后生效。";
            }

            return true;
        }

        private void RefreshProjectFiles(CodeRepository repo)
        {
            if (repo == null || string.IsNullOrWhiteSpace(repo.Path) || !Directory.Exists(repo.Path))
            {
                return;
            }

            try
            {
                var slnFiles = EnumerateProjectFiles(repo.Path, "*.sln")
                    .Where(path => path.IndexOf("\\.vs\\", StringComparison.OrdinalIgnoreCase) < 0)
                    .Take(100)
                    .ToList();

                repo.ProjectFiles = slnFiles.Count > 0
                    ? slnFiles
                    : EnumerateProjectFiles(repo.Path, "*.csproj").Take(100).ToList();
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"刷新仓库项目文件失败：{repo.Path}");
            }
        }

        private void SaveRepositories()
        {
            var settings = _dataPersistenceService.LoadSettings();
            settings.CodeRepositories = Repositories
                .Where(IsValidRepository)
                .GroupBy(repo => NormalizePath(repo.Path), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First().Clone())
                .OrderByDescending(repo => repo.LastUsed)
                .ThenBy(repo => repo.Name)
                .ToList();

            var ok = _dataPersistenceService.SaveSettings(settings);
            StatusText = ok ? $"已保存 {settings.CodeRepositories.Count} 个仓库。" : "保存失败，请查看日志。";
        }

        private static bool IsValidRepository(CodeRepository repo)
        {
            return repo != null && !string.IsNullOrWhiteSpace(repo.Path);
        }

        private static System.Collections.Generic.IEnumerable<string> EnumerateProjectFiles(string rootPath, string pattern)
        {
            return Directory.EnumerateFiles(rootPath, pattern, SearchOption.AllDirectories)
                .Where(path => path.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) < 0)
                .Where(path => path.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) < 0);
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim();
            }
        }

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}
