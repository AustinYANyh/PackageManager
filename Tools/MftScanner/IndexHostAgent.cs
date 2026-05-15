using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using MftScanner.Services;

namespace MftScanner
{
    internal sealed class IndexHostAgent : IDisposable
    {
        private const int SearchUiHotPathAckMilliseconds = 300;
        private const int SearchUiRestartReadyMilliseconds = 3000;
        private const int SearchUiRestartShownMilliseconds = 800;
        private const int SearchUiHeartbeatStaleMilliseconds = 5000;
        private const int SearchUiReadyPollMilliseconds = 50;
        private readonly IndexService _indexService = new IndexService();
        private readonly Action _showSearchUi;
        private readonly string _hostFingerprint;
        private readonly object _buildLock = new object();
        private readonly object _showSearchUiLock = new object();
        private readonly object _stateLock = new object();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Dictionary<SharedIndexClientSlotId, SharedIndexClientSlotResources> _slotResources
            = new Dictionary<SharedIndexClientSlotId, SharedIndexClientSlotResources>();
        private readonly Dictionary<SharedIndexClientSlotId, SlotSearchState> _slotSearchStates
            = new Dictionary<SharedIndexClientSlotId, SlotSearchState>();

        private MemoryMappedFile _stateMap;
        private Task<int> _buildTask;
        private long _stateSequence;
        private long _indexEpoch;
        private long _lastCommittedChangeSequence;
        private long _refreshSequence;
        private SharedIndexBuildState _buildState = SharedIndexBuildState.Unknown;

        private sealed class SlotSearchState
        {
            public readonly object SyncRoot = new object();
            public long Generation;
            public long LatestRequestId;
            public CancellationTokenSource CurrentSearchCts;
        }

        public IndexHostAgent(Action showSearchUi)
        {
            _showSearchUi = showSearchUi ?? throw new ArgumentNullException(nameof(showSearchUi));
            _hostFingerprint = ComputeHostFingerprint();
            _indexService.IndexStatusChanged += IndexService_IndexStatusChanged;
            _indexService.IndexChanged += IndexService_IndexChanged;
        }

        public void Start()
        {
            LoggingService.LogIndexPerf("INDEX",
                $"[INDEX HOST START] processId={Process.GetCurrentProcess().Id} isElevated={IsProcessElevated()} " +
                $"identity={LoggingService.FormatPerfValue(TryGetIdentityName())} process={LoggingService.FormatPerfValue(TryGetProcessPath())} " +
                $"fingerprint={LoggingService.FormatPerfValue(_hostFingerprint)}");
            InitializeSharedMemory();
            Task.Run(() => RunControlCommandServerLoop(_cts.Token));
            Task.Run(() => RunHeartbeatLoop(_cts.Token));

            foreach (var pair in _slotResources)
            {
                var slotResources = pair.Value;
                Task.Run(() => RunSlotWorker(slotResources, _cts.Token));
            }

            Task.Run(() => EnsureBuiltAsync(rebuild: false, _cts.Token));
        }

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
            }
            catch
            {
            }

            foreach (var slotResources in _slotResources.Values)
            {
                try
                {
                    slotResources.Dispose();
                }
                catch
                {
                }
            }

            _slotResources.Clear();

            try
            {
                _stateMap?.Dispose();
            }
            catch
            {
            }

            _indexService.Shutdown();
            _cts.Dispose();
        }

        private void InitializeSharedMemory()
        {
            _stateMap = SharedIndexMemoryProtocol.CreateStateMap();
            foreach (var slotId in SharedIndexMemoryProtocol.EnumerateSlots())
            {
                var slotResources = SharedIndexMemoryProtocol.CreateHostSlotResources(slotId);
                SharedIndexMemoryProtocol.InitializeRequestMap(slotResources.RequestMap);
                SharedIndexMemoryProtocol.InitializeResponseMap(slotResources.ResponseMap);
                SharedIndexMemoryProtocol.InitializeChangeMap(slotResources.ChangeMap);
                slotResources.RequestReadyEvent.Reset();
                slotResources.ResponseReadyEvent.Reset();
                slotResources.CancelEvent.Reset();
                slotResources.StatusChangedEvent.Reset();
                slotResources.ChangeAvailableEvent.Reset();
                _slotResources[slotId] = slotResources;
                _slotSearchStates[slotId] = new SlotSearchState();
            }

            PublishState(signalClients: false);
        }

        private void IndexService_IndexStatusChanged(object sender, IndexStatusChangedEventArgs e)
        {
            if (e.RequireSearchRefresh)
            {
                Interlocked.Increment(ref _refreshSequence);
            }

            PublishState(signalClients: true);
        }

        private void IndexService_IndexChanged(object sender, IndexChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            var sequence = Interlocked.Increment(ref _lastCommittedChangeSequence);
            var record = new SharedIndexChangeRecord
            {
                Sequence = sequence,
                ChangeType = e.Type,
                IsDirectory = e.IsDirectory,
                LowerName = e.LowerName,
                FullPath = e.FullPath,
                OldFullPath = e.OldFullPath,
                NewOriginalName = e.NewOriginalName,
                NewLowerName = e.NewLowerName
            };

            foreach (var slotResources in _slotResources.Values)
            {
                PublishChangeToSlot(slotResources, record, _cts.Token);
            }

            PublishState(signalClients: false);
        }

        private void RunSlotWorker(SharedIndexClientSlotResources slotResources, CancellationToken ct)
        {
            var waitHandles = new WaitHandle[] { slotResources.RequestReadyEvent, ct.WaitHandle };
            while (!ct.IsCancellationRequested)
            {
                var waitIndex = WaitHandle.WaitAny(waitHandles);
                if (waitIndex != 0)
                {
                    break;
                }

                SharedIndexIpcRequest request = null;
                SharedIndexIpcResponse response;
                long readRequestMs = 0;
                long executeMs = 0;
                long writeResponseMs = 0;
                var responseBytes = 0;
                try
                {
                    var stageStopwatch = Stopwatch.StartNew();
                    request = SharedIndexMemoryProtocol.ReadRequest(slotResources.RequestMap);
                    stageStopwatch.Stop();
                    readRequestMs = stageStopwatch.ElapsedMilliseconds;

                    if ((request?.CommandType ?? SharedIndexCommandType.None) == SharedIndexCommandType.Search)
                    {
                        if (_indexService.PreferSynchronousHostSearch)
                        {
                            CancelActiveSlotSearch(slotResources.SlotId);
                            stageStopwatch.Restart();
                            response = ExecuteRequestAsync(request, slotResources, ct).GetAwaiter().GetResult();
                            stageStopwatch.Stop();
                            executeMs = stageStopwatch.ElapsedMilliseconds;
                            response.Status = SharedIndexResponseStatus.Success;
                        }
                        else
                        {
                            DispatchSearchRequest(slotResources, request, readRequestMs, ct);
                            continue;
                        }
                    }
                    else
                    {
                        CancelActiveSlotSearch(slotResources.SlotId);

                        stageStopwatch.Restart();
                        response = ExecuteRequestAsync(request, slotResources, ct).GetAwaiter().GetResult();
                        stageStopwatch.Stop();
                        executeMs = stageStopwatch.ElapsedMilliseconds;
                        response.Status = SharedIndexResponseStatus.Success;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    response = BuildCanceledResponse(request);
                }
                catch (Exception ex)
                {
                    response = BuildErrorResponse(request, ex);
                }

                try
                {
                    var stageStopwatch = Stopwatch.StartNew();
                    responseBytes = SharedIndexMemoryProtocol.WriteResponse(slotResources.ResponseMap, response);
                    stageStopwatch.Stop();
                    writeResponseMs = stageStopwatch.ElapsedMilliseconds;
                    slotResources.ResponseReadyEvent.Set();
                    var outcome = response != null && response.Status == SharedIndexResponseStatus.Success ? "success" : "error";
                    LoggingService.LogIndexPerf("IPC",
                        $"[MMF HOST] outcome={outcome} slot={slotResources.SlotId} command={request?.CommandType ?? SharedIndexCommandType.None} " +
                        $"requestId={request?.RequestId ?? 0} keyword={LoggingService.FormatPerfValue(request?.Keyword)} filter={request?.Filter ?? SearchTypeFilter.All} " +
                        $"hostSearchMs={response?.HostSearchMs ?? 0} resultCount={(response?.Results == null ? 0 : response.Results.Count)} " +
                        $"responseBytes={responseBytes} readRequestMs={readRequestMs} executeMs={executeMs} writeResponseMs={writeResponseMs}");
                }
                catch (Exception ex)
                {
                    Services.LoggingService.LogWarning($"[INDEX HOST] 写入 {slotResources.SlotId} 响应失败：{ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private async Task<SharedIndexIpcResponse> ExecuteRequestAsync(SharedIndexIpcRequest request, SharedIndexClientSlotResources slotResources, CancellationToken hostToken)
        {
            switch (request?.CommandType ?? SharedIndexCommandType.None)
            {
                case SharedIndexCommandType.Build:
                    return await ExecuteBuildAsync(request?.RequestId ?? 0, rebuild: false, hostToken).ConfigureAwait(false);
                case SharedIndexCommandType.Rebuild:
                    return await ExecuteBuildAsync(request?.RequestId ?? 0, rebuild: true, hostToken).ConfigureAwait(false);
                case SharedIndexCommandType.State:
                    return BuildStateResponse(request?.RequestId ?? 0);
                case SharedIndexCommandType.Search:
                    await EnsureBuiltAsync(rebuild: false, hostToken).ConfigureAwait(false);
                    return await ExecuteSearchAsync(request, slotResources, hostToken).ConfigureAwait(false);
                case SharedIndexCommandType.MarkDeleted:
                    await EnsureBuiltAsync(rebuild: false, hostToken).ConfigureAwait(false);
                    return await ExecuteMarkDeletedAsync(request, hostToken).ConfigureAwait(false);
                default:
                    return BuildStateResponse(request?.RequestId ?? 0);
            }
        }

        private void DispatchSearchRequest(
            SharedIndexClientSlotResources slotResources,
            SharedIndexIpcRequest request,
            long readRequestMs,
            CancellationToken hostToken)
        {
            if (!_slotSearchStates.TryGetValue(slotResources.SlotId, out var state))
            {
                state = new SlotSearchState();
                _slotSearchStates[slotResources.SlotId] = state;
            }

            CancellationTokenSource previousCts = null;
            CancellationTokenSource searchCts;
            long generation;
            lock (state.SyncRoot)
            {
                previousCts = state.CurrentSearchCts;
                generation = ++state.Generation;
                state.LatestRequestId = request?.RequestId ?? 0;
                searchCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
                state.CurrentSearchCts = searchCts;
            }

            try
            {
                previousCts?.Cancel();
            }
            catch
            {
            }
            finally
            {
                try { previousCts?.Dispose(); } catch { }
            }

            Task.Run(async () =>
            {
                SharedIndexIpcResponse response;
                long executeMs = 0;
                try
                {
                    var executeStopwatch = Stopwatch.StartNew();
                    await EnsureBuiltAsync(rebuild: false, searchCts.Token).ConfigureAwait(false);
                    response = await ExecuteSearchAsync(request, slotResources, searchCts.Token).ConfigureAwait(false);
                    response.Status = SharedIndexResponseStatus.Success;
                    executeStopwatch.Stop();
                    executeMs = executeStopwatch.ElapsedMilliseconds;
                }
                catch (OperationCanceledException)
                {
                    response = BuildCanceledResponse(request);
                }
                catch (Exception ex)
                {
                    response = BuildErrorResponse(request, ex);
                }

                WriteSearchResponseIfCurrent(slotResources, state, generation, request, response, readRequestMs, executeMs);
            }, CancellationToken.None);
        }

        private void CancelActiveSlotSearch(SharedIndexClientSlotId slotId)
        {
            if (!_slotSearchStates.TryGetValue(slotId, out var state))
                return;

            CancellationTokenSource cts = null;
            lock (state.SyncRoot)
            {
                cts = state.CurrentSearchCts;
                state.CurrentSearchCts = null;
                state.Generation++;
            }

            try
            {
                cts?.Cancel();
            }
            catch
            {
            }
            finally
            {
                try { cts?.Dispose(); } catch { }
            }
        }

        private void WriteSearchResponseIfCurrent(
            SharedIndexClientSlotResources slotResources,
            SlotSearchState state,
            long generation,
            SharedIndexIpcRequest request,
            SharedIndexIpcResponse response,
            long readRequestMs,
            long executeMs)
        {
            var requestId = request?.RequestId ?? 0;
            var current = false;
            lock (state.SyncRoot)
            {
                current = state.Generation == generation && state.LatestRequestId == requestId;
                if (current)
                {
                    try
                    {
                        state.CurrentSearchCts?.Dispose();
                    }
                    catch
                    {
                    }

                    state.CurrentSearchCts = null;
                }
            }

            if (!current)
            {
                LoggingService.LogIndexPerf("IPC",
                    $"[MMF HOST] outcome=staleDropped slot={slotResources.SlotId} command={request?.CommandType ?? SharedIndexCommandType.None} " +
                    $"requestId={requestId} keyword={LoggingService.FormatPerfValue(request?.Keyword)} filter={request?.Filter ?? SearchTypeFilter.All} " +
                    $"executeMs={executeMs}");
                return;
            }

            try
            {
                var stageStopwatch = Stopwatch.StartNew();
                var responseBytes = SharedIndexMemoryProtocol.WriteResponse(slotResources.ResponseMap, response);
                stageStopwatch.Stop();
                slotResources.ResponseReadyEvent.Set();
                var outcome = response != null && response.Status == SharedIndexResponseStatus.Success ? "success" : "error";
                LoggingService.LogIndexPerf("IPC",
                    $"[MMF HOST] outcome={outcome} slot={slotResources.SlotId} command={request?.CommandType ?? SharedIndexCommandType.None} " +
                    $"requestId={requestId} keyword={LoggingService.FormatPerfValue(request?.Keyword)} filter={request?.Filter ?? SearchTypeFilter.All} " +
                    $"hostSearchMs={response?.HostSearchMs ?? 0} resultCount={(response?.Results == null ? 0 : response.Results.Count)} " +
                    $"responseBytes={responseBytes} readRequestMs={readRequestMs} executeMs={executeMs} writeResponseMs={stageStopwatch.ElapsedMilliseconds} staleDropped=false");
            }
            catch (Exception ex)
            {
                Services.LoggingService.LogWarning($"[INDEX HOST] 写入 {slotResources.SlotId} 搜索响应失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task<SharedIndexIpcResponse> ExecuteMarkDeletedAsync(SharedIndexIpcRequest request, CancellationToken hostToken)
        {
            await _indexService.NotifyDeletedAsync(
                request?.Keyword ?? string.Empty,
                request != null && (request.Flags & 1) != 0,
                hostToken).ConfigureAwait(false);

            return BuildStateResponse(request?.RequestId ?? 0);
        }

        private async Task<SharedIndexIpcResponse> ExecuteSearchAsync(SharedIndexIpcRequest request, SharedIndexClientSlotResources slotResources, CancellationToken hostToken)
        {
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken))
            {
                RegisteredWaitHandle cancelRegistration = null;
                try
                {
                    var searchStopwatch = Stopwatch.StartNew();
                    cancelRegistration = ThreadPool.RegisterWaitForSingleObject(
                        slotResources.CancelEvent,
                        (state, timedOut) =>
                        {
                            if (!timedOut)
                            {
                                try
                                {
                                    LoggingService.LogIndexPerf("IPC",
                                        $"[MMF] outcome=host-cancel-signal requestId={request?.RequestId ?? 0} keyword={LoggingService.FormatPerfValue(request?.Keyword)} filter={request?.Filter ?? SearchTypeFilter.All}");
                                    ((CancellationTokenSource)state).Cancel();
                                }
                                catch (ObjectDisposedException)
                                {
                                }
                            }
                        },
                        linkedCts,
                        Timeout.Infinite,
                        executeOnlyOnce: true);

                    var searchResult = await _indexService.SearchAsync(
                        request?.Keyword ?? string.Empty,
                        request != null && request.MaxResults > 0 ? request.MaxResults : 500,
                        Math.Max(request?.Offset ?? 0, 0),
                        request?.Filter ?? SearchTypeFilter.All,
                        null,
                        linkedCts.Token).ConfigureAwait(false);
                    searchStopwatch.Stop();
                    return BuildSearchResponse(searchResult, request?.RequestId ?? 0, searchStopwatch.ElapsedMilliseconds);
                }
                finally
                {
                    try
                    {
                        cancelRegistration?.Unregister(null);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private async Task<SharedIndexIpcResponse> ExecuteBuildAsync(long requestId, bool rebuild, CancellationToken ct)
        {
            var indexedCount = await EnsureBuiltAsync(rebuild, ct).ConfigureAwait(false);
            var response = BuildStateResponse(requestId);
            response.IndexedCount = indexedCount;
            return response;
        }

        private Task<int> EnsureBuiltAsync(bool rebuild, CancellationToken ct)
        {
            lock (_buildLock)
            {
                if (rebuild || _buildTask == null)
                {
                    Interlocked.Increment(ref _indexEpoch);
                    _buildState = SharedIndexBuildState.Building;
                    PublishState(signalClients: true);

                    var buildTask = rebuild
                        ? _indexService.RebuildIndexAsync(null, _cts.Token)
                        : _indexService.BuildIndexAsync(null, _cts.Token);
                    _buildTask = TrackBuildAsync(buildTask);
                }

                return _buildTask;
            }
        }

        private async Task<int> TrackBuildAsync(Task<int> buildTask)
        {
            try
            {
                var indexedCount = await buildTask.ConfigureAwait(false);
                _indexService.EnsureSearchHotStructuresReady(_cts.Token, "index-host-ready");
                _buildState = SharedIndexBuildState.Ready;
                PublishState(signalClients: true);
                return indexedCount;
            }
            catch
            {
                _buildState = SharedIndexBuildState.Failed;
                PublishState(signalClients: true);
                throw;
            }
        }

        private SharedIndexIpcResponse BuildStateResponse(long requestId)
        {
            return new SharedIndexIpcResponse
            {
                RequestId = requestId,
                IndexedCount = _indexService.IndexedCount,
                CurrentStatusMessage = _indexService.CurrentStatusMessage,
                IsBackgroundCatchUpInProgress = _indexService.IsBackgroundCatchUpInProgress,
                RequireSearchRefresh = false,
                RefreshSequence = Interlocked.Read(ref _refreshSequence),
                TotalIndexedCount = _indexService.IndexedCount,
                ContainsBucketStatus = _indexService.ContainsBucketStatus,
                TotalMatchedCount = 0,
                PhysicalMatchedCount = 0,
                UniqueMatchedCount = 0,
                DuplicatePathCount = 0,
                IsTruncated = false,
                Results = new List<ScannedFileInfo>()
            };
        }

        private SharedIndexIpcResponse BuildSearchResponse(SearchQueryResult result, long requestId, long hostSearchMs)
        {
            var response = BuildStateResponse(requestId);
            response.TotalIndexedCount = result?.TotalIndexedCount ?? _indexService.IndexedCount;
            response.TotalMatchedCount = result?.TotalMatchedCount ?? 0;
            response.PhysicalMatchedCount = result?.PhysicalMatchedCount ?? response.TotalMatchedCount;
            response.UniqueMatchedCount = result?.UniqueMatchedCount ?? response.TotalMatchedCount;
            response.DuplicatePathCount = result?.DuplicatePathCount ?? Math.Max(0, response.PhysicalMatchedCount - response.UniqueMatchedCount);
            response.IsTruncated = result != null && result.IsTruncated;
            response.HostSearchMs = result == null ? hostSearchMs : Math.Max(result.HostSearchMs, hostSearchMs);
            response.IsSnapshotStale = result != null && result.IsSnapshotStale;
            response.Results = result?.Results ?? new List<ScannedFileInfo>();
            return response;
        }

        private SharedIndexIpcResponse BuildErrorResponse(SharedIndexIpcRequest request, Exception ex)
        {
            var response = BuildStateResponse(request?.RequestId ?? 0);
            response.Status = SharedIndexResponseStatus.Error;
            response.ErrorMessage = ex?.Message ?? "后台索引宿主执行失败。";
            response.Results = new List<ScannedFileInfo>();
            return response;
        }

        private SharedIndexIpcResponse BuildCanceledResponse(SharedIndexIpcRequest request)
        {
            var response = BuildStateResponse(request?.RequestId ?? 0);
            response.Status = SharedIndexResponseStatus.Error;
            response.ErrorMessage = "请求已取消。";
            response.Results = new List<ScannedFileInfo>();
            return response;
        }

        private void PublishState(bool signalClients)
        {
            if (_stateMap == null)
            {
                return;
            }

            lock (_stateLock)
            {
                var snapshot = new SharedIndexStateSnapshot
                {
                    HostProcessId = Process.GetCurrentProcess().Id,
                    HostHeartbeatTicks = DateTime.UtcNow.Ticks,
                    StateSequence = Interlocked.Increment(ref _stateSequence),
                    IndexEpoch = Interlocked.Read(ref _indexEpoch),
                    IndexedCount = _indexService.IndexedCount,
                    IsBackgroundCatchUpInProgress = _indexService.IsBackgroundCatchUpInProgress,
                    StatusMessage = _indexService.CurrentStatusMessage ?? string.Empty,
                    BuildState = _buildState,
                    LastCommittedChangeSequence = Interlocked.Read(ref _lastCommittedChangeSequence),
                    RefreshSequence = Interlocked.Read(ref _refreshSequence),
                    HostFingerprint = _hostFingerprint,
                    ContainsBucketStatus = _indexService.ContainsBucketStatus
                };

                SharedIndexMemoryProtocol.WriteState(_stateMap, snapshot);
            }

            if (!signalClients)
            {
                return;
            }

            foreach (var slotResources in _slotResources.Values)
            {
                try
                {
                    slotResources.StatusChangedEvent.Set();
                }
                catch
                {
                }
            }
        }

        private static string ComputeHostFingerprint()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return string.Empty;
                }

                var dir = Path.GetDirectoryName(exePath) ?? string.Empty;
                return ToolBundleFingerprint.ComputeFromFiles(new[]
                {
                    exePath,
                    Path.Combine(dir, "MftScanner.Core.dll"),
                    Path.Combine(dir, "MftScanner.Native.dll")
                });
            }
            catch
            {
                return string.Empty;
            }
        }

        private void PublishChangeToSlot(SharedIndexClientSlotResources slotResources, SharedIndexChangeRecord record, CancellationToken ct)
        {
            if (slotResources == null || record == null)
            {
                return;
            }

            if (!SharedIndexMemoryProtocol.IsClientActive(slotResources.ChangeMap))
            {
                SharedIndexMemoryProtocol.ResetClientChangeCursor(slotResources.ChangeMap, record.Sequence);
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                var consumedSequence = SharedIndexMemoryProtocol.ReadConsumedChangeSequence(slotResources.ChangeMap);
                if (record.Sequence - consumedSequence <= SharedIndexMemoryProtocol.ChangeRingCapacity)
                {
                    SharedIndexMemoryProtocol.WriteChangeRecord(slotResources.ChangeMap, record);
                    slotResources.ChangeAvailableEvent.Set();
                    return;
                }

                if (!SharedIndexMemoryProtocol.IsClientActive(slotResources.ChangeMap))
                {
                    SharedIndexMemoryProtocol.ResetClientChangeCursor(slotResources.ChangeMap, record.Sequence);
                    return;
                }

                Thread.Sleep(5);
            }
        }

        private void RunHeartbeatLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    PublishState(signalClients: false);
                    if (ct.WaitHandle.WaitOne(1000))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                }
            }
        }

        private void RunControlCommandServerLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = CreatePipeServer(SharedIndexConstants.IndexHostCommandPipeName);
                    server.WaitForConnection();
                    HandleControlConnection(server, ct).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Services.LoggingService.LogWarning($"[INDEX HOST] 控制命令循环异常：{ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        server?.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private async Task HandleControlConnection(NamedPipeServerStream server, CancellationToken ct)
        {
            using (var reader = new StreamReader(server))
            using (var writer = new StreamWriter(server) { AutoFlush = true })
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                var request = string.IsNullOrWhiteSpace(line)
                    ? new SharedIndexRequest()
                    : JsonConvert.DeserializeObject<SharedIndexRequest>(line) ?? new SharedIndexRequest();

                SharedIndexResponse response;
                try
                {
                    response = await ExecuteControlRequestAsync(request, ct).ConfigureAwait(false);
                    response.success = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    response = new SharedIndexResponse
                    {
                        success = false,
                        error = ex.Message,
                        indexedCount = _indexService.IndexedCount,
                        currentStatusMessage = _indexService.CurrentStatusMessage,
                        isBackgroundCatchUpInProgress = _indexService.IsBackgroundCatchUpInProgress,
                        containsBucketStatus = _indexService.ContainsBucketStatus,
                        totalIndexedCount = _indexService.IndexedCount,
                        physicalMatchedCount = 0,
                        uniqueMatchedCount = 0,
                        duplicatePathCount = 0,
                        results = new List<ScannedFileInfo>()
                    };
                }

                await writer.WriteLineAsync(JsonConvert.SerializeObject(response)).ConfigureAwait(false);
            }
        }

        private Task<SharedIndexResponse> ExecuteControlRequestAsync(SharedIndexRequest request, CancellationToken ct)
        {
            var command = (request?.command ?? string.Empty).Trim();
            if (string.Equals(command, "show-search-ui", StringComparison.OrdinalIgnoreCase))
            {
                LoggingService.LogDebug("[INDEX HOST SHOW] stage=host-control-received command=show-search-ui");
                EnsureSearchUiShown();
            }

            if (string.Equals(command, "native-test-control", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(Environment.GetEnvironmentVariable("PM_MFT_INDEX_TEST_HOOKS"), "1", StringComparison.Ordinal))
                {
                    return Task.FromResult(new SharedIndexResponse
                    {
                        success = false,
                        error = "native test hooks are disabled"
                    });
                }

                try
                {
                    var raw = _indexService.InvokeNativeTestControl(request?.keyword ?? "{}");
                    return Task.FromResult(new SharedIndexResponse
                    {
                        success = true,
                        error = raw,
                        indexedCount = _indexService.IndexedCount,
                        currentStatusMessage = _indexService.CurrentStatusMessage,
                        isBackgroundCatchUpInProgress = _indexService.IsBackgroundCatchUpInProgress,
                        containsBucketStatus = _indexService.ContainsBucketStatus,
                        totalIndexedCount = _indexService.IndexedCount,
                        results = new List<ScannedFileInfo>()
                    });
                }
                catch (Exception ex)
                {
                    return Task.FromResult(new SharedIndexResponse
                    {
                        success = false,
                        error = ex.GetType().Name + ":" + ex.Message
                    });
                }
            }

            return Task.FromResult(new SharedIndexResponse
            {
                success = true,
                indexedCount = _indexService.IndexedCount,
                currentStatusMessage = _indexService.CurrentStatusMessage,
                isBackgroundCatchUpInProgress = _indexService.IsBackgroundCatchUpInProgress,
                containsBucketStatus = _indexService.ContainsBucketStatus,
                totalIndexedCount = _indexService.IndexedCount,
                totalMatchedCount = 0,
                physicalMatchedCount = 0,
                uniqueMatchedCount = 0,
                duplicatePathCount = 0,
                isTruncated = false,
                results = new List<ScannedFileInfo>()
            });
        }

        private void EnsureSearchUiShown()
        {
            lock (_showSearchUiLock)
            {
                var sessionId = SharedIndexConstants.SearchUiSessionId;
                if (TryReadSearchUiState(sessionId, out var state)
                    && IsSearchUiAlive(state))
                {
                    LoggingService.LogDebug($"[INDEX HOST SHOW] stage=show-hotpath-signaled pid={state.ProcessId} readyEpoch={state.ReadyEpoch} shownEpoch={state.ShownEpoch}");
                    if (TrySignalShowRequestAndWaitShown(sessionId, SearchUiHotPathAckMilliseconds))
                    {
                        LoggingService.LogDebug($"[INDEX HOST SHOW] stage=show-hotpath-acked pid={state.ProcessId}");
                        return;
                    }

                    LoggingService.LogWarning($"[INDEX HOST SHOW] stage=show-stale-ui-detected reason=hotpath-ack-timeout pid={state.ProcessId}");
                }
                else
                {
                    LoggingService.LogDebug($"[INDEX HOST SHOW] stage=show-stale-ui-detected reason={DescribeSearchUiState(state)}");
                }

                RestartSearchUi(sessionId, state);
            }
        }

        private static bool IsProcessElevated()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private static string TryGetIdentityName()
        {
            try
            {
                return WindowsIdentity.GetCurrent()?.Name ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string TryGetProcessPath()
        {
            try
            {
                return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryWaitForEvent(string eventName, int timeoutMilliseconds)
        {
            try
            {
                using (var handle = EventWaitHandle.OpenExisting(eventName))
                {
                    return handle.WaitOne(timeoutMilliseconds);
                }
            }
            catch
            {
                return false;
            }
        }

        private void RestartSearchUi(string sessionId, SearchUiStateSnapshot priorState)
        {
            if (priorState != null && priorState.ProcessId > 0)
            {
                TryTerminateProcess(priorState.ProcessId);
            }

            ResetNamedEventIfExists(SharedIndexConstants.BuildSearchUiReadyEventName(sessionId));
            ResetNamedEventIfExists(SharedIndexConstants.BuildSearchUiShownEventName(sessionId));
            TryClearSearchUiState(sessionId);

            LoggingService.LogDebug("[INDEX HOST SHOW] stage=show-ui-restart");
            _showSearchUi();

            if (!TryWaitForReadyState(sessionId, SearchUiRestartReadyMilliseconds, out var restartedState))
            {
                LoggingService.LogWarning("[INDEX HOST SHOW] stage=show-ui-restart-timeout reason=ready-timeout");
                throw new TimeoutException("文件搜索 UI 未在规定时间内进入可显示状态。");
            }

            LoggingService.LogDebug($"[INDEX HOST SHOW] stage=show-ui-restart-ready pid={restartedState.ProcessId} readyEpoch={restartedState.ReadyEpoch}");
            if (!TrySignalShowRequestAndWaitShown(sessionId, SearchUiRestartShownMilliseconds))
            {
                LoggingService.LogWarning("[INDEX HOST SHOW] stage=show-ui-restart-timeout reason=shown-timeout");
                throw new TimeoutException("文件搜索 UI 未在规定时间内完成显示。");
            }
        }

        private static bool TryReadSearchUiState(string sessionId, out SearchUiStateSnapshot snapshot)
        {
            snapshot = null;
            try
            {
                using (var stateMap = SharedIndexMemoryProtocol.OpenSearchUiStateMapForRead(sessionId))
                {
                    snapshot = SharedIndexMemoryProtocol.ReadSearchUiState(stateMap);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryWaitForReadyState(string sessionId, int timeoutMilliseconds, out SearchUiStateSnapshot snapshot)
        {
            snapshot = null;
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(timeoutMilliseconds, 0));
            while (DateTime.UtcNow <= deadline)
            {
                if (TryReadSearchUiState(sessionId, out snapshot) && IsSearchUiAlive(snapshot))
                {
                    return true;
                }

                Thread.Sleep(SearchUiReadyPollMilliseconds);
            }

            return false;
        }

        private static bool IsSearchUiAlive(SearchUiStateSnapshot snapshot)
        {
            if (snapshot == null
                || snapshot.ProcessId <= 0
                || !snapshot.IsReady
                || snapshot.ReadyEpoch <= 0)
            {
                return false;
            }

            if (!IsProcessAlive(snapshot.ProcessId))
            {
                return false;
            }

            var heartbeatAgeMs = (DateTime.UtcNow.Ticks - snapshot.HeartbeatTicks) / TimeSpan.TicksPerMillisecond;
            return heartbeatAgeMs >= 0 && heartbeatAgeMs <= SearchUiHeartbeatStaleMilliseconds;
        }

        private static string DescribeSearchUiState(SearchUiStateSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "state-unavailable";
            }

            if (snapshot.ProcessId <= 0)
            {
                return "missing-pid";
            }

            if (!snapshot.IsReady || snapshot.ReadyEpoch <= 0)
            {
                return "not-ready";
            }

            if (!IsProcessAlive(snapshot.ProcessId))
            {
                return "dead-process";
            }

            var heartbeatAgeMs = (DateTime.UtcNow.Ticks - snapshot.HeartbeatTicks) / TimeSpan.TicksPerMillisecond;
            return heartbeatAgeMs > SearchUiHeartbeatStaleMilliseconds
                ? $"stale-heartbeat({heartbeatAgeMs}ms)"
                : "unknown";
        }

        private static bool TrySignalShowRequestAndWaitShown(string sessionId, int shownWaitMilliseconds)
        {
            var shownEventName = SharedIndexConstants.BuildSearchUiShownEventName(sessionId);
            var showRequestEventName = SharedIndexConstants.BuildSearchUiShowRequestEventName(sessionId);
            ResetNamedEventIfExists(shownEventName);
            if (!SignalNamedEvent(showRequestEventName))
            {
                return false;
            }

            return TryWaitForEvent(shownEventName, shownWaitMilliseconds);
        }

        private static void ResetNamedEventIfExists(string eventName)
        {
            try
            {
                using (var handle = EventWaitHandle.OpenExisting(eventName))
                {
                    handle.Reset();
                }
            }
            catch
            {
            }
        }

        private static bool SignalNamedEvent(string eventName)
        {
            try
            {
                using (var handle = EventWaitHandle.OpenExisting(eventName))
                {
                    handle.Set();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void TryClearSearchUiState(string sessionId)
        {
            try
            {
                using (var stateMap = SharedIndexMemoryProtocol.OpenSearchUiStateMapForReadWrite(sessionId))
                {
                    SharedIndexMemoryProtocol.WriteSearchUiState(stateMap, new SearchUiStateSnapshot
                    {
                        ProcessId = 0,
                        HeartbeatTicks = 0,
                        IsReady = false,
                        ReadyEpoch = 0,
                        ShownEpoch = 0,
                        MainWindowHandle = 0
                    });
                }
            }
            catch
            {
            }
        }

        private static void TryTerminateProcess(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    if (process.HasExited)
                    {
                        return;
                    }

                    process.Kill();
                    process.WaitForExit(1000);
                }
            }
            catch
            {
            }
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

        private static NamedPipeServerStream CreatePipeServer(string pipeName)
        {
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096,
                4096,
                CreatePipeSecurity());
        }

        private static PipeSecurity CreatePipeSecurity()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
            return security;
        }

        public static void ShowSearchUiFromHost()
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--window search-ui --session-id {SharedIndexConstants.SearchUiSessionId}",
                UseShellExecute = true
            });
        }
    }
}
