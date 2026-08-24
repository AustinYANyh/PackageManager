using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using PackageManager.Services;

namespace PackageManager.Infrastructure
{
    /// <summary>
    /// WebView2 运行环境共享单例：应用启动即并行预热两套浏览器进程——
    /// 看板环境（WebView2Cache）与详情环境（WebView2DetailsCache，独立用户数据目录，
    /// 规避同目录多 WebView 控件并发创建时数十秒的通道排队阻塞）。
    /// 登录窗、命令面板等使用独立缓存目录的窗口不接入本服务。
    /// </summary>
    public static class WebView2EnvironmentService
    {
        private static readonly object Sync = new object();
        private static Task<CoreWebView2Environment> environmentTask;
        private static Task<CoreWebView2Environment> detailsEnvironmentTask;

    /// <summary>
    /// 获取（或首次创建）共享 WebView2 环境；并发调用共享同一次创建任务。
    /// </summary>
    /// <returns>共享 WebView2 环境。</returns>
    public static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        lock (Sync)
        {
            return environmentTask ??= CreateAsync("WebView2Cache");
        }
    }

    /// <summary>
    /// 获取（或首次创建）详情窗口专用环境：与看板共享环境使用不同的用户数据目录，
    /// 规避同文件夹下多 WebView 控件并发创建时浏览器进程通道排队导致的数十秒阻塞（实测 47.9s）。
    /// </summary>
    /// <returns>详情窗口专用 WebView2 环境。</returns>
    public static Task<CoreWebView2Environment> GetDetailsEnvironmentAsync()
    {
        lock (Sync)
        {
            return detailsEnvironmentTask ??= CreateAsync("WebView2DetailsCache");
        }
    }

    /// <summary>
    /// 预热共享环境与详情环境（应用启动期调用，fire-and-forget，幂等）。
    /// </summary>
    public static void Prewarm()
    {
        _ = GetEnvironmentAsync();
        _ = GetDetailsEnvironmentAsync();
    }

    private static async Task<CoreWebView2Environment> CreateAsync(string folderName)
    {
        var userDataFolder = Path.Combine(new DataPersistenceService().GetDataFolderPath(), folderName);
        Directory.CreateDirectory(userDataFolder);
        return await CoreWebView2Environment.CreateAsync(null, userDataFolder);
    }
}
}
