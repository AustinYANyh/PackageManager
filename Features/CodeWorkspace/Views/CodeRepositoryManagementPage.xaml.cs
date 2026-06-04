using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using PackageManager.Features.CodeWorkspace.Models;
using PackageManager.Services;
using PackageManager.Views;

namespace PackageManager.Features.CodeWorkspace.Views
{
    public partial class CodeRepositoryManagementPage : Page, INotifyPropertyChanged, ICentralPage
    {
        private readonly DataPersistenceService _dataPersistenceService;
        private RepositoryManagementRow _selectedRepository;
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

        public ObservableCollection<RepositoryManagementRow> Repositories { get; } = new ObservableCollection<RepositoryManagementRow>();

        public RepositoryManagementRow SelectedRepository
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
                         .Where(IsValidRepository))
            {
                Repositories.Add(RepositoryManagementRow.FromRepository(repo));
            }

            StatusText = $"已加载 {Repositories.Count} 个仓库。可拖放文件夹到页面中添加。";
        }

        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var path = FolderPickerService.PickFolder("选择代码仓库根目录");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            await AddRepositoryAsync(path);
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

        private async void RefreshProjectFilesButton_Click(object sender, RoutedEventArgs e)
        {
            var hasFailure = false;
            foreach (var repo in Repositories)
            {
                if (!await RefreshProjectFilesAsync(repo))
                {
                    hasFailure = true;
                }
            }

            StatusText = hasFailure ? "部分项目文件刷新失败，点击保存后生效。" : "项目文件已刷新，点击保存后生效。";
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

        private async void Page_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            var added = 0;
            foreach (var path in paths.Where(Directory.Exists))
            {
                if (await AddRepositoryAsync(path, saveImmediately: false))
                {
                    added++;
                }
            }

            StatusText = added > 0 ? $"已添加 {added} 个仓库，点击保存后生效。" : "未添加新仓库。";
        }

        private async Task<bool> AddRepositoryAsync(string path, bool saveImmediately = false)
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

            var repo = new RepositoryManagementRow
            {
                Name = new DirectoryInfo(path).Name,
                Path = path,
                LastUsed = DateTime.MinValue,
                UsageCount = 0,
            };
            Repositories.Add(repo);
            SelectedRepository = repo;
            StatusText = $"正在扫描项目文件: {repo.Name}";
            var scanOk = await RefreshProjectFilesAsync(repo);

            if (saveImmediately)
            {
                SaveRepositories();
            }
            else
            {
                StatusText = scanOk ? "已添加仓库，点击保存后生效。" : "已添加仓库，但项目文件扫描失败，点击保存后生效。";
            }

            return true;
        }

        private async Task<bool> RefreshProjectFilesAsync(RepositoryManagementRow repo)
        {
            if (repo == null || string.IsNullOrWhiteSpace(repo.Path) || !Directory.Exists(repo.Path))
            {
                return false;
            }

            try
            {
                repo.ProjectFiles = await Task.Run(() => ScanProjectFiles(repo.Path));
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"刷新仓库项目文件失败：{repo.Path}");
                MessageBox.Show($"扫描项目文件失败：{ex.Message}", "刷新项目文件", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusText = $"扫描项目文件失败: {ex.Message}";
                return false;
            }
        }

        private static System.Collections.Generic.List<string> ScanProjectFiles(string rootPath)
        {
            var slnFiles = EnumerateProjectFiles(rootPath, "*.sln")
                .Where(path => path.IndexOf("\\.vs\\", StringComparison.OrdinalIgnoreCase) < 0)
                .Take(100)
                .ToList();
            var csprojFiles = EnumerateProjectFiles(rootPath, "*.csproj")
                    .Take(100)
                    .ToList();

            return slnFiles
                .Concat(csprojFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void SaveRepositories()
        {
            var settings = _dataPersistenceService.LoadSettings();
            settings.CodeRepositories = Repositories
                .Where(IsValidRepository)
                .GroupBy(repo => NormalizePath(repo.Path), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First().ToRepository())
                .ToList();

            var ok = _dataPersistenceService.SaveSettings(settings);
            StatusText = ok ? $"已保存 {settings.CodeRepositories.Count} 个仓库。" : "保存失败，请查看日志。";
        }

        private static bool IsValidRepository(RepositoryManagementRow repo)
        {
            return repo != null && !string.IsNullOrWhiteSpace(repo.Path);
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

        public class RepositoryManagementRow : INotifyPropertyChanged
        {
            private string _name;
            private string _path;
            private string _note = "";
            private System.Collections.Generic.List<string> _projectFiles = new System.Collections.Generic.List<string>();
            private string _lastBuildProjectFile;
            private System.Collections.Generic.List<string> _lastBuildConfigurations = new System.Collections.Generic.List<string>();
            private string _lastBuildRestorePolicy = "Auto";
            private DateTime _lastUsed;
            private int _usageCount;
            private string _linkedPackageKey;
            private string _linkedPackageName;

            public event PropertyChangedEventHandler PropertyChanged;

            [DataGridColumn(1, DisplayName = "名称", Width = "180")]
            public string Name
            {
                get => _name;
                set => SetProperty(ref _name, value);
            }

            [DataGridColumn(2, DisplayName = "路径", Width = "*")]
            public string Path
            {
                get => _path;
                set => SetProperty(ref _path, value);
            }

            [DataGridColumn(3, DisplayName = "备注", Width = "130")]
            public string Note
            {
                get => _note;
                set => SetProperty(ref _note, value);
            }

            public System.Collections.Generic.List<string> ProjectFiles
            {
                get => _projectFiles;
                set
                {
                    if (SetProperty(ref _projectFiles, value ?? new System.Collections.Generic.List<string>()))
                    {
                        OnPropertyChanged(nameof(ProjectFileCount));
                    }
                }
            }

            public string LastBuildProjectFile
            {
                get => _lastBuildProjectFile;
                set => SetProperty(ref _lastBuildProjectFile, value);
            }

            public System.Collections.Generic.List<string> LastBuildConfigurations
            {
                get => _lastBuildConfigurations;
                set => SetProperty(ref _lastBuildConfigurations, value ?? new System.Collections.Generic.List<string>());
            }

            public string LastBuildRestorePolicy
            {
                get => string.IsNullOrWhiteSpace(_lastBuildRestorePolicy) ? "Auto" : _lastBuildRestorePolicy;
                set => SetProperty(ref _lastBuildRestorePolicy, string.IsNullOrWhiteSpace(value) ? "Auto" : value);
            }

            [DataGridColumn(4, DisplayName = "项目文件", Width = "80", IsReadOnly = true)]
            public int ProjectFileCount
            {
                get => ProjectFiles?.Count ?? 0;
                set { }
            }

            public DateTime LastUsed
            {
                get => _lastUsed;
                set
                {
                    if (SetProperty(ref _lastUsed, value))
                    {
                        OnPropertyChanged(nameof(LastUsedText));
                    }
                }
            }

            [DataGridColumn(5, DisplayName = "最后使用", Width = "130", IsReadOnly = true)]
            public string LastUsedText
            {
                get => LastUsed == DateTime.MinValue ? "从未使用" : LastUsed.ToString("yyyy-MM-dd HH:mm");
                set { }
            }

            [DataGridColumn(6, DisplayName = "次数", Width = "60", IsReadOnly = true)]
            public int UsageCount
            {
                get => _usageCount;
                set => SetProperty(ref _usageCount, value);
            }

            public string LinkedPackageKey
            {
                get => _linkedPackageKey;
                set => SetProperty(ref _linkedPackageKey, value);
            }

            public string LinkedPackageName
            {
                get => _linkedPackageName;
                set => SetProperty(ref _linkedPackageName, value);
            }

            public static RepositoryManagementRow FromRepository(CodeRepository repo)
            {
                return new RepositoryManagementRow
                {
                    Name = repo.Name,
                    Path = repo.Path,
                    Note = repo.Note ?? "",
                    ProjectFiles = repo.ProjectFiles == null
                        ? new System.Collections.Generic.List<string>()
                        : new System.Collections.Generic.List<string>(repo.ProjectFiles),
                    LastBuildProjectFile = repo.LastBuildProjectFile,
                    LastBuildConfigurations = repo.LastBuildConfigurations == null
                        ? new System.Collections.Generic.List<string>()
                        : new System.Collections.Generic.List<string>(repo.LastBuildConfigurations),
                    LastBuildRestorePolicy = repo.LastBuildRestorePolicy,
                    LastUsed = repo.LastUsed,
                    UsageCount = repo.UsageCount,
                    LinkedPackageKey = repo.LinkedPackageKey,
                    LinkedPackageName = repo.LinkedPackageName,
                };
            }

            public CodeRepository ToRepository()
            {
                return new CodeRepository
                {
                    Name = Name,
                    Path = Path,
                    Note = Note ?? "",
                    ProjectFiles = ProjectFiles == null
                        ? new System.Collections.Generic.List<string>()
                        : new System.Collections.Generic.List<string>(ProjectFiles),
                    LastBuildProjectFile = LastBuildProjectFile,
                    LastBuildConfigurations = LastBuildConfigurations == null
                        ? new System.Collections.Generic.List<string>()
                        : new System.Collections.Generic.List<string>(LastBuildConfigurations),
                    LastBuildRestorePolicy = LastBuildRestorePolicy,
                    LastUsed = LastUsed,
                    UsageCount = UsageCount,
                    LinkedPackageKey = LinkedPackageKey,
                    LinkedPackageName = LinkedPackageName,
                };
            }

            private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }

            private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
            {
                if (Equals(field, value))
                {
                    return false;
                }

                field = value;
                OnPropertyChanged(propertyName);
                return true;
            }
        }
    }
}
