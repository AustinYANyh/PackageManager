using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace MftScanner
{
    public sealed class IndexService : ISharedIndexService
    {
        private readonly IIndexServiceBackend _backend;

        public IndexService()
            : this(IndexServiceBackendFactory.Create())
        {
        }

        internal IndexService(IIndexServiceBackend backend)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public MemoryIndex Index => _backend.Index;
        public int IndexedCount => _backend.IndexedCount;
        public bool IsBackgroundCatchUpInProgress => _backend.IsBackgroundCatchUpInProgress;
        public string CurrentStatusMessage => _backend.CurrentStatusMessage;
        public ContainsBucketStatus ContainsBucketStatus => _backend.ContainsBucketStatus;
        public bool PreferSynchronousHostSearch => _backend.PreferSynchronousHostSearch;

        public event EventHandler<IndexChangedEventArgs> IndexChanged
        {
            add { _backend.IndexChanged += value; }
            remove { _backend.IndexChanged -= value; }
        }

        public event EventHandler<IndexStatusChangedEventArgs> IndexStatusChanged
        {
            add { _backend.IndexStatusChanged += value; }
            remove { _backend.IndexStatusChanged -= value; }
        }

        public Task<int> BuildIndexAsync(IProgress<string> progress, CancellationToken ct)
        {
            return _backend.BuildIndexAsync(progress, ct);
        }

        public Task<int> RebuildIndexAsync(IProgress<string> progress, CancellationToken ct)
        {
            return _backend.RebuildIndexAsync(progress, ct);
        }

        public Task<SearchQueryResult> SearchAsync(string keyword, int maxResults, int offset, IProgress<string> progress, CancellationToken ct)
        {
            return _backend.SearchAsync(keyword, maxResults, offset, progress, ct);
        }

        public Task<SearchQueryResult> SearchAsync(string keyword, int maxResults, int offset, SearchTypeFilter filter, IProgress<string> progress, CancellationToken ct)
        {
            return _backend.SearchAsync(keyword, maxResults, offset, filter, progress, ct);
        }

        public void Shutdown()
        {
            _backend.Shutdown();
        }

        public Task NotifyDeletedAsync(string fullPath, bool isDirectory, CancellationToken ct)
        {
            return _backend.NotifyDeletedAsync(fullPath, isDirectory, ct);
        }

        public void EnsureSearchHotStructuresReady(CancellationToken ct, string reason)
        {
            _backend.EnsureSearchHotStructuresReady(ct, reason);
        }
    }

    internal static class IndexServiceBackendFactory
    {
        public static IIndexServiceBackend Create()
        {
            var enableNative = string.Equals(
                Environment.GetEnvironmentVariable("PM_ENABLE_NATIVE_INDEX"),
                "1",
                StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    Environment.GetEnvironmentVariable("PM_DISABLE_NATIVE_INDEX"),
                    "1",
                    StringComparison.OrdinalIgnoreCase);

            if (!enableNative)
            {
                UsnDiagLog.Write("[NATIVE BACKEND] disabled by default; using managed backend");
                return new ManagedIndexServiceBackend();
            }

            if (NativeIndexServiceBackend.TryCreate(out var nativeBackend, out var reason))
            {
                UsnDiagLog.Write("[NATIVE BACKEND] activated");
                return nativeBackend;
            }

            UsnDiagLog.Write($"[NATIVE BACKEND] activation-failed reason={IndexPerfLog.FormatValue(reason)}");
            throw new InvalidOperationException("Native index backend is enabled but failed to start: " + reason);
        }
    }

    internal sealed class NativeIndexServiceBackend : IIndexServiceBackend, IDisposable
    {
        private readonly object _stateLock = new object();
        private readonly MemoryIndex _index = new MemoryIndex();
        private readonly NativeMethods.NativeJsonCallback _statusCallback;
        private readonly NativeMethods.NativeJsonCallback _changeCallback;
        private readonly GCHandle _selfHandle;
        private IntPtr _nativeHandle;
        private bool _isShutdown;
        private int _indexedCount;
        private string _currentStatusMessage = string.Empty;
        private bool _isBackgroundCatchUpInProgress;
        private int _searchWarmupDone;

        public event EventHandler<IndexChangedEventArgs> IndexChanged;
        public event EventHandler<IndexStatusChangedEventArgs> IndexStatusChanged;

        private NativeIndexServiceBackend()
        {
            _statusCallback = OnNativeStatus;
            _changeCallback = OnNativeChange;
            _selfHandle = GCHandle.Alloc(this);
            _nativeHandle = NativeMethods.pm_index_create(
                _statusCallback,
                _changeCallback,
                GCHandle.ToIntPtr(_selfHandle));

            if (_nativeHandle == IntPtr.Zero)
            {
                _selfHandle.Free();
                throw new InvalidOperationException("pm_index_create returned null");
            }

            RefreshStateFromNative();
        }

        public static bool TryCreate(out NativeIndexServiceBackend backend, out string reason)
        {
            backend = null;
            reason = null;

            if (!Environment.Is64BitProcess)
            {
                reason = "native backend requires x64 process";
                return false;
            }

            var assemblyDir = Path.GetDirectoryName(typeof(NativeIndexServiceBackend).Assembly.Location) ?? string.Empty;
            var candidatePath = Path.Combine(assemblyDir, "MftScanner.Native.dll");
            if (!File.Exists(candidatePath))
            {
                reason = "MftScanner.Native.dll not found";
                return false;
            }

            try
            {
                backend = new NativeIndexServiceBackend();
                return true;
            }
            catch (DllNotFoundException ex)
            {
                reason = ex.Message;
                backend = null;
                return false;
            }
            catch (BadImageFormatException ex)
            {
                reason = ex.Message;
                backend = null;
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                reason = ex.Message;
                backend = null;
                return false;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                backend = null;
                return false;
            }
        }

        public MemoryIndex Index => _index;
        public int IndexedCount => _indexedCount;
        public ContainsBucketStatus ContainsBucketStatus => ContainsBucketStatus.Empty;
        public bool PreferSynchronousHostSearch => true;

        public bool IsBackgroundCatchUpInProgress
        {
            get
            {
                lock (_stateLock)
                    return _isBackgroundCatchUpInProgress;
            }
        }

        public string CurrentStatusMessage
        {
            get
            {
                lock (_stateLock)
                    return _currentStatusMessage;
            }
        }

        public Task<int> BuildIndexAsync(IProgress<string> progress, CancellationToken ct)
        {
            return BuildNativeAsync(rebuild: false, progress, ct);
        }

        public Task<int> RebuildIndexAsync(IProgress<string> progress, CancellationToken ct)
        {
            return BuildNativeAsync(rebuild: true, progress, ct);
        }

        public Task<SearchQueryResult> SearchAsync(string keyword, int maxResults, int offset, IProgress<string> progress, CancellationToken ct)
        {
            return SearchAsync(keyword, maxResults, offset, SearchTypeFilter.All, progress, ct);
        }

        public Task<SearchQueryResult> SearchAsync(string keyword, int maxResults, int offset, SearchTypeFilter filter, IProgress<string> progress, CancellationToken ct)
        {
            return Task.FromResult(SearchNative(keyword, maxResults, offset, filter, progress, ct));
        }

        public Task NotifyDeletedAsync(string fullPath, bool isDirectory, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public void EnsureSearchHotStructuresReady(CancellationToken ct, string reason)
        {
            ct.ThrowIfCancellationRequested();
        }

        public void Shutdown()
        {
            if (_isShutdown)
                return;

            _isShutdown = true;

            ReleaseNativeHandle();
        }

        public void Dispose()
        {
            Shutdown();
        }

        private Task<int> BuildNativeAsync(bool rebuild, IProgress<string> progress, CancellationToken ct)
        {
            return Task.Run(() => InvokeBuild(rebuild, progress, ct), ct);
        }

        private SearchQueryResult SearchNative(string keyword, int maxResults, int offset, SearchTypeFilter filter, IProgress<string> progress, CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            ct.ThrowIfCancellationRequested();

            progress?.Report("正在通过 native v2 索引搜索...");
            var response = InvokeBinarySearch(keyword ?? string.Empty, maxResults, offset, filter);

            _indexedCount = response.totalIndexedCount;
            stopwatch.Stop();
            return new SearchQueryResult
            {
                TotalIndexedCount = response.totalIndexedCount,
                TotalMatchedCount = response.totalMatchedCount,
                PhysicalMatchedCount = response.physicalMatchedCount > 0 ? response.physicalMatchedCount : response.totalMatchedCount,
                UniqueMatchedCount = response.uniqueMatchedCount > 0 ? response.uniqueMatchedCount : response.totalMatchedCount,
                DuplicatePathCount = response.duplicatePathCount,
                IsTruncated = response.isTruncated,
                HostSearchMs = stopwatch.ElapsedMilliseconds,
                Results = response.results ?? new List<ScannedFileInfo>()
            };
        }

        private NativeSearchResponse InvokeBinarySearch(string keyword, int maxResults, int offset, SearchTypeFilter filter)
        {
            if (_nativeHandle == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(NativeIndexServiceBackend));

            IntPtr keywordPtr = IntPtr.Zero;
            IntPtr responsePtr = IntPtr.Zero;
            IntPtr errorPtr = IntPtr.Zero;
            try
            {
                keywordPtr = Utf8Interop.Alloc(keyword ?? string.Empty);
                int responseLength;
                var result = NativeMethods.pm_index_search_binary(
                    _nativeHandle,
                    keywordPtr,
                    maxResults,
                    offset,
                    (int)filter,
                    out responsePtr,
                    out responseLength,
                    out errorPtr);
                if (result == 0)
                {
                    var error = Utf8Interop.ReadAndFree(errorPtr);
                    errorPtr = IntPtr.Zero;
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "native binary search failed" : error);
                }

                return NativeSearchBinaryCodec.Read(responsePtr, responseLength);
            }
            finally
            {
                Utf8Interop.Free(keywordPtr);
                if (responsePtr != IntPtr.Zero)
                    NativeMethods.pm_index_free_bytes(responsePtr);
                if (errorPtr != IntPtr.Zero)
                    NativeMethods.pm_index_free_string(errorPtr);
            }
        }

        private int InvokeBuild(bool rebuild, IProgress<string> progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(rebuild ? "正在重建 native 索引..." : "正在启动 native 索引...");
            var responseJson = InvokeJsonCall(rebuild ? NativeMethods.pm_index_rebuild : NativeMethods.pm_index_build, "{}");
            var response = JsonConvert.DeserializeObject<NativeBuildResponse>(responseJson) ?? new NativeBuildResponse();
            if (!string.IsNullOrWhiteSpace(response.error))
            {
                throw new InvalidOperationException(response.error);
            }

            ApplyState(response.indexedCount, response.currentStatusMessage, response.isBackgroundCatchUpInProgress, response.requireSearchRefresh);
            WarmupSearchPath(ct);
            return response.indexedCount;
        }

        private void WarmupSearchPath(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _searchWarmupDone, 1) != 0)
            {
                return;
            }

            WarmupSearchQuery("codex", 1, ct);
            WarmupSearchQuery("d", 1, ct);
            WarmupSearchQuery("ve", 1, ct);

            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(desktopPath))
            {
                WarmupSearchQuery(desktopPath + " d", 1, ct);
            }
        }

        private void WarmupSearchQuery(string keyword, int maxResults, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var request = JsonConvert.SerializeObject(new NativeSearchRequest
                {
                    keyword = keyword ?? string.Empty,
                    maxResults = maxResults,
                    offset = 0,
                    filter = SearchTypeFilter.All.ToString()
                });

                var responseJson = InvokeJsonCall(NativeMethods.pm_index_search, request);
                JsonConvert.DeserializeObject<NativeSearchResponse>(responseJson);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                UsnDiagLog.Write(
                    $"[NATIVE WARMUP] failed keyword={IndexPerfLog.FormatValue(keyword)} " +
                    $"error={IndexPerfLog.FormatValue(ex.GetType().Name + ":" + ex.Message)}");
            }
        }

        private void RefreshStateFromNative()
        {
            var responseJson = InvokeJsonCall(NativeMethods.pm_index_get_state, "{}");
            var response = JsonConvert.DeserializeObject<NativeStateResponse>(responseJson);
            if (response == null)
                return;

            ApplyState(response.indexedCount, response.currentStatusMessage, response.isBackgroundCatchUpInProgress, response.requireSearchRefresh);
        }

        private void ApplyState(int indexedCount, string message, bool isCatchUp, bool requireSearchRefresh)
        {
            lock (_stateLock)
            {
                _currentStatusMessage = message ?? string.Empty;
                _isBackgroundCatchUpInProgress = isCatchUp;
                _indexedCount = indexedCount;
            }

            IndexStatusChanged?.Invoke(this, new IndexStatusChangedEventArgs(
                message ?? string.Empty,
                indexedCount,
                isCatchUp,
                requireSearchRefresh));
        }

        private string InvokeJsonCall(NativeMethods.NativeJsonOperation operation, string requestJson)
        {
            if (_nativeHandle == IntPtr.Zero)
                throw new ObjectDisposedException(nameof(NativeIndexServiceBackend));

            IntPtr requestPtr = IntPtr.Zero;
            IntPtr responsePtr = IntPtr.Zero;
            try
            {
                requestPtr = Utf8Interop.Alloc(requestJson ?? "{}");
                var result = operation(_nativeHandle, requestPtr, out responsePtr);
                var responseJson = Utf8Interop.ReadAndFree(responsePtr);
                if (result == 0)
                {
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(responseJson)
                        ? "native operation failed"
                        : responseJson);
                }

                return string.IsNullOrWhiteSpace(responseJson) ? "{}" : responseJson;
            }
            finally
            {
                Utf8Interop.Free(requestPtr);
            }
        }

        private void OnNativeStatus(IntPtr userData, IntPtr jsonUtf8)
        {
            var json = Utf8Interop.Read(jsonUtf8);
            if (string.IsNullOrWhiteSpace(json))
                return;

            try
            {
                var status = JsonConvert.DeserializeObject<NativeStateResponse>(json);
                if (status != null)
                {
                    ApplyState(status.indexedCount, status.currentStatusMessage, status.isBackgroundCatchUpInProgress, status.requireSearchRefresh);
                }
            }
            catch (Exception ex)
            {
                UsnDiagLog.Write($"[NATIVE BACKEND] status-callback-parse-failed error={IndexPerfLog.FormatValue(ex.Message)}");
            }
        }

        private void OnNativeChange(IntPtr userData, IntPtr jsonUtf8)
        {
            var json = Utf8Interop.Read(jsonUtf8);
            if (string.IsNullOrWhiteSpace(json))
                return;

            try
            {
                var change = JsonConvert.DeserializeObject<NativeChangeResponse>(json);
                if (change == null)
                    return;

                if (Enum.TryParse(change.type, ignoreCase: true, out IndexChangeType changeType))
                {
                    IndexChanged?.Invoke(this, new IndexChangedEventArgs(
                        changeType,
                        change.lowerName,
                        change.fullPath,
                        change.oldFullPath,
                        change.newOriginalName,
                        change.newLowerName,
                        change.isDirectory));
                }
            }
            catch (Exception ex)
            {
                UsnDiagLog.Write($"[NATIVE BACKEND] change-callback-parse-failed error={IndexPerfLog.FormatValue(ex.Message)}");
            }
        }

        private void ReleaseNativeHandle()
        {
            try
            {
                if (_nativeHandle != IntPtr.Zero)
                {
                    NativeMethods.pm_index_shutdown(_nativeHandle);
                    NativeMethods.pm_index_destroy(_nativeHandle);
                    _nativeHandle = IntPtr.Zero;
                }
            }
            finally
            {
                if (_selfHandle.IsAllocated)
                    _selfHandle.Free();
            }
        }

        private sealed class NativeBuildResponse
        {
            public int indexedCount { get; set; }
            public string currentStatusMessage { get; set; }
            public bool isBackgroundCatchUpInProgress { get; set; }
            public bool requireSearchRefresh { get; set; }
            public string error { get; set; }
        }

        private sealed class NativeStateResponse
        {
            public int indexedCount { get; set; }
            public string currentStatusMessage { get; set; }
            public bool isBackgroundCatchUpInProgress { get; set; }
            public bool requireSearchRefresh { get; set; }
        }

        private sealed class NativeSearchRequest
        {
            public string keyword { get; set; }
            public int maxResults { get; set; }
            public int offset { get; set; }
            public string filter { get; set; }
        }

        internal sealed class NativeSearchResponse
        {
            public int totalIndexedCount { get; set; }
            public int totalMatchedCount { get; set; }
            public int physicalMatchedCount { get; set; }
            public int uniqueMatchedCount { get; set; }
            public int duplicatePathCount { get; set; }
            public bool isTruncated { get; set; }
            public List<ScannedFileInfo> results { get; set; }
            public string error { get; set; }
        }

        private sealed class NativeChangeResponse
        {
            public string type { get; set; }
            public string lowerName { get; set; }
            public string fullPath { get; set; }
            public string oldFullPath { get; set; }
            public string newOriginalName { get; set; }
            public string newLowerName { get; set; }
            public bool isDirectory { get; set; }
        }
    }

    internal static class NativeMethods
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void NativeJsonCallback(IntPtr userData, IntPtr jsonUtf8);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int NativeJsonOperation(IntPtr handle, IntPtr requestJsonUtf8, out IntPtr responseJsonUtf8);

        [DllImport("MftScanner.Native.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pm_index_create")]
        internal static extern IntPtr pm_index_create(NativeJsonCallback statusCallback, NativeJsonCallback changeCallback, IntPtr userData);

        [DllImport("MftScanner.Native.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pm_index_destroy")]
        internal static extern void pm_index_destroy(IntPtr handle);

        [DllImport("MftScanner.Native.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pm_index_build")]
        internal static extern int pm_index_build(IntPtr handle, IntPtr requestJsonUtf8, out IntPtr responseJsonUtf8);

        [DllImport("MftScanner.Native.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pm_index_rebuild")]
        internal static extern int pm_index_rebuild(IntPtr handle, IntPtr requestJsonUtf8, out IntPtr responseJsonUtf8);

        [DllImport("MftScanner.Native.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pm_index_search")]
        internal static extern int pm_index_search(IntPtr handle, IntPtr requestJsonUtf8, out IntPtr responseJsonUtf8);

        [DllImport("MftScanner.Native.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pm_index_search_binary")]
        internal static extern int pm_index_search_binary(
            IntPtr handle,
            IntPtr keywordUtf8,
            int maxResults,
            int offset,
            int filterValue,
            out IntPtr responseBytes,
            out int responseLength,
            out IntPtr errorUtf8);

        [DllImport("MftScanner.Native.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pm_index_get_state")]
        internal static extern int pm_index_get_state(IntPtr handle, IntPtr requestJsonUtf8, out IntPtr responseJsonUtf8);

        [DllImport("MftScanner.Native.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pm_index_shutdown")]
        internal static extern void pm_index_shutdown(IntPtr handle);

        [DllImport("MftScanner.Native.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pm_index_free_string")]
        internal static extern void pm_index_free_string(IntPtr value);

        [DllImport("MftScanner.Native.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "pm_index_free_bytes")]
        internal static extern void pm_index_free_bytes(IntPtr value);
    }

    internal static class NativeSearchBinaryCodec
    {
        public static NativeIndexServiceBackend.NativeSearchResponse Read(IntPtr ptr, int length)
        {
            if (ptr == IntPtr.Zero || length <= 0)
                return new NativeIndexServiceBackend.NativeSearchResponse();

            var bytes = new byte[length];
            Marshal.Copy(ptr, bytes, 0, length);
            using (var stream = new MemoryStream(bytes, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: false))
            {
                var version = reader.ReadInt32();
                if (version != 1)
                    throw new InvalidOperationException("native binary search payload version mismatch");

                var response = new NativeIndexServiceBackend.NativeSearchResponse
                {
                    totalIndexedCount = reader.ReadInt32(),
                    totalMatchedCount = reader.ReadInt32(),
                    physicalMatchedCount = reader.ReadInt32(),
                    uniqueMatchedCount = reader.ReadInt32(),
                    duplicatePathCount = reader.ReadInt32(),
                    isTruncated = reader.ReadByte() != 0
                };

                var count = reader.ReadInt32();
                if (count < 0)
                    throw new InvalidOperationException("native binary search result count is invalid");

                response.results = new List<ScannedFileInfo>(count);
                for (var i = 0; i < count; i++)
                {
                    response.results.Add(new ScannedFileInfo
                    {
                        FullPath = ReadUtf16String(reader),
                        FileName = ReadUtf16String(reader),
                        SizeBytes = reader.ReadInt64(),
                        ModifiedTimeUtc = ReadUtcTicks(reader.ReadInt64()),
                        RootPath = ReadUtf16String(reader),
                        RootDisplayName = ReadUtf16String(reader),
                        IsDirectory = reader.ReadByte() != 0
                    });
                }

                return response;
            }
        }

        private static DateTime ReadUtcTicks(long ticks)
        {
            return ticks <= 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc);
        }

        private static string ReadUtf16String(BinaryReader reader)
        {
            var byteLength = reader.ReadInt32();
            if (byteLength <= 0)
                return string.Empty;

            if ((byteLength & 1) != 0)
                throw new InvalidOperationException("native binary search string length is invalid");

            var bytes = reader.ReadBytes(byteLength);
            if (bytes.Length != byteLength)
                throw new EndOfStreamException("native binary search string is truncated");

            return Encoding.Unicode.GetString(bytes);
        }
    }

    internal static class Utf8Interop
    {
        public static IntPtr Alloc(string value)
        {
            if (value == null)
                return IntPtr.Zero;

            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return ptr;
        }

        public static void Free(IntPtr ptr)
        {
            if (ptr != IntPtr.Zero)
                Marshal.FreeHGlobal(ptr);
        }

        public static string Read(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
                return null;

            var bytes = new List<byte>(256);
            var offset = 0;
            while (true)
            {
                var value = Marshal.ReadByte(ptr, offset++);
                if (value == 0)
                    break;
                bytes.Add(value);
            }

            return bytes.Count == 0 ? string.Empty : Encoding.UTF8.GetString(bytes.ToArray());
        }

        public static string ReadAndFree(IntPtr ptr)
        {
            try
            {
                return Read(ptr);
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                    NativeMethods.pm_index_free_string(ptr);
            }
        }
    }
}
