using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace MftScanner
{
    internal sealed class IndexHostAgent : IDisposable
    {
        private readonly IndexService _indexService = new IndexService();
        private readonly Action _showSearchUi;
        private readonly List<NamedPipeServerStream> _eventSubscribers = new List<NamedPipeServerStream>();
        private readonly object _subscriberLock = new object();
        private readonly object _buildLock = new object();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task<int> _buildTask;

        public IndexHostAgent(Action showSearchUi)
        {
            _showSearchUi = showSearchUi ?? throw new ArgumentNullException(nameof(showSearchUi));
            _indexService.IndexStatusChanged += IndexService_IndexStatusChanged;
            _indexService.IndexChanged += IndexService_IndexChanged;
        }

        public void Start()
        {
            Task.Run(() => RunCommandServerLoop(_cts.Token));
            Task.Run(() => RunEventServerLoop(_cts.Token));
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

            lock (_subscriberLock)
            {
                foreach (var subscriber in _eventSubscribers.ToArray())
                {
                    TryDisposeSubscriber(subscriber);
                }

                _eventSubscribers.Clear();
            }

            _indexService.Shutdown();
            _cts.Dispose();
        }

        private void IndexService_IndexStatusChanged(object sender, IndexStatusChangedEventArgs e)
        {
            Broadcast(new SharedIndexEventMessage
            {
                type = "status",
                indexedCount = e.IndexedCount,
                currentStatusMessage = e.Message,
                isBackgroundCatchUpInProgress = e.IsBackgroundCatchUpInProgress,
                requireSearchRefresh = e.RequireSearchRefresh
            });
        }

        private void IndexService_IndexChanged(object sender, IndexChangedEventArgs e)
        {
            Broadcast(new SharedIndexEventMessage
            {
                type = "change",
                changeType = e.Type.ToString(),
                lowerName = e.LowerName,
                fullPath = e.FullPath,
                oldFullPath = e.OldFullPath,
                newOriginalName = e.NewOriginalName,
                newLowerName = e.NewLowerName,
                isDirectory = e.IsDirectory
            });
        }

        private void RunCommandServerLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = CreatePipeServer(SharedIndexConstants.IndexHostCommandPipeName);
                    server.WaitForConnection();
                    HandleCommandConnection(server, ct).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Services.LoggingService.LogWarning($"[INDEX HOST] 命令循环异常：{ex.GetType().Name}: {ex.Message}");
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

        private void RunEventServerLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = CreatePipeServer(SharedIndexConstants.IndexHostEventPipeName);
                    server.WaitForConnection();
                    RegisterEventSubscriber(server);
                    server = null;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Services.LoggingService.LogWarning($"[INDEX HOST] 事件循环异常：{ex.GetType().Name}: {ex.Message}");
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

        private async Task HandleCommandConnection(NamedPipeServerStream server, CancellationToken ct)
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
                    response = await ExecuteRequestAsync(request, ct).ConfigureAwait(false);
                    response.success = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    response = BuildStateResponse();
                    response.success = false;
                    response.error = ex.Message;
                }

                await writer.WriteLineAsync(JsonConvert.SerializeObject(response)).ConfigureAwait(false);
            }
        }

        private async Task<SharedIndexResponse> ExecuteRequestAsync(SharedIndexRequest request, CancellationToken ct)
        {
            var command = (request?.command ?? string.Empty).Trim();
            switch (command.ToLowerInvariant())
            {
                case "build":
                    return await ExecuteBuildAsync(rebuild: false, ct).ConfigureAwait(false);
                case "rebuild":
                    return await ExecuteBuildAsync(rebuild: true, ct).ConfigureAwait(false);
                case "search":
                    await EnsureBuiltAsync(rebuild: false, ct).ConfigureAwait(false);
                    var filter = ParseFilter(request?.filter);
                    var searchResult = await _indexService.SearchAsync(
                        request?.keyword ?? string.Empty,
                        request?.maxResults > 0 ? request.maxResults : 500,
                        Math.Max(request?.offset ?? 0, 0),
                        filter,
                        null,
                        ct).ConfigureAwait(false);
                    return BuildSearchResponse(searchResult);
                case "state":
                    return BuildStateResponse();
                case "show-search-ui":
                    _showSearchUi();
                    return BuildStateResponse();
                default:
                    return BuildStateResponse();
            }
        }

        private async Task<SharedIndexResponse> ExecuteBuildAsync(bool rebuild, CancellationToken ct)
        {
            var indexedCount = await EnsureBuiltAsync(rebuild, ct).ConfigureAwait(false);
            var response = BuildStateResponse();
            response.indexedCount = indexedCount;
            return response;
        }

        private Task<int> EnsureBuiltAsync(bool rebuild, CancellationToken ct)
        {
            lock (_buildLock)
            {
                if (rebuild || _buildTask == null)
                {
                    _buildTask = rebuild
                        ? _indexService.RebuildIndexAsync(null, _cts.Token)
                        : _indexService.BuildIndexAsync(null, _cts.Token);
                }

                return _buildTask;
            }
        }

        private SharedIndexResponse BuildStateResponse()
        {
            return new SharedIndexResponse
            {
                indexedCount = _indexService.IndexedCount,
                currentStatusMessage = _indexService.CurrentStatusMessage,
                isBackgroundCatchUpInProgress = _indexService.IsBackgroundCatchUpInProgress,
                totalIndexedCount = _indexService.IndexedCount
            };
        }

        private SharedIndexResponse BuildSearchResponse(SearchQueryResult result)
        {
            var response = BuildStateResponse();
            response.totalIndexedCount = result?.TotalIndexedCount ?? _indexService.IndexedCount;
            response.totalMatchedCount = result?.TotalMatchedCount ?? 0;
            response.isTruncated = result != null && result.IsTruncated;
            response.results = result?.Results ?? new List<ScannedFileInfo>();
            return response;
        }

        private static SearchTypeFilter ParseFilter(string filter)
        {
            if (Enum.TryParse(filter, true, out SearchTypeFilter parsed))
            {
                return parsed;
            }

            return SearchTypeFilter.All;
        }

        private static NamedPipeServerStream CreatePipeServer(string pipeName)
        {
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                65536,
                65536,
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

        private void RegisterEventSubscriber(NamedPipeServerStream server)
        {
            lock (_subscriberLock)
            {
                _eventSubscribers.Add(server);
            }

            Broadcast(new SharedIndexEventMessage
            {
                type = "status",
                indexedCount = _indexService.IndexedCount,
                currentStatusMessage = _indexService.CurrentStatusMessage,
                isBackgroundCatchUpInProgress = _indexService.IsBackgroundCatchUpInProgress,
                requireSearchRefresh = false
            });
        }

        private void Broadcast(SharedIndexEventMessage message)
        {
            var payload = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message) + Environment.NewLine);
            List<NamedPipeServerStream> stale = null;
            lock (_subscriberLock)
            {
                foreach (var subscriber in _eventSubscribers.ToArray())
                {
                    try
                    {
                        if (!subscriber.IsConnected)
                        {
                            (stale ??= new List<NamedPipeServerStream>()).Add(subscriber);
                            continue;
                        }

                        subscriber.Write(payload, 0, payload.Length);
                        subscriber.Flush();
                    }
                    catch
                    {
                        (stale ??= new List<NamedPipeServerStream>()).Add(subscriber);
                    }
                }

                if (stale == null)
                {
                    return;
                }

                foreach (var subscriber in stale)
                {
                    _eventSubscribers.Remove(subscriber);
                    TryDisposeSubscriber(subscriber);
                }
            }
        }

        private static void TryDisposeSubscriber(NamedPipeServerStream subscriber)
        {
            try
            {
                subscriber.Dispose();
            }
            catch
            {
            }
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
