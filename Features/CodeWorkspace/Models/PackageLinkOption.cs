using PackageManager.Models;

namespace PackageManager.Features.CodeWorkspace.Models
{
    public class PackageLinkOption
    {
        public string Key { get; set; }

        public string ProductName { get; set; }

        public string FtpServerPath { get; set; }

        public PackageInfo Package { get; set; }

        public string DisplayName => string.IsNullOrWhiteSpace(FtpServerPath)
            ? ProductName
            : $"{ProductName}  ({FtpServerPath})";

        public override string ToString()
        {
            return DisplayName;
        }
    }
}
