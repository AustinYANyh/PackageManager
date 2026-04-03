using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace PackageManager.Services
{
    /// <summary>
    /// 文件服务类，用于读取FTP或HTTP服务器上的文件夹信息
    /// </summary>
    public class FtpService
    {
        /// <summary>
        /// 获取或设置数据持久化服务实例，用于加载设置（如是否过滤日志目录）。
        /// </summary>
        public static DataPersistenceService DataService { get; set; }
        /// <summary>
        /// 异步获取服务器路径下的所有文件夹名称
        /// </summary>
        /// <param name="serverUrl">服务器地址（支持FTP和HTTP协议）</param>
        /// <param name="username">用户名</param>
        /// <param name="password">密码</param>
        /// <returns>文件夹名称列表</returns>
        public async Task<List<string>> GetDirectoriesAsync(string serverUrl, string username = "hwclient", string password = "hw_ftpa206")
        {
            return await Task.Run(() => GetDirectories(serverUrl, username, password));
        }

        /// <summary>
        /// 异步获取服务器路径下的所有文件名称并解析时间信息
        /// </summary>
        /// <param name="serverUrl">服务器地址（支持FTP和HTTP协议）</param>
        /// <param name="username">用户名</param>
        /// <param name="password">密码</param>
        /// <returns>文件名称列表（按时间排序）</returns>
        public async Task<List<string>> GetFilesAsync(string serverUrl, string username = "hwclient", string password = "hw_ftpa206")
        {
            return await Task.Run(() => GetFiles(serverUrl, username, password));
        }

        /// <summary>
        /// 获取服务器路径下的所有文件名称并解析时间信息
        /// </summary>
        /// <param name="serverUrl">服务器地址（支持FTP和HTTP协议）</param>
        /// <param name="username">用户名</param>
        /// <param name="password">密码</param>
        /// <returns>文件名称列表（按时间排序）</returns>
        public List<string> GetFiles(string serverUrl, string username = null, string password = null)
        {
            var files = new List<string>();

            try
            {
                // 确保URL以/结尾
                if (!serverUrl.EndsWith("/"))
                    serverUrl += "/";

                var uri = new Uri(serverUrl);
                
                if (uri.Scheme.ToLower() == "ftp")
                {
                    files = GetFtpFiles(serverUrl, username, password);
                }
                else if (uri.Scheme.ToLower() == "http" || uri.Scheme.ToLower() == "https")
                {
                    files = GetHttpFiles(serverUrl, username, password);
                }
                else
                {
                    throw new NotSupportedException($"不支持的协议: {uri.Scheme}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"获取文件失败: {ex.Message}", ex);
            }

            // 按文件名中的时间信息排序
            return SortFilesByTime(files);
        }

        /// <summary>
        /// 获取服务器路径下的所有文件夹名称
        /// </summary>
        /// <param name="serverUrl">服务器地址（支持FTP和HTTP协议）</param>
        /// <param name="username">用户名</param>
        /// <param name="password">密码</param>
        /// <returns>文件夹名称列表</returns>
        public List<string> GetDirectories(string serverUrl, string username = null, string password = null)
        {
            var directories = new List<string>();

            try
            {
                // 确保URL以/结尾
                if (!serverUrl.EndsWith("/"))
                    serverUrl += "/";

                var uri = new Uri(serverUrl);
                
                if (uri.Scheme.ToLower() == "ftp")
                {
                    directories = GetFtpDirectories(serverUrl, username, password);
                }
                else if (uri.Scheme.ToLower() == "http" || uri.Scheme.ToLower() == "https")
                {
                    directories = GetHttpDirectories(serverUrl, username, password);
                }
                else
                {
                    throw new NotSupportedException($"不支持的协议: {uri.Scheme}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"获取目录失败: {ex.Message}", ex);
            }
            
            // 按照版本号排序，支持：v11.3.2.0、v11.3.2、v11.3.2.0_log、2025.09.30_v1.5.2（日期前缀不参与排序）
            directories.RemoveAll(x => x.Contains("父目录"));
            var filterLog = DataService?.LoadSettings()?.FilterLogDirectories ?? true;
            if (filterLog)
            {
                directories.RemoveAll(x => x.IndexOf("log", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            return SortNamesByVersion(directories);
        }

        /// <summary>
        /// 按版本号语义排序名称，支持任意位置的 vX.Y[.Z[.W]]，例如：
        /// 2025.09.30_v1.5.2、v11.3.2.0_log、v11.3.2。
        /// </summary>
        /// <param name="names">待排序的名称集合。</param>
        /// <returns>按版本号升序排列的名称列表；若集合为 null 则返回空列表。</returns>
        public static List<string> SortNamesByVersion(IEnumerable<string> names)
        {
            if (names == null)
            {
                return new List<string>();
            }

            return names
                   .Where(name => !string.IsNullOrWhiteSpace(name))
                   .Select(name => new
                   {
                       Original = name,
                       VersionObj = TryExtractVersionFromName(name),
                   })
                   .OrderBy(x => x.VersionObj ?? new Version(0, 0))
                   .ThenBy(x => x.Original, StringComparer.OrdinalIgnoreCase)
                   .Select(x => x.Original)
                   .ToList();
        }

        /// <summary>
        /// 从目录名称中提取版本号，支持任意位置的 vX.Y[.Z[.W]]
        /// 例如：2025.09.30_v1.5.2、v11.3.2.0_log、v11.3.2
        /// 返回解析后的 Version；若未匹配到则返回 null
        /// </summary>
        private static Version TryExtractVersionFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            // 匹配 v 后面的最多四段数字（Version 支持最多四段）
            var match = Regex.Match(name, @"[vV](\d+(?:\.\d+){0,3})", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var verText = match.Groups[1].Value;
                try
                {
                    return Version.Parse(verText);
                }
                catch
                {
                    // 解析失败，不参与版本排序
                }
            }
            return null;
        }

        /// <summary>
        /// 获取FTP目录列表
        /// </summary>
        private List<string> GetFtpDirectories(string ftpUrl, string username, string password)
        {
            var directories = new List<string>();
            
            var request = (FtpWebRequest)WebRequest.Create(ftpUrl);
            request.Method = WebRequestMethods.Ftp.ListDirectoryDetails;

            // 设置凭据
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                request.Credentials = new NetworkCredential(username, password);
            }
            else
            {
                request.Credentials = CredentialCache.DefaultNetworkCredentials;
            }

            using (var response = (FtpWebResponse)request.GetResponse())
            using (var responseStream = response.GetResponseStream())
            using (var reader = new StreamReader(responseStream))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    // 解析FTP LIST命令的输出
                    // 通常格式为: drwxrwxrwx   1 owner    group            0 Jan 01 12:00 dirname
                    if (line.StartsWith("d")) // 'd' 表示目录
                    {
                        var parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 9)
                        {
                            // 最后一部分是文件夹名称
                            var dirName = parts[parts.Length - 1];
                            if (!string.IsNullOrEmpty(dirName) && dirName != "." && dirName != "..")
                            {
                                directories.Add(dirName);
                            }
                        }
                    }
                }
            }
            
            return directories;
        }

        /// <summary>
        /// 获取HTTP目录列表
        /// </summary>
        private List<string> GetHttpDirectories(string httpUrl, string username, string password)
        {
            var directories = new List<string>();
            
            var request = (HttpWebRequest)WebRequest.Create(httpUrl);
            request.Method = "GET";

            // 设置凭据
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                request.Credentials = new NetworkCredential(username, password);
            }

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var responseStream = response.GetResponseStream())
            using (var reader = new StreamReader(responseStream))
            {
                var html = reader.ReadToEnd();
                
                // 解析HTML中的目录链接
                // 匹配形如 <a href="dirname/">dirname/</a> 的链接
                var regex = new Regex(@"<a\s+href=""([^""]+/)""[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase);
                var matches = regex.Matches(html);
                
                foreach (Match match in matches)
                {
                    var href = match.Groups[1].Value;
                    var text = match.Groups[2].Value.Trim();
                    
                    directories.Add(text);
                }
            }
            
            return directories;
        }

        /// <summary>
        /// 测试服务器连接
        /// </summary>
        /// <param name="serverUrl">服务器地址（支持FTP和HTTP协议）</param>
        /// <param name="username">用户名</param>
        /// <param name="password">密码</param>
        /// <returns>连接是否成功</returns>
        public bool TestConnection(string serverUrl, string username = null, string password = null)
        {
            try
            {
                // 确保URL以/结尾
                if (!serverUrl.EndsWith("/"))
                    serverUrl += "/";

                var uri = new Uri(serverUrl);
                
                if (uri.Scheme.ToLower() == "ftp")
                {
                    return TestFtpConnection(serverUrl, username, password);
                }
                else if (uri.Scheme.ToLower() == "http" || uri.Scheme.ToLower() == "https")
                {
                    return TestHttpConnection(serverUrl, username, password);
                }
                else
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 测试FTP连接
        /// </summary>
        private bool TestFtpConnection(string ftpUrl, string username, string password)
        {
            try
            {
                var request = (FtpWebRequest)WebRequest.Create(ftpUrl);
                request.Method = WebRequestMethods.Ftp.ListDirectory;

                // 设置凭据
                if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
                {
                    request.Credentials = new NetworkCredential(username, password);
                }
                else
                {
                    request.Credentials = CredentialCache.DefaultNetworkCredentials;
                }

                using (var response = (FtpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == FtpStatusCode.OpeningData || 
                           response.StatusCode == FtpStatusCode.DataAlreadyOpen;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 测试HTTP连接
        /// </summary>
        private bool TestHttpConnection(string httpUrl, string username, string password)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(httpUrl);
                request.Method = "HEAD"; // 使用HEAD方法减少数据传输

                // 设置凭据
                if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
                {
                    request.Credentials = new NetworkCredential(username, password);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取FTP文件列表
        /// </summary>
        private List<string> GetFtpFiles(string ftpUrl, string username, string password)
        {
            var files = new List<string>();
            
            var request = (FtpWebRequest)WebRequest.Create(ftpUrl);
            request.Method = WebRequestMethods.Ftp.ListDirectoryDetails;

            // 设置凭据
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                request.Credentials = new NetworkCredential(username, password);
            }
            else
            {
                request.Credentials = CredentialCache.DefaultNetworkCredentials;
            }

            using (var response = (FtpWebResponse)request.GetResponse())
            using (var responseStream = response.GetResponseStream())
            using (var reader = new StreamReader(responseStream))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    // 解析FTP LIST命令的输出
                    // 通常格式为: -rw-rw-rw-   1 owner    group            size Jan 01 12:00 filename
                    if (line.StartsWith("-")) // '-' 表示文件
                    {
                        var parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 9)
                        {
                            // 最后一部分是文件名
                            var fileName = parts[parts.Length - 1];
                            if (!string.IsNullOrEmpty(fileName))
                            {
                                files.Add(fileName);
                            }
                        }
                    }
                }
            }
            
            return files;
        }

        /// <summary>
        /// 获取HTTP文件列表
        /// </summary>
        private List<string> GetHttpFiles(string httpUrl, string username, string password)
        {
            var files = new List<string>();
            
            var request = (HttpWebRequest)WebRequest.Create(httpUrl);
            request.Method = "GET";

            // 设置凭据
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                request.Credentials = new NetworkCredential(username, password);
            }

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var responseStream = response.GetResponseStream())
            using (var reader = new StreamReader(responseStream))
            {
                var html = reader.ReadToEnd();
                
                // 解析HTML中的文件链接
                // 匹配形如 <a href="filename">filename</a> 的链接（不以/结尾的）
                var regex = new Regex(@"<a\s+href=""([^""]+)""[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase);
                var matches = regex.Matches(html);
                
                foreach (Match match in matches)
                {
                    var href = match.Groups[1].Value;
                    var text = match.Groups[2].Value.Trim();
                    
                    // 排除目录链接（以/结尾的）和特殊链接
                    if (!href.EndsWith("/") && !href.StartsWith("?") && !href.StartsWith("../") && text != "..")
                    {
                        files.Add(text);
                    }
                }
            }
            
            return files;
        }

        /// <summary>
        /// 按文件名中的时间信息排序文件列表
        /// </summary>
        /// <param name="files">文件列表</param>
        /// <returns>排序后的文件列表</returns>
        private List<string> SortFilesByTime(List<string> files)
        {
            var fileTimeList = new List<(string fileName, DateTime time)>();
            foreach (var file in files)
            {
                bool timeFound = false;
                DateTime extractedTime = ParseTimeFromFileName(file);
                if (!extractedTime.Equals(DateTime.MinValue))
                {
                    timeFound = true;
                }
                fileTimeList.Add((file, timeFound ? extractedTime : DateTime.MinValue));
            }

            // 按时间排序，时间相同的按文件名排序
            return fileTimeList
                .OrderBy(x => x.time)
                .ThenBy(x => x.fileName)
                .Select(x => x.fileName)
                .ToList();
        }
        
        /// <summary>
        /// 从文件名中解析时间信息
        /// </summary>
        /// <param name="fileName">文件名</param>
        /// <returns>解析出的时间，如果解析失败返回DateTime.MinValue</returns>
        public static DateTime ParseTimeFromFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return DateTime.MinValue;

            // 支持多种时间格式：yyyyMMdd, yyyy-MM-dd, yyyyMMddHHmmss, yyyy-MM-dd_HH-mm-ss等
            var timeRegexes = new[]
            {
                new Regex(@"(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})", RegexOptions.IgnoreCase),      // yyyyMMddHHmm
                new Regex(@"(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})", RegexOptions.IgnoreCase),      // yyyyMMddHHmmss
                new Regex(@"(\d{4})-(\d{2})-(\d{2})_(\d{2})-(\d{2})-(\d{2})", RegexOptions.IgnoreCase), // yyyy-MM-dd_HH-mm-ss
                new Regex(@"(\d{4})(\d{2})(\d{2})", RegexOptions.IgnoreCase),                           // yyyyMMdd
                new Regex(@"(\d{4})-(\d{2})-(\d{2})", RegexOptions.IgnoreCase),                         // yyyy-MM-dd
                new Regex(@"(\d{2})(\d{2})(\d{2})", RegexOptions.IgnoreCase),                           // yyMMdd
            };

            foreach (var regex in timeRegexes)
            {
                var match = regex.Match(fileName);
                if (match.Success)
                {
                    try
                    {
                        if (match.Groups.Count == 6) // yyyyMMddHHmm
                        {
                            var year = int.Parse(match.Groups[1].Value);
                            var month = int.Parse(match.Groups[2].Value);
                            var day = int.Parse(match.Groups[3].Value);
                            var hour = int.Parse(match.Groups[4].Value);
                            var minute = int.Parse(match.Groups[5].Value);
                            return new DateTime(year, month, day, hour, minute, 0);
                        }
                        if (match.Groups.Count == 7) // yyyyMMddHHmmss or yyyy-MM-dd_HH-mm-ss
                        {
                            var year = int.Parse(match.Groups[1].Value);
                            var month = int.Parse(match.Groups[2].Value);
                            var day = int.Parse(match.Groups[3].Value);
                            var hour = int.Parse(match.Groups[4].Value);
                            var minute = int.Parse(match.Groups[5].Value);
                            var second = int.Parse(match.Groups[6].Value);
                            return new DateTime(year, month, day, hour, minute, second);
                        }
                        else if (match.Groups.Count == 4) // yyyyMMdd or yyyy-MM-dd or yyMMdd
                        {
                            var yearStr = match.Groups[1].Value;
                            var year = int.Parse(yearStr);
                            if (yearStr.Length == 2) // yyMMdd
                            {
                                year += year > 50 ? 1900 : 2000; // 简单的年份转换逻辑
                            }
                            var month = int.Parse(match.Groups[2].Value);
                            var day = int.Parse(match.Groups[3].Value);
                            return new DateTime(year, month, day);
                        }
                    }
                    catch
                    {
                        // 时间解析失败，继续尝试下一个正则表达式
                    }
                }
            }

            return DateTime.MinValue;
        }
    }
}
