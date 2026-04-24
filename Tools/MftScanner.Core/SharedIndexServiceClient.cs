using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace MftScanner
{
    public sealed class SharedIndexServiceClient : ISharedIndexService, IDisposable
    {
        private const int CommandConnectTimeoutMilliseconds = 2000;
        private const int EventConnectTimeoutMilliseconds = 2000;
        private const int HostStartupWaitMilliseconds = 15000;
        private const int HostStartupProbeIntervalMilliseconds = 500;

        private readonly string _consumerName;
        private readonly object _fallbackLock = new object();
        private readonly object _stateLock = new object();
        private readonly CancellationTokenSource _eventLoopCts = new CancellationTokenSource();
        private readonly Task _eventLoopTask;
        private ISharedIndexService _fallbackService;
        private int _indexedCount;
        private string _currentStatusMessage = string.Empty;
        private bool _isBackgroundCatchUpInProgress;

        public SharedIndexServiceClient(string consumerName)
        {
            _consumerName = string.IsNullOrWhiteSpace(consumerName) ? "UnknownConsumer" : consumerName.Trim();
            _eventLoopTask = Task.Run(() => RunEventLoop(_eventLoopCts.Token));
        }

        public int IndexedCount
        {
            get
            {
                var fallback = _fallbackService;
                if (fallback != null)
                {
                    return fallback.IndexedCount;
                }

                lock (_stateLock)
                {
                    return _indexedCount;
                }
            }
        }

        public bool IsBackgroundCatchUpInProgress
        {
            get
            {
                var fallback = _fallbackService;
                if (fallback != null)
                {
                    return fallback.IsBackgroundCatchUpInProgress;
                }

                lock (_stateLock)
                {
                    return _isBackgroundCatchUpInProgress;
                }
            }
        }

        public string CurrentStatusMessage
        {
            get
            {
                var fallback = _fallbackService;
                if (fallback != null)
                {
                    return fallback.CurrentStatusMessage;
                }

                lock (_stateLock)
                {
                    return _currentStatusMessage;
                }
            }
        }

        public event EventHandler<IndexChangedEventArgs> IndexChanged;
        public event EventHandler<IndexStatusChangedEventArgs> IndexStatusChanged;

        public Task<int> BuildIndexAsync(IProgress<string> progress, CancellationToken ct)
        {
            return ExecuteHostIntCommandAsync("build", progress, ct);
        }

        public Task<int> RebuildIndexAsync(IProgress<string> progress, CancellationToken ct)
        {
            return ExecuteHostIntCommandAsync("rebuild", progress, ct);
        }

        public Task<SearchQueryResult> SearchAsync(string keyword, int maxResults, int offset, IProgress<string> progress, CancellationToken ct)
        {
            return SearchAsync(keyword, maxResults, offset, SearchTypeFilter.All, progress, ct);
        }

        public async Task<SearchQueryResult> SearchAsync(string keyword, int maxResults, int offset, SearchTypeFilter filter, IProgress<string> progress, CancellationToken ct)
        {
            return await SearchAsyncCore(keyword, maxResults, offset, filter, progress, ct, allowHostWarmupRetry: true).ConfigureAwait(false);
        }

        private async Task<SearchQueryResult> SearchAsyncCore(string keyword, int maxResults, int offset, SearchTypeFilter filter, IProgress<string> progress, CancellationToken ct, bool allowHostWarmupRetry)
        {
            var fallback = _fallbackService;
            if (fallback != null)
            {
                return await fallback.SearchAsync(keyword, maxResults, offset, filter, progress, ct).ConfigureAwait(false);
            }

            try
            {
                var response = await SendRequestAsync(new SharedIndexRequest
                {
                    command = "search",
                    consumer = _consumerName,
                    keyword = keyword ?? string.Empty,
                    maxResults = maxResults,
                    offset = offset,
                    filter = filter.ToString()
                }, ct).ConfigureAwait(false);

                ApplyResponseState(response);
                return new SearchQueryResult
                {
                    TotalIndexedCount = response.totalIndexedCount,
                    TotalMatchedCount = response.totalMatchedCount,
                    IsTruncated = response.isTruncated,
                    Results = response.results ?? new List<ScannedFileInfo>()
                };
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                if (allowHostWarmupRetry && await RetryAgainstHostIfStartingAsync(progress, ct).ConfigureAwait(false))
                {
                    return await SearchAsyncCore(keyword, maxResults, offset, filter, progress, ct, allowHostWarmupRetry: false).ConfigureAwait(false);
                }

                progress?.Report("后台索引不可用，正在回退到本地索引...");
                return await GetOrCreateFallbackService().SearchAsync(keyword, maxResults, offset, filter, progress, ct).ConfigureAwait(false);
            }
        }

        public void Shutdown()
        {
            try
            {
                _eventLoopCts.Cancel();
            }
            catch
            {
            }

            try
            {
                _fallbackService?.Shutdown();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Shutdown();
        }

        public static bool TryShowSearchUi()
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    var response = SendRequestAsyncStatic(new SharedIndexRequest
                    {
                        command = "show-search-ui",
                        consumer = "PackageManager"
                    }, CommandConnectTimeoutMilliseconds, cts.Token).GetAwaiter().GetResult();
                    return response != null && response.success;
                }
            }
            catch
            {
                return false;
            }
        }

        private async Task<int> ExecuteHostIntCommandAsync(string command, IProgress<string> progress, CancellationToken ct)
        {
            return await ExecuteHostIntCommandAsync(command, progress, ct, allowHostWarmupRetry: true).ConfigureAwait(false);
        }

        private async Task<int> ExecuteHostIntCommandAsync(string command, IProgress<string> progress, CancellationToken ct, bool allowHostWarmupRetry)
        {
            var fallback = _fallbackService;
            if (fallback != null)
            {
                return command == "rebuild"
                    ? await fallback.RebuildIndexAsync(progress, ct).ConfigureAwait(false)
                    : await fallback.BuildIndexAsync(progress, ct).ConfigureAwait(false);
            }

            try
            {
                var response = await SendRequestAsync(new SharedIndexRequest
                {
                    command = command,
                    consumer = _consumerName
                }, ct).ConfigureAwait(false);

                ApplyResponseState(response);
                if (!string.IsNullOrWhiteSpace(response.currentStatusMessage))
                {
                    progress?.Report(response.currentStatusMessage);
                }

                return response.indexedCount;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                if (allowHostWarmupRetry && await RetryAgainstHostIfStartingAsync(progress, ct).ConfigureAwait(false))
                {
                    return await ExecuteHostIntCommandAsync(command, progress, ct, allowHostWarmupRetry: false).ConfigureAwait(false);
                }

                progress?.Report("后台索引不可用，正在回退到本地索引...");
                var local = GetOrCreateFallbackService();
                return command == "rebuild"
                    ? await local.RebuildIndexAsync(progress, ct).ConfigureAwait(false)
                    : await local.BuildIndexAsync(progress, ct).ConfigureAwait(false);
            }
        }

        private ISharedIndexService GetOrCreateFallbackService()
        {
            if (_fallbackService != null)
            {
                return _fallbackService;
            }

            lock (_fallbackLock)
            {
                if (_fallbackService != null)
                {
                    return _fallbackService;
                }

                var fallback = new IndexService();
                fallback.IndexChanged += (sender, args) => IndexChanged?.Invoke(this, args);
                fallback.IndexStatusChanged += (sender, args) => IndexStatusChanged?.Invoke(this, args);
                _fallbackService = fallback;
                return fallback;
            }
        }

        private async Task RunEventLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (_fallbackService != null)
                {
                    return;
                }

                NamedPipeClientStream stream = null;
                StreamReader reader = null;
                StreamWriter writer = null;
                try
                {
                    stream = new NamedPipeClientStream(".", SharedIndexConstants.IndexHostEventPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await ConnectAsync(stream, EventConnectTimeoutMilliseconds, ct).ConfigureAwait(false);

                    reader = new StreamReader(stream);
                    writer = new StreamWriter(stream) { AutoFlush = true };
                    await writer.WriteLineAsync(JsonConvert.SerializeObject(new SharedIndexRequest
                    {
                        command = "subscribe",
                        consumer = _consumerName
                    })).ConfigureAwait(false);

                    while (!ct.IsCancellationRequested && stream.IsConnected)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null)
                        {
                            break;
                        }

                        HandleEventMessage(line);
                    }
                }
                catch
                {
                }
                finally
                {
                    try
                    {
                        writer?.Dispose();
                    }
                    catch
                    {
                    }

                    try
                    {
                        reader?.Dispose();
                    }
                    catch
                    {
                    }

                    try
                    {
                        stream?.Dispose();
                    }
                    catch
                    {
                    }
                }

                try
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private void HandleEventMessage(string json)
        {
            var message = JsonConvert.DeserializeObject<SharedIndexEventMessage>(json);
            if (message == null)
            {
                return;
            }

            if (string.Equals(message.type, "status", StringComparison.OrdinalIgnoreCase))
            {
                lock (_stateLock)
                {
                    _indexedCount = message.indexedCount;
                    _currentStatusMessage = message.currentStatusMessage ?? string.Empty;
                    _isBackgroundCatchUpInProgress = message.isBackgroundCatchUpInProgress;
                }

                IndexStatusChanged?.Invoke(this, new IndexStatusChangedEventArgs(
                    message.currentStatusMessage ?? string.Empty,
                    message.indexedCount,
                    message.isBackgroundCatchUpInProgress,
                    message.requireSearchRefresh));
                return;
            }

            if (string.Equals(message.type, "change", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse(message.changeType, true, out IndexChangeType changeType))
            {
                IndexChanged?.Invoke(this, new IndexChangedEventArgs(
                    changeType,
                    message.lowerName,
                    message.fullPath,
                    message.oldFullPath,
                    message.newOriginalName,
                    message.newLowerName,
                    message.isDirectory));
            }
        }

        private void ApplyResponseState(SharedIndexResponse response)
        {
            if (response == null)
            {
                return;
            }

            lock (_stateLock)
            {
                _indexedCount = response.indexedCount > 0 ? response.indexedCount : response.totalIndexedCount;
                _currentStatusMessage = response.currentStatusMessage ?? string.Empty;
                _isBackgroundCatchUpInProgress = response.isBackgroundCatchUpInProgress;
            }
        }

        private Task<SharedIndexResponse> SendRequestAsync(SharedIndexRequest request, CancellationToken ct)
        {
            return SendRequestAsyncStatic(request, CommandConnectTimeoutMilliseconds, ct);
        }

        private async Task<bool> RetryAgainstHostIfStartingAsync(IProgress<string> progress, CancellationToken ct)
        {
            if (_fallbackService != null)
            {
                return false;
            }

            progress?.Report("后台索引宿主连接中，正在等待其完成启动...");
            return await WaitForHostAvailabilityAsync(HostStartupWaitMilliseconds, ct).ConfigureAwait(false);
        }

        private static async Task<bool> WaitForHostAvailabilityAsync(int timeoutMilliseconds, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(timeoutMilliseconds, 0));
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                try
                {
                    var response = await SendRequestAsyncStatic(new SharedIndexRequest
                    {
                        command = "state",
                        consumer = "HostAvailabilityProbe"
                    }, CommandConnectTimeoutMilliseconds, ct).ConfigureAwait(false);
                    return response != null && response.success;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }

                await Task.Delay(HostStartupProbeIntervalMilliseconds, ct).ConfigureAwait(false);
            }

            return false;
        }

        private static async Task<SharedIndexResponse> SendRequestAsyncStatic(SharedIndexRequest request, int connectTimeoutMilliseconds, CancellationToken ct)
        {
            using (var stream = new NamedPipeClientStream(".", SharedIndexConstants.IndexHostCommandPipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await ConnectAsync(stream, connectTimeoutMilliseconds, ct).ConfigureAwait(false);
                using (var reader = new StreamReader(stream))
                using (var writer = new StreamWriter(stream) { AutoFlush = true })
                {
                    var payload = JsonConvert.SerializeObject(request ?? new SharedIndexRequest());
                    await writer.WriteLineAsync(payload).ConfigureAwait(false);
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        throw new IOException("后台索引宿主未返回数据。");
                    }

                    var response = JsonConvert.DeserializeObject<SharedIndexResponse>(line);
                    if (response == null)
                    {
                        throw new IOException("后台索引宿主返回了无效数据。");
                    }

                    if (!response.success)
                    {
                        throw new InvalidOperationException(response.error ?? "后台索引宿主执行失败。");
                    }

                    return response;
                }
            }
        }

        private static async Task ConnectAsync(NamedPipeClientStream stream, int timeoutMilliseconds, CancellationToken ct)
        {
            await stream.ConnectAsync(timeoutMilliseconds, ct).ConfigureAwait(false);
        }
    }
}
