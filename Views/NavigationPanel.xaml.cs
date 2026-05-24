using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PackageManager.Models;
using PackageManager.Services;
using PackageManager.Shell;

namespace PackageManager.Views;

/// <summary>
/// 左侧导航面板，承载系统功能入口列表。
/// </summary>
public partial class NavigationPanel : UserControl
{
    private NavigationActionItem lastSelectedItem;

    private bool revertingSelection;

    private NavigationService _navigationService;

    /// <summary>
    /// 初始化 <see cref="NavigationPanel"/> 的新实例。
    /// </summary>
    public NavigationPanel()
    {
        InitializeComponent();
        Loaded += NavigationPanel_Loaded;
    }

    /// <summary>
    /// 获取统一的系统入口列表数据源。
    /// </summary>
    public ObservableCollection<NavigationActionItem> ActionItems { get; } = new();

    /// <summary>
    /// 按名称选中对应的导航项。
    /// </summary>
    /// <param name="name">要选中的导航项名称。</param>
    public void SelectActionByName(string name)
    {
        var item = ActionItems.FirstOrDefault(i => i.Name == name);
        if (item == null)
        {
            return;
        }

        revertingSelection = true;
        ActionListBox.SelectedItem = item;
        lastSelectedItem = item;
        revertingSelection = false;
    }

    private void NavigationPanel_Loaded(object sender, RoutedEventArgs e)
    {
        _navigationService = ServiceLocator.Resolve<NavigationService>();
        if (_navigationService == null)
        {
            return;
        }

        var registry = _navigationService.Registry;

        // 构建统一的导航动作列表
        ActionItems.Clear();

        // 主页入口
        ActionItems.Add(new NavigationActionItem
        {
            Name = "产品分类",
            Glyph = "",
            Command = new RelayCommand(() => _navigationService.NavigateHome())
        });

        // 从 ToolRegistry 构建其余导航项
        foreach (var tool in registry.Tools)
        {
            var key = tool.Key;
            ActionItems.Add(new NavigationActionItem
            {
                Name = tool.DisplayName,
                Glyph = tool.Glyph,
                Command = new RelayCommand(() => _navigationService.NavigateTo(key))
            });
        }

        // 启动时默认选中"产品分类"
        lastSelectedItem = ActionItems.FirstOrDefault(i => i.Name == "产品分类") ?? ActionItems.FirstOrDefault();
        if (lastSelectedItem != null)
        {
            revertingSelection = true;
            ActionListBox.SelectedItem = lastSelectedItem;
            revertingSelection = false;
        }

        // 监听导航事件以同步选中项
        _navigationService.Navigated += name =>
        {
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                SelectActionByName(name);
            }));
        };
    }

    private void ActionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (revertingSelection)
        {
            return;
        }

        var listBox = sender as ListBox;
        var item = listBox?.SelectedItem as NavigationActionItem;
        var cmd = item?.Command;

        var before = _navigationService?.NavigationVersion ?? 0;

        if (cmd?.CanExecute(null) == true)
        {
            cmd.Execute(null);
        }

        var after = _navigationService?.NavigationVersion ?? before;
        if (after == before)
        {
            revertingSelection = true;
            listBox.SelectedItem = lastSelectedItem;
            revertingSelection = false;
        }
        else
        {
            lastSelectedItem = item;
        }
    }

    private void CategoryChildListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (revertingSelection)
        {
            return;
        }

        var listBox = sender as ListBox;
        var item = listBox?.SelectedItem as NavigationActionItem;
        var cmd = item?.Command;

        var before = _navigationService?.NavigationVersion ?? 0;

        if (cmd?.CanExecute(null) == true)
        {
            cmd.Execute(null);
        }

        var after = _navigationService?.NavigationVersion ?? before;
        if (after == before)
        {
            revertingSelection = true;
            listBox.SelectedItem = lastSelectedItem;
            revertingSelection = false;
        }
        else
        {
            lastSelectedItem = item;
        }
    }

    /// <summary>
    /// 导航动作项，表示左侧面板中的单个功能入口。
    /// </summary>
    public class NavigationActionItem
    {
        /// <summary>
        /// 获取或设置导航项的显示名称。
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 获取或设置导航项的图标字形。
        /// </summary>
        public string Glyph { get; set; }

        /// <summary>
        /// 获取或设置点击该导航项时执行的命令。
        /// </summary>
        public ICommand Command { get; set; }

        /// <summary>
        /// 获取子导航项集合。
        /// </summary>
        public ObservableCollection<NavigationActionItem> Children { get; } = new();
    }
}
