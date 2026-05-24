using System.IO;
using PackageManager.Shell;
using PackageManager.Views;
using PackageManager.Views.KanBan;

namespace PackageManager.Services
{
    public static class ToolRegistration
    {
        public static void RegisterAll(ToolRegistry registry)
        {
            var persistence = ServiceLocator.Resolve<DataPersistenceService>();
            var appFinder = ServiceLocator.Resolve<ApplicationFinderService>();
            var lanTransfer = ServiceLocator.Resolve<LanTransferService>();

            registry.Register(new ToolPageDescriptor
            {
                Key = "product-logs",
                DisplayName = "产品日志",
                Glyph = "",
                Factory = () => new ProductLogsPage(Path.Combine(Path.GetTempPath(), "HongWaSoftLog"))
            });

            registry.Register(new ToolPageDescriptor
            {
                Key = "kanban",
                DisplayName = "看板统计",
                Glyph = "",
                Factory = () => new KanbanStatsPage()
            });

            registry.Register(new ToolPageDescriptor
            {
                Key = "plugin-management",
                DisplayName = "插件管理",
                Glyph = "",
                Factory = () => new PluginManagementPage(persistence, appFinder)
            });

            registry.Register(new ToolPageDescriptor
            {
                Key = "lan-transfer",
                DisplayName = "文件传输",
                Glyph = "",
                Factory = () => new LanTransferPage(lanTransfer)
            });

            registry.Register(new ToolPageDescriptor
            {
                Key = "local-path-settings",
                DisplayName = "路径设置",
                Glyph = "",
                Factory = () => new LocalPathSettingsPage(persistence, null)
            });

            registry.Register(new ToolPageDescriptor
            {
                Key = "package-config",
                DisplayName = "产品管理",
                Glyph = "",
                Factory = () => new PackageConfigPage()
            });

            registry.Register(new ToolPageDescriptor
            {
                Key = "log-viewer",
                DisplayName = "软件日志",
                Glyph = "",
                Factory = () => new LogViewerPage()
            });

            registry.Register(new ToolPageDescriptor
            {
                Key = "changelog",
                DisplayName = "更新日志",
                Glyph = "",
                Factory = () => new ChangelogPage()
            });

            registry.Register(new ToolPageDescriptor
            {
                Key = "settings",
                DisplayName = "软件设置",
                Glyph = "",
                Factory = () => new SettingsPage(persistence, lanTransfer)
            });
        }
    }
}
