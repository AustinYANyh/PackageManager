using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MftScanner
{
    /// <summary>
    /// 增量过滤器，封装查询取消逻辑。
    /// 每次调用 <see cref="QueryAsync"/> 时自动取消上一次未完成的查询，
    /// 确保 UI 仅渲染最新一次查询的结果。
    /// 需求：4.1、4.2
    /// </summary>
    public sealed class IncrementalFilter
    {
        private readonly ISharedIndexService _indexService;
        private CancellationTokenSource _currentCts;
        private readonly object _lock = new object();

        public IncrementalFilter(ISharedIndexService indexService)
        {
            _indexService = indexService;
        }

        /// <summary>
        /// 触发新查询，自动取消上一次未完成的查询。
        /// 若查询因内部取消（非 <paramref name="externalCt"/>）而中断，返回空结果而非抛出异常。
        /// 需求 4.1、4.2
        /// </summary>
        /// <param name="keyword">搜索关键词。</param>
        /// <param name="maxResults">最多返回的结果数量。</param>
        /// <param name="externalCt">外部取消令牌（如窗口关闭）。</param>
        public async Task<SearchQueryResult> QueryAsync(
            string keyword,
            int maxResults,
            int offset = 0,
            CancellationToken externalCt = default)
        {
            CancellationTokenSource newCts;

            lock (_lock)
            {
                // 取消并释放上一次的 CTS（需求 4.1）
                var old = _currentCts;
                if (old != null)
                {
                    old.Cancel();
                    old.Dispose();
                }

                // 创建新 CTS，链接外部令牌
                newCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
                _currentCts = newCts;
            }

            try
            {
                return await _indexService.SearchAsync(keyword, maxResults, offset, null, newCts.Token)
                    .ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!externalCt.IsCancellationRequested)
            {
                // 内部取消（被下一次查询抢占），返回空结果（需求 4.3）
                return new SearchQueryResult
                {
                    TotalIndexedCount = _indexService.IndexedCount,
                    TotalMatchedCount = 0,
                    IsTruncated = false,
                    Results = new List<ScannedFileInfo>()
                };
            }
            catch (OperationCanceledException) when (!externalCt.IsCancellationRequested)
            {
                // 同上，兼容 OperationCanceledException
                return new SearchQueryResult
                {
                    TotalIndexedCount = _indexService.IndexedCount,
                    TotalMatchedCount = 0,
                    IsTruncated = false,
                    Results = new List<ScannedFileInfo>()
                };
            }
        }
    }
}
