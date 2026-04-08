using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PackageManager.Services.RevitCleanup
{
    internal sealed class RevitFileQueryService
    {
        private readonly EverythingIndexProvider everythingProvider = new EverythingIndexProvider();
        private readonly LocalIndexProvider localIndexProvider = new LocalIndexProvider();

        public Task EnsureIndexReadyAsync(RevitFileQueryOptions options, IProgress<RevitFileQueryProgress> progress, CancellationToken cancellationToken)
        {
            options = (options ?? new RevitFileQueryOptions()).Normalize();
            if (everythingProvider.IsAvailable())
            {
                return Task.CompletedTask;
            }

            return localIndexProvider.EnsureIndexReadyAsync(options, progress, cancellationToken);
        }

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

            return await localIndexProvider.QueryAsync(options, progress, cancellationToken).ConfigureAwait(false);
        }

        public Task RemoveFilesFromIndexAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken)
        {
            return localIndexProvider.RemoveFilesFromIndexAsync(filePaths, cancellationToken);
        }
    }
}
