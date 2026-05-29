namespace PackageManager.Features.CodeWorkspace.Models
{
    public class RepositoryLinkOption
    {
        public string Key { get; set; }

        public string Name { get; set; }

        public string Path { get; set; }

        public CodeRepository Repository { get; set; }

        public string DisplayName => string.IsNullOrWhiteSpace(Path)
            ? Name
            : $"{Name}  ({Path})";

        public override string ToString()
        {
            return DisplayName;
        }
    }
}
