using System.Net;

namespace PackageManager.Services
{
    public class CredentialStore
    {
        private readonly DataPersistenceService _persistence;

        public CredentialStore(DataPersistenceService persistence)
        {
            _persistence = persistence;
        }

        public NetworkCredential GetFtpReadCredential()
        {
            var settings = _persistence?.LoadSettings();
            var user = settings?.FtpReadUser;
            var pass = settings?.FtpReadPassword;
            if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
            {
                return new NetworkCredential(user, CredentialProtectionService.Unprotect(pass));
            }

            return new NetworkCredential("hwclient", "hw_ftpa206");
        }

        public NetworkCredential GetFtpDownloadCredential()
        {
            var settings = _persistence?.LoadSettings();
            var user = settings?.FtpDownloadUser;
            var pass = settings?.FtpDownloadPassword;
            if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
            {
                return new NetworkCredential(user, CredentialProtectionService.Unprotect(pass));
            }

            return new NetworkCredential("hongwauser", "hw_ftpa206");
        }

        public NetworkCredential GetFtpWriteCredential()
        {
            var settings = _persistence?.LoadSettings();
            var user = settings?.FtpWriteUser;
            var pass = settings?.FtpWritePassword;
            if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
            {
                return new NetworkCredential(user, CredentialProtectionService.Unprotect(pass));
            }

            return new NetworkCredential("hwuser", "hongwa666.");
        }
    }
}
