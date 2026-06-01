using PackageManager.Features.CodeWorkspace.Models;
using PackageManager.Models;

namespace PackageManager.Features.CodeWorkspace.Services
{
    public enum CodeWorkspaceNavigationRequestKind
    {
        SelectLinkedRepository,
        BindPackageToRepository,
    }

    public class CodeWorkspaceNavigationRequest
    {
        public CodeWorkspaceNavigationRequestKind Kind { get; set; }

        public string PackageKey { get; set; }

        public string PackageName { get; set; }

        public PackageInfo Package { get; set; }

        public CodeRepository Repository { get; set; }
    }

    public class CodeWorkspaceNavigationRequestService
    {
        private CodeWorkspaceNavigationRequest _pendingRequest;

        public void RequestSelectRepositoryForPackage(PackageInfo package)
        {
            _pendingRequest = BuildRequest(CodeWorkspaceNavigationRequestKind.SelectLinkedRepository, package);
        }

        public void RequestBindRepositoryForPackage(PackageInfo package)
        {
            _pendingRequest = BuildRequest(CodeWorkspaceNavigationRequestKind.BindPackageToRepository, package);
        }

        public CodeWorkspaceNavigationRequest Consume()
        {
            var request = _pendingRequest;
            _pendingRequest = null;
            return request;
        }

        private static CodeWorkspaceNavigationRequest BuildRequest(CodeWorkspaceNavigationRequestKind kind, PackageInfo package)
        {
            return new CodeWorkspaceNavigationRequest
            {
                Kind = kind,
                Package = package,
                PackageKey = CodePackageLinkService.BuildPackageKey(package),
                PackageName = package?.ProductName,
            };
        }
    }
}
