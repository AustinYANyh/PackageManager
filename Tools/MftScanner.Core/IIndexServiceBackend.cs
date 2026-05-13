using System;
using System.Threading;
using System.Threading.Tasks;

namespace MftScanner
{
    internal interface IIndexServiceBackend
    {
        MemoryIndex Index { get; }
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
        Task NotifyDeletedAsync(string fullPath, bool isDirectory, CancellationToken ct);
        void EnsureSearchHotStructuresReady(CancellationToken ct, string reason);
        void Shutdown();
    }
}
