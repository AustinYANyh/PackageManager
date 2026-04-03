using System;
using System.Security.Cryptography;
using System.Text;

namespace PackageManager.Services
{
    internal static class CredentialProtectionService
    {
        /// <summary>
        /// 使用 DPAPI 加密保护明文字符串。
        /// </summary>
        /// <param name="plainText">要加密的明文字符串。</param>
        /// <returns>Base64 编码的加密字符串；输入为空时返回 null。</returns>
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

        /// <summary>
        /// 解密由 <see cref="Protect"/> 方法加密的字符串。
        /// </summary>
        /// <param name="protectedText">Base64 编码的加密字符串。</param>
        /// <returns>解密后的明文字符串；解密失败时返回空字符串。</returns>
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
