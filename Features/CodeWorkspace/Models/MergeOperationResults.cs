using System.Collections.Generic;

namespace PackageManager.Features.CodeWorkspace.Models
{
    public class MergePrecheckResult
    {
        public bool Success { get; set; }

        public string Message { get; set; }

        public string CurrentBranch { get; set; }

        public string RemoteStatus { get; set; }
    }

    public class MergeExecutionResult
    {
        public bool Success { get; set; }

        public bool HasConflict { get; set; }

        public string Message { get; set; }

        public List<MergeConflictFile> ConflictFiles { get; } = new List<MergeConflictFile>();
    }
}
