using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using PackageManager.Function.PackageManage;
using PackageManager.Models;
using PackageManager.Services;

namespace PackageManager.Views;

/// <summary>
/// 包管理配置页面（支持导航），复用窗口逻辑。
/// </summary>
public partial class PackageConfigPage : Page, INotifyPropertyChanged, ICentralPage, IPackageEditorHost
{
    private readonly DataPersistenceService dataService;

    private PackageItem selectedItem;

    public ObservableCollection<VisibilityItem> VisibilityItems { get; } = new();

    public PackageConfigPage()
    {
        InitializeComponent();
        dataService = new DataPersistenceService();
        DataContext = this;
        LoadData();
        dataService.LoadMainWindowState();
        LoadVisibilityData();
    }

    public event Action RequestExit;

    public event PropertyChangedEventHandler PropertyChanged;

    public ObservableCollection<PackageItem> AllItems { get; } = new();

    public PackageItem SelectedItem
    {
        get => selectedItem;

        set => SetProperty(ref selectedItem, value);
    }

    public void EditItem(PackageItem item, bool isNew)
    {
        if (item.IsBuiltIn)
        {
            MessageBox.Show("内置项不可编辑", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ownerWindow = Window.GetWindow(this);
        var win = new PackageEditWindow(item, isNew)
        {
            Owner = ownerWindow,
        };
        var result = win.ShowDialog();
        if ((result != true) && isNew)
        {
            AllItems.Remove(item);
            SelectedItem = null;
        }
    }

    public void RemoveItem(PackageItem item)
    {
        if (item.IsBuiltIn)
        {
            MessageBox.Show("内置项不可删除", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AllItems.Remove(item);
        if (ReferenceEquals(SelectedItem, item))
        {
            SelectedItem = null;
        }
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

    private void LoadData()
    {
        AllItems.Clear();
        foreach (var bi in dataService.GetBuiltInPackageConfigs())
        {
            AllItems.Add(PackageItem.From(bi, true, this));
        }

        foreach (var ci in dataService.LoadPackageConfigs())
        {
            AllItems.Add(PackageItem.From(ci, false, this));
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var item = new PackageItem(this)
        {
            ProductName = string.Empty,
            FtpServerPath = string.Empty,
            LocalPath = string.Empty,
            FinalizeFtpServerPath = string.Empty,
            SupportsConfigOps = true,
            IsBuiltIn = false,
        };
        AllItems.Add(item);
        SelectedItem = item;
        EditItem(item, true);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var customList = AllItems.Where(i => !i.IsBuiltIn).Select(PackageItem.ToConfig).ToList();
            var ok = dataService.SavePackageConfigs(customList);
            if (ok)
            {
                // 保存成功后立即让主窗体重载包配置，无需重启
                var main = Application.Current?.MainWindow as MainWindow;
                main?.ReloadPackagesFromConfig();

                LoggingService.LogInfo("保存成功，配置已立即生效，正在自动加载版本");
                RequestExit?.Invoke();
            }
            else
            {
                MessageBox.Show("保存失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "保存失败");
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        RequestExit?.Invoke();
    }

    private void OpenVisibilityPanel_Click(object sender, RoutedEventArgs e)
    {
        VisibilityPanel.Visibility = Visibility.Visible;
        // LoadVisibilityData();
    }

    private void LoadVisibilityData()
    {
        try
        {
            
            VisibilityItems.Clear();
            var names = dataService.GetBuiltInPackageConfigs().Select(i => i.ProductName)
                                   .Concat(dataService.LoadPackageConfigs().Select(i => i.ProductName))
                                   .Distinct()
                                   .ToList();
            var vis = dataService.GetProductVisibility();
            foreach (var name in names)
            {
                var flag = true;
                if (!string.IsNullOrWhiteSpace(name) && vis != null && vis.TryGetValue(name, out var v))
                {
                    flag = v;
                }
                VisibilityItems.Add(new VisibilityItem { ProductName = name, IsVisible = flag });
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "打开可见性设置失败");
        }
    }

    private void ConfirmVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var map = VisibilityItems
                .Where(i => !string.IsNullOrWhiteSpace(i.ProductName))
                .ToDictionary(i => i.ProductName, i => i.IsVisible);
            var main = Application.Current?.MainWindow as MainWindow;
            dataService.SaveProductVisibility(map);
            if (main != null)
            {
                dataService.SaveMainWindowState(main.Packages);
            }
            VisibilityPanel.Visibility = Visibility.Collapsed;
            main?.ReloadPackagesFromConfig();
        }
        catch (Exception ex)
        {
            LoggingService.LogError(ex, "保存可见性设置失败");
            MessageBox.Show($"保存可见性设置失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        VisibilityPanel.Visibility = Visibility.Collapsed;
    }
}
