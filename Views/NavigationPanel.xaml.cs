using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PackageManager.Views;

public partial class NavigationPanel : UserControl
{
    private NavigationActionItem lastSelectedItem;

    private bool revertingSelection;

    public NavigationPanel()
    {
        InitializeComponent();
        Loaded += NavigationPanel_Loaded;
    }

    // 统一的系统入口列表数据源
    public ObservableCollection<NavigationActionItem> ActionItems { get; } = new();

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
        var mw = Window.GetWindow(this) as MainWindow;
        if (mw == null)
        {
            return;
        }

        // 构建统一的导航动作列表
        ActionItems.Clear();

        ActionItems.Add(new NavigationActionItem { Name = "产品分类", Glyph = "\uE8D2", Command = mw.NavigateHomeCommand });
        ActionItems.Add(new NavigationActionItem { Name = "产品日志", Glyph = "\uE7BA", Command = mw.OpenProductLogsCommand });
        ActionItems.Add(new NavigationActionItem { Name = "看板统计", Glyph = "\uE9D9", Command = mw.OpenKanbanStatsPageCommand });
        ActionItems.Add(new NavigationActionItem { Name = "路径设置", Glyph = "\uE8B7", Command = mw.LocalPathSettingsCommand });
        ActionItems.Add(new NavigationActionItem { Name = "产品管理", Glyph = "\uE8F1", Command = mw.OpenPackageConfigCommand });
        ActionItems.Add(new NavigationActionItem { Name = "软件日志", Glyph = "\uE7BA", Command = mw.OpenLogViewerCommand });
        ActionItems.Add(new NavigationActionItem { Name = "更新日志", Glyph = "\uE8A5", Command = mw.OpenChangelogPageCommand });
        ActionItems.Add(new NavigationActionItem { Name = "软件设置", Glyph = "\uE713", Command = mw.SettingsCommand });

        // 启动时默认选中“产品分类”，确保左侧有选中高亮
        lastSelectedItem = ActionItems.FirstOrDefault(i => i.Name == "产品分类") ?? ActionItems.FirstOrDefault();
        if (lastSelectedItem != null)
        {
            revertingSelection = true; // 防止触发 SelectionChanged 导航
            ActionListBox.SelectedItem = lastSelectedItem;
            revertingSelection = false;
        }

        // 监听主窗口是否切回主页，以同步左侧导航选中项
        mw.PropertyChanged += (s, args) =>
        {
            if ((args.PropertyName == nameof(MainWindow.IsHomeActive)) && mw.IsHomeActive)
            {
                var homeItem = ActionItems.FirstOrDefault(i => i.Name == "产品分类") ?? ActionItems.FirstOrDefault();
                if ((homeItem != null) && !ReferenceEquals(ActionListBox.SelectedItem, homeItem))
                {
                    revertingSelection = true;
                    ActionListBox.SelectedItem = homeItem;
                    lastSelectedItem = homeItem;
                    revertingSelection = false;
                }
            }
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

        var mw = Window.GetWindow(this) as MainWindow;
        var before = mw?.NavigationVersion ?? 0;

        if (cmd?.CanExecute(null) == true)
        {
            cmd.Execute(null);
        }

        var after = mw?.NavigationVersion ?? before;
        if (after == before)
        {
            // 导航未发生，回退到先前选中项
            revertingSelection = true;
            listBox.SelectedItem = lastSelectedItem;
            revertingSelection = false;
        }
        else
        {
            // 导航成功，记录为最近选中项
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

        var mw = Window.GetWindow(this) as MainWindow;
        var before = mw?.NavigationVersion ?? 0;

        if (cmd?.CanExecute(null) == true)
        {
            cmd.Execute(null);
        }

        var after = mw?.NavigationVersion ?? before;
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

        public ObservableCollection<NavigationActionItem> Children { get; } = new();
    }
}