using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PackageManager.Services.RevitCleanup
{
    internal sealed class RevitFileQueryService
    {
        private readonly EverythingIndexProvider everythingProvider = new EverythingIndexProvider();
        private readonly MftIndexProvider mftProvider = new MftIndexProvider();
        private readonly LocalIndexProvider localIndexProvider = new LocalIndexProvider();

        /// <summary>
        /// 确保至少一个索引提供方已就绪；若 Everything 和 MFT 均不可用则构建本地索引。
        /// </summary>
        /// <param name="options">查询选项。</param>
        /// <param name="progress">进度报告。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public Task EnsureIndexReadyAsync(RevitFileQueryOptions options, IProgress<RevitFileQueryProgress> progress, CancellationToken cancellationToken)
        {
            options = (options ?? new RevitFileQueryOptions()).Normalize();
            if (everythingProvider.IsAvailable() || mftProvider.IsAvailable())
            {
                return Task.CompletedTask;
            }

            return localIndexProvider.EnsureIndexReadyAsync(options, progress, cancellationToken);
        }

        /// <summary>
        /// 按优先级尝试 Everything → MFT → 本地索引查询文件。
        /// </summary>
        /// <param name="options">查询选项。</param>
        /// <param name="progress">进度报告。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>查询结果。</returns>
        public async Task<RevitFileQueryResult> QueryAsync(RevitFileQueryOptions options, IProgress<RevitFileQueryProgress> progress, CancellationToken cancellationToken)
        {
            options = (options ?? new RevitFileQueryOptions()).Normalize();
            if (options.Roots.Count == 0)
            {
                return new RevitFileQueryResult
                {
                    ProviderDisplayText = "本地索引",
                    SourceKind = RevitFileQuerySourceKind.LocalIndex,
                    Files = Array.Empty<RevitIndexedFileInfo>()
                };
            }

            if (!options.ForceRebuildLocalIndex && everythingProvider.IsAvailable())
            {
                try
                {
                    var everythingResult = await everythingProvider.QueryAsync(options, progress, cancellationToken).ConfigureAwait(false);
                    if (everythingResult != null)
                    {
                        return everythingResult;
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.LogError(ex, "使用 Everything 索引查询 Revit 文件失败，已回退到本地索引");
                }
            }

            if (!options.ForceRebuildLocalIndex && mftProvider.IsAvailable())
            {
                try
                {
                    var mftResult = await mftProvider.QueryAsync(options, progress, cancellationToken).ConfigureAwait(false);
                    if (mftResult != null)
                    {
                        return mftResult;
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.LogError(ex, "使用 MFT 索引查询 Revit 文件失败，已回退到本地索引");
                }
            }

            return await localIndexProvider.QueryAsync(options, progress, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 从本地索引中移除指定文件记录。
        /// </summary>
        /// <param name="filePaths">要移除的文件路径集合。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public Task RemoveFilesFromIndexAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken)
        {
            return localIndexProvider.RemoveFilesFromIndexAsync(filePaths, cancellationToken);
        }
    }
}
