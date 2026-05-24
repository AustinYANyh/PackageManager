using System;

namespace PackageManager.Services
{
    internal static class UserFeatureAccessService
    {
        private const string AustinUserName = "AustinYanyh";
        private const string AustinUserName2 = "AustinYan";

        /// <summary>
        /// 获取当前用户是否可以使用受限功能。
        /// </summary>
        public static bool CanUseAustinOnlyFeatures =>
            Environment.UserName.Equals(AustinUserName, StringComparison.OrdinalIgnoreCase) ||
            Environment.UserName.Equals(AustinUserName2, StringComparison.OrdinalIgnoreCase);
    }
}
