using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MftScanner
{
    internal sealed class IndexSnapshotStore
    {
        private const int SnapshotVersion1 = 1;
        private const int SnapshotVersion2 = 2;

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
                using (var stream = new FileStream(_snapshotFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
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

            Directory.CreateDirectory(_snapshotDirectoryPath);
            var tempPath = _snapshotFilePath + ".tmp";

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                WriteVersion2(writer, snapshot);
            }

            if (File.Exists(_snapshotFilePath))
                File.Delete(_snapshotFilePath);

            File.Move(tempPath, _snapshotFilePath);
            var fileInfo = new FileInfo(_snapshotFilePath);
            return new IndexSnapshotSaveMetrics(
                SnapshotVersion2,
                fileInfo.Exists ? fileInfo.Length : 0,
                snapshot.Records?.Length ?? 0,
                snapshot.Volumes?.Length ?? 0,
                snapshot.Volumes?.Sum(v => v.FrnEntries?.Length ?? 0) ?? 0);
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
            return volumes == null ? null : new IndexSnapshot(records, volumes);
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
                for (var j = 0; j < frnCount; j++)
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

            return new IndexSnapshot(records, volumes, stringPool.Length);
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

        private static void WriteVersion2(BinaryWriter writer, IndexSnapshot snapshot)
        {
            writer.Write(SnapshotVersion2);

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
    }

    internal sealed class IndexSnapshot
    {
        public IndexSnapshot(FileRecord[] records, VolumeSnapshot[] volumes, int stringPoolCount = 0)
        {
            Records = records ?? Array.Empty<FileRecord>();
            Volumes = volumes ?? Array.Empty<VolumeSnapshot>();
            StringPoolCount = stringPoolCount;
        }

        public FileRecord[] Records { get; }

        public VolumeSnapshot[] Volumes { get; }

        public int StringPoolCount { get; }
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
