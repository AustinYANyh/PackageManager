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

            // --- 包管理 ---

            registry.Register(new ToolPageDescriptor
            {
                Key = "packages-home",
                DisplayName = "产品分类",
                Glyph = "",
                Group = "包管理",
                Factory = () => ServiceLocator.Resolve<MainWindow>().GetOrCreateHomePage()
            });

            registry.Register(new ToolPageDescriptor
            {
                Key = "local-path-settings",
                DisplayName = "路径设置",
                Glyph = "",
                Group = "包管理",
                Factory = () => new LocalPathSettingsPage(persistence, ServiceLocator.Resolve<MainWindow>().Packages)
            });

            registry.Register(new ToolPageDescriptor
            {
                Key = "package-config",
                DisplayName = "产品管理",
                Glyph = "",
                Group = "包管理",
                Factory = () => new PackageConfigPage()
            });

            registry.Register(new ToolPageDescriptor
            {
                Key = "changelog",
                DisplayName = "更新日志",
                Glyph = "",
                Group = "包管理",
                Factory = () => new ChangelogPage()
            });

            // --- 项目与协作 ---

            registry.Register(new ToolPageDescriptor
            {
                Key = "kanban",
                DisplayName = "看板统计",
                Glyph = "",
                Group = "项目与协作",
                Factory = () => new KanbanStatsPage()
            });

            registry.Register(new ToolPageDescriptor
            {
                Key = "lan-transfer",
                DisplayName = "文件传输",
                Glyph = "",
                Group = "项目与协作",
                Factory = () => new LanTransferPage(lanTransfer)
            });

            // --- 开发工具 ---

            registry.Register(new ToolPageDescriptor
            {
                Key = "plugin-management",
                DisplayName = "插件管理",
                Glyph = "",
                Group = "开发工具",
                Factory = () => new PluginManagementPage(persistence, appFinder)
            });

            // --- 日志 ---

            registry.Register(new ToolPageDescriptor
            {
                Key = "product-logs",
                DisplayName = "产品日志",
                Glyph = "",
                Group = "日志",
                Factory = () => new ProductLogsPage(Path.Combine(Path.GetTempPath(), "HongWaSoftLog"))
            });

            registry.Register(new ToolPageDescriptor
            {
                Key = "log-viewer",
                DisplayName = "软件日志",
                Glyph = "",
                Group = "日志",
                Factory = () => new LogViewerPage()
            });

            // --- 设置 ---

            registry.Register(new ToolPageDescriptor
            {
                Key = "settings",
                DisplayName = "软件设置",
                Glyph = "",
                Group = "设置",
                Factory = () => new SettingsPage(persistence, lanTransfer)
            });
        }
    }
}
