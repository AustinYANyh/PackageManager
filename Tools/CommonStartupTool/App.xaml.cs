using System.Windows;
using PackageManager.Function.StartupTool;
using PackageManager.Services;

namespace CommonStartupTool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new CommonStartupWindow(new DataPersistenceService());
        MainWindow = window;
        window.Show();
    }
}
