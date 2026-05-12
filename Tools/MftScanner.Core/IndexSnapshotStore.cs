using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace MftScanner
{
    internal sealed class IndexSnapshotStore
    {
        private const int SnapshotVersion1 = 1;
        private const int SnapshotVersion2 = 2;
        private const int SnapshotVersion3 = 3;
        private const int SnapshotVersion4 = 4;
        private const int SnapshotVersion5 = 5;
        private const int SnapshotVersion6 = 6;
        private const int SnapshotVersion7 = 7;
        private const int SnapshotCompressionDeflate = 1;
        private const int PostingsSnapshotVersion1 = 1;
        private const int PostingsSnapshotVersion2 = 2;
        private const int PostingsSnapshotVersion3 = 3;
        private const int ShortQuerySnapshotVersion1 = 1;
        private const int ShortQuerySidecarMagic = 0x31515253; // SQR1
        private const int RecordsSidecarMagic = 0x3752534D; // MSR7
        private const int DirsSidecarMagic = 0x3744534D; // MSD7
        private const int ParentOrderSidecarMagic = 0x314F5053; // SPO1
        private const string SnapshotWriteMutexName = "PackageManager.MftScannerIndex.WriteLock";
        private const int SnapshotWriteLockTimeoutMilliseconds = 200;
        private static readonly TimeSpan SnapshotTempMaxAge = TimeSpan.FromHours(1);

        private readonly string _snapshotDirectoryPath;
        private readonly string _snapshotFilePath;
        private readonly string _recordsFilePath;
        private readonly string _directoriesFilePath;
        private readonly string _parentOrderFilePath;
        private readonly string _postingsFilePath;
        private readonly string _shortQueryFilePath;

        public IndexSnapshotStore()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _snapshotDirectoryPath = Path.Combine(appDataPath, "PackageManager", "MftScannerIndex");
            _snapshotFilePath = Path.Combine(_snapshotDirectoryPath, "index.bin");
            _recordsFilePath = Path.Combine(_snapshotDirectoryPath, "index.records.bin");
            _directoriesFilePath = Path.Combine(_snapshotDirectoryPath, "index.dirs.bin");
            _parentOrderFilePath = Path.Combine(_snapshotDirectoryPath, "index.parentorder.bin");
            _postingsFilePath = Path.Combine(_snapshotDirectoryPath, "index.postings.bin");
            _shortQueryFilePath = Path.Combine(_snapshotDirectoryPath, "index.shortquery.bin");
        }

        public bool TryLoad(out IndexSnapshot snapshot, out IndexSnapshotLoadMetrics metrics)
        {
            snapshot = null;
            metrics = null;
            if (!File.Exists(_snapshotFilePath))
                return false;

            try
            {
                if (TryLoadVersion7(out snapshot, out metrics))
                    return true;

                using (var stream = new FileStream(_snapshotFilePath, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false))
                {
                    var version = reader.ReadInt32();
                    switch (version)
                    {
                        case SnapshotVersion1:
                            snapshot = ReadVersion1(reader);
                            break;
                        case SnapshotVersion2:
                            snapshot = ReadVersion2(reader);
                            break;
                        case SnapshotVersion3:
                            snapshot = ReadVersion3(reader);
                            break;
                        case SnapshotVersion4:
                            snapshot = ReadVersion4(reader);
                            break;
                        case SnapshotVersion5:
                            snapshot = ReadVersion5(reader);
                            break;
                        case SnapshotVersion6:
                            snapshot = ReadVersion6(reader);
                            break;
                        default:
                            return false;
                    }

                    if (snapshot == null)
                        return false;

                    metrics = new IndexSnapshotLoadMetrics(
                        version,
                        stream.Length,
                        snapshot.Records?.Length ?? 0,
                        snapshot.Volumes?.Length ?? 0,
                        snapshot.Volumes?.Sum(v => v.FrnEntries?.Length ?? 0) ?? 0,
                        snapshot.StringPoolCount);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public IndexSnapshotSaveMetrics Save(IndexSnapshot snapshot)
        {
            if (snapshot == null)
                return null;

            Mutex writeMutex = null;
            var lockTaken = false;
            var tempPath = _snapshotFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                Directory.CreateDirectory(_snapshotDirectoryPath);
                writeMutex = new Mutex(false, SnapshotWriteMutexName);
                try
                {
                    lockTaken = writeMutex.WaitOne(SnapshotWriteLockTimeoutMilliseconds);
                }
                catch (AbandonedMutexException)
                {
                    lockTaken = true;
                }

                if (!lockTaken)
                {
                    UsnDiagLog.Write("[SNAPSHOT SAVE SKIP] write lock busy");
                    return null;
                }

                CleanupStaleTempSnapshots();
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
                {
                    WriteVersion6(writer, snapshot);
                }

                if (File.Exists(_snapshotFilePath))
                {
                    File.Replace(tempPath, _snapshotFilePath, null, true);
                }
                else
                {
                    File.Move(tempPath, _snapshotFilePath);
                }

                SaveOrDeletePostingsSnapshot(snapshot);
                SaveVersion7Sidecars(snapshot);

                var fileInfo = new FileInfo(_snapshotFilePath);
                return new IndexSnapshotSaveMetrics(
                    SnapshotVersion6,
                    fileInfo.Exists ? fileInfo.Length : 0,
                    snapshot.Records?.Length ?? 0,
                    snapshot.Volumes?.Length ?? 0,
                    snapshot.Volumes?.Sum(v => v.FrnEntries?.Length ?? 0) ?? 0);
            }
            catch (Exception ex)
            {
                UsnDiagLog.Write($"[SNAPSHOT SAVE FAIL] {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            finally
            {
                if (lockTaken && writeMutex != null)
                {
                    try
                    {
                        writeMutex.ReleaseMutex();
                    }
                    catch
                    {
                    }
                }

                writeMutex?.Dispose();

                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }
            }
        }

        public ContainsPostingsSnapshot TryLoadContainsPostingsSnapshot(ulong expectedFingerprint, int expectedRecordCount)
        {
            return TryLoadPostingsSnapshot(expectedFingerprint, expectedRecordCount);
        }

        public ShortQuerySnapshot TryLoadShortQuerySnapshot(ulong expectedFingerprint, int expectedRecordCount)
        {
            return TryReadShortQuerySnapshot(expectedFingerprint, expectedRecordCount, out var snapshot, out _)
                ? snapshot
                : null;
        }

        public int[] TryLoadParentOrderSnapshot(ulong expectedFingerprint, int expectedRecordCount)
        {
            return TryReadParentOrderSnapshot(expectedFingerprint, expectedRecordCount, out var parentOrder, out _)
                ? parentOrder
                : null;
        }

        public void SaveContainsPostingsSnapshot(ulong contentFingerprint, ContainsPostingsSnapshot postings)
        {
            SavePostingsSnapshot(contentFingerprint, postings);
        }

        private bool TryLoadVersion7(out IndexSnapshot snapshot, out IndexSnapshotLoadMetrics metrics)
        {
            snapshot = null;
            metrics = null;
            if (!File.Exists(_recordsFilePath) || !File.Exists(_directoriesFilePath))
                return false;

            try
            {
                var snapshotWriteUtc = File.GetLastWriteTimeUtc(_snapshotFilePath);
                var recordsWriteUtc = File.GetLastWriteTimeUtc(_recordsFilePath);
                var dirsWriteUtc = File.GetLastWriteTimeUtc(_directoriesFilePath);
                if (recordsWriteUtc.AddSeconds(1) < snapshotWriteUtc || dirsWriteUtc.AddSeconds(1) < snapshotWriteUtc)
                    return false;

                var recordsStopwatch = System.Diagnostics.Stopwatch.StartNew();
                if (!TryReadVersion7Records(out var records, out var sidecarFingerprint, out var recordStringPoolCount, out var recordsBytes))
                    return false;
                recordsStopwatch.Stop();

                var dirsTask = Task.Run(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var success = TryReadVersion7Directories(sidecarFingerprint, out var loadedVolumes, out var poolCount, out var bytes);
                    sw.Stop();
                    return (success, loadedVolumes, poolCount, bytes, elapsedMs: sw.ElapsedMilliseconds);
                });
                var parentOrderTask = Task.Run(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var success = TryReadParentOrderSnapshot(sidecarFingerprint, records.Length, out var parentOrder, out var bytes);
                    sw.Stop();
                    return (success, parentOrder, bytes, elapsedMs: sw.ElapsedMilliseconds);
                });
                var shortQueryTask = Task.Run(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var success = TryReadShortQuerySnapshot(sidecarFingerprint, records.Length, out var loadedShortQuery, out var bytes);
                    sw.Stop();
                    return (success, loadedShortQuery, bytes, elapsedMs: sw.ElapsedMilliseconds);
                });

                var dirsResult = dirsTask.GetAwaiter().GetResult();
                var parentOrderResult = parentOrderTask.GetAwaiter().GetResult();
                var shortQueryResult = shortQueryTask.GetAwaiter().GetResult();
                if (!dirsResult.success)
                    return false;

                snapshot = new IndexSnapshot(
                    records,
                    dirsResult.loadedVolumes,
                    recordStringPoolCount + dirsResult.poolCount,
                    null,
                    sidecarFingerprint,
                    shortQueryResult.success ? shortQueryResult.loadedShortQuery : null,
                    parentOrderResult.success ? parentOrderResult.parentOrder : null);
                metrics = new IndexSnapshotLoadMetrics(
                    SnapshotVersion7,
                    recordsBytes + dirsResult.bytes + (parentOrderResult.success ? parentOrderResult.bytes : 0) + (shortQueryResult.success ? shortQueryResult.bytes : 0),
                    records.Length,
                    dirsResult.loadedVolumes.Length,
                    dirsResult.loadedVolumes.Sum(v => v.FrnEntries?.Length ?? 0),
                    recordStringPoolCount + dirsResult.poolCount);
                UsnDiagLog.Write(
                    $"[V7 SNAPSHOT LOAD] outcome=success recordsMs={recordsStopwatch.ElapsedMilliseconds} dirsMs={dirsResult.elapsedMs} " +
                    $"parentOrderMs={parentOrderResult.elapsedMs} shortQueryMs={shortQueryResult.elapsedMs} " +
                    $"shortQuery={(shortQueryResult.success ? "loaded" : "miss")} parentOrder={(parentOrderResult.success ? "loaded" : "miss")} " +
                    $"records={records.Length} volumes={dirsResult.loadedVolumes.Length} fileBytes={recordsBytes + dirsResult.bytes + (parentOrderResult.success ? parentOrderResult.bytes : 0) + (shortQueryResult.success ? shortQueryResult.bytes : 0)} " +
                    $"sidecarFingerprint={sidecarFingerprint}");
                return true;
            }
            catch (Exception ex)
            {
                UsnDiagLog.Write($"[V7 SNAPSHOT LOAD] outcome=failed error={ex.GetType().Name}:{ex.Message}");
                return false;
            }
        }

        private bool TryReadVersion7Records(
            out FileRecord[] records,
            out ulong fingerprint,
            out int stringPoolCount,
            out long fileBytes)
        {
            records = null;
            fingerprint = 0;
            stringPoolCount = 0;
            fileBytes = 0;

            using (var stream = new FileStream(_recordsFilePath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false))
            {
                fileBytes = stream.Length;
                if (reader.ReadInt32() != RecordsSidecarMagic)
                    return false;

                if (reader.ReadInt32() != SnapshotVersion7)
                    return false;

                fingerprint = reader.ReadUInt64();
                var stringPool = ReadVersion7StringPool(reader);
                stringPoolCount = stringPool.Length;
                var recordCount = reader.ReadInt32();
                if (recordCount < 0)
                    return false;

                var originalNameIndexes = ReadInt32Array(reader, recordCount);
                var lowerNameIndexes = ReadInt32Array(reader, recordCount);
                var parentFrns = ReadUInt64Array(reader, recordCount);
                var frns = ReadUInt64Array(reader, recordCount);
                var drives = reader.ReadBytes(recordCount);
                var flags = reader.ReadBytes(recordCount);
                if (drives.Length != recordCount || flags.Length != recordCount)
                    return false;

                var loadedRecords = new FileRecord[recordCount];
                Parallel.For(0, recordCount, i =>
                {
                    var originalName = GetStringByIndex(stringPool, originalNameIndexes[i]) ?? string.Empty;
                    var lowerName = GetStringByIndex(stringPool, lowerNameIndexes[i]) ?? originalName.ToLowerInvariant();
                    loadedRecords[i] = new FileRecord(
                        lowerName,
                        originalName,
                        parentFrns[i],
                        (char)drives[i],
                        (flags[i] & 1) != 0,
                        frns[i]);
                });

                records = loadedRecords;
                return true;
            }
        }

        private bool TryReadVersion7Directories(
            ulong expectedFingerprint,
            out VolumeSnapshot[] volumes,
            out int stringPoolCount,
            out long fileBytes)
        {
            volumes = null;
            stringPoolCount = 0;
            fileBytes = 0;

            using (var stream = new FileStream(_directoriesFilePath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false))
            {
                fileBytes = stream.Length;
                if (reader.ReadInt32() != DirsSidecarMagic)
                    return false;

                if (reader.ReadInt32() != SnapshotVersion7)
                    return false;

                var fingerprint = reader.ReadUInt64();
                if (fingerprint != expectedFingerprint)
                    return false;

                var volumeCount = reader.ReadInt32();
                if (volumeCount < 0)
                    return false;

                volumes = new VolumeSnapshot[volumeCount];
                for (var i = 0; i < volumeCount; i++)
                {
                    var driveLetter = reader.ReadChar();
                    var nextUsn = reader.ReadInt64();
                    var journalId = reader.ReadUInt64();
                    var stringPool = ReadVersion7StringPool(reader);
                    stringPoolCount += stringPool.Length;

                    var frnCount = reader.ReadInt32();
                    if (frnCount < 0)
                        return false;

                    var nameIndexes = ReadInt32Array(reader, frnCount);
                    var frns = ReadUInt64Array(reader, frnCount);
                    var parentFrns = ReadUInt64Array(reader, frnCount);
                    var flags = reader.ReadBytes(frnCount);
                    if (flags.Length != frnCount)
                        return false;

                    var entries = new FrnSnapshotEntry[frnCount];
                    Parallel.For(0, frnCount, j =>
                    {
                        entries[j] = new FrnSnapshotEntry(
                            frns[j],
                            GetStringByIndex(stringPool, nameIndexes[j]) ?? string.Empty,
                            parentFrns[j],
                            (flags[j] & 1) != 0);
                    });
                    volumes[i] = new VolumeSnapshot(driveLetter, nextUsn, journalId, entries);
                }

                return true;
            }
        }

        private bool TryReadParentOrderSnapshot(
            ulong expectedFingerprint,
            int expectedRecordCount,
            out int[] parentOrder,
            out long fileBytes)
        {
            parentOrder = null;
            fileBytes = 0;
            if (!File.Exists(_parentOrderFilePath) || expectedFingerprint == 0 || expectedRecordCount <= 0)
            {
                UsnDiagLog.Write(
                    $"[PARENT ORDER SNAPSHOT LOAD] outcome=miss reason=missing-or-invalid-request exists={File.Exists(_parentOrderFilePath)} " +
                    $"expectedFingerprint={expectedFingerprint} expectedRecords={expectedRecordCount}");
                return false;
            }

            try
            {
                using (var stream = new FileStream(_parentOrderFilePath, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false))
                {
                    fileBytes = stream.Length;
                    if (reader.ReadInt32() != ParentOrderSidecarMagic)
                        return false;

                    if (reader.ReadInt32() != SnapshotVersion7)
                        return false;

                    var fingerprint = reader.ReadUInt64();
                    if (fingerprint != expectedFingerprint)
                    {
                        UsnDiagLog.Write(
                            $"[PARENT ORDER SNAPSHOT LOAD] outcome=miss reason=fingerprint-mismatch fileBytes={fileBytes} " +
                            $"expectedFingerprint={expectedFingerprint} actualFingerprint={fingerprint} expectedRecords={expectedRecordCount}");
                        return false;
                    }

                    var recordCount = reader.ReadInt32();
                    if (recordCount != expectedRecordCount)
                    {
                        UsnDiagLog.Write(
                            $"[PARENT ORDER SNAPSHOT LOAD] outcome=miss reason=record-count-mismatch fileBytes={fileBytes} " +
                            $"expectedRecords={expectedRecordCount} actualRecords={recordCount}");
                        return false;
                    }

                    parentOrder = ReadInt32Array(reader);
                    if (parentOrder == null || parentOrder.Length != expectedRecordCount)
                    {
                        return false;
                    }

                    UsnDiagLog.Write(
                        $"[PARENT ORDER SNAPSHOT LOAD] outcome=success fileBytes={fileBytes} records={recordCount} indices={parentOrder.Length}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                UsnDiagLog.Write($"[PARENT ORDER SNAPSHOT LOAD] outcome=failed error={ex.GetType().Name}:{ex.Message}");
                return false;
            }
        }

        private static IndexSnapshot ReadVersion1(BinaryReader reader)
        {
            var recordCount = reader.ReadInt32();
            if (recordCount < 0)
                return null;

            var records = new FileRecord[recordCount];
            for (var i = 0; i < recordCount; i++)
            {
                var lowerName = reader.ReadString();
                var originalName = reader.ReadString();
                var parentFrn = reader.ReadUInt64();
                var driveLetter = reader.ReadChar();
                var isDirectory = reader.ReadBoolean();
                records[i] = new FileRecord(lowerName, originalName, parentFrn, driveLetter, isDirectory);
            }

            var volumes = ReadVolumesV1(reader);
            if (volumes == null)
                return null;

            records = AttachRecordFrns(records, volumes);
            return new IndexSnapshot(records, volumes);
        }

        private static IndexSnapshot ReadVersion2(BinaryReader reader)
        {
            var stringPoolCount = reader.ReadInt32();
            if (stringPoolCount < 0)
                return null;

            var stringPool = new string[stringPoolCount];
            for (var i = 0; i < stringPool.Length; i++)
                stringPool[i] = reader.ReadString();

            var recordCount = reader.ReadInt32();
            if (recordCount < 0)
                return null;

            var records = new FileRecord[recordCount];
            for (var i = 0; i < recordCount; i++)
            {
                var originalName = GetStringByIndex(stringPool, reader.ReadInt32());
                if (originalName == null)
                    return null;

                var parentFrn = reader.ReadUInt64();
                var driveLetter = reader.ReadChar();
                var isDirectory = reader.ReadBoolean();
                records[i] = new FileRecord(
                    originalName.ToLowerInvariant(),
                    originalName,
                    parentFrn,
                    driveLetter,
                    isDirectory);
            }

            var volumeCount = reader.ReadInt32();
            if (volumeCount < 0)
                return null;

            var volumes = new VolumeSnapshot[volumeCount];
            for (var i = 0; i < volumeCount; i++)
            {
                var driveLetter = reader.ReadChar();
                var nextUsn = reader.ReadInt64();
                var journalId = reader.ReadUInt64();
                var frnCount = reader.ReadInt32();
                if (frnCount < 0)
                    return null;

                var frnEntries = new FrnSnapshotEntry[frnCount];
                for (var j = 0; j < frnEntries.Length; j++)
                {
                    var name = GetStringByIndex(stringPool, reader.ReadInt32());
                    if (name == null)
                        return null;

                    frnEntries[j] = new FrnSnapshotEntry(
                        reader.ReadUInt64(),
                        name,
                        reader.ReadUInt64(),
                        reader.ReadBoolean());
                }

                volumes[i] = new VolumeSnapshot(driveLetter, nextUsn, journalId, frnEntries);
            }

            records = AttachRecordFrns(records, volumes);
            return new IndexSnapshot(records, volumes, stringPool.Length);
        }

        private static IndexSnapshot ReadVersion3(BinaryReader reader)
        {
            return ReadVersion3Body(reader, readContainsPostings: false);
        }

        private static IndexSnapshot ReadVersion4(BinaryReader reader)
        {
            return ReadVersion3Body(reader, readContainsPostings: true, contentFingerprint: 0);
        }

        private static IndexSnapshot ReadVersion5(BinaryReader reader)
        {
            var compression = reader.ReadInt32();
            if (compression != SnapshotCompressionDeflate)
                return null;

            using (var compressed = new DeflateStream(reader.BaseStream, CompressionMode.Decompress, leaveOpen: true))
            using (var bodyReader = new BinaryReader(compressed, Encoding.UTF8, leaveOpen: false))
            {
                var contentFingerprint = bodyReader.ReadUInt64();
                return ReadVersion3Body(bodyReader, readContainsPostings: true, contentFingerprint: contentFingerprint);
            }
        }

        private static IndexSnapshot ReadVersion6(BinaryReader reader)
        {
            var compression = reader.ReadInt32();
            if (compression != SnapshotCompressionDeflate)
                return null;

            using (var compressed = new DeflateStream(reader.BaseStream, CompressionMode.Decompress, leaveOpen: true))
            using (var bodyReader = new BinaryReader(compressed, Encoding.UTF8, leaveOpen: false))
            {
                var contentFingerprint = bodyReader.ReadUInt64();
                return ReadVersion3Body(bodyReader, readContainsPostings: true, contentFingerprint: contentFingerprint);
            }
        }

        private static IndexSnapshot ReadVersion3Body(BinaryReader reader, bool readContainsPostings)
        {
            return ReadVersion3Body(reader, readContainsPostings, contentFingerprint: 0);
        }

        private static IndexSnapshot ReadVersion3Body(BinaryReader reader, bool readContainsPostings, ulong contentFingerprint)
        {
            var stringPoolCount = reader.ReadInt32();
            if (stringPoolCount < 0)
                return null;

            var stringPool = new string[stringPoolCount];
            for (var i = 0; i < stringPool.Length; i++)
                stringPool[i] = reader.ReadString();

            var recordCount = reader.ReadInt32();
            if (recordCount < 0)
                return null;

            var records = new FileRecord[recordCount];
            for (var i = 0; i < recordCount; i++)
            {
                var originalName = GetStringByIndex(stringPool, reader.ReadInt32());
                if (originalName == null)
                    return null;

                var parentFrn = reader.ReadUInt64();
                var driveLetter = reader.ReadChar();
                var isDirectory = reader.ReadBoolean();
                var frn = reader.ReadUInt64();
                records[i] = new FileRecord(
                    originalName.ToLowerInvariant(),
                    originalName,
                    parentFrn,
                    driveLetter,
                    isDirectory,
                    frn);
            }

            var volumeCount = reader.ReadInt32();
            if (volumeCount < 0)
                return null;

            var volumes = new VolumeSnapshot[volumeCount];
            for (var i = 0; i < volumeCount; i++)
            {
                var driveLetter = reader.ReadChar();
                var nextUsn = reader.ReadInt64();
                var journalId = reader.ReadUInt64();
                var frnCount = reader.ReadInt32();
                if (frnCount < 0)
                    return null;

                var frnEntries = new FrnSnapshotEntry[frnCount];
                for (var j = 0; j < frnEntries.Length; j++)
                {
                    var name = GetStringByIndex(stringPool, reader.ReadInt32());
                    if (name == null)
                        return null;

                    frnEntries[j] = new FrnSnapshotEntry(
                        reader.ReadUInt64(),
                        name,
                        reader.ReadUInt64(),
                        reader.ReadBoolean());
                }

                volumes[i] = new VolumeSnapshot(driveLetter, nextUsn, journalId, frnEntries);
            }

            var containsPostings = readContainsPostings
                ? ReadContainsPostings(reader)
                : null;

            return new IndexSnapshot(records, volumes, stringPool.Length, containsPostings, contentFingerprint);
        }

        private static VolumeSnapshot[] ReadVolumesV1(BinaryReader reader)
        {
            var volumeCount = reader.ReadInt32();
            if (volumeCount < 0)
                return null;

            var volumes = new VolumeSnapshot[volumeCount];
            for (var i = 0; i < volumeCount; i++)
            {
                var driveLetter = reader.ReadChar();
                var nextUsn = reader.ReadInt64();
                var journalId = reader.ReadUInt64();
                var frnCount = reader.ReadInt32();
                if (frnCount < 0)
                    return null;

                var frnEntries = new FrnSnapshotEntry[frnCount];
                for (var j = 0; j < frnCount; j++)
                {
                    frnEntries[j] = new FrnSnapshotEntry(
                        reader.ReadUInt64(),
                        reader.ReadString(),
                        reader.ReadUInt64(),
                        reader.ReadBoolean());
                }

                volumes[i] = new VolumeSnapshot(driveLetter, nextUsn, journalId, frnEntries);
            }

            return volumes;
        }

        private static void WriteVersion3(BinaryWriter writer, IndexSnapshot snapshot)
        {
            writer.Write(SnapshotVersion3);
            WriteVersion3Body(writer, snapshot);
        }

        private static void WriteVersion4(BinaryWriter writer, IndexSnapshot snapshot)
        {
            writer.Write(SnapshotVersion4);
            WriteVersion3Body(writer, snapshot);
        }

        private static void WriteVersion5(BinaryWriter writer, IndexSnapshot snapshot)
        {
            writer.Write(SnapshotVersion5);
            writer.Write(SnapshotCompressionDeflate);
            using (var compressed = new DeflateStream(writer.BaseStream, CompressionLevel.Fastest, leaveOpen: true))
            using (var bodyWriter = new BinaryWriter(compressed, Encoding.UTF8, leaveOpen: false))
            {
                var fingerprint = snapshot.ContentFingerprint != 0
                    ? snapshot.ContentFingerprint
                    : IndexSnapshotFingerprint.Compute(snapshot.Records);
                bodyWriter.Write(fingerprint);
                WriteVersion3Body(bodyWriter, snapshot);
            }
        }

        private static void WriteVersion6(BinaryWriter writer, IndexSnapshot snapshot)
        {
            writer.Write(SnapshotVersion6);
            writer.Write(SnapshotCompressionDeflate);
            using (var compressed = new DeflateStream(writer.BaseStream, CompressionLevel.Fastest, leaveOpen: true))
            using (var bodyWriter = new BinaryWriter(compressed, Encoding.UTF8, leaveOpen: false))
            {
                var fingerprint = snapshot.ContentFingerprint != 0
                    ? snapshot.ContentFingerprint
                    : IndexSnapshotFingerprint.Compute(snapshot.Records);
                bodyWriter.Write(fingerprint);
                WriteVersion3Body(bodyWriter, new IndexSnapshot(
                    snapshot.Records,
                    snapshot.Volumes,
                    snapshot.StringPoolCount,
                    containsPostings: null,
                    contentFingerprint: fingerprint));
            }
        }

        private static void WriteVersion3Body(BinaryWriter writer, IndexSnapshot snapshot)
        {

            var records = snapshot.Records ?? Array.Empty<FileRecord>();
            var volumes = snapshot.Volumes ?? Array.Empty<VolumeSnapshot>();
            var stringPool = BuildStringPool(records, volumes);
            var stringIndexMap = new Dictionary<string, int>(stringPool.Length, StringComparer.Ordinal);
            for (var i = 0; i < stringPool.Length; i++)
                stringIndexMap[stringPool[i]] = i;

            writer.Write(stringPool.Length);
            for (var i = 0; i < stringPool.Length; i++)
                writer.Write(stringPool[i]);

            writer.Write(records.Length);
            for (var i = 0; i < records.Length; i++)
            {
                var record = records[i];
                writer.Write(stringIndexMap[record.OriginalName ?? string.Empty]);
                writer.Write(record.ParentFrn);
                writer.Write(record.DriveLetter);
                writer.Write(record.IsDirectory);
                writer.Write(record.Frn);
            }

            writer.Write(volumes.Length);
            for (var i = 0; i < volumes.Length; i++)
            {
                var volume = volumes[i];
                writer.Write(volume.DriveLetter);
                writer.Write(volume.NextUsn);
                writer.Write(volume.JournalId);

                var frnEntries = volume.FrnEntries ?? Array.Empty<FrnSnapshotEntry>();
                writer.Write(frnEntries.Length);
                for (var j = 0; j < frnEntries.Length; j++)
                {
                    var entry = frnEntries[j];
                    writer.Write(stringIndexMap[entry.Name ?? string.Empty]);
                    writer.Write(entry.Frn);
                    writer.Write(entry.ParentFrn);
                    writer.Write(entry.IsDirectory);
                }
            }

            WriteContainsPostings(writer, snapshot.ContainsPostings);
        }

        private static ContainsPostingsSnapshot ReadContainsPostings(BinaryReader reader)
        {
            return ReadContainsPostingsV1(reader);
        }

        private static ContainsPostingsSnapshot ReadContainsPostingsV1(BinaryReader reader)
        {
            try
            {
                var hasPostings = reader.ReadBoolean();
                if (!hasPostings)
                    return null;

                var recordCount = reader.ReadInt32();
                var keyCount = reader.ReadInt32();
                if (recordCount < 0 || keyCount < 0)
                    return null;

                var keys = new ulong[keyCount];
                var offsets = new int[keyCount];
                var counts = new int[keyCount];
                var byteCounts = new int[keyCount];
                for (var i = 0; i < keyCount; i++)
                {
                    keys[i] = reader.ReadUInt64();
                    offsets[i] = reader.ReadInt32();
                    counts[i] = reader.ReadInt32();
                    byteCounts[i] = reader.ReadInt32();
                }

                var byteLength = reader.ReadInt32();
                if (byteLength < 0)
                    return null;

                var bytes = reader.ReadBytes(byteLength);
                return bytes.Length == byteLength
                    ? new ContainsPostingsSnapshot(
                        recordCount,
                        ContainsPostingsBucketKind.Trigram,
                        keys,
                        offsets,
                        counts,
                        byteCounts,
                        bytes)
                    : null;
            }
            catch (EndOfStreamException)
            {
                return null;
            }
        }

        private static ContainsPostingsSnapshot ReadContainsPostingsV2(BinaryReader reader, bool shortAsciiTokensIncluded = false)
        {
            try
            {
                var recordCount = reader.ReadInt32();
                var sectionCount = reader.ReadInt32();
                if (recordCount < 0 || sectionCount < 0 || sectionCount > 16)
                    return null;

                var sections = new List<ContainsPostingsBucketSnapshot>(sectionCount);
                for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
                {
                    var kind = (ContainsPostingsBucketKind)reader.ReadInt32();
                    var keyCount = reader.ReadInt32();
                    if (keyCount < 0)
                        return null;

                    if (kind != ContainsPostingsBucketKind.Trigram)
                    {
                        SkipContainsPostingsBucket(reader, keyCount);
                        continue;
                    }

                    var keys = new ulong[keyCount];
                    var offsets = new int[keyCount];
                    var counts = new int[keyCount];
                    var byteCounts = new int[keyCount];
                    for (var i = 0; i < keyCount; i++)
                    {
                        keys[i] = reader.ReadUInt64();
                        offsets[i] = reader.ReadInt32();
                        counts[i] = reader.ReadInt32();
                        byteCounts[i] = reader.ReadInt32();
                    }

                    var byteLength = reader.ReadInt32();
                    if (byteLength < 0)
                        return null;

                    var bytes = reader.ReadBytes(byteLength);
                    if (bytes.Length != byteLength)
                        return null;

                    sections.Add(new ContainsPostingsBucketSnapshot(kind, keys, offsets, counts, byteCounts, bytes));
                }

                return new ContainsPostingsSnapshot(recordCount, sections.ToArray(), shortAsciiTokensIncluded);
            }
            catch (EndOfStreamException)
            {
                return null;
            }
        }

        private static void SkipContainsPostingsBucket(BinaryReader reader, int keyCount)
        {
            var metadataBytes = checked(keyCount * (sizeof(ulong) + sizeof(int) + sizeof(int) + sizeof(int)));
            if (metadataBytes > 0)
            {
                reader.BaseStream.Seek(metadataBytes, SeekOrigin.Current);
            }

            var byteLength = reader.ReadInt32();
            if (byteLength > 0)
            {
                reader.BaseStream.Seek(byteLength, SeekOrigin.Current);
            }
        }

        private static ContainsPostingsSnapshot ReadContainsPostingsV3(BinaryReader reader)
        {
            try
            {
                var shortAsciiTokensIncluded = reader.ReadBoolean();
                return ReadContainsPostingsV2(reader, shortAsciiTokensIncluded);
            }
            catch (EndOfStreamException)
            {
                return null;
            }
        }

        private static void WriteContainsPostings(BinaryWriter writer, ContainsPostingsSnapshot snapshot)
        {
            if (snapshot == null
                || snapshot.Keys == null
                || snapshot.Offsets == null
                || snapshot.Counts == null
                || snapshot.ByteCounts == null
                || snapshot.Bytes == null
                || snapshot.Keys.Length != snapshot.Offsets.Length
                || snapshot.Keys.Length != snapshot.Counts.Length
                || snapshot.Keys.Length != snapshot.ByteCounts.Length)
            {
                writer.Write(false);
                return;
            }

            writer.Write(true);
            writer.Write(snapshot.RecordCount);
            writer.Write(snapshot.Keys.Length);
            for (var i = 0; i < snapshot.Keys.Length; i++)
            {
                writer.Write(snapshot.Keys[i]);
                writer.Write(snapshot.Offsets[i]);
                writer.Write(snapshot.Counts[i]);
                writer.Write(snapshot.ByteCounts[i]);
            }

            writer.Write(snapshot.Bytes.Length);
            writer.Write(snapshot.Bytes);
        }

        private static void WriteContainsPostingsV2(BinaryWriter writer, ContainsPostingsSnapshot snapshot)
        {
            var sections = snapshot?.Buckets ?? Array.Empty<ContainsPostingsBucketSnapshot>();
            writer.Write(snapshot?.RecordCount ?? 0);
            writer.Write(sections.Length);
            for (var sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
            {
                var section = sections[sectionIndex];
                writer.Write((int)section.Kind);
                writer.Write(section.Keys.Length);
                for (var i = 0; i < section.Keys.Length; i++)
                {
                    writer.Write(section.Keys[i]);
                    writer.Write(section.Offsets[i]);
                    writer.Write(section.Counts[i]);
                    writer.Write(section.ByteCounts[i]);
                }

                writer.Write(section.Bytes.Length);
                writer.Write(section.Bytes);
            }
        }

        private void SaveVersion7Sidecars(IndexSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Records == null || snapshot.Volumes == null)
                return;

            var fingerprint = snapshot.ContentFingerprint != 0
                ? snapshot.ContentFingerprint
                : IndexSnapshotFingerprint.Compute(snapshot.Records);
            var recordsTempPath = _recordsFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var dirsTempPath = _directoriesFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var parentOrderTempPath = _parentOrderFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var shortQueryTempPath = _shortQueryFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var recordsStopwatch = System.Diagnostics.Stopwatch.StartNew();
                WriteVersion7Records(recordsTempPath, snapshot.Records, fingerprint);
                ReplaceFile(recordsTempPath, _recordsFilePath);
                recordsStopwatch.Stop();

                var dirsStopwatch = System.Diagnostics.Stopwatch.StartNew();
                WriteVersion7Directories(dirsTempPath, snapshot.Volumes, fingerprint);
                ReplaceFile(dirsTempPath, _directoriesFilePath);
                dirsStopwatch.Stop();

                var parentOrderStopwatch = System.Diagnostics.Stopwatch.StartNew();
                WriteParentOrderSidecar(parentOrderTempPath, snapshot.ParentOrder, fingerprint, snapshot.Records.Length);
                if (File.Exists(parentOrderTempPath))
                {
                    ReplaceFile(parentOrderTempPath, _parentOrderFilePath);
                }
                parentOrderStopwatch.Stop();

                var shortQueryStopwatch = System.Diagnostics.Stopwatch.StartNew();
                WriteShortQuerySidecar(shortQueryTempPath, snapshot.ShortQuery, fingerprint, snapshot.Records.Length);
                if (File.Exists(shortQueryTempPath))
                {
                    ReplaceFile(shortQueryTempPath, _shortQueryFilePath);
                }
                shortQueryStopwatch.Stop();

                var recordsLength = new FileInfo(_recordsFilePath).Length;
                var dirsLength = new FileInfo(_directoriesFilePath).Length;
                var parentOrderLength = File.Exists(_parentOrderFilePath) ? new FileInfo(_parentOrderFilePath).Length : 0;
                var shortQueryLength = File.Exists(_shortQueryFilePath) ? new FileInfo(_shortQueryFilePath).Length : 0;
                UsnDiagLog.Write(
                    $"[V7 SNAPSHOT SAVE] outcome=success recordsMs={recordsStopwatch.ElapsedMilliseconds} dirsMs={dirsStopwatch.ElapsedMilliseconds} " +
                    $"parentOrderMs={parentOrderStopwatch.ElapsedMilliseconds} shortQueryMs={shortQueryStopwatch.ElapsedMilliseconds} " +
                    $"recordsBytes={recordsLength} dirsBytes={dirsLength} parentOrderBytes={parentOrderLength} shortQueryBytes={shortQueryLength} " +
                    $"records={snapshot.Records.Length} volumes={snapshot.Volumes.Length}");
            }
            catch (Exception ex)
            {
                UsnDiagLog.Write($"[V7 SNAPSHOT SAVE] outcome=failed error={ex.GetType().Name}:{ex.Message}");
                TryDeleteFile(recordsTempPath);
                TryDeleteFile(dirsTempPath);
                TryDeleteFile(parentOrderTempPath);
                TryDeleteFile(shortQueryTempPath);
            }
        }

        private static void WriteVersion7Records(string filePath, FileRecord[] records, ulong fingerprint)
        {
            var recordCount = records?.Length ?? 0;
            var stringPool = new List<string>(Math.Max(16, recordCount / 2));
            var stringIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
            var originalNameIndexes = new int[recordCount];
            var lowerNameIndexes = new int[recordCount];
            var parentFrns = new ulong[recordCount];
            var frns = new ulong[recordCount];
            var drives = new byte[recordCount];
            var flags = new byte[recordCount];

            for (var i = 0; i < recordCount; i++)
            {
                var record = records[i];
                originalNameIndexes[i] = GetOrAddStringIndex(record?.OriginalName, stringPool, stringIndexes);
                lowerNameIndexes[i] = GetOrAddStringIndex(record?.LowerName, stringPool, stringIndexes);
                parentFrns[i] = record?.ParentFrn ?? 0;
                frns[i] = record?.Frn ?? 0;
                drives[i] = (byte)(record?.DriveLetter ?? '\0');
                flags[i] = (byte)((record != null && record.IsDirectory) ? 1 : 0);
            }

            using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(RecordsSidecarMagic);
                writer.Write(SnapshotVersion7);
                writer.Write(fingerprint);
                WriteVersion7StringPool(writer, stringPool);
                writer.Write(recordCount);
                WriteInt32Array(writer, originalNameIndexes);
                WriteInt32Array(writer, lowerNameIndexes);
                WriteUInt64Array(writer, parentFrns);
                WriteUInt64Array(writer, frns);
                writer.Write(drives);
                writer.Write(flags);
            }
        }

        private static void WriteVersion7Directories(string filePath, VolumeSnapshot[] volumes, ulong fingerprint)
        {
            volumes = volumes ?? Array.Empty<VolumeSnapshot>();
            using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(DirsSidecarMagic);
                writer.Write(SnapshotVersion7);
                writer.Write(fingerprint);
                writer.Write(volumes.Length);

                for (var i = 0; i < volumes.Length; i++)
                {
                    var volume = volumes[i];
                    var entries = volume?.FrnEntries ?? Array.Empty<FrnSnapshotEntry>();
                    var stringPool = new List<string>(Math.Max(16, entries.Length / 2));
                    var stringIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
                    var nameIndexes = new int[entries.Length];
                    var frns = new ulong[entries.Length];
                    var parentFrns = new ulong[entries.Length];
                    var flags = new byte[entries.Length];

                    for (var j = 0; j < entries.Length; j++)
                    {
                        var entry = entries[j];
                        nameIndexes[j] = GetOrAddStringIndex(entry?.Name, stringPool, stringIndexes);
                        frns[j] = entry?.Frn ?? 0;
                        parentFrns[j] = entry?.ParentFrn ?? 0;
                        flags[j] = (byte)((entry != null && entry.IsDirectory) ? 1 : 0);
                    }

                    writer.Write(volume?.DriveLetter ?? '\0');
                    writer.Write(volume?.NextUsn ?? 0);
                    writer.Write(volume?.JournalId ?? 0);
                    WriteVersion7StringPool(writer, stringPool);
                    writer.Write(entries.Length);
                    WriteInt32Array(writer, nameIndexes);
                    WriteUInt64Array(writer, frns);
                    WriteUInt64Array(writer, parentFrns);
                    writer.Write(flags);
                }
            }
        }

        private void WriteParentOrderSidecar(string filePath, int[] parentOrder, ulong fingerprint, int expectedRecordCount)
        {
            if (parentOrder == null || fingerprint == 0 || expectedRecordCount <= 0 || parentOrder.Length != expectedRecordCount)
                return;

            try
            {
                Directory.CreateDirectory(_snapshotDirectoryPath);
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
                {
                    writer.Write(ParentOrderSidecarMagic);
                    writer.Write(SnapshotVersion7);
                    writer.Write(fingerprint);
                    writer.Write(expectedRecordCount);
                    WriteInt32ArrayWithLength(writer, parentOrder);
                }
            }
            catch (Exception ex)
            {
                UsnDiagLog.Write($"[PARENT ORDER SNAPSHOT SAVE] outcome=failed error={ex.GetType().Name}:{ex.Message}");
                TryDeleteFile(filePath);
            }
        }

        private bool TryReadShortQuerySnapshot(
            ulong expectedFingerprint,
            int expectedRecordCount,
            out ShortQuerySnapshot snapshot,
            out long fileBytes)
        {
            snapshot = null;
            fileBytes = 0;
            if (!File.Exists(_shortQueryFilePath) || expectedFingerprint == 0 || expectedRecordCount <= 0)
            {
                UsnDiagLog.Write(
                    $"[SHORT QUERY SNAPSHOT LOAD] outcome=miss reason=missing-or-invalid-request exists={File.Exists(_shortQueryFilePath)} " +
                    $"expectedFingerprint={expectedFingerprint} expectedRecords={expectedRecordCount}");
                return false;
            }

            try
            {
                using (var stream = new FileStream(_shortQueryFilePath, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false))
                {
                    fileBytes = stream.Length;
                    if (reader.ReadInt32() != ShortQuerySidecarMagic)
                        return false;

                    if (reader.ReadInt32() != ShortQuerySnapshotVersion1)
                        return false;

                    var fingerprint = reader.ReadUInt64();
                    if (fingerprint != expectedFingerprint)
                    {
                        UsnDiagLog.Write(
                            $"[SHORT QUERY SNAPSHOT LOAD] outcome=miss reason=fingerprint-mismatch fileBytes={fileBytes} " +
                            $"expectedFingerprint={expectedFingerprint} actualFingerprint={fingerprint} expectedRecords={expectedRecordCount}");
                        return false;
                    }

                    var recordCount = reader.ReadInt32();
                    if (recordCount != expectedRecordCount)
                    {
                        UsnDiagLog.Write(
                            $"[SHORT QUERY SNAPSHOT LOAD] outcome=miss reason=record-count-mismatch fileBytes={fileBytes} " +
                            $"expectedRecords={expectedRecordCount} actualRecords={recordCount}");
                        return false;
                    }

                    var singleCounts = ReadInt32Array(reader, 128);
                    var singleDriveCounts = ReadInt32Matrix(reader, 26, 128);
                    var bigramCounts = ReadInt32Array(reader, 128 * 128);
                    var bigramDriveCounts = ReadInt32Matrix(reader, 26, 128 * 128);
                    var bigramSamples = ReadShortQueryPageSamples(reader);
                    var nonAsciiSingles = ReadShortQueryPostings(reader);
                    var nonAsciiBigrams = ReadShortQueryPostings(reader);

                    snapshot = new ShortQuerySnapshot(
                        recordCount,
                        singleCounts,
                        singleDriveCounts,
                        bigramCounts,
                        bigramDriveCounts,
                        bigramSamples,
                        nonAsciiSingles,
                        nonAsciiBigrams);

                    UsnDiagLog.Write(
                        $"[SHORT QUERY SNAPSHOT LOAD] outcome=success fileBytes={fileBytes} records={recordCount} " +
                        $"asciiSingles={singleCounts.Length} asciiBigrams={bigramCounts.Length} " +
                        $"nonAsciiBuckets={snapshot.NonAsciiBucketCount} nonAsciiPostings={snapshot.NonAsciiPostingCount}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                UsnDiagLog.Write($"[SHORT QUERY SNAPSHOT LOAD] outcome=failed error={ex.GetType().Name}:{ex.Message}");
                return false;
            }
        }

        private void WriteShortQuerySidecar(
            string filePath,
            ShortQuerySnapshot snapshot,
            ulong fingerprint,
            int expectedRecordCount)
        {
            if (snapshot == null || fingerprint == 0 || expectedRecordCount <= 0 || snapshot.RecordCount != expectedRecordCount)
                return;

            try
            {
                Directory.CreateDirectory(_snapshotDirectoryPath);
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
                {
                    writer.Write(ShortQuerySidecarMagic);
                    writer.Write(ShortQuerySnapshotVersion1);
                    writer.Write(fingerprint);
                    writer.Write(snapshot.RecordCount);
                    WriteInt32Array(writer, snapshot.SingleCharAsciiCounts ?? Array.Empty<int>());
                    WriteInt32Matrix(writer, snapshot.SingleCharAsciiDriveCounts ?? Array.Empty<int[]>(), 26, 128);
                    WriteInt32Array(writer, snapshot.BigramAsciiCounts ?? Array.Empty<int>());
                    WriteInt32Matrix(writer, snapshot.BigramAsciiDriveCounts ?? Array.Empty<int[]>(), 26, 128 * 128);
                    WriteShortQueryPageSamples(writer, snapshot.BigramAsciiPageSamples);
                    WriteShortQueryPostings(writer, snapshot.NonAsciiSingles, false);
                    WriteShortQueryPostings(writer, snapshot.NonAsciiBigrams, true);
                }
            }
            catch (Exception ex)
            {
                UsnDiagLog.Write($"[SHORT QUERY SNAPSHOT SAVE] outcome=failed error={ex.GetType().Name}:{ex.Message}");
                TryDeleteFile(filePath);
            }
        }

        private static void WriteShortQueryPageSamples(BinaryWriter writer, ShortQueryPageSampleSnapshot[] samples)
        {
            samples = samples ?? Array.Empty<ShortQueryPageSampleSnapshot>();
            writer.Write(samples.Length);
            for (var i = 0; i < samples.Length; i++)
            {
                var sample = samples[i];
                writer.Write(sample?.Token ?? 0);
                WriteInt32ArrayWithLength(writer, sample?.RecordIds ?? Array.Empty<int>());
            }
        }

        private static ShortQueryPageSampleSnapshot[] ReadShortQueryPageSamples(BinaryReader reader)
        {
            var count = reader.ReadInt32();
            if (count < 0)
                throw new InvalidDataException("Invalid short query sample count.");

            var samples = new ShortQueryPageSampleSnapshot[count];
            for (var i = 0; i < count; i++)
            {
                var token = reader.ReadInt32();
                var ids = ReadInt32Array(reader);
                samples[i] = new ShortQueryPageSampleSnapshot(token, ids);
            }

            return samples;
        }

        private static void WriteShortQueryPostings(BinaryWriter writer, ShortQueryPostingSnapshot[] postings, bool bigram)
        {
            postings = postings ?? Array.Empty<ShortQueryPostingSnapshot>();
            writer.Write(postings.Length);
            for (var i = 0; i < postings.Length; i++)
            {
                var posting = postings[i];
                writer.Write(posting == null ? 0UL : posting.Key);
                WriteInt32ArrayWithLength(writer, posting?.RecordIds ?? Array.Empty<int>());
            }
        }

        private static ShortQueryPostingSnapshot[] ReadShortQueryPostings(BinaryReader reader)
        {
            var count = reader.ReadInt32();
            if (count < 0)
                throw new InvalidDataException("Invalid short query postings count.");

            var postings = new ShortQueryPostingSnapshot[count];
            for (var i = 0; i < count; i++)
            {
                var key = reader.ReadUInt64();
                var ids = ReadInt32Array(reader);
                postings[i] = new ShortQueryPostingSnapshot(key, ids);
            }

            return postings;
        }

        private static int GetOrAddStringIndex(string value, List<string> pool, Dictionary<string, int> indexes)
        {
            value = value ?? string.Empty;
            if (indexes.TryGetValue(value, out var index))
                return index;

            index = pool.Count;
            pool.Add(value);
            indexes[value] = index;
            return index;
        }

        private static void WriteVersion7StringPool(BinaryWriter writer, List<string> stringPool)
        {
            var pool = stringPool ?? new List<string>();
            var offsets = new int[pool.Count + 1];
            using (var bytes = new MemoryStream())
            {
                for (var i = 0; i < pool.Count; i++)
                {
                    offsets[i] = checked((int)bytes.Length);
                    var valueBytes = Encoding.UTF8.GetBytes(pool[i] ?? string.Empty);
                    bytes.Write(valueBytes, 0, valueBytes.Length);
                }

                offsets[pool.Count] = checked((int)bytes.Length);
                writer.Write(pool.Count);
                WriteInt32Array(writer, offsets);
                writer.Write(checked((int)bytes.Length));
                writer.Write(bytes.GetBuffer(), 0, checked((int)bytes.Length));
            }
        }

        private static string[] ReadVersion7StringPool(BinaryReader reader)
        {
            var count = reader.ReadInt32();
            if (count < 0)
                throw new InvalidDataException("Invalid v7 string pool count.");

            var offsets = ReadInt32Array(reader, count + 1);
            var byteLength = reader.ReadInt32();
            if (byteLength < 0)
                throw new InvalidDataException("Invalid v7 string pool bytes length.");

            var bytes = reader.ReadBytes(byteLength);
            if (bytes.Length != byteLength)
                throw new EndOfStreamException();

            var result = new string[count];
            Parallel.For(0, count, i =>
            {
                var start = offsets[i];
                var end = offsets[i + 1];
                result[i] = start == end
                    ? string.Empty
                    : Encoding.UTF8.GetString(bytes, start, end - start);
            });
            return result;
        }

        private static void WriteInt32Array(BinaryWriter writer, int[] values)
        {
            values = values ?? Array.Empty<int>();
            for (var i = 0; i < values.Length; i++)
                writer.Write(values[i]);
        }

        private static void WriteUInt64Array(BinaryWriter writer, ulong[] values)
        {
            values = values ?? Array.Empty<ulong>();
            for (var i = 0; i < values.Length; i++)
                writer.Write(values[i]);
        }

        private static void WriteInt32ArrayWithLength(BinaryWriter writer, int[] values)
        {
            values = values ?? Array.Empty<int>();
            writer.Write(values.Length);
            for (var i = 0; i < values.Length; i++)
                writer.Write(values[i]);
        }

        private static void WriteInt32Matrix(BinaryWriter writer, int[][] values, int expectedRows, int expectedColumns)
        {
            values = values ?? Array.Empty<int[]>();
            writer.Write(values.Length);
            for (var i = 0; i < values.Length; i++)
            {
                var row = values[i] ?? Array.Empty<int>();
                writer.Write(row.Length);
                for (var j = 0; j < row.Length; j++)
                    writer.Write(row[j]);
            }
        }

        private static int[] ReadInt32Array(BinaryReader reader)
        {
            var count = reader.ReadInt32();
            if (count < 0)
                throw new InvalidDataException("Invalid int32 array length.");

            var values = new int[count];
            for (var i = 0; i < count; i++)
                values[i] = reader.ReadInt32();
            return values;
        }

        private static int[] ReadInt32Array(BinaryReader reader, int count)
        {
            if (count < 0)
                throw new InvalidDataException("Invalid int32 array count.");

            var values = new int[count];
            for (var i = 0; i < count; i++)
                values[i] = reader.ReadInt32();
            return values;
        }

        private static int[][] ReadInt32Matrix(BinaryReader reader, int expectedRows, int expectedColumns)
        {
            var rows = reader.ReadInt32();
            if (rows < 0)
                throw new InvalidDataException("Invalid int32 matrix row count.");

            var matrix = new int[rows][];
            for (var i = 0; i < rows; i++)
            {
                var columns = reader.ReadInt32();
                if (columns < 0)
                    throw new InvalidDataException("Invalid int32 matrix column count.");

                matrix[i] = ReadInt32Array(reader, columns);
            }

            return matrix;
        }

        private static ulong[] ReadUInt64Array(BinaryReader reader, int count)
        {
            if (count < 0)
                throw new InvalidDataException("Invalid uint64 array count.");

            var values = new ulong[count];
            for (var i = 0; i < count; i++)
                values[i] = reader.ReadUInt64();
            return values;
        }

        private static void ReplaceFile(string sourcePath, string destinationPath)
        {
            if (File.Exists(destinationPath))
                File.Replace(sourcePath, destinationPath, null, true);
            else
                File.Move(sourcePath, destinationPath);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private ContainsPostingsSnapshot TryLoadPostingsSnapshot(ulong expectedFingerprint, int expectedRecordCount)
        {
            if (!File.Exists(_postingsFilePath) || expectedFingerprint == 0 || expectedRecordCount <= 0)
            {
                UsnDiagLog.Write(
                    $"[POSTINGS SNAPSHOT LOAD] outcome=miss reason=missing-or-invalid-request exists={File.Exists(_postingsFilePath)} " +
                    $"expectedFingerprint={expectedFingerprint} expectedRecords={expectedRecordCount}");
                return null;
            }

            try
            {
                using (var stream = new FileStream(_postingsFilePath, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false))
                {
                    var version = reader.ReadInt32();
                    var fingerprint = reader.ReadUInt64();
                    if (fingerprint != expectedFingerprint)
                    {
                        UsnDiagLog.Write(
                            $"[POSTINGS SNAPSHOT LOAD] outcome=miss reason=fingerprint-mismatch version={version} fileBytes={stream.Length} " +
                            $"expectedFingerprint={expectedFingerprint} actualFingerprint={fingerprint} expectedRecords={expectedRecordCount}");
                        return null;
                    }

                    ContainsPostingsSnapshot postings;
                    if (version == PostingsSnapshotVersion1)
                    {
                        postings = ReadContainsPostingsV1(reader);
                    }
                    else if (version == PostingsSnapshotVersion2)
                    {
                        postings = ReadContainsPostingsV2(reader, shortAsciiTokensIncluded: false);
                    }
                    else if (version == PostingsSnapshotVersion3)
                    {
                        postings = ReadContainsPostingsV3(reader);
                    }
                    else
                    {
                        UsnDiagLog.Write(
                            $"[POSTINGS SNAPSHOT LOAD] outcome=miss reason=version-mismatch version={version} fileBytes={stream.Length} " +
                            $"expectedFingerprint={expectedFingerprint} expectedRecords={expectedRecordCount}");
                        return null;
                    }

                    if (postings == null || postings.RecordCount != expectedRecordCount)
                    {
                        UsnDiagLog.Write(
                            $"[POSTINGS SNAPSHOT LOAD] outcome=miss reason=record-count-mismatch version={version} fileBytes={stream.Length} " +
                            $"expectedRecords={expectedRecordCount} actualRecords={(postings == null ? 0 : postings.RecordCount)}");
                        return null;
                    }

                    UsnDiagLog.Write(
                        $"[POSTINGS SNAPSHOT LOAD] outcome=success version={version} fileBytes={stream.Length} records={postings.RecordCount} buckets={postings.BucketCount} bytes={postings.TotalBytes} shortAscii={postings.ShortAsciiTokensIncluded}");
                    return postings;
                }
            }
            catch (Exception ex)
            {
                UsnDiagLog.Write($"[POSTINGS SNAPSHOT LOAD] outcome=failed error={ex.GetType().Name}:{ex.Message}");
                return null;
            }
        }

        private void SaveOrDeletePostingsSnapshot(IndexSnapshot snapshot)
        {
            var postings = snapshot?.ContainsPostings;
            var fingerprint = snapshot?.ContentFingerprint ?? 0;
            var expectedRecordCount = snapshot?.Records?.Length ?? 0;
            if (postings == null
                || fingerprint == 0
                || postings.RecordCount != expectedRecordCount)
            {
                UsnDiagLog.Write(
                    $"[POSTINGS SNAPSHOT SAVE] outcome=preserve-existing reason=no-current-postings " +
                    $"fingerprint={fingerprint} expectedRecords={expectedRecordCount} postingsRecords={(postings == null ? 0 : postings.RecordCount)}");
                return;
            }

            SavePostingsSnapshot(fingerprint, postings);
        }

        private void SavePostingsSnapshot(ulong fingerprint, ContainsPostingsSnapshot postings)
        {
            if (postings == null || fingerprint == 0 || postings.RecordCount <= 0)
                return;

            var tempPath = _postingsFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(_snapshotDirectoryPath);
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
                {
                    writer.Write(PostingsSnapshotVersion3);
                    writer.Write(fingerprint);
                    writer.Write(postings.ShortAsciiTokensIncluded);
                    WriteContainsPostingsV2(writer, postings);
                }

                if (File.Exists(_postingsFilePath))
                    File.Replace(tempPath, _postingsFilePath, null, true);
                else
                    File.Move(tempPath, _postingsFilePath);

                var fileInfo = new FileInfo(_postingsFilePath);
                UsnDiagLog.Write(
                    $"[POSTINGS SNAPSHOT SAVE] outcome=success version={PostingsSnapshotVersion3} fileBytes={(fileInfo.Exists ? fileInfo.Length : 0)} records={postings.RecordCount} buckets={postings.BucketCount} bytes={postings.TotalBytes} shortAscii={postings.ShortAsciiTokensIncluded}");
            }
            catch (Exception ex)
            {
                UsnDiagLog.Write($"[POSTINGS SNAPSHOT SAVE] outcome=failed error={ex.GetType().Name}:{ex.Message}");
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }

        private void TryDeletePostingsSnapshot()
        {
            try
            {
                if (File.Exists(_postingsFilePath))
                    File.Delete(_postingsFilePath);
            }
            catch
            {
            }
        }

        private void CleanupStaleTempSnapshots()
        {
            try
            {
                var directory = new DirectoryInfo(_snapshotDirectoryPath);
                if (!directory.Exists)
                    return;

                var cutoffUtc = DateTime.UtcNow - SnapshotTempMaxAge;
                foreach (var file in directory.EnumerateFiles("index.bin.*.tmp"))
                {
                    try
                    {
                        if (file.LastWriteTimeUtc < cutoffUtc)
                        {
                            file.Delete();
                        }
                    }
                    catch
                    {
                    }
                }

                foreach (var file in directory.EnumerateFiles("index.postings.bin.*.tmp"))
                {
                    try
                    {
                        if (file.LastWriteTimeUtc < cutoffUtc)
                        {
                            file.Delete();
                        }
                    }
                    catch
                    {
                    }
                }

                foreach (var file in directory.EnumerateFiles("index.records.bin.*.tmp"))
                {
                    try
                    {
                        if (file.LastWriteTimeUtc < cutoffUtc)
                            file.Delete();
                    }
                    catch
                    {
                    }
                }

                foreach (var file in directory.EnumerateFiles("index.dirs.bin.*.tmp"))
                {
                    try
                    {
                        if (file.LastWriteTimeUtc < cutoffUtc)
                            file.Delete();
                    }
                    catch
                    {
                    }
                }

                foreach (var file in directory.EnumerateFiles("index.parentorder.bin.*.tmp"))
                {
                    try
                    {
                        if (file.LastWriteTimeUtc < cutoffUtc)
                            file.Delete();
                    }
                    catch
                    {
                    }
                }

                foreach (var file in directory.EnumerateFiles("index.shortquery.bin.*.tmp"))
                {
                    try
                    {
                        if (file.LastWriteTimeUtc < cutoffUtc)
                            file.Delete();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static string[] BuildStringPool(FileRecord[] records, VolumeSnapshot[] volumes)
        {
            var pool = new List<string>(Math.Max(records.Length, 16));
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string value)
            {
                var normalized = value ?? string.Empty;
                if (seen.Add(normalized))
                    pool.Add(normalized);
            }

            for (var i = 0; i < records.Length; i++)
                Add(records[i].OriginalName);

            for (var i = 0; i < volumes.Length; i++)
            {
                var entries = volumes[i].FrnEntries ?? Array.Empty<FrnSnapshotEntry>();
                for (var j = 0; j < entries.Length; j++)
                    Add(entries[j].Name);
            }

            return pool.ToArray();
        }

        private static string GetStringByIndex(string[] pool, int index)
        {
            return index >= 0 && index < pool.Length ? pool[index] : null;
        }

        private static FileRecord[] AttachRecordFrns(FileRecord[] records, VolumeSnapshot[] volumes)
        {
            if (records == null || records.Length == 0 || volumes == null || volumes.Length == 0)
                return records ?? Array.Empty<FileRecord>();

            var perDriveLookups = new Dictionary<char, Dictionary<RecordIdentity, ulong>>();
            for (var i = 0; i < volumes.Length; i++)
            {
                var volume = volumes[i];
                var map = new Dictionary<RecordIdentity, ulong>();
                var entries = volume.FrnEntries ?? Array.Empty<FrnSnapshotEntry>();
                for (var j = 0; j < entries.Length; j++)
                {
                    var entry = entries[j];
                    map[new RecordIdentity(entry.Name, entry.ParentFrn, entry.IsDirectory)] = entry.Frn;
                }

                perDriveLookups[char.ToUpperInvariant(volume.DriveLetter)] = map;
            }

            var enriched = new FileRecord[records.Length];
            for (var i = 0; i < records.Length; i++)
            {
                var record = records[i];
                if (record == null)
                {
                    continue;
                }

                var driveLetter = char.ToUpperInvariant(record.DriveLetter);
                ulong frn = 0;
                if (perDriveLookups.TryGetValue(driveLetter, out var lookup))
                {
                    lookup.TryGetValue(new RecordIdentity(record.OriginalName, record.ParentFrn, record.IsDirectory), out frn);
                }

                enriched[i] = new FileRecord(
                    record.LowerName,
                    record.OriginalName,
                    record.ParentFrn,
                    record.DriveLetter,
                    record.IsDirectory,
                    frn);
            }

            return enriched;
        }

        private struct RecordIdentity : IEquatable<RecordIdentity>
        {
            public RecordIdentity(string name, ulong parentFrn, bool isDirectory)
            {
                Name = name ?? string.Empty;
                ParentFrn = parentFrn;
                IsDirectory = isDirectory;
            }

            public string Name { get; }
            public ulong ParentFrn { get; }
            public bool IsDirectory { get; }

            public bool Equals(RecordIdentity other)
            {
                return ParentFrn == other.ParentFrn
                       && IsDirectory == other.IsDirectory
                       && string.Equals(Name, other.Name, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is RecordIdentity other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = StringComparer.Ordinal.GetHashCode(Name ?? string.Empty);
                    hash = (hash * 397) ^ ParentFrn.GetHashCode();
                    hash = (hash * 397) ^ IsDirectory.GetHashCode();
                    return hash;
                }
            }
        }
    }

    internal sealed class IndexSnapshot
    {
        public IndexSnapshot(
            FileRecord[] records,
            VolumeSnapshot[] volumes,
            int stringPoolCount = 0,
            ContainsPostingsSnapshot containsPostings = null,
            ulong contentFingerprint = 0,
            ShortQuerySnapshot shortQuerySnapshot = null,
            int[] parentOrder = null)
        {
            Records = records ?? Array.Empty<FileRecord>();
            Volumes = volumes ?? Array.Empty<VolumeSnapshot>();
            StringPoolCount = stringPoolCount;
            ContainsPostings = containsPostings;
            ContentFingerprint = contentFingerprint == 0
                ? IndexSnapshotFingerprint.Compute(Records)
                : contentFingerprint;
            ShortQuery = shortQuerySnapshot;
            ParentOrder = parentOrder;
        }

        public FileRecord[] Records { get; }

        public VolumeSnapshot[] Volumes { get; }

        public int StringPoolCount { get; }
        public ContainsPostingsSnapshot ContainsPostings { get; }
        public ulong ContentFingerprint { get; }
        public ShortQuerySnapshot ShortQuery { get; }
        public int[] ParentOrder { get; }
    }

    internal sealed class ShortQuerySnapshot
    {
        public ShortQuerySnapshot(
            int recordCount,
            int[] singleCharAsciiCounts,
            int[][] singleCharAsciiDriveCounts,
            int[] bigramAsciiCounts,
            int[][] bigramAsciiDriveCounts,
            ShortQueryPageSampleSnapshot[] bigramAsciiPageSamples,
            ShortQueryPostingSnapshot[] nonAsciiSingles,
            ShortQueryPostingSnapshot[] nonAsciiBigrams)
        {
            RecordCount = recordCount;
            SingleCharAsciiCounts = singleCharAsciiCounts ?? Array.Empty<int>();
            SingleCharAsciiDriveCounts = singleCharAsciiDriveCounts ?? Array.Empty<int[]>();
            BigramAsciiCounts = bigramAsciiCounts ?? Array.Empty<int>();
            BigramAsciiDriveCounts = bigramAsciiDriveCounts ?? Array.Empty<int[]>();
            BigramAsciiPageSamples = bigramAsciiPageSamples ?? Array.Empty<ShortQueryPageSampleSnapshot>();
            NonAsciiSingles = nonAsciiSingles ?? Array.Empty<ShortQueryPostingSnapshot>();
            NonAsciiBigrams = nonAsciiBigrams ?? Array.Empty<ShortQueryPostingSnapshot>();
        }

        public int RecordCount { get; }
        public int[] SingleCharAsciiCounts { get; }
        public int[][] SingleCharAsciiDriveCounts { get; }
        public int[] BigramAsciiCounts { get; }
        public int[][] BigramAsciiDriveCounts { get; }
        public ShortQueryPageSampleSnapshot[] BigramAsciiPageSamples { get; }
        public ShortQueryPostingSnapshot[] NonAsciiSingles { get; }
        public ShortQueryPostingSnapshot[] NonAsciiBigrams { get; }
        public int NonAsciiBucketCount => (NonAsciiSingles?.Length ?? 0) + (NonAsciiBigrams?.Length ?? 0);
        public int NonAsciiPostingCount => (NonAsciiSingles?.Sum(x => x?.RecordIds?.Length ?? 0) ?? 0)
                                           + (NonAsciiBigrams?.Sum(x => x?.RecordIds?.Length ?? 0) ?? 0);
    }

    internal sealed class ShortQueryPageSampleSnapshot
    {
        public ShortQueryPageSampleSnapshot(int token, int[] recordIds)
        {
            Token = token;
            RecordIds = recordIds ?? Array.Empty<int>();
        }

        public int Token { get; }
        public int[] RecordIds { get; }
    }

    internal sealed class ShortQueryPostingSnapshot
    {
        public ShortQueryPostingSnapshot(ulong key, int[] recordIds)
        {
            Key = key;
            RecordIds = recordIds ?? Array.Empty<int>();
        }

        public ulong Key { get; }
        public int[] RecordIds { get; }
    }

    internal enum ContainsPostingsBucketKind
    {
        Char = 1,
        Bigram = 2,
        Trigram = 3
    }

    internal sealed class ContainsPostingsBucketSnapshot
    {
        public ContainsPostingsBucketSnapshot(
            ContainsPostingsBucketKind kind,
            ulong[] keys,
            int[] offsets,
            int[] counts,
            int[] byteCounts,
            byte[] bytes)
        {
            Kind = kind;
            Keys = keys ?? Array.Empty<ulong>();
            Offsets = offsets ?? Array.Empty<int>();
            Counts = counts ?? Array.Empty<int>();
            ByteCounts = byteCounts ?? Array.Empty<int>();
            Bytes = bytes ?? Array.Empty<byte>();
        }

        public ContainsPostingsBucketKind Kind { get; }
        public ulong[] Keys { get; }
        public int[] Offsets { get; }
        public int[] Counts { get; }
        public int[] ByteCounts { get; }
        public byte[] Bytes { get; }
    }

    internal sealed class ContainsPostingsSnapshot
    {
        public ContainsPostingsSnapshot(
            int recordCount,
            ulong[] keys,
            int[] offsets,
            int[] counts,
            int[] byteCounts,
            byte[] bytes)
            : this(
                recordCount,
                ContainsPostingsBucketKind.Trigram,
                keys,
                offsets,
                counts,
                byteCounts,
                bytes,
                shortAsciiTokensIncluded: false)
        {
        }

        public ContainsPostingsSnapshot(
            int recordCount,
            ContainsPostingsBucketKind kind,
            ulong[] keys,
            int[] offsets,
            int[] counts,
            int[] byteCounts,
            byte[] bytes,
            bool shortAsciiTokensIncluded = false)
            : this(recordCount, new[]
            {
                new ContainsPostingsBucketSnapshot(kind, keys, offsets, counts, byteCounts, bytes)
            }, shortAsciiTokensIncluded)
        {
        }

        public ContainsPostingsSnapshot(int recordCount, ContainsPostingsBucketSnapshot[] buckets, bool shortAsciiTokensIncluded = false)
        {
            RecordCount = recordCount;
            Buckets = buckets ?? Array.Empty<ContainsPostingsBucketSnapshot>();
            ShortAsciiTokensIncluded = shortAsciiTokensIncluded;
            var trigram = GetBucket(ContainsPostingsBucketKind.Trigram) ?? Buckets.FirstOrDefault();
            Keys = trigram?.Keys ?? Array.Empty<ulong>();
            Offsets = trigram?.Offsets ?? Array.Empty<int>();
            Counts = trigram?.Counts ?? Array.Empty<int>();
            ByteCounts = trigram?.ByteCounts ?? Array.Empty<int>();
            Bytes = trigram?.Bytes ?? Array.Empty<byte>();
        }

        public int RecordCount { get; }
        public ContainsPostingsBucketSnapshot[] Buckets { get; }
        public bool ShortAsciiTokensIncluded { get; }
        public ulong[] Keys { get; }
        public int[] Offsets { get; }
        public int[] Counts { get; }
        public int[] ByteCounts { get; }
        public byte[] Bytes { get; }
        public int BucketCount => Buckets?.Sum(bucket => bucket?.Keys?.Length ?? 0) ?? 0;
        public int TotalBytes => Buckets?.Sum(bucket => bucket?.Bytes?.Length ?? 0) ?? 0;

        public ContainsPostingsBucketSnapshot GetBucket(ContainsPostingsBucketKind kind)
        {
            if (Buckets == null)
                return null;

            for (var i = 0; i < Buckets.Length; i++)
            {
                if (Buckets[i] != null && Buckets[i].Kind == kind)
                    return Buckets[i];
            }

            return null;
        }
    }

    internal static class IndexSnapshotFingerprint
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        public static ulong Compute(IReadOnlyList<FileRecord> records)
        {
            unchecked
            {
                var hash = OffsetBasis;
                var count = records?.Count ?? 0;
                hash = Add(hash, count);
                for (var i = 0; i < count; i++)
                {
                    var record = records[i];
                    if (record == null)
                    {
                        hash = Add(hash, 0);
                        continue;
                    }

                    hash = Add(hash, record.Frn);
                    hash = Add(hash, record.ParentFrn);
                    hash = Add(hash, char.ToUpperInvariant(record.DriveLetter));
                    hash = Add(hash, record.IsDirectory ? 1 : 0);
                    hash = Add(hash, record.OriginalName);
                }

                return hash == 0 ? OffsetBasis : hash;
            }
        }

        private static ulong Add(ulong hash, string value)
        {
            if (string.IsNullOrEmpty(value))
                return Add(hash, 0);

            unchecked
            {
                for (var i = 0; i < value.Length; i++)
                    hash = Add(hash, value[i]);

                return Add(hash, 0);
            }
        }

        private static ulong Add(ulong hash, int value)
        {
            return Add(hash, unchecked((ulong)value));
        }

        private static ulong Add(ulong hash, ulong value)
        {
            unchecked
            {
                for (var i = 0; i < 8; i++)
                {
                    hash ^= (byte)(value >> (i * 8));
                    hash *= Prime;
                }

                return hash;
            }
        }
    }

    public sealed class VolumeSnapshot
    {
        public VolumeSnapshot(char driveLetter, long nextUsn, ulong journalId, FrnSnapshotEntry[] frnEntries)
        {
            DriveLetter = driveLetter;
            NextUsn = nextUsn;
            JournalId = journalId;
            FrnEntries = frnEntries ?? Array.Empty<FrnSnapshotEntry>();
        }

        public char DriveLetter { get; }

        public long NextUsn { get; }

        public ulong JournalId { get; }

        public FrnSnapshotEntry[] FrnEntries { get; }
    }

    public sealed class FrnSnapshotEntry
    {
        public FrnSnapshotEntry(ulong frn, string name, ulong parentFrn, bool isDirectory)
        {
            Frn = frn;
            Name = name;
            ParentFrn = parentFrn;
            IsDirectory = isDirectory;
        }

        public ulong Frn { get; }

        public string Name { get; }

        public ulong ParentFrn { get; }

        public bool IsDirectory { get; }
    }

    internal sealed class IndexSnapshotLoadMetrics
    {
        public IndexSnapshotLoadMetrics(int version, long fileBytes, int recordCount, int volumeCount, int frnEntryCount, int stringPoolCount)
        {
            Version = version;
            FileBytes = fileBytes;
            RecordCount = recordCount;
            VolumeCount = volumeCount;
            FrnEntryCount = frnEntryCount;
            StringPoolCount = stringPoolCount;
        }

        public int Version { get; }

        public long FileBytes { get; }

        public int RecordCount { get; }

        public int VolumeCount { get; }

        public int FrnEntryCount { get; }

        public int StringPoolCount { get; }
    }

    internal sealed class IndexSnapshotSaveMetrics
    {
        public IndexSnapshotSaveMetrics(int version, long fileBytes, int recordCount, int volumeCount, int frnEntryCount)
        {
            Version = version;
            FileBytes = fileBytes;
            RecordCount = recordCount;
            VolumeCount = volumeCount;
            FrnEntryCount = frnEntryCount;
        }

        public int Version { get; }

        public long FileBytes { get; }

        public int RecordCount { get; }

        public int VolumeCount { get; }

        public int FrnEntryCount { get; }
    }
}
