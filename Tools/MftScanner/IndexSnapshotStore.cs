using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MftScanner
{
    internal sealed class IndexSnapshotStore
    {
        private const int SnapshotVersion = 1;

        private readonly string _snapshotDirectoryPath;
        private readonly string _snapshotFilePath;

        public IndexSnapshotStore()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _snapshotDirectoryPath = Path.Combine(appDataPath, "PackageManager", "MftScannerIndex");
            _snapshotFilePath = Path.Combine(_snapshotDirectoryPath, "index.bin");
        }

        public bool TryLoad(out IndexSnapshot snapshot)
        {
            snapshot = null;
            if (!File.Exists(_snapshotFilePath))
                return false;

            try
            {
                using (var stream = new FileStream(_snapshotFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false))
                {
                    if (reader.ReadInt32() != SnapshotVersion)
                        return false;

                    var recordCount = reader.ReadInt32();
                    if (recordCount < 0)
                        return false;

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

                    var volumeCount = reader.ReadInt32();
                    if (volumeCount < 0)
                        return false;

                    var volumes = new VolumeSnapshot[volumeCount];
                    for (var i = 0; i < volumeCount; i++)
                    {
                        var driveLetter = reader.ReadChar();
                        var nextUsn = reader.ReadInt64();
                        var journalId = reader.ReadUInt64();
                        var frnCount = reader.ReadInt32();
                        if (frnCount < 0)
                            return false;

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

                    snapshot = new IndexSnapshot(records, volumes);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public void Save(IndexSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            Directory.CreateDirectory(_snapshotDirectoryPath);
            var tempPath = _snapshotFilePath + ".tmp";

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(SnapshotVersion);

                var records = snapshot.Records ?? Array.Empty<FileRecord>();
                writer.Write(records.Length);
                for (var i = 0; i < records.Length; i++)
                {
                    var record = records[i];
                    writer.Write(record.LowerName ?? string.Empty);
                    writer.Write(record.OriginalName ?? string.Empty);
                    writer.Write(record.ParentFrn);
                    writer.Write(record.DriveLetter);
                    writer.Write(record.IsDirectory);
                }

                var volumes = snapshot.Volumes ?? Array.Empty<VolumeSnapshot>();
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
                        writer.Write(entry.Frn);
                        writer.Write(entry.Name ?? string.Empty);
                        writer.Write(entry.ParentFrn);
                        writer.Write(entry.IsDirectory);
                    }
                }
            }

            if (File.Exists(_snapshotFilePath))
                File.Delete(_snapshotFilePath);

            File.Move(tempPath, _snapshotFilePath);
        }
    }

    internal sealed class IndexSnapshot
    {
        public IndexSnapshot(FileRecord[] records, VolumeSnapshot[] volumes)
        {
            Records = records ?? Array.Empty<FileRecord>();
            Volumes = volumes ?? Array.Empty<VolumeSnapshot>();
        }

        public FileRecord[] Records { get; }

        public VolumeSnapshot[] Volumes { get; }
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
}
