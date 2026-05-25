using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PackageManager.Features.Dashboard.ViewModels;
using PackageManager.Features.DevTools;
using PackageManager.Services;
using PackageManager.Shell;

namespace PackageManager.Views
{
    public partial class DashboardPage : Page
    {
        private readonly DashboardViewModel _viewModel;

        public DashboardPage()
        {
            InitializeComponent();

            _viewModel = new DashboardViewModel();
            DataContext = _viewModel;

            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _viewModel.Refresh();
        }

        private void RefreshOverview_Click(object sender, MouseButtonEventArgs e)
        {
            _viewModel.Refresh();
        }

        private void NavigationCard_Click(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            var key = border?.Tag as string;
            if (string.IsNullOrEmpty(key)) return;

            var navService = ServiceLocator.Resolve<NavigationService>();
            if (navService == null) return;

            navService.NavigateTo(key);
        }

        private void NotificationCard_Click(object sender, MouseButtonEventArgs e)
        {
            var mainWindow = Application.Current?.MainWindow as MainWindow;
            if (mainWindow != null)
            {
                mainWindow.NotificationPopup.IsOpen = true;
            }
        }

        private void ToolCard_Click(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            var toolKey = border?.Tag as string;
            if (string.IsNullOrEmpty(toolKey)) return;

            var owner = Window.GetWindow(this);

            switch (toolKey)
            {
                case "tool:csv-crypto":
                    DevToolLauncher.OpenCsvCrypto(owner);
                    break;
                case "tool:revit-cleanup":
                    DevToolLauncher.OpenRevitFileCleanup();
                    break;
                case "tool:unlock-files":
                    DevToolLauncher.OpenUnlockFiles(owner);
                    break;
                case "tool:sln-updater":
                    DevToolLauncher.OpenSlnUpdate(owner);
                    break;
                case "tool:git-proxy":
                    _ = DevToolLauncher.ToggleGitProxy(null);
                    break;
                case "tool:vcs-mapping":
                    DevToolLauncher.OpenVcsMapping();
                    break;
                case "tool:revit-activation":
                    DevToolLauncher.OpenRevitActivation();
                    break;
            }
        }
    }
}
