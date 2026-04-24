namespace MftScanner
{
    public static class SharedIndexConstants
    {
        public const string IndexHostMutexName = "PackageManager.MftScanner.IndexHost.Singleton";
        public const string IndexHostTaskName = "PackageManager\\MftScannerIndexHost";
        public const string IndexHostCommandPipeName = "PackageManager.MftScanner.IndexHost.Command";
        public const string IndexHostEventPipeName = "PackageManager.MftScanner.IndexHost.Events";
        public const string IndexHostSessionId = "PackageManager.MftScanner.IndexHost";
        public const string SearchUiSessionId = "PackageManager.MftScanner.SearchUi";
    }
}
