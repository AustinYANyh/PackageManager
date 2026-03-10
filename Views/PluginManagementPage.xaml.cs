using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using PackageManager.Models;
using PackageManager.Services;

namespace PackageManager.Views;

public partial class PluginManagementPage : Page, INotifyPropertyChanged, ICentralPage
{
    private static readonly HashSet<string> ExcludedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".dll",
        ".sig",
        ".zip",
        ".config",
    };

    private readonly DataPersistenceService dataPersistenceService;
    private readonly ApplicationFinderService applicationFinderService;
    private string addinRootPathText;
    private string currentVersionFolder;
    private string currentVersionFolderText;
    private ApplicationVersion selectedRevitExecutableVersion;

    public PluginManagementPage(DataPersistenceService dataPersistenceService, ApplicationFinderService applicationFinderService)
    {
        InitializeComponent();
        this.dataPersistenceService = dataPersistenceService ?? throw new ArgumentNullException(nameof(dataPersistenceService));
        this.applicationFinderService = applicationFinderService ?? throw new ArgumentNullException(nameof(applicationFinderService));
        DataContext = this;

        LoadAddinRootPath();
        LoadRevitExecutableVersions();
        RefreshPlugins();
    }

    public event Action RequestExit;

    public event PropertyChangedEventHandler PropertyChanged;

    public ObservableCollection<ApplicationVersion> RevitExecutableVersions { get; } = new();

    public ObservableCollection<PluginAddinInfo> Plugins { get; } = new();

    public string AddinRootPathText
    {
        get => addinRootPathText;
        set => SetProperty(ref addinRootPathText, value);
    }

    public ApplicationVersion SelectedRevitExecutableVersion
    {
        get => selectedRevitExecutableVersion;
        set => SetProperty(ref selectedRevitExecutableVersion, value);
    }

    public string CurrentVersionFolderText
    {
        get => currentVersionFolderText;
        set => SetProperty(ref currentVersionFolderText, value);
    }

    private void LoadAddinRootPath()
    {
        var settings = dataPersistenceService.LoadSettings();
        var path = settings?.AddinPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = @"C:\ProgramData\Autodesk\Revit\Addins";
        }

        AddinRootPathText = path;
    }

    private void LoadRevitExecutableVersions()
    {
        try
        {
            var programName = "Revit";
            var versions = dataPersistenceService.GetCachedData(programName) ?? new List<ApplicationVersion>();
            if (!versions.Any())
            {
                versions = applicationFinderService.FindAllApplicationVersions(programName) ?? new List<ApplicationVersion>();
                if (versions.Any())
                {
                    dataPersistenceService.UpdateCachedData(programName, versions);
                }
            }

            var merged = versions
                         .Where(v => !string.IsNullOrWhiteSpace(v?.Version))
                         .GroupBy(v => v.Version, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First())
                         .ToDictionary(v => v.Version, StringComparer.OrdinalIgnoreCase);

            foreach (var folderVersion in EnumerateVersionFolders())
            {
                if (!merged.ContainsKey(folderVersion))
                {
                    merged[folderVersion] = new ApplicationVersion
                    {
                        Name = "Revit",
                        Version = folderVersion,
                    };
                }
            }

            foreach (var version in merged.Values.OrderByDescending(v => ParseVersionNumber(v.Version)).ThenByDescending(v => v.Version))
            {
                RevitExecutableVersions.Add(version);
            }

            SelectedRevitExecutableVersion = RevitExecutableVersions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "加载Revit可执行版本失败");
        }
    }

    private IEnumerable<string> EnumerateVersionFolders()
    {
        if (string.IsNullOrWhiteSpace(AddinRootPathText) || !Directory.Exists(AddinRootPathText))
        {
            return Enumerable.Empty<string>();
        }

        return Directory.EnumerateDirectories(AddinRootPathText, "*", SearchOption.TopDirectoryOnly)
                        .Select(Path.GetFileName)
                        .Where(name => !string.IsNullOrWhiteSpace(name) && name.All(char.IsDigit))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
    }

    private void RefreshPlugins()
    {
        Plugins.Clear();

        if (SelectedRevitExecutableVersion == null)
        {
            currentVersionFolder = null;
            CurrentVersionFolderText = "当前版本目录: 未找到可执行版本";
            return;
        }

        currentVersionFolder = Path.Combine(AddinRootPathText ?? string.Empty, SelectedRevitExecutableVersion.Version ?? string.Empty);
        CurrentVersionFolderText = Directory.Exists(currentVersionFolder)
            ? $"当前版本目录: {currentVersionFolder}"
            : $"当前版本目录不存在: {currentVersionFolder}";

        if (!Directory.Exists(currentVersionFolder))
        {
            return;
        }

        var files = Directory.EnumerateFiles(currentVersionFolder, "*", SearchOption.TopDirectoryOnly)
                             .Where(path => !ExcludedExtensions.Contains(Path.GetExtension(path) ?? string.Empty))
                             .OrderBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
                             .ThenBy(path => Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var item = new PluginAddinInfo();
            item.UpdateFromPath(file);
            Plugins.Add(item);
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedVersion = SelectedRevitExecutableVersion?.Version;
        LoadAddinRootPath();

        RevitExecutableVersions.Clear();
        LoadRevitExecutableVersions();
        if (!string.IsNullOrWhiteSpace(selectedVersion))
        {
            SelectedRevitExecutableVersion = RevitExecutableVersions.FirstOrDefault(v => string.Equals(v.Version, selectedVersion, StringComparison.OrdinalIgnoreCase))
                                             ?? SelectedRevitExecutableVersion;
        }

        RefreshPlugins();
    }

    private void RevitVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((e.AddedItems.Count > 0) && (e.AddedItems[0] is ApplicationVersion version))
        {
            SelectedRevitExecutableVersion = version;
        }

        RefreshPlugins();
    }

    private void PluginEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if ((sender is not CheckBox checkBox) || (checkBox.DataContext is not PluginAddinInfo item))
        {
            return;
        }

        var targetEnabled = checkBox.IsChecked == true;
        var originalPath = item.FullPath;

        try
        {
            if (string.IsNullOrWhiteSpace(originalPath) || !File.Exists(originalPath))
            {
                throw new FileNotFoundException("插件文件不存在", originalPath);
            }

            var targetPath = targetEnabled ? GetEnabledFilePath(originalPath) : GetDisabledFilePath(originalPath);
            if (!string.Equals(originalPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(targetPath))
                {
                    throw new IOException($"目标文件已存在：{Path.GetFileName(targetPath)}");
                }

                File.Move(originalPath, targetPath);
                item.UpdateFromPath(targetPath);
            }
            else
            {
                item.IsEnabled = targetEnabled;
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "切换插件启用状态失败");
            item.UpdateFromPath(originalPath);
            MessageBox.Show($"切换插件状态失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenVersionFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(currentVersionFolder) || !Directory.Exists(currentVersionFolder))
            {
                MessageBox.Show("当前版本目录不存在，请先检查 Addin 路径或选择的版本。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Process.Start(currentVersionFolder);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开插件目录失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        RequestExit?.Invoke();
    }

    private static string GetEnabledFilePath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        return Path.Combine(directory, name + ".addin");
    }

    private static string GetDisabledFilePath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var index = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(directory, $"{name}.addin{index}");
            index++;
        }
        while (File.Exists(candidate) && !string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase));

        return candidate;
    }

    private static int ParseVersionNumber(string version)
    {
        return int.TryParse(version, out var result) ? result : 0;
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
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
