using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;

namespace PackageManager.Features.CodeWorkspace.Views
{
    public partial class ProjectFileSelectionDialog : Window, INotifyPropertyChanged
    {
        private readonly string _rootPath;
        private readonly List<string> _projectFiles;
        private ProjectFileItem _selectedProjectFileItem;
        private bool _showCsproj;
        private string _statusText;

        public ProjectFileSelectionDialog(IEnumerable<string> projectFiles, string rootPath)
        {
            InitializeComponent();
            DataContext = this;
            _rootPath = rootPath;
            _projectFiles = (projectFiles ?? Enumerable.Empty<string>())
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _showCsproj = !_projectFiles.Any(IsSlnFile) && _projectFiles.Any(IsCsprojFile);
            ReloadProjectFiles();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<ProjectFileItem> ProjectFiles { get; } = new ObservableCollection<ProjectFileItem>();

        public bool ShowCsproj
        {
            get => _showCsproj;
            set
            {
                if (_showCsproj != value)
                {
                    _showCsproj = value;
                    OnPropertyChanged();
                    ReloadProjectFiles();
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set
            {
                if (_statusText != value)
                {
                    _statusText = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool CanConfirm => SelectedProjectFileItem != null;

        public ProjectFileItem SelectedProjectFileItem
        {
            get => _selectedProjectFileItem;
            set
            {
                if (!ReferenceEquals(_selectedProjectFileItem, value))
                {
                    _selectedProjectFileItem = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanConfirm));
                }
            }
        }

        public string SelectedProjectFile { get; private set; }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedProjectFileItem == null)
            {
                DialogResult = false;
                return;
            }

            SelectedProjectFile = SelectedProjectFileItem.FullPath;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            OK_Click(sender, e);
        }

        private void ProjectFilesGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            if (!(e.Column is DataGridTextColumn textColumn) ||
                (e.PropertyName != nameof(ProjectFileItem.DisplayName) &&
                 e.PropertyName != nameof(ProjectFileItem.ProjectFileName)))
            {
                return;
            }

            var style = new Style(typeof(TextBlock), textColumn.ElementStyle);
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(ProjectFileItem.RelativePath))));
            textColumn.ElementStyle = style;
        }

        private void ReloadProjectFiles()
        {
            var extension = ShowCsproj ? ".csproj" : ".sln";
            var files = _projectFiles.Where(ShowCsproj ? IsCsprojFile : IsSlnFile);
            LoadProjectFiles(files, extension);
        }

        private void LoadProjectFiles(IEnumerable<string> files, string extension)
        {
            ProjectFiles.Clear();
            var items = files
                .Select(file => new ProjectFileItem
                {
                    FullPath = file,
                    DisplayName = Path.GetFileNameWithoutExtension(file),
                    ProjectFileName = Path.GetFileName(file),
                    RelativePath = GetRelativePathSafe(_rootPath, file),
                })
                .OrderBy(item => item.ProjectFileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var item in items)
            {
                ProjectFiles.Add(item);
            }

            SelectedProjectFileItem = ProjectFiles.FirstOrDefault();
            StatusText = ProjectFiles.Count == 0
                ? $"未找到 {extension} 文件"
                : $"已找到 {ProjectFiles.Count} 个 {extension} 文件";
        }

        private static string GetRelativePathSafe(string rootPath, string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(filePath))
                {
                    return filePath;
                }

                var rootUri = new Uri(AppendDirectorySeparatorChar(Path.GetFullPath(rootPath)));
                var fileUri = new Uri(Path.GetFullPath(filePath));
                return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString())
                    .Replace('/', Path.DirectorySeparatorChar);
            }
            catch
            {
                return filePath;
            }
        }

        private static string AppendDirectorySeparatorChar(string path)
        {
            if (string.IsNullOrEmpty(path) || path.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                return path;
            }

            return path + Path.DirectorySeparatorChar;
        }

        private static bool IsSlnFile(string filePath)
        {
            return string.Equals(Path.GetExtension(filePath), ".sln", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCsprojFile(string filePath)
        {
            return string.Equals(Path.GetExtension(filePath), ".csproj", StringComparison.OrdinalIgnoreCase);
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public sealed class ProjectFileItem
        {
            public string FullPath { get; set; }

            [DataGridColumn(1, DisplayName = "项目名称", Width = "190", IsReadOnly = true)]
            public string DisplayName { get; set; }

            [DataGridColumn(2, DisplayName = "项目文件", Width = "210", IsReadOnly = true)]
            public string ProjectFileName { get; set; }

            public string RelativePath { get; set; }
        }
    }
}
