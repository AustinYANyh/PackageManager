using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace MftScanner
{
    public sealed class SharedIndexServiceClient : ISharedIndexService, IDisposable
    {
        private const int HostStartupWaitMilliseconds = 15000;
        private const int HostStartupProbeIntervalMilliseconds = 500;
        private const int HostReadyPollIntervalMilliseconds = 200;
        private const int ShowSearchUiRequestTimeoutMilliseconds = 5000;
        private const int ShowSearchUiPipeConnectMilliseconds = 1200;

        private readonly string _consumerName;
        private readonly SharedIndexClientSlotId _slotId;
        private readonly object _resourceLock = new object();
        private readonly object _stateLock = new object();
        private readonly SemaphoreSlim _requestGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _eventLoopCts = new CancellationTokenSource();
        private readonly Task _eventLoopTask;
        private readonly Task _heartbeatTask;

        private SharedIndexClientSlotResources _slotResources;
        private MemoryMappedFile _stateMap;
        private long _nextRequestId;
        private long _lastStateSequence;
        private long _lastRefreshSequence;
        private long _lastChangeSequence;
        private int _connectedHostProcessId;
        private int _indexedCount;
        private string _currentStatusMessage = string.Empty;
        private bool _isBackgroundCatchUpInProgress;
        private SharedIndexBuildState _buildState = SharedIndexBuildState.Unknown;
        private ContainsBucketStatus _containsBucketStatus = ContainsBucketStatus.Empty;

        public SharedIndexServiceClient(string consumerName)
        {
            _consumerName = string.IsNullOrWhiteSpace(consumerName) ? "UnknownConsumer" : consumerName.Trim();
            if (!SharedIndexMemoryProtocol.TryResolveClientSlot(_consumerName, out _slotId))
            {
                throw new ArgumentException("共享索引客户端只支持固定的 CtrlE/CtrlQ 槽位。", nameof(consumerName));
            }

            _eventLoopTask = Task.Run(() => RunEventLoop(_eventLoopCts.Token));
            _heartbeatTask = Task.Run(() => RunHeartbeatLoop(_eventLoopCts.Token));
        }

        public int IndexedCount
        {
            get
            {
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
                lock (_stateLock)
                {
                    return _currentStatusMessage;
                }
            }
        }

        public ContainsBucketStatus ContainsBucketStatus
        {
            get
            {
                lock (_stateLock)
                {
                    return _containsBucketStatus ?? ContainsBucketStatus.Empty;
                }
            }
        }

        public event EventHandler<IndexChangedEventArgs> IndexChanged;
        public event EventHandler<IndexStatusChangedEventArgs> IndexStatusChanged;

        public Task<int> BuildIndexAsync(IProgress<string> progress, CancellationToken ct)
        {
            return WaitForSharedIndexReadyAsync(progress, ct);
        }

        public Task<int> RebuildIndexAsync(IProgress<string> progress, CancellationToken ct)
        {
            return ExecuteHostIntCommandAsync(SharedIndexCommandType.Rebuild, progress, ct);
        }

        public Task<SearchQueryResult> SearchAsync(string keyword, int maxResults, int offset, IProgress<string> progress, CancellationToken ct)
        {
            return SearchAsync(keyword, maxResults, offset, SearchTypeFilter.All, progress, ct);
        }

        public async Task<SearchQueryResult> SearchAsync(string keyword, int maxResults, int offset, SearchTypeFilter filter, IProgress<string> progress, CancellationToken ct)
        {
            return await SearchAsyncCore(keyword, maxResults, offset, filter, progress, ct, allowHostWarmupRetry: true).ConfigureAwait(false);
        }

        public async Task NotifyDeletedAsync(string fullPath, bool isDirectory, CancellationToken ct)
        {
            try
            {
                var response = await SendRequestAsync(new SharedIndexIpcRequest
                {
                    CommandType = SharedIndexCommandType.MarkDeleted,
                    Filter = SearchTypeFilter.All,
                    MaxResults = 0,
                    Offset = 0,
                    Flags = isDirectory ? 1 : 0,
                    Keyword = fullPath ?? string.Empty
                }, ct).ConfigureAwait(false);

                ApplyResponseState(response);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw BuildHostUnavailableException("mark-deleted", ex);
            }
        }

        public async Task<string> SendTestControlAsync(string requestJson, CancellationToken ct)
        {
            var response = await SendControlRequestAsyncStatic(new SharedIndexRequest
            {
                command = "native-test-control",
                consumer = _consumerName,
                keyword = requestJson ?? "{}"
            }, ct).ConfigureAwait(false);

            return response?.error;
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

            ResetResources();
        }

        public void Dispose()
        {
            Shutdown();
        }

        public static bool TryShowSearchUi()
        {
            try
            {
                using (var cts = new CancellationTokenSource(ShowSearchUiRequestTimeoutMilliseconds))
                {
                    var response = SendControlRequestAsyncStatic(new SharedIndexRequest
                    {
                        command = "show-search-ui",
                        consumer = "PackageManager"
                    }, cts.Token).GetAwaiter().GetResult();
                    return response != null && response.success;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool TryWaitForHostAvailability(int timeoutMilliseconds)
        {
            try
            {
                using (var cts = new CancellationTokenSource(Math.Max(timeoutMilliseconds, 0)))
                {
                    return WaitForHostAvailabilityAsync(timeoutMilliseconds, cts.Token).GetAwaiter().GetResult();
                }
            }
            catch
            {
                return false;
            }
        }

        private async Task<SearchQueryResult> SearchAsyncCore(string keyword, int maxResults, int offset, SearchTypeFilter filter, IProgress<string> progress, CancellationToken ct, bool allowHostWarmupRetry)
        {
            try
            {
                var response = await SendRequestAsync(new SharedIndexIpcRequest
                {
                    CommandType = SharedIndexCommandType.Search,
                    Filter = filter,
                    MaxResults = maxResults,
                    Offset = offset,
                    Keyword = keyword ?? string.Empty
                }, ct).ConfigureAwait(false);

                ApplyResponseState(response);
                return new SearchQueryResult
                {
                    TotalIndexedCount = response.TotalIndexedCount,
                    TotalMatchedCount = response.TotalMatchedCount,
                    PhysicalMatchedCount = response.PhysicalMatchedCount,
                    UniqueMatchedCount = response.UniqueMatchedCount,
                    DuplicatePathCount = response.DuplicatePathCount,
                    IsTruncated = response.IsTruncated,
                    HostSearchMs = response.HostSearchMs,
                    IsSnapshotStale = response.IsSnapshotStale,
                    ContainsBucketStatus = response.ContainsBucketStatus ?? ContainsBucketStatus.Empty,
                    Results = response.Results ?? new List<ScannedFileInfo>()
                };
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                if (allowHostWarmupRetry && await RetryAgainstHostIfStartingAsync(progress, ct).ConfigureAwait(false))
                {
                    return await SearchAsyncCore(keyword, maxResults, offset, filter, progress, ct, allowHostWarmupRetry: false).ConfigureAwait(false);
                }

                throw BuildHostUnavailableException("search", ex);
            }
        }

        private async Task<int> ExecuteHostIntCommandAsync(SharedIndexCommandType commandType, IProgress<string> progress, CancellationToken ct)
        {
            return await ExecuteHostIntCommandAsync(commandType, progress, ct, allowHostWarmupRetry: true).ConfigureAwait(false);
        }

        private async Task<int> ExecuteHostIntCommandAsync(SharedIndexCommandType commandType, IProgress<string> progress, CancellationToken ct, bool allowHostWarmupRetry)
        {
            try
            {
                var response = await SendRequestAsync(new SharedIndexIpcRequest
                {
                    CommandType = commandType,
                    Filter = SearchTypeFilter.All,
                    MaxResults = 0,
                    Offset = 0,
                    Keyword = string.Empty
                }, ct).ConfigureAwait(false);

                ApplyResponseState(response);
                if (!string.IsNullOrWhiteSpace(response.CurrentStatusMessage))
                {
                    progress?.Report(response.CurrentStatusMessage);
                }

                if (commandType == SharedIndexCommandType.Build)
                {
                    return response.IndexedCount > 0 ? response.IndexedCount : response.TotalIndexedCount;
                }

                return response.IndexedCount;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                if (allowHostWarmupRetry && await RetryAgainstHostIfStartingAsync(progress, ct).ConfigureAwait(false))
                {
                    return await ExecuteHostIntCommandAsync(commandType, progress, ct, allowHostWarmupRetry: false).ConfigureAwait(false);
                }

                throw BuildHostUnavailableException(commandType.ToString(), ex);
            }
        }

        private async Task RunEventLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                SharedIndexClientSlotResources slotResources = null;
                try
                {
                    await EnsureResourcesAvailableAsync(ct).ConfigureAwait(false);
                    slotResources = GetSlotResources();
                    if (slotResources == null)
                    {
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                        continue;
                    }

                    ApplyLatestState(raiseEvent: false);
                    DrainPendingChanges();

                    var waitHandles = new WaitHandle[] { slotResources.StatusChangedEvent, slotResources.ChangeAvailableEvent, ct.WaitHandle };
                    while (!ct.IsCancellationRequested)
                    {
                        var waitIndex = WaitHandle.WaitAny(waitHandles, 1000);
                        if (waitIndex == 2)
                        {
                            return;
                        }

                        if (waitIndex == 0)
                        {
                            ApplyLatestState(raiseEvent: true);
                        }
                        else if (waitIndex == 1)
                        {
                            DrainPendingChanges();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    ResetResources();
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

        private async Task RunHeartbeatLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var slotResources = GetSlotResources();
                    if (slotResources != null)
                    {
                        SharedIndexMemoryProtocol.WriteClientHeartbeatTicks(slotResources.ChangeMap, DateTime.UtcNow.Ticks);
                    }
                }
                catch
                {
                    ResetResources();
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

        private void ApplyLatestState(bool raiseEvent)
        {
            MemoryMappedFile stateMap;
            lock (_resourceLock)
            {
                stateMap = _stateMap;
            }

            if (stateMap == null)
            {
                return;
            }

            SharedIndexStateSnapshot snapshot;
            try
            {
                snapshot = SharedIndexMemoryProtocol.ReadState(stateMap);
            }
            catch
            {
                ResetResources();
                throw;
            }

            bool shouldRaise;
            bool requireRefresh;
            lock (_stateLock)
            {
                shouldRaise = snapshot.StateSequence > _lastStateSequence;
                if (!shouldRaise)
                {
                    ApplySnapshotState(snapshot);
                    return;
                }

                _lastStateSequence = snapshot.StateSequence;
                ApplySnapshotState(snapshot);
                requireRefresh = snapshot.RefreshSequence > _lastRefreshSequence;
                if (requireRefresh)
                {
                    _lastRefreshSequence = snapshot.RefreshSequence;
                }
            }

            if (!raiseEvent)
            {
                return;
            }

            IndexStatusChanged?.Invoke(this, new IndexStatusChangedEventArgs(
                snapshot.StatusMessage ?? string.Empty,
                snapshot.IndexedCount,
                snapshot.IsBackgroundCatchUpInProgress,
                requireRefresh,
                snapshot.ContainsBucketStatus));
        }

        private void DrainPendingChanges()
        {
            SharedIndexClientSlotResources slotResources = GetSlotResources();
            if (slotResources == null)
            {
                return;
            }

            var publishedSequence = SharedIndexMemoryProtocol.ReadPublishedChangeSequence(slotResources.ChangeMap);
            if (publishedSequence - _lastChangeSequence > SharedIndexMemoryProtocol.ChangeRingCapacity)
            {
                _lastChangeSequence = publishedSequence;
                SharedIndexMemoryProtocol.WriteConsumedChangeSequence(slotResources.ChangeMap, _lastChangeSequence);
                RaiseRefreshRequired();
                return;
            }

            while (_lastChangeSequence < publishedSequence)
            {
                var nextSequence = _lastChangeSequence + 1;
                SharedIndexChangeRecord record;
                try
                {
                    record = SharedIndexMemoryProtocol.ReadChangeRecord(slotResources.ChangeMap, nextSequence);
                }
                catch
                {
                    _lastChangeSequence = publishedSequence;
                    SharedIndexMemoryProtocol.WriteConsumedChangeSequence(slotResources.ChangeMap, _lastChangeSequence);
                    RaiseRefreshRequired();
                    return;
                }

                _lastChangeSequence = nextSequence;

                IndexChanged?.Invoke(this, new IndexChangedEventArgs(
                    record.ChangeType,
                    record.LowerName,
                    record.FullPath,
                    record.OldFullPath,
                    record.NewOriginalName,
                    record.NewLowerName,
                    record.IsDirectory));
            }

            SharedIndexMemoryProtocol.WriteConsumedChangeSequence(slotResources.ChangeMap, _lastChangeSequence);
        }

        private void RaiseRefreshRequired()
        {
            string message;
            int indexedCount;
            bool isBackgroundCatchUpInProgress;
            ContainsBucketStatus containsBucketStatus;
            lock (_stateLock)
            {
                message = _currentStatusMessage;
                indexedCount = _indexedCount;
                isBackgroundCatchUpInProgress = _isBackgroundCatchUpInProgress;
                containsBucketStatus = _containsBucketStatus;
            }

            IndexStatusChanged?.Invoke(this, new IndexStatusChangedEventArgs(
                message ?? string.Empty,
                indexedCount,
                isBackgroundCatchUpInProgress,
                requireSearchRefresh: true,
                containsBucketStatus));
        }

        private async Task<SharedIndexIpcResponse> SendRequestAsync(SharedIndexIpcRequest request, CancellationToken ct)
        {
            var totalStopwatch = Stopwatch.StartNew();
            long openMs = 0;
            long writeMs = 0;
            long waitMs = 0;
            long readMs = 0;
            long queuedMs = 0;
            var requestBytes = 0;
            var fastSearchPath = request != null
                && request.CommandType == SharedIndexCommandType.Search
                && GetSlotResources() != null;
            if (!fastSearchPath)
            {
                await EnsureResourcesAvailableAsync(ct).ConfigureAwait(false);
                EnsureConnectedHostIsAlive();
                await EnsureResourcesAvailableAsync(ct).ConfigureAwait(false);
                await EnsureConnectedHostGenerationAsync(ct).ConfigureAwait(false);
            }
            if (request != null && request.CommandType == SharedIndexCommandType.Search)
            {
                SignalInFlightSearchCancellation();
            }

            var queueStopwatch = Stopwatch.StartNew();
            await _requestGate.WaitAsync(ct).ConfigureAwait(false);
            queueStopwatch.Stop();
            queuedMs = queueStopwatch.ElapsedMilliseconds;
            try
            {
                var slotResources = GetSlotResources();
                if (slotResources == null)
                {
                    throw new IOException("共享索引客户端槽位未初始化。");
                }

                var stageStopwatch = Stopwatch.StartNew();
                SharedIndexMemoryProtocol.WriteClientHeartbeatTicks(slotResources.ChangeMap, DateTime.UtcNow.Ticks);
                openMs = stageStopwatch.ElapsedMilliseconds;

                request.RequestId = Interlocked.Increment(ref _nextRequestId);
                slotResources.CancelEvent.Reset();
                slotResources.ResponseReadyEvent.Reset();

                stageStopwatch.Restart();
                requestBytes = SharedIndexMemoryProtocol.WriteRequest(slotResources.RequestMap, request);
                slotResources.RequestReadyEvent.Set();
                writeMs = stageStopwatch.ElapsedMilliseconds;

                stageStopwatch.Restart();
                WaitForResponse(slotResources, ct);
                waitMs = stageStopwatch.ElapsedMilliseconds;

                SharedIndexIpcResponse response;
                while (true)
                {
                    stageStopwatch.Restart();
                    response = SharedIndexMemoryProtocol.ReadResponse(slotResources.ResponseMap);
                    readMs += stageStopwatch.ElapsedMilliseconds;
                    if (response == null)
                    {
                        throw new IOException("后台索引宿主未返回数据。");
                    }

                    if (response.RequestId == request.RequestId)
                    {
                        break;
                    }

                    if (response.RequestId > request.RequestId)
                    {
                        throw new IOException($"后台索引宿主返回了错误的请求编号：期望 {request.RequestId}，实际 {response.RequestId}。");
                    }

                    IndexPerfLog.Write("IPC",
                        $"[MMF] outcome=stale-response command={request.CommandType} consumer={IndexPerfLog.FormatValue(_consumerName)} " +
                        $"expectedRequestId={request.RequestId} actualRequestId={response.RequestId}");

                    stageStopwatch.Restart();
                    WaitForResponse(slotResources, ct);
                    waitMs += stageStopwatch.ElapsedMilliseconds;
                }

                if (response.Status != SharedIndexResponseStatus.Success)
                {
                    throw new InvalidOperationException(response.ErrorMessage ?? "后台索引宿主执行失败。");
                }

                totalStopwatch.Stop();
                IndexPerfLog.Write("IPC",
                    $"[MMF] outcome=success command={request.CommandType} consumer={IndexPerfLog.FormatValue(_consumerName)} " +
                    $"keyword={IndexPerfLog.FormatValue(request.Keyword)} filter={request.Filter} " +
                    $"hostSearchMs={response.HostSearchMs} requestBytes={requestBytes} resultCount={(response.Results == null ? 0 : response.Results.Count)} " +
                    $"queuedMs={queuedMs} openMs={openMs} writeMs={writeMs} waitMs={waitMs} readMs={readMs} totalMs={totalStopwatch.ElapsedMilliseconds}");
                return response;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                ResetResources();
                totalStopwatch.Stop();
                IndexPerfLog.Write("IPC",
                    $"[MMF] outcome=failed command={request?.CommandType} consumer={IndexPerfLog.FormatValue(_consumerName)} " +
                    $"keyword={IndexPerfLog.FormatValue(request?.Keyword)} filter={(request == null ? SearchTypeFilter.All.ToString() : request.Filter.ToString())} " +
                    $"queuedMs={queuedMs} openMs={openMs} writeMs={writeMs} waitMs={waitMs} readMs={readMs} totalMs={totalStopwatch.ElapsedMilliseconds} " +
                    $"error={ex.GetType().Name}:{IndexPerfLog.FormatValue(ex.Message)}");
                throw;
            }
            finally
            {
                _requestGate.Release();
            }
        }

        private void SignalInFlightSearchCancellation()
        {
            try
            {
                var slotResources = GetSlotResources();
                slotResources?.CancelEvent.Set();
            }
            catch
            {
            }
        }

        private static void WaitForResponse(SharedIndexClientSlotResources slotResources, CancellationToken ct)
        {
            if (slotResources.ResponseReadyEvent.WaitOne(0))
            {
                return;
            }

            try
            {
                var waitHandles = new WaitHandle[] { slotResources.ResponseReadyEvent, ct.WaitHandle };
                var waitIndex = WaitHandle.WaitAny(waitHandles);
                if (waitIndex == 0)
                {
                    return;
                }

                throw new OperationCanceledException(ct);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    slotResources.CancelEvent.Set();
                }
                catch
                {
                }

                IndexPerfLog.Write("IPC", "[MMF] outcome=cancel-signal-sent drain=false");
                ct.ThrowIfCancellationRequested();
                throw;
            }
        }

        private async Task EnsureResourcesAvailableAsync(CancellationToken ct)
        {
            if (GetSlotResources() != null)
            {
                return;
            }

            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                lock (_resourceLock)
                {
                    if (_slotResources != null && _stateMap != null)
                    {
                        return;
                    }

                    var stateMap = SharedIndexMemoryProtocol.OpenStateMapForRead();
                    var slotResources = SharedIndexMemoryProtocol.OpenClientSlotResources(_slotId);
                    try
                    {
                        var snapshot = SharedIndexMemoryProtocol.ReadState(stateMap);
                        SharedIndexMemoryProtocol.ResetClientChangeCursor(slotResources.ChangeMap, snapshot.LastCommittedChangeSequence);
                        SharedIndexMemoryProtocol.WriteClientHeartbeatTicks(slotResources.ChangeMap, DateTime.UtcNow.Ticks);
                        AdoptHostSnapshotAfterConnect(snapshot);
                        _stateMap = stateMap;
                        _slotResources = slotResources;
                    }
                    catch
                    {
                        try
                        {
                            slotResources.Dispose();
                        }
                        catch
                        {
                        }

                        try
                        {
                            stateMap.Dispose();
                        }
                        catch
                        {
                        }

                        throw;
                    }
                }
            }, ct).ConfigureAwait(false);
        }

        private async Task EnsureConnectedHostGenerationAsync(CancellationToken ct)
        {
            var snapshot = ReadCurrentStateSnapshot();
            if (snapshot == null || snapshot.HostProcessId <= 0)
            {
                return;
            }

            bool reconnect;
            lock (_resourceLock)
            {
                reconnect = _slotResources == null ||
                    _stateMap == null ||
                    (_connectedHostProcessId > 0 && _connectedHostProcessId != snapshot.HostProcessId);
            }

            if (!reconnect)
            {
                lock (_stateLock)
                {
                    ApplySnapshotState(snapshot);
                }

                return;
            }

            ResetResources();
            await EnsureResourcesAvailableAsync(ct).ConfigureAwait(false);
        }

        private void AdoptHostSnapshotAfterConnect(SharedIndexStateSnapshot snapshot)
        {
            lock (_stateLock)
            {
                _lastChangeSequence = snapshot.LastCommittedChangeSequence;
                _lastRefreshSequence = snapshot.RefreshSequence;
                _lastStateSequence = snapshot.StateSequence;
                _connectedHostProcessId = snapshot.HostProcessId;
                _nextRequestId = 0;
                ApplySnapshotState(snapshot);
            }
        }

        private SharedIndexClientSlotResources GetSlotResources()
        {
            lock (_resourceLock)
            {
                return _slotResources;
            }
        }

        private void EnsureConnectedHostIsAlive()
        {
            lock (_resourceLock)
            {
                if (_slotResources == null || _connectedHostProcessId <= 0)
                {
                    return;
                }

                if (IsProcessAlive(_connectedHostProcessId))
                {
                    return;
                }
            }

            ResetResources();
        }

        private void ResetResources()
        {
            lock (_resourceLock)
            {
                try
                {
                    _slotResources?.Dispose();
                }
                catch
                {
                }

                try
                {
                    _stateMap?.Dispose();
                }
                catch
                {
                }

                _slotResources = null;
                _stateMap = null;
                _connectedHostProcessId = 0;
                _nextRequestId = 0;
            }
        }

        private void ApplyResponseState(SharedIndexIpcResponse response)
        {
            if (response == null)
            {
                return;
            }

            lock (_stateLock)
            {
                _indexedCount = response.IndexedCount > 0 ? response.IndexedCount : response.TotalIndexedCount;
                _currentStatusMessage = response.CurrentStatusMessage ?? string.Empty;
                _isBackgroundCatchUpInProgress = response.IsBackgroundCatchUpInProgress;
                _containsBucketStatus = response.ContainsBucketStatus ?? ContainsBucketStatus.Empty;
                if (_indexedCount > 0)
                {
                    _buildState = SharedIndexBuildState.Ready;
                }
            }
        }

        private async Task<int> WaitForSharedIndexReadyAsync(IProgress<string> progress, CancellationToken ct)
        {
            progress?.Report("正在连接共享索引宿主...");
            if (!await WaitForHostAvailabilityAsync(HostStartupWaitMilliseconds, ct).ConfigureAwait(false))
            {
                throw BuildHostUnavailableException("build", null);
            }

            await EnsureResourcesAvailableAsync(ct).ConfigureAwait(false);
            var buildStarted = false;
            while (!ct.IsCancellationRequested)
            {
                var snapshot = ReadCurrentStateSnapshot();
                if (snapshot != null)
                {
                    lock (_stateLock)
                    {
                        _lastStateSequence = Math.Max(_lastStateSequence, snapshot.StateSequence);
                        _lastRefreshSequence = Math.Max(_lastRefreshSequence, snapshot.RefreshSequence);
                        _lastChangeSequence = Math.Max(_lastChangeSequence, snapshot.LastCommittedChangeSequence);
                        ApplySnapshotState(snapshot);
                    }

                    if (!string.IsNullOrWhiteSpace(snapshot.StatusMessage))
                    {
                        progress?.Report(snapshot.StatusMessage);
                    }

                    if (snapshot.BuildState == SharedIndexBuildState.Failed)
                    {
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(snapshot.StatusMessage)
                            ? "共享索引宿主初始化失败。"
                            : snapshot.StatusMessage);
                    }

                    if (snapshot.BuildState == SharedIndexBuildState.Ready)
                    {
                        return snapshot.IndexedCount;
                    }

                    if (!buildStarted && snapshot.BuildState != SharedIndexBuildState.Building)
                    {
                        buildStarted = true;
                        return await ExecuteHostIntCommandAsync(SharedIndexCommandType.Build, progress, ct, allowHostWarmupRetry: false).ConfigureAwait(false);
                    }
                }

                await Task.Delay(HostReadyPollIntervalMilliseconds, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            return 0;
        }

        private SharedIndexStateSnapshot ReadCurrentStateSnapshot()
        {
            MemoryMappedFile stateMap;
            lock (_resourceLock)
            {
                stateMap = _stateMap;
            }

            if (stateMap == null)
            {
                return null;
            }

            try
            {
                return SharedIndexMemoryProtocol.ReadState(stateMap);
            }
            catch
            {
                ResetResources();
                throw;
            }
        }

        private void ApplySnapshotState(SharedIndexStateSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            _indexedCount = snapshot.IndexedCount;
            _currentStatusMessage = snapshot.StatusMessage ?? string.Empty;
            _isBackgroundCatchUpInProgress = snapshot.IsBackgroundCatchUpInProgress;
            _buildState = snapshot.BuildState;
            _containsBucketStatus = snapshot.ContainsBucketStatus ?? ContainsBucketStatus.Empty;
        }

        private IOException BuildHostUnavailableException(string operation, Exception ex)
        {
            IndexPerfLog.Write("IPC",
                $"[HOST REQUIRED] consumer={IndexPerfLog.FormatValue(_consumerName)} operation={IndexPerfLog.FormatValue(operation)} " +
                $"reason={IndexPerfLog.FormatValue((ex?.GetType().Name ?? "Unavailable") + ":" + (ex?.Message ?? "共享索引宿主不可用。"))}");
            return new IOException("共享索引宿主不可用，已禁止回退到本地索引。", ex);
        }

        private async Task<bool> RetryAgainstHostIfStartingAsync(IProgress<string> progress, CancellationToken ct)
        {
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
                    using (var stateMap = SharedIndexMemoryProtocol.OpenStateMapForRead())
                    {
                        var snapshot = SharedIndexMemoryProtocol.ReadState(stateMap);
                        if (snapshot.HostProcessId > 0 && IsProcessAlive(snapshot.HostProcessId))
                        {
                            return true;
                        }
                    }
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

        private static bool IsProcessAlive(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return !process.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }

        private static async Task<SharedIndexResponse> SendControlRequestAsyncStatic(SharedIndexRequest request, CancellationToken ct)
        {
            using (var stream = new NamedPipeClientStream(".", SharedIndexConstants.IndexHostCommandPipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await stream.ConnectAsync(ShowSearchUiPipeConnectMilliseconds, ct).ConfigureAwait(false);
                using (var reader = new StreamReader(stream))
                using (var writer = new StreamWriter(stream) { AutoFlush = true })
                {
                    await writer.WriteLineAsync(JsonConvert.SerializeObject(request ?? new SharedIndexRequest())).ConfigureAwait(false);
                    var readTask = reader.ReadLineAsync();
                    var completedTask = await Task.WhenAny(readTask, Task.Delay(ShowSearchUiRequestTimeoutMilliseconds, ct)).ConfigureAwait(false);
                    if (!ReferenceEquals(completedTask, readTask))
                    {
                        throw new TimeoutException("后台索引宿主未在规定时间内返回 show-search-ui 响应。");
                    }

                    var line = await readTask.ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        throw new IOException("后台索引宿主未返回控制命令响应。");
                    }

                    var response = JsonConvert.DeserializeObject<SharedIndexResponse>(line);
                    if (response == null)
                    {
                        throw new IOException("后台索引宿主返回了无效控制命令响应。");
                    }

                    if (!response.success)
                    {
                        throw new InvalidOperationException(response.error ?? "后台索引宿主执行失败。");
                    }

                    return response;
                }
            }
        }
    }
}
