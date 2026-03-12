using System;
using System.Security.Cryptography;
using System.Text;

namespace PackageManager.Services
{
    internal static class CredentialProtectionService
    {
        public static string Protect(string plainText)
        {
            if (string.IsNullOrWhiteSpace(plainText))
            {
                return null;
            }

            var input = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(input, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        public static string Unprotect(string protectedText)
        {
            if (string.IsNullOrWhiteSpace(protectedText))
            {
                return string.Empty;
            }

            try
            {
                var protectedBytes = Convert.FromBase64String(protectedText);
                var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
