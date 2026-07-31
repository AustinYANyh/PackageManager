using System;
using System.Windows;
using MftScanner;

namespace PackageManager.Services
{
    internal sealed class FileSearchWindowManager
    {
        private readonly string _sessionId = SharedIndexConstants.SearchUiSessionId;

        /// <summary>
        /// 显示或激活文件搜索窗口；若索引宿主未运行则尝试静默启动。
        /// </summary>
        public void ShowOrActivate()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(ShowOrActivate));
                return;
            }

            // 先探活：宿主引擎 wedge（Phase-2 看门狗会停心跳）或进程死亡时，先自愈再唤起，
            // 避免在控制线程存活但引擎卡死的宿主窗口里搜不出结果。
            var health = SharedIndexServiceClient.TryReadHostHealth(null);
            if (health.State == HostHealth.Hung || health.State == HostHealth.Dead)
            {
                LoggingService.LogInfo(
                    $"[文件搜索] 宿主 {health.State}（heartbeatAgeMs=" +
                    $"{(health.HeartbeatAgeMs == long.MaxValue ? "n/a" : health.HeartbeatAgeMs.ToString())}），触发自愈后唤起。");
                IndexHostTaskService.EnsureHostHealthyOrRestart(8000);
            }

            // 健康路径：直接请宿主显示窗口。
            if (SharedIndexServiceClient.TryShowSearchUi())
            {
                return;
            }

            // 仍失败则再自愈一次（强杀假死/死亡宿主并重启，绕开单实例互斥锁）后重试。
            if (IndexHostTaskService.EnsureHostHealthyOrRestart(8000)
                && SharedIndexServiceClient.TryShowSearchUi())
            {
                return;
            }

            LoggingService.LogWarning($"文件搜索 UI 唤起失败：宿主自愈后仍未就绪，已禁止本地直启兜底。SessionId={_sessionId}");
        }

        /// <summary>
        /// 关闭文件搜索窗口（当前实现为空操作）。
        /// </summary>
        public void Shutdown()
        {
        }
    }
}
