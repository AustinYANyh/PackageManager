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

            //不用排序，直接返回
            return directories;
            
            // return directories.OrderBy(d => d).ToList();
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
    }
}