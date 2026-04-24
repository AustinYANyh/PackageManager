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

        public static string BuildSearchUiShowRequestEventName(string sessionId)
        {
            return "PackageManager.MftScanner.Show." + NormalizeSessionId(sessionId);
        }

        public static string BuildSearchUiSingleInstanceMutexName(string sessionId)
        {
            return "PackageManager.MftScanner.Singleton." + NormalizeSessionId(sessionId);
        }

        public static string BuildSearchUiReadyEventName(string sessionId)
        {
            return "PackageManager.MftScanner.Ready." + NormalizeSessionId(sessionId);
        }

        public static string BuildSearchUiShownEventName(string sessionId)
        {
            return "PackageManager.MftScanner.Shown." + NormalizeSessionId(sessionId);
        }

        public static string BuildSearchUiStateMapName(string sessionId)
        {
            return "PackageManager.MftScanner.UiState." + NormalizeSessionId(sessionId);
        }

        private static string NormalizeSessionId(string sessionId)
        {
            return string.IsNullOrWhiteSpace(sessionId) ? "default" : sessionId.Trim();
        }
    }
}
