using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace MftScanner
{
    public enum SharedIndexClientSlotId
    {
        CtrlE = 1,
        CtrlQ = 2
    }

    public enum SharedIndexCommandType
    {
        None = 0,
        Build = 1,
        Rebuild = 2,
        State = 3,
        Search = 4
    }

    public enum SharedIndexResponseStatus
    {
        Success = 0,
        Error = 1
    }

    public enum SharedIndexBuildState
    {
        Unknown = 0,
        Building = 1,
        Ready = 2,
        Failed = 3
    }

    public sealed class SharedIndexIpcRequest
    {
        public long RequestId { get; set; }
        public SharedIndexCommandType CommandType { get; set; }
        public SearchTypeFilter Filter { get; set; }
        public int MaxResults { get; set; }
        public int Offset { get; set; }
        public int Flags { get; set; }
        public string Keyword { get; set; }
    }

    public sealed class SharedIndexIpcResponse
    {
        public long RequestId { get; set; }
        public SharedIndexResponseStatus Status { get; set; }
        public string ErrorMessage { get; set; }
        public int IndexedCount { get; set; }
        public string CurrentStatusMessage { get; set; }
        public bool IsBackgroundCatchUpInProgress { get; set; }
        public bool RequireSearchRefresh { get; set; }
        public long RefreshSequence { get; set; }
        public int TotalIndexedCount { get; set; }
        public int TotalMatchedCount { get; set; }
        public bool IsTruncated { get; set; }
        public long HostSearchMs { get; set; }
        public ContainsBucketStatus ContainsBucketStatus { get; set; } = ContainsBucketStatus.Empty;
        public List<ScannedFileInfo> Results { get; set; }
    }

    public sealed class SharedIndexStateSnapshot
    {
        public int HostProcessId { get; set; }
        public long HostHeartbeatTicks { get; set; }
        public long StateSequence { get; set; }
        public long IndexEpoch { get; set; }
        public int IndexedCount { get; set; }
        public bool IsBackgroundCatchUpInProgress { get; set; }
        public string StatusMessage { get; set; }
        public SharedIndexBuildState BuildState { get; set; }
        public long LastCommittedChangeSequence { get; set; }
        public long RefreshSequence { get; set; }
        public ContainsBucketStatus ContainsBucketStatus { get; set; } = ContainsBucketStatus.Empty;
    }

    public sealed class SharedIndexChangeRecord
    {
        public long Sequence { get; set; }
        public IndexChangeType ChangeType { get; set; }
        public bool IsDirectory { get; set; }
        public string LowerName { get; set; }
        public string FullPath { get; set; }
        public string OldFullPath { get; set; }
        public string NewOriginalName { get; set; }
        public string NewLowerName { get; set; }
    }

    public sealed class SearchUiStateSnapshot
    {
        public int ProcessId { get; set; }
        public long HeartbeatTicks { get; set; }
        public bool IsReady { get; set; }
        public long ReadyEpoch { get; set; }
        public long ShownEpoch { get; set; }
        public long MainWindowHandle { get; set; }
    }

    public sealed class SharedIndexClientSlotResources : IDisposable
    {
        public SharedIndexClientSlotResources(
            SharedIndexClientSlotId slotId,
            MemoryMappedFile requestMap,
            MemoryMappedFile responseMap,
            MemoryMappedFile changeMap,
            EventWaitHandle requestReadyEvent,
            EventWaitHandle responseReadyEvent,
            EventWaitHandle cancelEvent,
            EventWaitHandle statusChangedEvent,
            EventWaitHandle changeAvailableEvent)
        {
            SlotId = slotId;
            RequestMap = requestMap;
            ResponseMap = responseMap;
            ChangeMap = changeMap;
            RequestReadyEvent = requestReadyEvent;
            ResponseReadyEvent = responseReadyEvent;
            CancelEvent = cancelEvent;
            StatusChangedEvent = statusChangedEvent;
            ChangeAvailableEvent = changeAvailableEvent;
        }

        public SharedIndexClientSlotId SlotId { get; }
        public MemoryMappedFile RequestMap { get; }
        public MemoryMappedFile ResponseMap { get; }
        public MemoryMappedFile ChangeMap { get; }
        public EventWaitHandle RequestReadyEvent { get; }
        public EventWaitHandle ResponseReadyEvent { get; }
        public EventWaitHandle CancelEvent { get; }
        public EventWaitHandle StatusChangedEvent { get; }
        public EventWaitHandle ChangeAvailableEvent { get; }

        public void Dispose()
        {
            try { ChangeAvailableEvent?.Dispose(); } catch { }
            try { StatusChangedEvent?.Dispose(); } catch { }
            try { CancelEvent?.Dispose(); } catch { }
            try { ResponseReadyEvent?.Dispose(); } catch { }
            try { RequestReadyEvent?.Dispose(); } catch { }
            try { ChangeMap?.Dispose(); } catch { }
            try { ResponseMap?.Dispose(); } catch { }
            try { RequestMap?.Dispose(); } catch { }
        }
    }

    public static class SharedIndexMemoryProtocol
    {
        public const int ProtocolVersion = 3;
        public const int RequestCapacityBytes = 64 * 1024;
        public const int ResponseCapacityBytes = 32 * 1024 * 1024;
        public const int StateCapacityBytes = 64 * 1024;
        public const int SearchUiStateCapacityBytes = 4 * 1024;
        public const int ChangeRingCapacity = 4096;
        public const int ChangeRecordSizeBytes = 32 * 1024;
        public const long ClientHeartbeatTimeoutTicks = TimeSpan.TicksPerSecond * 5;
        public const long ClientHeartbeatIntervalTicks = TimeSpan.TicksPerSecond;

        private const int RequestMagic = unchecked((int)0x51445850);
        private const int ResponseMagic = unchecked((int)0x52535850);
        private const int StateMagic = unchecked((int)0x53545850);
        private const int SearchUiStateMagic = unchecked((int)0x55535850);
        private const int ChangeMagic = unchecked((int)0x43475850);

        private const int StateHeaderReservedBytes = 64;
        private const int ChangeHeaderReservedBytes = 128;

        public static long ChangeMapCapacityBytes => ChangeHeaderReservedBytes + (long)ChangeRingCapacity * ChangeRecordSizeBytes;

        public static IEnumerable<SharedIndexClientSlotId> EnumerateSlots()
        {
            yield return SharedIndexClientSlotId.CtrlE;
            yield return SharedIndexClientSlotId.CtrlQ;
        }

        public static bool TryResolveClientSlot(string consumerName, out SharedIndexClientSlotId slotId)
        {
            if (!string.IsNullOrWhiteSpace(consumerName))
            {
                var normalized = consumerName.Trim();
                if (normalized.StartsWith("CtrlE", StringComparison.OrdinalIgnoreCase))
                {
                    slotId = SharedIndexClientSlotId.CtrlE;
                    return true;
                }

                if (normalized.StartsWith("CtrlQ", StringComparison.OrdinalIgnoreCase))
                {
                    slotId = SharedIndexClientSlotId.CtrlQ;
                    return true;
                }
            }

            slotId = SharedIndexClientSlotId.CtrlQ;
            return false;
        }

        public static string BuildStateMapName()
        {
            return "PackageManager.MftScanner.IndexHost.State";
        }

        public static string BuildRequestMapName(SharedIndexClientSlotId slotId)
        {
            return "PackageManager.MftScanner.IndexHost.Request." + slotId;
        }

        public static string BuildResponseMapName(SharedIndexClientSlotId slotId)
        {
            return "PackageManager.MftScanner.IndexHost.Response." + slotId;
        }

        public static string BuildChangeMapName(SharedIndexClientSlotId slotId)
        {
            return "PackageManager.MftScanner.IndexHost.Changes." + slotId;
        }

        public static string BuildRequestReadyEventName(SharedIndexClientSlotId slotId)
        {
            return "PackageManager.MftScanner.IndexHost.RequestReady." + slotId;
        }

        public static string BuildResponseReadyEventName(SharedIndexClientSlotId slotId)
        {
            return "PackageManager.MftScanner.IndexHost.ResponseReady." + slotId;
        }

        public static string BuildCancelEventName(SharedIndexClientSlotId slotId)
        {
            return "PackageManager.MftScanner.IndexHost.Cancel." + slotId;
        }

        public static string BuildStatusChangedEventName(SharedIndexClientSlotId slotId)
        {
            return "PackageManager.MftScanner.IndexHost.StatusChanged." + slotId;
        }

        public static string BuildChangeAvailableEventName(SharedIndexClientSlotId slotId)
        {
            return "PackageManager.MftScanner.IndexHost.ChangeAvailable." + slotId;
        }

        public static SharedIndexClientSlotResources CreateHostSlotResources(SharedIndexClientSlotId slotId)
        {
            return new SharedIndexClientSlotResources(
                slotId,
                CreateOrOpenMemoryMappedFile(BuildRequestMapName(slotId), RequestCapacityBytes),
                CreateOrOpenMemoryMappedFile(BuildResponseMapName(slotId), ResponseCapacityBytes),
                CreateOrOpenMemoryMappedFile(BuildChangeMapName(slotId), ChangeMapCapacityBytes),
                CreateEvent(BuildRequestReadyEventName(slotId), EventResetMode.AutoReset),
                CreateEvent(BuildResponseReadyEventName(slotId), EventResetMode.AutoReset),
                CreateEvent(BuildCancelEventName(slotId), EventResetMode.ManualReset),
                CreateEvent(BuildStatusChangedEventName(slotId), EventResetMode.AutoReset),
                CreateEvent(BuildChangeAvailableEventName(slotId), EventResetMode.AutoReset));
        }

        public static SharedIndexClientSlotResources OpenClientSlotResources(SharedIndexClientSlotId slotId)
        {
            return new SharedIndexClientSlotResources(
                slotId,
                MemoryMappedFile.OpenExisting(BuildRequestMapName(slotId), MemoryMappedFileRights.ReadWrite),
                MemoryMappedFile.OpenExisting(BuildResponseMapName(slotId), MemoryMappedFileRights.ReadWrite),
                MemoryMappedFile.OpenExisting(BuildChangeMapName(slotId), MemoryMappedFileRights.ReadWrite),
                EventWaitHandle.OpenExisting(BuildRequestReadyEventName(slotId)),
                EventWaitHandle.OpenExisting(BuildResponseReadyEventName(slotId)),
                EventWaitHandle.OpenExisting(BuildCancelEventName(slotId)),
                EventWaitHandle.OpenExisting(BuildStatusChangedEventName(slotId)),
                EventWaitHandle.OpenExisting(BuildChangeAvailableEventName(slotId)));
        }

        public static MemoryMappedFile CreateStateMap()
        {
            return CreateOrOpenMemoryMappedFile(BuildStateMapName(), StateCapacityBytes);
        }

        public static MemoryMappedFile OpenStateMapForRead()
        {
            return MemoryMappedFile.OpenExisting(BuildStateMapName(), MemoryMappedFileRights.Read);
        }

        public static MemoryMappedFile OpenStateMapForReadWrite()
        {
            return MemoryMappedFile.OpenExisting(BuildStateMapName(), MemoryMappedFileRights.ReadWrite);
        }

        public static MemoryMappedFile CreateSearchUiStateMap(string sessionId)
        {
            return CreateOrOpenMemoryMappedFile(
                SharedIndexConstants.BuildSearchUiStateMapName(sessionId),
                SearchUiStateCapacityBytes);
        }

        public static MemoryMappedFile OpenSearchUiStateMapForRead(string sessionId)
        {
            return MemoryMappedFile.OpenExisting(
                SharedIndexConstants.BuildSearchUiStateMapName(sessionId),
                MemoryMappedFileRights.Read);
        }

        public static MemoryMappedFile OpenSearchUiStateMapForReadWrite(string sessionId)
        {
            return MemoryMappedFile.OpenExisting(
                SharedIndexConstants.BuildSearchUiStateMapName(sessionId),
                MemoryMappedFileRights.ReadWrite);
        }

        public static void InitializeChangeMap(MemoryMappedFile map)
        {
            using (var stream = map.CreateViewStream(0, ChangeMapCapacityBytes, MemoryMappedFileAccess.ReadWrite))
            using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: false))
            {
                writer.Write(ChangeMagic);
                writer.Write(ProtocolVersion);
                writer.Write(ChangeRingCapacity);
                writer.Write(ChangeRecordSizeBytes);
                writer.Write(0L); // published sequence
                writer.Write(0L); // consumed sequence
                writer.Write(0L); // client heartbeat
                writer.Write(new byte[ChangeHeaderReservedBytes - (sizeof(int) * 4 + sizeof(long) * 3)]);
            }
        }

        public static void ResetClientChangeCursor(MemoryMappedFile changeMap, long baselineSequence)
        {
            using (var accessor = changeMap.CreateViewAccessor(0, ChangeHeaderReservedBytes, MemoryMappedFileAccess.ReadWrite))
            {
                accessor.Write(16, baselineSequence);
                accessor.Write(24, baselineSequence);
            }
        }

        public static long ReadPublishedChangeSequence(MemoryMappedFile changeMap)
        {
            using (var accessor = changeMap.CreateViewAccessor(0, ChangeHeaderReservedBytes, MemoryMappedFileAccess.Read))
            {
                return accessor.ReadInt64(16);
            }
        }

        public static long ReadConsumedChangeSequence(MemoryMappedFile changeMap)
        {
            using (var accessor = changeMap.CreateViewAccessor(0, ChangeHeaderReservedBytes, MemoryMappedFileAccess.Read))
            {
                return accessor.ReadInt64(24);
            }
        }

        public static void WriteConsumedChangeSequence(MemoryMappedFile changeMap, long consumedSequence)
        {
            using (var accessor = changeMap.CreateViewAccessor(0, ChangeHeaderReservedBytes, MemoryMappedFileAccess.ReadWrite))
            {
                accessor.Write(24, consumedSequence);
                accessor.Flush();
            }
        }

        public static long ReadClientHeartbeatTicks(MemoryMappedFile changeMap)
        {
            using (var accessor = changeMap.CreateViewAccessor(0, ChangeHeaderReservedBytes, MemoryMappedFileAccess.Read))
            {
                return accessor.ReadInt64(32);
            }
        }

        public static void WriteClientHeartbeatTicks(MemoryMappedFile changeMap, long heartbeatTicks)
        {
            using (var accessor = changeMap.CreateViewAccessor(0, ChangeHeaderReservedBytes, MemoryMappedFileAccess.ReadWrite))
            {
                accessor.Write(32, heartbeatTicks);
                accessor.Flush();
            }
        }

        public static bool IsClientActive(MemoryMappedFile changeMap)
        {
            var heartbeatTicks = ReadClientHeartbeatTicks(changeMap);
            if (heartbeatTicks <= 0)
            {
                return false;
            }

            return Math.Abs(DateTime.UtcNow.Ticks - heartbeatTicks) <= ClientHeartbeatTimeoutTicks;
        }

        public static void WriteChangeRecord(MemoryMappedFile changeMap, SharedIndexChangeRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            var offset = GetChangeRecordOffset(record.Sequence);
            using (var stream = changeMap.CreateViewStream(offset, ChangeRecordSizeBytes, MemoryMappedFileAccess.ReadWrite))
            using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: false))
            {
                writer.Write(record.Sequence);
                writer.Write((int)record.ChangeType);
                writer.Write(record.IsDirectory ? 1 : 0);
                WriteSizedString(writer, record.LowerName);
                WriteSizedString(writer, record.FullPath);
                WriteSizedString(writer, record.OldFullPath);
                WriteSizedString(writer, record.NewOriginalName);
                WriteSizedString(writer, record.NewLowerName);

                var remaining = ChangeRecordSizeBytes - (int)stream.Position;
                if (remaining < 0)
                {
                    throw new InvalidOperationException("索引变更记录超过共享内存槽位大小。");
                }

                if (remaining > 0)
                {
                    writer.Write(new byte[remaining]);
                }
            }

            using (var accessor = changeMap.CreateViewAccessor(0, ChangeHeaderReservedBytes, MemoryMappedFileAccess.ReadWrite))
            {
                accessor.Write(16, record.Sequence);
                accessor.Flush();
            }
        }

        public static SharedIndexChangeRecord ReadChangeRecord(MemoryMappedFile changeMap, long sequence)
        {
            var offset = GetChangeRecordOffset(sequence);
            using (var stream = changeMap.CreateViewStream(offset, ChangeRecordSizeBytes, MemoryMappedFileAccess.Read))
            using (var reader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: false))
            {
                var storedSequence = reader.ReadInt64();
                if (storedSequence != sequence)
                {
                    throw new InvalidOperationException($"共享索引变更序号不连续，期望 {sequence}，实际 {storedSequence}。");
                }

                return new SharedIndexChangeRecord
                {
                    Sequence = storedSequence,
                    ChangeType = (IndexChangeType)reader.ReadInt32(),
                    IsDirectory = reader.ReadInt32() != 0,
                    LowerName = ReadSizedString(reader),
                    FullPath = ReadSizedString(reader),
                    OldFullPath = ReadSizedString(reader),
                    NewOriginalName = ReadSizedString(reader),
                    NewLowerName = ReadSizedString(reader)
                };
            }
        }

        public static void WriteState(MemoryMappedFile stateMap, SharedIndexStateSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            using (var stream = stateMap.CreateViewStream(0, StateCapacityBytes, MemoryMappedFileAccess.ReadWrite))
            using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: false))
            {
                writer.Write(StateMagic);
                writer.Write(ProtocolVersion);
                writer.Write(snapshot.HostProcessId);
                writer.Write(snapshot.HostHeartbeatTicks);
                writer.Write(snapshot.StateSequence);
                writer.Write(snapshot.IndexEpoch);
                writer.Write(snapshot.IndexedCount);
                writer.Write(snapshot.IsBackgroundCatchUpInProgress ? 1 : 0);
                writer.Write((int)snapshot.BuildState);
                writer.Write(snapshot.LastCommittedChangeSequence);
                writer.Write(snapshot.RefreshSequence);
                WriteContainsBucketStatus(writer, snapshot.ContainsBucketStatus);
                WriteSizedString(writer, snapshot.StatusMessage);

                var remaining = StateCapacityBytes - (int)stream.Position;
                if (remaining < 0)
                {
                    throw new InvalidOperationException("共享索引状态块超过容量。");
                }

                if (remaining > 0)
                {
                    writer.Write(new byte[remaining]);
                }
            }
        }

        public static SharedIndexStateSnapshot ReadState(MemoryMappedFile stateMap)
        {
            using (var stream = stateMap.CreateViewStream(0, StateCapacityBytes, MemoryMappedFileAccess.Read))
            using (var reader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: false))
            {
                var magic = reader.ReadInt32();
                var version = reader.ReadInt32();
                if (magic != StateMagic || version != ProtocolVersion)
                {
                    throw new InvalidOperationException("共享索引状态块版本不匹配。");
                }

                return new SharedIndexStateSnapshot
                {
                    HostProcessId = reader.ReadInt32(),
                    HostHeartbeatTicks = reader.ReadInt64(),
                    StateSequence = reader.ReadInt64(),
                    IndexEpoch = reader.ReadInt64(),
                    IndexedCount = reader.ReadInt32(),
                    IsBackgroundCatchUpInProgress = reader.ReadInt32() != 0,
                    BuildState = (SharedIndexBuildState)reader.ReadInt32(),
                    LastCommittedChangeSequence = reader.ReadInt64(),
                    RefreshSequence = reader.ReadInt64(),
                    ContainsBucketStatus = ReadContainsBucketStatus(reader),
                    StatusMessage = ReadSizedString(reader)
                };
            }
        }

        public static void WriteSearchUiState(MemoryMappedFile stateMap, SearchUiStateSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            using (var stream = stateMap.CreateViewStream(0, SearchUiStateCapacityBytes, MemoryMappedFileAccess.ReadWrite))
            using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: false))
            {
                writer.Write(SearchUiStateMagic);
                writer.Write(ProtocolVersion);
                writer.Write(snapshot.ProcessId);
                writer.Write(snapshot.HeartbeatTicks);
                writer.Write(snapshot.IsReady ? 1 : 0);
                writer.Write(snapshot.ReadyEpoch);
                writer.Write(snapshot.ShownEpoch);
                writer.Write(snapshot.MainWindowHandle);

                var remaining = SearchUiStateCapacityBytes - (int)stream.Position;
                if (remaining < 0)
                {
                    throw new InvalidOperationException("SearchUi 状态块超过容量。");
                }

                if (remaining > 0)
                {
                    writer.Write(new byte[remaining]);
                }
            }
        }

        public static SearchUiStateSnapshot ReadSearchUiState(MemoryMappedFile stateMap)
        {
            using (var stream = stateMap.CreateViewStream(0, SearchUiStateCapacityBytes, MemoryMappedFileAccess.Read))
            using (var reader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: false))
            {
                var magic = reader.ReadInt32();
                var version = reader.ReadInt32();
                if (magic != SearchUiStateMagic || version != ProtocolVersion)
                {
                    throw new InvalidOperationException("SearchUi 状态块版本不匹配。");
                }

                return new SearchUiStateSnapshot
                {
                    ProcessId = reader.ReadInt32(),
                    HeartbeatTicks = reader.ReadInt64(),
                    IsReady = reader.ReadInt32() != 0,
                    ReadyEpoch = reader.ReadInt64(),
                    ShownEpoch = reader.ReadInt64(),
                    MainWindowHandle = reader.ReadInt64()
                };
            }
        }

        public static void WriteRequest(MemoryMappedFile requestMap, SharedIndexIpcRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            using (var stream = requestMap.CreateViewStream(0, RequestCapacityBytes, MemoryMappedFileAccess.ReadWrite))
            using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: false))
            {
                writer.Write(RequestMagic);
                writer.Write(ProtocolVersion);
                writer.Write(request.RequestId);
                writer.Write((int)request.CommandType);
                writer.Write((int)request.Filter);
                writer.Write(request.MaxResults);
                writer.Write(request.Offset);
                writer.Write(request.Flags);
                WriteSizedString(writer, request.Keyword);

                var remaining = RequestCapacityBytes - (int)stream.Position;
                if (remaining < 0)
                {
                    throw new InvalidOperationException("共享索引请求块超过容量。");
                }

                if (remaining > 0)
                {
                    writer.Write(new byte[remaining]);
                }
            }
        }

        public static SharedIndexIpcRequest ReadRequest(MemoryMappedFile requestMap)
        {
            using (var stream = requestMap.CreateViewStream(0, RequestCapacityBytes, MemoryMappedFileAccess.Read))
            using (var reader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: false))
            {
                var magic = reader.ReadInt32();
                var version = reader.ReadInt32();
                if (magic != RequestMagic || version != ProtocolVersion)
                {
                    throw new InvalidOperationException("共享索引请求块版本不匹配。");
                }

                return new SharedIndexIpcRequest
                {
                    RequestId = reader.ReadInt64(),
                    CommandType = (SharedIndexCommandType)reader.ReadInt32(),
                    Filter = (SearchTypeFilter)reader.ReadInt32(),
                    MaxResults = reader.ReadInt32(),
                    Offset = reader.ReadInt32(),
                    Flags = reader.ReadInt32(),
                    Keyword = ReadSizedString(reader)
                };
            }
        }

        public static void WriteResponse(MemoryMappedFile responseMap, SharedIndexIpcResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }

            using (var stream = responseMap.CreateViewStream(0, ResponseCapacityBytes, MemoryMappedFileAccess.ReadWrite))
            using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: false))
            {
                writer.Write(ResponseMagic);
                writer.Write(ProtocolVersion);
                writer.Write(response.RequestId);
                writer.Write((int)response.Status);
                writer.Write(response.IndexedCount);
                writer.Write(response.IsBackgroundCatchUpInProgress ? 1 : 0);
                writer.Write(response.RequireSearchRefresh ? 1 : 0);
                writer.Write(response.RefreshSequence);
                writer.Write(response.TotalIndexedCount);
                writer.Write(response.TotalMatchedCount);
                writer.Write(response.IsTruncated ? 1 : 0);
                writer.Write(response.HostSearchMs);
                WriteContainsBucketStatus(writer, response.ContainsBucketStatus);
                WriteSizedString(writer, response.CurrentStatusMessage);
                WriteSizedString(writer, response.ErrorMessage);

                var results = response.Results ?? new List<ScannedFileInfo>();
                writer.Write(results.Count);
                for (var i = 0; i < results.Count; i++)
                {
                    var item = results[i] ?? new ScannedFileInfo();
                    WriteSizedString(writer, item.FullPath);
                    WriteSizedString(writer, item.FileName);
                    writer.Write(item.SizeBytes);
                    writer.Write(item.ModifiedTimeUtc.Ticks);
                    WriteSizedString(writer, item.RootPath);
                    WriteSizedString(writer, item.RootDisplayName);
                    writer.Write(item.IsDirectory ? 1 : 0);
                }

                var remaining = ResponseCapacityBytes - (int)stream.Position;
                if (remaining < 0)
                {
                    throw new InvalidOperationException("共享索引响应块超过容量。");
                }

                if (remaining > 0)
                {
                    writer.Write(new byte[remaining]);
                }
            }
        }

        public static SharedIndexIpcResponse ReadResponse(MemoryMappedFile responseMap)
        {
            using (var stream = responseMap.CreateViewStream(0, ResponseCapacityBytes, MemoryMappedFileAccess.Read))
            using (var reader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: false))
            {
                var magic = reader.ReadInt32();
                var version = reader.ReadInt32();
                if (magic != ResponseMagic || version != ProtocolVersion)
                {
                    throw new InvalidOperationException("共享索引响应块版本不匹配。");
                }

                var response = new SharedIndexIpcResponse
                {
                    RequestId = reader.ReadInt64(),
                    Status = (SharedIndexResponseStatus)reader.ReadInt32(),
                    IndexedCount = reader.ReadInt32(),
                    IsBackgroundCatchUpInProgress = reader.ReadInt32() != 0,
                    RequireSearchRefresh = reader.ReadInt32() != 0,
                    RefreshSequence = reader.ReadInt64(),
                    TotalIndexedCount = reader.ReadInt32(),
                    TotalMatchedCount = reader.ReadInt32(),
                    IsTruncated = reader.ReadInt32() != 0,
                    HostSearchMs = reader.ReadInt64(),
                    ContainsBucketStatus = ReadContainsBucketStatus(reader),
                    CurrentStatusMessage = ReadSizedString(reader),
                    ErrorMessage = ReadSizedString(reader),
                    Results = new List<ScannedFileInfo>()
                };

                var count = reader.ReadInt32();
                for (var i = 0; i < count; i++)
                {
                    response.Results.Add(new ScannedFileInfo
                    {
                        FullPath = ReadSizedString(reader),
                        FileName = ReadSizedString(reader),
                        SizeBytes = reader.ReadInt64(),
                        ModifiedTimeUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
                        RootPath = ReadSizedString(reader),
                        RootDisplayName = ReadSizedString(reader),
                        IsDirectory = reader.ReadInt32() != 0
                    });
                }

                return response;
            }
        }

        private static long GetChangeRecordOffset(long sequence)
        {
            if (sequence <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sequence));
            }

            var slotIndex = (int)((sequence - 1) % ChangeRingCapacity);
            return ChangeHeaderReservedBytes + (long)slotIndex * ChangeRecordSizeBytes;
        }

        private static MemoryMappedFile CreateOrOpenMemoryMappedFile(string name, long capacity)
        {
            return MemoryMappedFile.CreateOrOpen(
                name,
                capacity,
                MemoryMappedFileAccess.ReadWrite,
                MemoryMappedFileOptions.None,
                CreateMemoryMappedFileSecurity(),
                HandleInheritability.None);
        }

        private static EventWaitHandle CreateEvent(string name, EventResetMode resetMode)
        {
            bool createdNew;
            return new EventWaitHandle(false, resetMode, name, out createdNew, CreateEventSecurity());
        }

        private static MemoryMappedFileSecurity CreateMemoryMappedFileSecurity()
        {
            var security = new MemoryMappedFileSecurity();
            security.AddAccessRule(new AccessRule<MemoryMappedFileRights>(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                MemoryMappedFileRights.FullControl,
                AccessControlType.Allow));
            return security;
        }

        private static EventWaitHandleSecurity CreateEventSecurity()
        {
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                EventWaitHandleRights.FullControl,
                AccessControlType.Allow));
            return security;
        }

        private static void WriteSizedString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.Unicode.GetBytes(value ?? string.Empty);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static void WriteContainsBucketStatus(BinaryWriter writer, ContainsBucketStatus status)
        {
            var value = status ?? ContainsBucketStatus.Empty;
            writer.Write(value.CharReady ? 1 : 0);
            writer.Write(value.BigramReady ? 1 : 0);
            writer.Write(value.TrigramReady ? 1 : 0);
            writer.Write(value.IsOverlayOverflowed ? 1 : 0);
            writer.Write(value.Epoch);
        }

        private static string ReadSizedString(BinaryReader reader)
        {
            var byteLength = reader.ReadInt32();
            if (byteLength <= 0)
            {
                return string.Empty;
            }

            return Encoding.Unicode.GetString(reader.ReadBytes(byteLength));
        }

        private static ContainsBucketStatus ReadContainsBucketStatus(BinaryReader reader)
        {
            return new ContainsBucketStatus
            {
                CharReady = reader.ReadInt32() != 0,
                BigramReady = reader.ReadInt32() != 0,
                TrigramReady = reader.ReadInt32() != 0,
                IsOverlayOverflowed = reader.ReadInt32() != 0,
                Epoch = reader.ReadInt64()
            };
        }
    }
}
