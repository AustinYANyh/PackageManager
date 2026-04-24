using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using Newtonsoft.Json;

namespace MftScanner
{
    public enum FileSearchTypeFilter
    {
        All,
        Launchable,
        Folder,
        Script,
        Log,
        Config
    }

    public sealed class EverythingSearchResultItem
    {
        public EverythingSearchResultItem(string fullPath, bool isDirectory)
        {
            FullPath = fullPath ?? string.Empty;
            IsDirectory = isDirectory;
            FileName = Path.GetFileName(FullPath);
            DirectoryPath = Path.GetDirectoryName(FullPath) ?? string.Empty;
            Extension = isDirectory ? string.Empty : (Path.GetExtension(FullPath) ?? string.Empty).ToLowerInvariant();
            TypeText = ResolveTypeText(isDirectory, Extension);
            SizeText = string.Empty;
            ModifiedText = string.Empty;
            ModifiedTime = DateTime.MinValue;
        }

        public EverythingSearchResultItem(ScannedFileInfo source)
            : this(source == null ? string.Empty : source.FullPath, source != null && source.IsDirectory)
        {
        }

        [DataGridColumn(1, DisplayName = "名称", Width = "220", IsReadOnly = true)]
        public string FileName { get; set; }

        [DataGridColumn(2, DisplayName = "路径", Width = "560", IsReadOnly = true)]
        public string DirectoryPath { get; set; }

        [DataGridColumn(3, DisplayName = "类型", Width = "120", IsReadOnly = true)]
        public string TypeText { get; set; }

        [DataGridColumn(4, DisplayName = "大小", Width = "110", IsReadOnly = true)]
        public string SizeText { get; set; }

        [DataGridColumn(5, DisplayName = "修改时间", Width = "160", IsReadOnly = true)]
        public string ModifiedText { get; set; }

        public string FullPath { get; set; }
        public bool IsDirectory { get; set; }
        public string Extension { get; set; }
        public long SizeBytes { get; set; }
        public DateTime ModifiedTime { get; set; }
        public bool MetadataLoaded { get; set; }

        public void ApplyMetadata(FileMetadata metadata)
        {
            MetadataLoaded = true;
            if (metadata == null || !metadata.Exists)
            {
                SizeBytes = 0;
                ModifiedTime = DateTime.MinValue;
                SizeText = string.Empty;
                ModifiedText = string.Empty;
                return;
            }

            SizeBytes = metadata.SizeBytes;
            ModifiedTime = metadata.ModifiedTime;
            SizeText = IsDirectory ? string.Empty : FormatSize(metadata.SizeBytes);
            ModifiedText = metadata.ModifiedTime == DateTime.MinValue
                ? string.Empty
                : metadata.ModifiedTime.ToString("yyyy-MM-dd HH:mm:ss");
        }

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            var units = new[] { "B", "KB", "MB", "GB", "TB" };
            var size = (double)bytes;
            var index = 0;
            while (size >= 1024d && index < units.Length - 1)
            {
                size /= 1024d;
                index++;
            }

            return index == 0 ? string.Format("{0:F0} {1}", size, units[index]) : string.Format("{0:F2} {1}", size, units[index]);
        }

        public static bool IsLaunchableExtension(string extension)
        {
            return extension == ".exe" || extension == ".bat" || extension == ".cmd" || extension == ".ps1" || extension == ".lnk";
        }

        public static bool IsScriptExtension(string extension)
        {
            return extension == ".bat" || extension == ".cmd" || extension == ".ps1";
        }

        public static bool IsLogExtension(string extension)
        {
            return extension == ".log" || extension == ".txt";
        }

        public static bool IsConfigExtension(string extension)
        {
            return extension == ".json" || extension == ".xml" || extension == ".ini" || extension == ".config" || extension == ".yaml" || extension == ".yml";
        }

        private static string ResolveTypeText(bool isDirectory, string extension)
        {
            if (isDirectory) return "文件夹";
            if (IsLaunchableExtension(extension)) return "可执行";
            if (IsScriptExtension(extension)) return "脚本";
            if (IsLogExtension(extension)) return "日志";
            if (IsConfigExtension(extension)) return "配置";
            return "文件";
        }
    }

    public sealed class FileMetadata
    {
        public bool Exists { get; set; }
        public long SizeBytes { get; set; }
        public DateTime ModifiedTime { get; set; }
    }

    internal sealed class SearchHistoryEntry
    {
        public string Query { get; set; }
        public DateTime Timestamp { get; set; }

        [JsonIgnore]
        public string TimeText
        {
            get { return Timestamp.ToString("MM-dd HH:mm"); }
        }
    }

    internal sealed class SearchWindowState
    {
        public string SortKey { get; set; }
        public string ScopePath { get; set; }
        public string ViewModeKey { get; set; }
        public List<SearchHistoryEntry> RecentSearches { get; set; }
    }

    internal sealed class SearchWindowStateStore
    {
        private readonly string _filePath;

        public SearchWindowStateStore()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PackageManager", "MftScanner");
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "file_search_state.json");
        }

        public SearchWindowState Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return new SearchWindowState { RecentSearches = new List<SearchHistoryEntry>() };

                var json = File.ReadAllText(_filePath);
                var state = JsonConvert.DeserializeObject<SearchWindowState>(json) ?? new SearchWindowState();
                state.RecentSearches = state.RecentSearches ?? new List<SearchHistoryEntry>();
                return state;
            }
            catch
            {
                return new SearchWindowState { RecentSearches = new List<SearchHistoryEntry>() };
            }
        }

        public void Save(SearchWindowState state)
        {
            try
            {
                state = state ?? new SearchWindowState();
                state.RecentSearches = state.RecentSearches ?? new List<SearchHistoryEntry>();
                File.WriteAllText(_filePath, JsonConvert.SerializeObject(state, Formatting.Indented));
            }
            catch
            {
            }
        }
    }

    internal sealed class ComboOption
    {
        public ComboOption(string key, string displayName)
        {
            Key = key;
            DisplayName = displayName;
        }

        public string Key { get; private set; }
        public string DisplayName { get; private set; }
    }

    internal sealed class ReplaceableObservableCollection<T> : ObservableCollection<T>
    {
        public void ReplaceAll(IList<T> items)
        {
            CheckReentrancy();
            Items.Clear();
            if (items != null)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    Items.Add(items[i]);
                }
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public void AddRange(IList<T> items)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            CheckReentrancy();
            for (var i = 0; i < items.Count; i++)
            {
                Items.Add(items[i]);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
