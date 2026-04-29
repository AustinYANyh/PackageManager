using System.Collections.Generic;

namespace MftScanner
{
    public sealed class SharedIndexRequest
    {
        public string command { get; set; }
        public string consumer { get; set; }
        public string keyword { get; set; }
        public int maxResults { get; set; }
        public int offset { get; set; }
        public string filter { get; set; }
    }

    public sealed class SharedIndexResponse
    {
        public bool success { get; set; }
        public string error { get; set; }
        public int indexedCount { get; set; }
        public string currentStatusMessage { get; set; }
        public bool isBackgroundCatchUpInProgress { get; set; }
        public bool requireSearchRefresh { get; set; }
        public ContainsBucketStatus containsBucketStatus { get; set; }
        public int totalIndexedCount { get; set; }
        public int totalMatchedCount { get; set; }
        public int physicalMatchedCount { get; set; }
        public int uniqueMatchedCount { get; set; }
        public int duplicatePathCount { get; set; }
        public bool isTruncated { get; set; }
        public List<ScannedFileInfo> results { get; set; }
    }

    public sealed class SharedIndexEventMessage
    {
        public string type { get; set; }
        public int indexedCount { get; set; }
        public string currentStatusMessage { get; set; }
        public bool isBackgroundCatchUpInProgress { get; set; }
        public bool requireSearchRefresh { get; set; }
        public ContainsBucketStatus containsBucketStatus { get; set; }
        public string changeType { get; set; }
        public string lowerName { get; set; }
        public string fullPath { get; set; }
        public string oldFullPath { get; set; }
        public string newOriginalName { get; set; }
        public string newLowerName { get; set; }
        public bool isDirectory { get; set; }
    }
}
