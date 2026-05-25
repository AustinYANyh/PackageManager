using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PackageManager.Models;
using PackageManager.Services;
using PackageManager.Shell;

namespace PackageManager.Views;

public partial class NavigationPanel : UserControl
{
    private NavigationActionItem lastSelectedItem;

    private bool revertingSelection;

    private NavigationService _navigationService;

    public NavigationPanel()
    {
        InitializeComponent();
        Loaded += NavigationPanel_Loaded;
    }

    public ObservableCollection<NavigationActionItem> ActionItems { get; } = new();

    public void SelectActionByName(string name)
    {
        var item = ActionItems.FirstOrDefault(i => i.Name == name && !i.IsGroupHeader);
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

        ActionItems.Clear();

        // 仪表盘入口（Home）
        ActionItems.Add(new NavigationActionItem
        {
            Name = "仪表盘",
            Glyph = "",
            Command = new RelayCommand(() => _navigationService.NavigateHome())
        });

        // 按 Group 分组构建导航项
        string lastGroup = null;
        foreach (var tool in registry.Tools)
        {
            if (!string.IsNullOrEmpty(tool.Group) && tool.Group != lastGroup)
            {
                ActionItems.Add(new NavigationActionItem
                {
                    Name = tool.Group,
                    IsGroupHeader = true
                });
                lastGroup = tool.Group;
            }

            var key = tool.Key;
            ActionItems.Add(new NavigationActionItem
            {
                Name = tool.DisplayName,
                Glyph = tool.Glyph,
                Command = new RelayCommand(() => _navigationService.NavigateTo(key))
            });
        }

        // 启动时默认选中"仪表盘"
        lastSelectedItem = ActionItems.FirstOrDefault(i => i.Name == "仪表盘");
        if (lastSelectedItem != null)
        {
            revertingSelection = true;
            ActionListBox.SelectedItem = lastSelectedItem;
            revertingSelection = false;
        }

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

        if (item?.IsGroupHeader == true)
        {
            revertingSelection = true;
            listBox.SelectedItem = lastSelectedItem;
            revertingSelection = false;
            return;
        }

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

        if (item?.IsGroupHeader == true)
        {
            revertingSelection = true;
            listBox.SelectedItem = lastSelectedItem;
            revertingSelection = false;
            return;
        }

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

    public class NavigationActionItem
    {
        public string Name { get; set; }

        public string Glyph { get; set; }

        public ICommand Command { get; set; }

        public bool IsGroupHeader { get; set; }

        public ObservableCollection<NavigationActionItem> Children { get; } = new();
    }
}
