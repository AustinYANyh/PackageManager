namespace MftScanner
{
    public enum SearchTypeFilter
    {
        All,
        Launchable,
        Folder,
        Script,
        Log,
        Config
    }

    internal static class SearchTypeFilterHelper
    {
        public static bool IsLaunchableExtension(string extension)
        {
            return extension == ".exe" || extension == ".bat" || extension == ".cmd" || extension == ".ps1" || extension == ".lnk";
        }

        public static bool IsScriptExtension(string extension)
        {
            return extension == ".bat" || extension == ".cmd" || extension == ".ps1";
        }

        public static bool IsLogExtension(string extension)
        {
            return extension == ".log" || extension == ".txt";
        }

        public static bool IsConfigExtension(string extension)
        {
            return extension == ".json" || extension == ".xml" || extension == ".ini" || extension == ".config" || extension == ".yaml" || extension == ".yml";
        }
    }
}
