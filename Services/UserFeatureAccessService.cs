using System;

namespace PackageManager.Services
{
    internal static class UserFeatureAccessService
    {
        private const string AustinUserName = "AustinYanyh";

        public static bool CanUseAustinOnlyFeatures =>
            Environment.UserName.Equals(AustinUserName, StringComparison.OrdinalIgnoreCase);
    }
}
