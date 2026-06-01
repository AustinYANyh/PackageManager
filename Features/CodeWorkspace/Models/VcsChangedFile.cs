using System;
using System.Windows.Media;
using Newtonsoft.Json;

namespace PackageManager.Features.CodeWorkspace.Models
{
    public class VcsChangedFile
    {
        private static readonly Brush AddedBrush = CreateBrush(0x2E, 0xA0, 0x43);
        private static readonly Brush ModifiedBrush = CreateBrush(0xD9, 0x7A, 0x00);
        private static readonly Brush DeletedBrush = CreateBrush(0xD1, 0x24, 0x2F);
        private static readonly Brush ConflictBrush = CreateBrush(0xB4, 0x23, 0x18);
        private static readonly Brush UnknownBrush = CreateBrush(0x8A, 0x94, 0xA3);

        public VcsType VcsType { get; set; }

        public char StatusCode { get; set; }

        public string RelativePath { get; set; }

        public string AbsolutePath { get; set; }

        public string GroupName { get; set; }

        public string WorkingDirectory { get; set; }

        public string OriginalRelativePath { get; set; }

        [JsonIgnore]
        public string DisplayStatus => StatusCode == '\0' ? "?" : StatusCode.ToString();

        [JsonIgnore]
        public string DisplayPath => string.IsNullOrWhiteSpace(RelativePath) ? AbsolutePath : RelativePath;

        [JsonIgnore]
        public string ToolTip => $"{GroupName}{Environment.NewLine}{DisplayStatus}  {DisplayPath}";

        [JsonIgnore]
        public bool IsAdded => StatusCode == 'A' || StatusCode == '?';

        [JsonIgnore]
        public bool IsDeleted => StatusCode == 'D' || StatusCode == '!';

        [JsonIgnore]
        public bool IsConflict => StatusCode == 'C' || StatusCode == 'U';

        [JsonIgnore]
        public Brush StatusBrush
        {
            get
            {
                if (IsConflict)
                {
                    return ConflictBrush;
                }

                if (IsAdded)
                {
                    return AddedBrush;
                }

                if (IsDeleted)
                {
                    return DeletedBrush;
                }

                if (StatusCode == 'M' || StatusCode == '~' || StatusCode == 'R')
                {
                    return ModifiedBrush;
                }

                return UnknownBrush;
            }
        }

        public VcsChangedFile Clone()
        {
            return new VcsChangedFile
            {
                VcsType = VcsType,
                StatusCode = StatusCode,
                RelativePath = RelativePath,
                AbsolutePath = AbsolutePath,
                GroupName = GroupName,
                WorkingDirectory = WorkingDirectory,
                OriginalRelativePath = OriginalRelativePath,
            };
        }

        private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
