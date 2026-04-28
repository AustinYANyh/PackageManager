using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace MftScanner
{
    internal sealed class IndexSnapshotStore
    {
        private const int SnapshotVersion1 = 1;
        private const int SnapshotVersion2 = 2;
        private const int SnapshotVersion3 = 3;
        private const int SnapshotVersion4 = 4;
        private const string SnapshotWriteMutexName = "PackageManager.MftScannerIndex.WriteLock";
        private const int SnapshotWriteLockTimeoutMilliseconds = 200;

        private readonly string _snapshotDirectoryPath;
        private readonly string _snapshotFilePath;

        public IndexSnapshotStore()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _snapshotDirectoryPath = Path.Combine(appDataPath, "PackageManager", "MftScannerIndex");
            _snapshotFilePath = Path.Combine(_snapshotDirectoryPath, "index.bin");
        }

        public bool TryLoad(out IndexSnapshot snapshot, out IndexSnapshotLoadMetrics metrics)
        {
            snapshot = null;
            metrics = null;
            if (!File.Exists(_snapshotFilePath))
                return false;

            try
            {
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

                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
                {
                    WriteVersion4(writer, snapshot);
                }

                if (File.Exists(_snapshotFilePath))
                {
                    File.Replace(tempPath, _snapshotFilePath, null, true);
                }
                else
                {
                    File.Move(tempPath, _snapshotFilePath);
                }

                var fileInfo = new FileInfo(_snapshotFilePath);
                return new IndexSnapshotSaveMetrics(
                    SnapshotVersion4,
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
            return ReadVersion3Body(reader, readContainsPostings: false);
        }

        private static IndexSnapshot ReadVersion3Body(BinaryReader reader, bool readContainsPostings)
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

            return new IndexSnapshot(records, volumes, stringPool.Length, containsPostings);
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
                    ? new ContainsPostingsSnapshot(recordCount, keys, offsets, counts, byteCounts, bytes)
                    : null;
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
            ContainsPostingsSnapshot containsPostings = null)
        {
            Records = records ?? Array.Empty<FileRecord>();
            Volumes = volumes ?? Array.Empty<VolumeSnapshot>();
            StringPoolCount = stringPoolCount;
            ContainsPostings = containsPostings;
        }

        public FileRecord[] Records { get; }

        public VolumeSnapshot[] Volumes { get; }

        public int StringPoolCount { get; }
        public ContainsPostingsSnapshot ContainsPostings { get; }
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
        {
            RecordCount = recordCount;
            Keys = keys ?? Array.Empty<ulong>();
            Offsets = offsets ?? Array.Empty<int>();
            Counts = counts ?? Array.Empty<int>();
            ByteCounts = byteCounts ?? Array.Empty<int>();
            Bytes = bytes ?? Array.Empty<byte>();
        }

        public int RecordCount { get; }
        public ulong[] Keys { get; }
        public int[] Offsets { get; }
        public int[] Counts { get; }
        public int[] ByteCounts { get; }
        public byte[] Bytes { get; }
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
