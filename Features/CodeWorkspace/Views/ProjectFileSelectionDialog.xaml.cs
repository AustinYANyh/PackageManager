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
        private readonly bool _enableBuildConfigurations;
        private readonly string _preferredProjectFile;
        private readonly HashSet<string> _preferredConfigurations;
        private readonly string _preferredRestorePolicy;
        private ProjectFileItem _selectedProjectFileItem;
        private bool _showCsproj;
        private string _statusText;
        private string _selectedBuildConfiguration;
        private BuildRestorePolicyOption _selectedBuildRestorePolicy;

        public ProjectFileSelectionDialog(
            IEnumerable<string> projectFiles,
            string rootPath,
            bool enableBuildConfigurations = false,
            string preferredProjectFile = null,
            IEnumerable<string> preferredConfigurations = null,
            string preferredRestorePolicy = null)
        {
            InitializeComponent();
            _rootPath = rootPath;
            _enableBuildConfigurations = enableBuildConfigurations;
            _preferredProjectFile = preferredProjectFile;
            _preferredRestorePolicy = NormalizeRestorePolicy(preferredRestorePolicy);
            _preferredConfigurations = new HashSet<string>(
                preferredConfigurations ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            _projectFiles = (projectFiles ?? Enumerable.Empty<string>())
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _showCsproj = ShouldShowCsprojInitially();
            DataContext = this;
            ReloadProjectFiles();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<ProjectFileItem> ProjectFiles { get; } = new ObservableCollection<ProjectFileItem>();

        public ObservableCollection<string> BuildConfigurations { get; } = new ObservableCollection<string>();

        public ObservableCollection<BuildRestorePolicyOption> BuildRestorePolicies { get; } =
            new ObservableCollection<BuildRestorePolicyOption>
            {
                new BuildRestorePolicyOption("Auto", "自动"),
                new BuildRestorePolicyOption("Always", "始终 Restore"),
                new BuildRestorePolicyOption("Never", "不 Restore"),
            };

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

        public Visibility BuildConfigurationVisibility => _enableBuildConfigurations ? Visibility.Visible : Visibility.Collapsed;

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

        public bool CanConfirm => SelectedProjectFileItem != null &&
            (!_enableBuildConfigurations ||
             (!string.IsNullOrWhiteSpace(SelectedBuildConfiguration) && SelectedBuildRestorePolicy != null));

        public string SelectedBuildConfiguration
        {
            get => _selectedBuildConfiguration;
            set
            {
                if (_selectedBuildConfiguration != value)
                {
                    _selectedBuildConfiguration = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanConfirm));
                }
            }
        }

        public BuildRestorePolicyOption SelectedBuildRestorePolicy
        {
            get => _selectedBuildRestorePolicy;
            set
            {
                if (!ReferenceEquals(_selectedBuildRestorePolicy, value))
                {
                    _selectedBuildRestorePolicy = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanConfirm));
                }
            }
        }

        public ProjectFileItem SelectedProjectFileItem
        {
            get => _selectedProjectFileItem;
            set
            {
                if (!ReferenceEquals(_selectedProjectFileItem, value))
                {
                    _selectedProjectFileItem = value;
                    LoadBuildConfigurations();
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanConfirm));
                }
            }
        }

        public string SelectedProjectFile { get; private set; }

        public List<string> SelectedConfigurations { get; private set; } = new List<string>();

        public string SelectedRestorePolicy { get; private set; } = "Auto";

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            if (!CanConfirm)
            {
                DialogResult = false;
                return;
            }

            SelectedProjectFile = SelectedProjectFileItem.FullPath;
            SelectedConfigurations = BuildSelectedConfigurations();
            SelectedRestorePolicy = SelectedBuildRestorePolicy?.Value ?? "Auto";
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

            SelectedProjectFileItem = SelectInitialProjectFileItem();
            StatusText = ProjectFiles.Count == 0
                ? $"未找到 {extension} 文件"
                : $"已找到 {ProjectFiles.Count} 个 {extension} 文件";
        }

        private ProjectFileItem SelectInitialProjectFileItem()
        {
            if (!string.IsNullOrWhiteSpace(_preferredProjectFile))
            {
                var preferred = ProjectFiles.FirstOrDefault(item =>
                    string.Equals(Path.GetFullPath(item.FullPath), Path.GetFullPath(_preferredProjectFile), StringComparison.OrdinalIgnoreCase));
                if (preferred != null)
                {
                    return preferred;
                }
            }

            return ProjectFiles.FirstOrDefault();
        }

        private List<string> BuildSelectedConfigurations()
        {
            if (!_enableBuildConfigurations)
            {
                return new List<string>();
            }

            return BuildConfigurations
                .Where(config => string.Equals(config, SelectedBuildConfiguration, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private void LoadBuildConfigurations()
        {
            BuildConfigurations.Clear();
            SelectedBuildConfiguration = null;
            SelectedBuildRestorePolicy = null;
            if (!_enableBuildConfigurations || SelectedProjectFileItem == null)
            {
                OnPropertyChanged(nameof(CanConfirm));
                return;
            }

            var configurations = IsSlnFile(SelectedProjectFileItem.FullPath)
                ? ReadSolutionConfigurations(SelectedProjectFileItem.FullPath)
                : new List<string> { "Debug", "Release" };
            if (configurations.Count == 0)
            {
                configurations.Add("Debug");
                configurations.Add("Release");
            }

            foreach (var configuration in configurations)
            {
                BuildConfigurations.Add(configuration);
            }

            SelectedBuildConfiguration = SelectDefaultBuildConfiguration(configurations);
            SelectedBuildRestorePolicy = SelectDefaultRestorePolicy();
            OnPropertyChanged(nameof(CanConfirm));
        }

        private static List<string> ReadSolutionConfigurations(string solutionFile)
        {
            var configurations = new List<string>();
            var inSection = false;
            foreach (var rawLine in File.ReadLines(solutionFile))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("GlobalSection(SolutionConfigurationPlatforms)", StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                    continue;
                }

                if (!inSection)
                {
                    continue;
                }

                if (line.StartsWith("EndGlobalSection", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var equalsIndex = line.IndexOf('=');
                if (equalsIndex < 0)
                {
                    continue;
                }

                var left = line.Substring(0, equalsIndex).Trim();
                var pipeIndex = left.IndexOf('|');
                if (pipeIndex <= 0)
                {
                    continue;
                }

                var configuration = left.Substring(0, pipeIndex).Trim();
                if (!string.IsNullOrWhiteSpace(configuration) &&
                    !configurations.Contains(configuration, StringComparer.OrdinalIgnoreCase))
                {
                    configurations.Add(configuration);
                }
            }

            return configurations;
        }

        private string SelectDefaultBuildConfiguration(IReadOnlyList<string> configurations)
        {
            var preferred = configurations.FirstOrDefault(config => _preferredConfigurations.Contains(config));
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return preferred;
            }

            var debug2024 = configurations.FirstOrDefault(config => string.Equals(config, "Debug2024", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(debug2024))
            {
                return debug2024;
            }

            var debug = configurations.FirstOrDefault(config => string.Equals(config, "Debug", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(debug))
            {
                return debug;
            }

            var latestYearDebug = configurations
                .Select(config => new { Configuration = config, Year = TryReadDebugYear(config) })
                .Where(item => item.Year.HasValue)
                .OrderByDescending(item => item.Year.Value)
                .Select(item => item.Configuration)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(latestYearDebug))
            {
                return latestYearDebug;
            }

            return configurations.FirstOrDefault(config => string.Equals(config, "Debug", StringComparison.OrdinalIgnoreCase))
                ?? configurations.FirstOrDefault(config => config.StartsWith("Debug", StringComparison.OrdinalIgnoreCase))
                ?? configurations.FirstOrDefault();
        }

        private static int? TryReadDebugYear(string configuration)
        {
            const string debugPrefix = "Debug";
            if (string.IsNullOrWhiteSpace(configuration) ||
                !configuration.StartsWith(debugPrefix, StringComparison.OrdinalIgnoreCase) ||
                configuration.Length <= debugPrefix.Length)
            {
                return null;
            }

            var suffix = configuration.Substring(debugPrefix.Length);
            return int.TryParse(suffix, out var year) ? year : (int?)null;
        }

        private BuildRestorePolicyOption SelectDefaultRestorePolicy()
        {
            return BuildRestorePolicies.FirstOrDefault(option =>
                       string.Equals(option.Value, _preferredRestorePolicy, StringComparison.OrdinalIgnoreCase))
                   ?? BuildRestorePolicies.First(option => option.Value == "Auto");
        }

        private static string NormalizeRestorePolicy(string restorePolicy)
        {
            if (string.Equals(restorePolicy, "Always", StringComparison.OrdinalIgnoreCase))
            {
                return "Always";
            }

            if (string.Equals(restorePolicy, "Never", StringComparison.OrdinalIgnoreCase))
            {
                return "Never";
            }

            return "Auto";
        }

        private bool ShouldShowCsprojInitially()
        {
            if (!string.IsNullOrWhiteSpace(_preferredProjectFile) &&
                File.Exists(_preferredProjectFile) &&
                IsCsprojFile(_preferredProjectFile))
            {
                return true;
            }

            return !_projectFiles.Any(IsSlnFile) && _projectFiles.Any(IsCsprojFile);
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

        public sealed class BuildRestorePolicyOption
        {
            public BuildRestorePolicyOption(string value, string displayName)
            {
                Value = value;
                DisplayName = displayName;
            }

            public string Value { get; }

            public string DisplayName { get; }

            public override string ToString()
            {
                return DisplayName;
            }
        }

    }
}
