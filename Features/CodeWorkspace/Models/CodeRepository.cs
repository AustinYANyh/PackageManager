using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;

namespace PackageManager.Features.CodeWorkspace.Models
{
    /// <summary>
    /// 表示一个代码仓库配置。
    /// </summary>
    public class CodeRepository : INotifyPropertyChanged
    {
        private string _name;
        private string _path;
        private DateTime _lastUsed;
        private int _usageCount;
        private string _note = "";
        private List<string> _projectFiles = new List<string>();

        public event PropertyChangedEventHandler PropertyChanged;

        [DataGridColumn(1, DisplayName = "仓库", Width = "250", IsReadOnly = true)]
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        [DataGridColumn(2, DisplayName = "路径", Width = "2.1*", IsReadOnly = true)]
        public string Path
        {
            get => _path;
            set => SetProperty(ref _path, value);
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

        public int UsageCount
        {
            get => _usageCount;
            set => SetProperty(ref _usageCount, value);
        }

        [DataGridColumn(3, DisplayName = "备注", Width = "180", IsReadOnly = true,IsVisible = false)]
        public string Note
        {
            get => _note;
            set => SetProperty(ref _note, value);
        }

        public List<string> ProjectFiles
        {
            get => _projectFiles;
            set
            {
                if (SetProperty(ref _projectFiles, value ?? new List<string>()))
                {
                    OnPropertyChanged(nameof(ProjectFileCount));
                }
            }
        }

        [DataGridColumn(4, DisplayName = "项目文件", Width = "80", IsReadOnly = true,IsVisible = false)]
        public int ProjectFileCount
        {
            get => ProjectFiles?.Count ?? 0;
            set { }
        }

        [DataGridColumn(5, DisplayName = "最后使用", Width = "140", IsReadOnly = true,IsVisible = false)]
        public string LastUsedText
        {
            get => LastUsed == DateTime.MinValue ? "从未使用" : LastUsed.ToString("yyyy-MM-dd HH:mm");
            set { }
        }

        [DataGridMultiButton(nameof(ActionButtons), 6, DisplayName = "操作", Width = "680", ButtonSpacing = 12)]
        public string Actions { get; set; }

        public ICommand CommitCommand { get; set; }

        public ICommand OpenVSCommand { get; set; }

        public ICommand OpenRiderCommand { get; set; }

        public ICommand OpenCursorCommand { get; set; }

        public ICommand OpenClaudeCommand { get; set; }

        public ICommand OpenCodexCommand { get; set; }

        public ICommand OpenFolderCommand { get; set; }

        public List<ButtonConfig> ActionButtons => new List<ButtonConfig>
        {
            new ButtonConfig { Text = "提交", Width = 60, Height = 26, CommandProperty = nameof(CommitCommand) },
            new ButtonConfig { Text = "VS", Width = 54, Height = 26, CommandProperty = nameof(OpenVSCommand) },
            new ButtonConfig { Text = "Rider", Width = 60, Height = 26, CommandProperty = nameof(OpenRiderCommand) },
            new ButtonConfig { Text = "Cursor", Width = 64, Height = 26, CommandProperty = nameof(OpenCursorCommand) },
            new ButtonConfig { Text = "Claude", Width = 68, Height = 26, CommandProperty = nameof(OpenClaudeCommand) },
            new ButtonConfig { Text = "Codex", Width = 64, Height = 26, CommandProperty = nameof(OpenCodexCommand) },
            new ButtonConfig { Text = "文件夹", Width = 68, Height = 26, CommandProperty = nameof(OpenFolderCommand) },
        };

        public CodeRepository Clone()
        {
            return new CodeRepository
            {
                Name = Name,
                Path = Path,
                LastUsed = LastUsed,
                UsageCount = UsageCount,
                Note = Note ?? "",
                ProjectFiles = ProjectFiles == null ? new List<string>() : new List<string>(ProjectFiles),
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
