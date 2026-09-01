using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using PackageManager.Features.DailyLog.Services;
using PackageManager.Services;
using PackageManager.Services.PingCode;
using PackageManager.Views.KanBan;

namespace PackageManager.Features.PingCode.Perf
{
    /// <summary>
    /// 看板性能自动化测试：命令行 --kanban-perf-test 触发。
    /// 3 轮真实打开迭代看板（等待首推渲染完成）测"打开速度"，3 轮直接数据拉取测"数据链速度"，
    /// 按阈值判定 PASS/FAIL。结果三通道输出：控制台（若由控制台启动）、调试日志、
    /// 报告文件 %LocalAppData%\PackageManager\logs\kanban-perf-report-&lt;时间&gt;.txt。
    /// 退出码：0=达标 1=未达标 2=执行错误。
    /// </summary>
    internal static class KanbanPerfTestRunner
    {
        /// <summary>看板打开全链路阈值（最差一轮判定）：基线 2026-09-01（打开 P50≈2.5-3.5s / 122 卡），回退检测用。</summary>
        private const int OpenThresholdMs = 4000;

        /// <summary>数据拉取阈值（均值判定）：基线 P50≈1.2-1.7s（2 页分页）。</summary>
        private const int FetchThresholdMs = 2500;

        /// <summary>工作项详情打开阈值（最差一轮判定）：基线 P50≈1.1s / P90≈2s（含详情+评论+附件渲染就绪）。</summary>
        private const int DetailsThresholdMs = 3000;

        /// <summary>
        /// 执行看板性能测试并返回退出码。结果写入调试日志与报告文件
        /// %LocalAppData%\PackageManager\logs\kanban-perf-report-&lt;时间&gt;.txt（不依赖控制台，
        /// 避免无控制台宿主/父进程等待时的互锁）。
        /// </summary>
        /// <returns>0=达标；1=未达标；2=执行错误。</returns>
        public static async Task<int> RunAsync()
        {
            var report = new StringBuilder();
            var exitCode = 0;
            try
            {
                W("====== 看板性能自动化测试 ======");
                W($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}  阈值: 打开≤{OpenThresholdMs}ms / 拉取均值≤{FetchThresholdMs}ms");

                var api = new PingCodeApiService();
                W("步骤 1: 解析项目与迭代…");
                var projects = await api.GetProjectsAsync();
                var project = projects.FirstOrDefault(p => (p?.Name ?? "").Contains("建模组"))
                              ?? projects.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p?.Id));
                if (project == null)
                {
                    W("FAIL: 无可用 PingCode 项目");
                    return 2;
                }

                var iterations = await api.GetNotCompletedIterationsByProjectAsync(project.Id);
                // 不用手选记忆（storedId 传 null）：保证测试输入可重复，结果可对比
                var recommended = PingCodeTodoService.SelectRecommendedIteration(iterations, null, DateTime.Today, out _);
                if (recommended == null)
                {
                    W($"FAIL: 项目「{project.Name}」无未完成迭代");
                    return 2;
                }

                W($"项目: {project.Name} / 迭代: {recommended.Name}");

                W("步骤 2: 数据拉取速度 ×3 …");
                var fetchSamples = new List<long>();
                for (var i = 0; i < 3; i++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var items = await api.GetIterationWorkItemsAsync(recommended.Id);
                    sw.Stop();
                    fetchSamples.Add(sw.ElapsedMilliseconds);
                    W($"  第{i + 1}次: {sw.ElapsedMilliseconds}ms（{items?.Count ?? 0} 项）");
                }

                W("步骤 3: 看板打开速度 ×3（真实窗口，等首推渲染完成）…");
                var openSamples = new List<long>();
                var segmentSamples = new List<Dictionary<string, long>>();
                for (var i = 0; i < 3; i++)
                {
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    WorkItemKanbanWebViewWindow win = null;
                    try
                    {
                        win = new WorkItemKanbanWebViewWindow(recommended.Id, null, null);
                        win.PerfFirstPushCompleted += () => tcs.TrySetResult(true);
                        win.Show();
                        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(40)));
                        if (done != tcs.Task)
                        {
                            W($"  第{i + 1}轮: 超时（>40s）");
                            exitCode = Math.Max(exitCode, 1);
                        }
                        else
                        {
                            openSamples.Add(win.PerfOpenTotalMs);
                            segmentSamples.Add(win.PerfOpenSegments.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase));
                            W($"  第{i + 1}轮打开: {win.PerfOpenTotalMs}ms");
                        }

                        await Task.Delay(800);
                    }
                    finally
                    {
                        win?.Close();
                    }
                }

                var fetchAvg = fetchSamples.Count > 0 ? (long)Math.Round(fetchSamples.Average()) : -1;
                var openAvg = openSamples.Count > 0 ? (long)Math.Round(openSamples.Average()) : -1;
                var openWorst = openSamples.Count > 0 ? openSamples.Max() : -1;
                var fetchOk = (fetchAvg >= 0) && (fetchAvg <= FetchThresholdMs);
                var openOk = (openWorst >= 0) && (openWorst <= OpenThresholdMs);

                W("步骤 4: 工作项详情打开速度 ×3（共享详情窗口，等内容就绪）…");
                var detailSamples = new List<long>();
                try
                {
                    var detailsWin = await Views.KanBan.WorkItemDetailsWindow.GetSharedAsync(api);
                    var probeItems = await api.GetIterationWorkItemsAsync(recommended.Id);
                    var targets = (probeItems ?? new List<PackageManager.Services.PingCode.Dto.WorkItemInfo>())
                        .Where(i => (i != null) && !string.IsNullOrWhiteSpace(i?.Id))
                        .Take(3)
                        .ToList();
                    foreach (var t in targets)
                    {
                        var ready = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                        void OnReady(long ms) => ready.TrySetResult(ms);
                        detailsWin.PerfDetailsReady += OnReady;
                        try
                        {
                            var shownWatch = System.Diagnostics.Stopwatch.StartNew();
                            await detailsWin.ShowWorkItemAsync(t.Id, null, null);
                            var done = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                            if (done != ready.Task)
                            {
                                W($"  {t.Identifier}: 超时（>30s）");
                                exitCode = Math.Max(exitCode, 1);
                            }
                            else
                            {
                                var elapsed = ready.Task.Result;
                                detailSamples.Add(elapsed);
                                W($"  {t.Identifier}: 内容就绪 {elapsed}ms");
                            }

                            await Task.Delay(500);
                        }
                        finally
                        {
                            detailsWin.PerfDetailsReady -= OnReady;
                        }
                    }

                    // 共享单例只隐藏不关闭（关闭会销毁，主程序运行期复用语义）
                    detailsWin.Hide();
                }
                catch (Exception dex)
                {
                    W("详情测试异常: " + dex.Message);
                }

                var detailOk = true;
                var detailWorst = detailSamples.Count > 0 ? detailSamples.Max() : -1;
                if (detailSamples.Count > 0)
                {
                    detailOk = detailWorst <= DetailsThresholdMs;
                }
                else
                {
                    detailOk = false;
                    exitCode = Math.Max(exitCode, 1);
                }

                W("------ 结果 ------");
                W($"数据拉取: 均值 {fetchAvg}ms  [阈值 ≤{FetchThresholdMs}ms] {(fetchOk ? "达标 PASS" : "未达标 FAIL")}");
                W($"看板打开: 均值 {openAvg}ms / 最差 {openWorst}ms  [阈值 ≤{OpenThresholdMs}ms(最差)] {(openOk ? "达标 PASS" : "未达标 FAIL")}");
                W($"详情打开: 样本 {detailSamples.Count} 个 / 最差 {detailWorst}ms  [阈值 ≤{DetailsThresholdMs}ms(最差)] {(detailOk ? "达标 PASS" : "未达标 FAIL")}");
                if (segmentSamples.Count > 0)
                {
                    var keys = segmentSamples.SelectMany(d => d.Keys).Distinct().ToList();
                    var segLine = string.Join(" | ", keys.Select(k =>
                    {
                        var values = segmentSamples.Where(d => d.ContainsKey(k)).Select(d => d[k]).ToList();
                        return values.Count > 0 ? $"{k}均值{(long)Math.Round(values.Average())}ms" : $"{k}无样本";
                    }));
                    W($"打开分段: {segLine}");
                }

                var pass = fetchOk && openOk && detailOk && (exitCode == 0);
                exitCode = pass ? 0 : 1;
                W($"总评: {(pass ? "PASS（性能达标）" : "FAIL（存在未达标项，对比日志定位回退点）")}");
            }
            catch (Exception ex)
            {
                W("执行异常: " + ex.Message);
                LoggingService.LogError(ex, "[看板性能测试] 执行异常");
                exitCode = 2;
            }
            finally
            {
                var reportPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PackageManager", "logs",
                    $"kanban-perf-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                try
                {
                    File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
                }
                catch
                {
                }

                LoggingService.LogInfo($"[看板性能测试] 完成，退出码 {exitCode}，报告 {reportPath}");
            }

            return exitCode;

            void W(string line)
            {
                report.AppendLine(line);
                LoggingService.LogDebug("[看板性能测试] " + line);
            }
        }
    }
}
