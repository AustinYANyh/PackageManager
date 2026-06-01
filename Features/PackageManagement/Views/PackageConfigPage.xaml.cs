using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

    /// <summary>
    /// 获取产品可见性配置项的集合。
    /// </summary>
    public ObservableCollection<VisibilityItem> VisibilityItems { get; } = new();

    /// <summary>
    /// 初始化 <see cref="PackageConfigPage"/> 的新实例。
    /// </summary>
    public PackageConfigPage()
    {
        InitializeComponent();
        dataService = new DataPersistenceService();
        DataContext = this;
        LoadData();
        dataService.LoadMainWindowState();
        LoadVisibilityData();
        VisibilityItems.CollectionChanged += VisibilityItems_CollectionChanged;
        foreach (var item in VisibilityItems) { item.PropertyChanged += VisibilityItem_PropertyChanged; }
    }

    /// <summary>
    /// 请求退出当前页面的导航事件。
    /// </summary>
    public event Action RequestExit;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// 获取所有包配置项（内置 + 自定义）的集合。
    /// </summary>
    public ObservableCollection<PackageItem> AllItems { get; } = new();

    /// <summary>
    /// 获取或设置当前选中的包配置项。
    /// </summary>
    public PackageItem SelectedItem
    {
        get => selectedItem;

        set => SetProperty(ref selectedItem, value);
    }

    /// <summary>
    /// 打开编辑窗口编辑指定包配置项。
    /// </summary>
    /// <param name="item">要编辑的包配置项。</param>
    /// <param name="isNew">是否为新建项。</param>
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

    /// <summary>
    /// 从列表中移除指定的包配置项。
    /// </summary>
    /// <param name="item">要移除的包配置项。</param>
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

    /// <summary>
    /// 触发 <see cref="PropertyChanged"/> 事件。
    /// </summary>
    /// <param name="propertyName">发生更改的属性名称，默认为调用方成员名。</param>
    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 设置属性值并在值变化时触发 <see cref="PropertyChanged"/> 通知。
    /// </summary>
    /// <typeparam name="T">属性值的类型。</typeparam>
    /// <param name="field">属性后备字段的引用。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">属性名称，默认为调用方成员名。</param>
    /// <returns>如果值已更改返回 <c>true</c>，否则返回 <c>false</c>。</returns>
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
        LoadVisibilityData();
        UpdateToggleButtonText();
    }

    private void LoadVisibilityData()
    {
        try
        {
            foreach (var old in VisibilityItems) { old.PropertyChanged -= VisibilityItem_PropertyChanged; }
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
            UpdateToggleButtonText();
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
        LoadVisibilityData();
    }

    private void ToggleAllVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        var allSelected = VisibilityItems.Count > 0 && VisibilityItems.All(i => i.IsVisible);
        foreach (var item in VisibilityItems)
        {
            item.IsVisible = !allSelected;
        }
        UpdateToggleButtonText();
    }

    private void UpdateToggleButtonText()
    {
        if (ToggleAllVisibilityButton == null) return;
        var allSelected = VisibilityItems.Count > 0 && VisibilityItems.All(i => i.IsVisible);
        ToggleAllVisibilityButton.Content = allSelected ? "全不选" : "全选";
    }

    private void VisibilityItems_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (VisibilityItem item in e.NewItems)
            {
                item.PropertyChanged += VisibilityItem_PropertyChanged;
            }
        }
        if (e.OldItems != null)
        {
            foreach (VisibilityItem item in e.OldItems)
            {
                item.PropertyChanged -= VisibilityItem_PropertyChanged;
            }
        }
        UpdateToggleButtonText();
    }

    private void VisibilityItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VisibilityItem.IsVisible))
        {
            UpdateToggleButtonText();
        }
    }
}
