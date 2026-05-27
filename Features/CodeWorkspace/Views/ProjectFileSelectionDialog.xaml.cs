using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;

namespace PackageManager.Features.CodeWorkspace.Views
{
    public partial class ProjectFileSelectionDialog : Window, INotifyPropertyChanged
    {
        private ProjectFileItem _selectedProjectFileItem;

        public ProjectFileSelectionDialog(IEnumerable<string> projectFiles, string rootPath)
        {
            InitializeComponent();
            DataContext = this;

            var items = (projectFiles ?? Enumerable.Empty<string>())
                .Where(File.Exists)
                .Select(file => new ProjectFileItem
                {
                    FullPath = file,
                    DisplayName = Path.GetFileNameWithoutExtension(file),
                    RelativePath = GetRelativePathSafe(rootPath, file),
                })
                .OrderBy(item => item.RelativePath)
                .ToList();

            foreach (var item in items)
            {
                ProjectFiles.Add(item);
            }

            SelectedProjectFileItem = ProjectFiles.FirstOrDefault();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<ProjectFileItem> ProjectFiles { get; } = new ObservableCollection<ProjectFileItem>();

        public ProjectFileItem SelectedProjectFileItem
        {
            get => _selectedProjectFileItem;
            set
            {
                if (!ReferenceEquals(_selectedProjectFileItem, value))
                {
                    _selectedProjectFileItem = value;
                    OnPropertyChanged();
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

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public sealed class ProjectFileItem
        {
            public string FullPath { get; set; }

            [DataGridColumn(1, DisplayName = "项目名称", Width = "190", IsReadOnly = true)]
            public string DisplayName { get; set; }

            [DataGridColumn(2, DisplayName = "相对路径", Width = "330", IsReadOnly = true)]
            public string RelativePath { get; set; }
        }
    }
}
