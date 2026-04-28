using System;
using System.Threading;
using System.Threading.Tasks;

namespace MftScanner
{
    public interface ISharedIndexService
    {
        int IndexedCount { get; }
        bool IsBackgroundCatchUpInProgress { get; }
        string CurrentStatusMessage { get; }
        ContainsBucketStatus ContainsBucketStatus { get; }

        event EventHandler<IndexChangedEventArgs> IndexChanged;
        event EventHandler<IndexStatusChangedEventArgs> IndexStatusChanged;

        Task<int> BuildIndexAsync(IProgress<string> progress, CancellationToken ct);
        Task<int> RebuildIndexAsync(IProgress<string> progress, CancellationToken ct);
        Task<SearchQueryResult> SearchAsync(string keyword, int maxResults, int offset, IProgress<string> progress, CancellationToken ct);
        Task<SearchQueryResult> SearchAsync(string keyword, int maxResults, int offset, SearchTypeFilter filter, IProgress<string> progress, CancellationToken ct);
        void Shutdown();
    }

    public static class SharedIndexServiceFactory
    {
        public static ISharedIndexService Create(string consumerName)
        {
            return new SharedIndexServiceClient(consumerName);
        }
    }
}
