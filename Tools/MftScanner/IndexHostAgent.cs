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
        private const int SearchUiReadyWaitMilliseconds = 5000;
        private const int SearchUiShownWaitMilliseconds = 3000;
        private readonly IndexService _indexService = new IndexService();
        private readonly Action _showSearchUi;
        private readonly object _buildLock = new object();
        private readonly object _stateLock = new object();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Dictionary<SharedIndexClientSlotId, SharedIndexClientSlotResources> _slotResources
            = new Dictionary<SharedIndexClientSlotId, SharedIndexClientSlotResources>();

        private MemoryMappedFile _stateMap;
        private Task<int> _buildTask;
        private long _stateSequence;
        private long _indexEpoch;
        private long _lastCommittedChangeSequence;
        private long _refreshSequence;
        private SharedIndexBuildState _buildState = SharedIndexBuildState.Unknown;

        public IndexHostAgent(Action showSearchUi)
        {
            _showSearchUi = showSearchUi ?? throw new ArgumentNullException(nameof(showSearchUi));
            _indexService.IndexStatusChanged += IndexService_IndexStatusChanged;
            _indexService.IndexChanged += IndexService_IndexChanged;
        }

        public void Start()
        {
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
                SharedIndexMemoryProtocol.InitializeChangeMap(slotResources.ChangeMap);
                slotResources.CancelEvent.Reset();
                _slotResources[slotId] = slotResources;
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
                try
                {
                    request = SharedIndexMemoryProtocol.ReadRequest(slotResources.RequestMap);
                    response = ExecuteRequestAsync(request, slotResources, ct).GetAwaiter().GetResult();
                    response.Status = SharedIndexResponseStatus.Success;
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
                    SharedIndexMemoryProtocol.WriteResponse(slotResources.ResponseMap, response);
                    slotResources.ResponseReadyEvent.Set();
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
                default:
                    return BuildStateResponse(request?.RequestId ?? 0);
            }
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
                                ((CancellationTokenSource)state).Cancel();
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
                TotalMatchedCount = 0,
                IsTruncated = false,
                Results = new List<ScannedFileInfo>()
            };
        }

        private SharedIndexIpcResponse BuildSearchResponse(SearchQueryResult result, long requestId, long hostSearchMs)
        {
            var response = BuildStateResponse(requestId);
            response.TotalIndexedCount = result?.TotalIndexedCount ?? _indexService.IndexedCount;
            response.TotalMatchedCount = result?.TotalMatchedCount ?? 0;
            response.IsTruncated = result != null && result.IsTruncated;
            response.HostSearchMs = result == null ? hostSearchMs : Math.Max(result.HostSearchMs, hostSearchMs);
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
                    RefreshSequence = Interlocked.Read(ref _refreshSequence)
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
                        totalIndexedCount = _indexService.IndexedCount,
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

            return Task.FromResult(new SharedIndexResponse
            {
                indexedCount = _indexService.IndexedCount,
                currentStatusMessage = _indexService.CurrentStatusMessage,
                isBackgroundCatchUpInProgress = _indexService.IsBackgroundCatchUpInProgress,
                totalIndexedCount = _indexService.IndexedCount,
                totalMatchedCount = 0,
                isTruncated = false,
                results = new List<ScannedFileInfo>()
            });
        }

        private void EnsureSearchUiShown()
        {
            var sessionId = SharedIndexConstants.SearchUiSessionId;
            var readyEventName = SharedIndexConstants.BuildSearchUiReadyEventName(sessionId);
            var shownEventName = SharedIndexConstants.BuildSearchUiShownEventName(sessionId);
            var showRequestEventName = SharedIndexConstants.BuildSearchUiShowRequestEventName(sessionId);

            if (!TryIsEventSignaled(readyEventName))
            {
                LoggingService.LogDebug("[INDEX HOST SHOW] stage=search-ui-start-requested");
                _showSearchUi();
                if (!TryWaitForEvent(readyEventName, SearchUiReadyWaitMilliseconds))
                {
                    LoggingService.LogWarning("[INDEX HOST SHOW] stage=show-request-timeout reason=search-ui-ready-timeout");
                    throw new TimeoutException("文件搜索 UI 未在规定时间内完成就绪。");
                }

                LoggingService.LogDebug("[INDEX HOST SHOW] stage=search-ui-ready");
            }

            ResetNamedEventIfExists(shownEventName);
            SignalNamedEvent(showRequestEventName);
            LoggingService.LogDebug("[INDEX HOST SHOW] stage=show-request-signaled");
            if (!TryWaitForEvent(shownEventName, SearchUiShownWaitMilliseconds))
            {
                LoggingService.LogWarning("[INDEX HOST SHOW] stage=show-request-timeout reason=search-ui-shown-timeout");
                throw new TimeoutException("文件搜索 UI 未在规定时间内完成显示。");
            }
        }

        private static bool TryIsEventSignaled(string eventName)
        {
            try
            {
                using (var handle = EventWaitHandle.OpenExisting(eventName))
                {
                    return handle.WaitOne(0);
                }
            }
            catch
            {
                return false;
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

        private static void SignalNamedEvent(string eventName)
        {
            using (var handle = EventWaitHandle.OpenExisting(eventName))
            {
                handle.Set();
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
