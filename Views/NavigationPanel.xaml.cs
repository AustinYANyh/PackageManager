using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PackageManager.Models;

namespace PackageManager.Views
{
    public partial class NavigationPanel : UserControl
    {
        private NavigationActionItem _lastSelectedItem;
        private bool _revertingSelection;

        public class NavigationActionItem
        {
            public string Name { get; set; }
            public string Glyph { get; set; }
            public ICommand Command { get; set; }
            public ObservableCollection<NavigationActionItem> Children { get; } = new ObservableCollection<NavigationActionItem>();
        }

        // 统一的系统入口列表数据源
        public ObservableCollection<NavigationActionItem> ActionItems { get; } = new ObservableCollection<NavigationActionItem>();

        public NavigationPanel()
        {
            InitializeComponent();
            Loaded += NavigationPanel_Loaded;
        }

        private void NavigationPanel_Loaded(object sender, RoutedEventArgs e)
        {
            var mw = Window.GetWindow(this) as MainWindow;
            if (mw == null) return;

            // 构建统一的导航动作列表
            ActionItems.Clear();
            
            ActionItems.Add(new NavigationActionItem { Name = "产品分类", Glyph = "\uE8D2", Command = mw.NavigateHomeCommand});
            ActionItems.Add(new NavigationActionItem { Name = "产品日志", Glyph = "\uE7BA", Command = mw.OpenProductLogsCommand });
            ActionItems.Add(new NavigationActionItem { Name = "路径设置", Glyph = "\uE8B7", Command = mw.LocalPathSettingsCommand });
            ActionItems.Add(new NavigationActionItem { Name = "产品管理", Glyph = "\uE8F1", Command = mw.OpenPackageConfigCommand });
            ActionItems.Add(new NavigationActionItem { Name = "软件日志", Glyph = "\uE7BA", Command = mw.OpenLogViewerCommand });
            ActionItems.Add(new NavigationActionItem { Name = "更新日志", Glyph = "\uE8A5", Command = mw.OpenChangelogPageCommand });
            ActionItems.Add(new NavigationActionItem { Name = "软件设置", Glyph = "\uE713", Command = mw.SettingsCommand });

            // 启动时默认选中“产品分类”，确保左侧有选中高亮
            _lastSelectedItem = ActionItems.FirstOrDefault(i => i.Name == "产品分类") ?? ActionItems.FirstOrDefault();
            if (_lastSelectedItem != null)
            {
                _revertingSelection = true; // 防止触发 SelectionChanged 导航
                ActionListBox.SelectedItem = _lastSelectedItem;
                _revertingSelection = false;
            }

            // 监听主窗口是否切回主页，以同步左侧导航选中项
            mw.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(MainWindow.IsHomeActive) && mw.IsHomeActive)
                {
                    var homeItem = ActionItems.FirstOrDefault(i => i.Name == "产品分类") ?? ActionItems.FirstOrDefault();
                    if (homeItem != null && !ReferenceEquals(ActionListBox.SelectedItem, homeItem))
                    {
                        _revertingSelection = true;
                        ActionListBox.SelectedItem = homeItem;
                        _lastSelectedItem = homeItem;
                        _revertingSelection = false;
                    }
                }
            };
        }

        private void ActionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_revertingSelection) return;
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
                _revertingSelection = true;
                listBox.SelectedItem = _lastSelectedItem;
                _revertingSelection = false;
            }
            else
            {
                // 导航成功，记录为最近选中项
                _lastSelectedItem = item;
            }
        }

        private void CategoryChildListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_revertingSelection) return;
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
                _revertingSelection = true;
                listBox.SelectedItem = _lastSelectedItem;
                _revertingSelection = false;
            }
            else
            {
                _lastSelectedItem = item;
            }
        }
    }
}
