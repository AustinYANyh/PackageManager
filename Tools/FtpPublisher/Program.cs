using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace FtpPublisher
{
    /// <summary>
    /// FTP 发布工具的主程序类，负责将构建产物上传到 FTP 服务器。
    /// </summary>
    class Program
    {
        /// <summary>
        /// 程序入口点，执行 FTP 发布流程。
        /// </summary>
        /// <param name="args">命令行参数，支持 <c>--pause</c> 以在完成后暂停。</param>
        /// <returns>成功返回 0，失败返回 1。</returns>
        static int Main(string[] args)
        {
            var exitCode = 0;
            try
            {
                Run().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                exitCode = 1;
            }

            if (args != null && args.Any(a => string.Equals(a, "--pause", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("按任意键退出...");
                Console.ReadKey(true);
            }

            return exitCode;
        }
        
        /// <summary>
        /// 将给定的 URL 转换为 FTP URL 格式，并确保路径以斜杠结尾。
        /// </summary>
        /// <param name="anyUrl">原始 URL（支持 HTTP/HTTPS/FTP）。</param>
        /// <returns>规范化后的 FTP URL；如果输入为空则原样返回。</returns>
        private static string ToFtpUrl(string anyUrl)
        {
            if (string.IsNullOrWhiteSpace(anyUrl))
            {
                return anyUrl;
            }

            // 已是FTP地址则直接规范化尾部斜杠
            if (anyUrl.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            {
                var u = new Uri(anyUrl);
                var s = u.ToString();
                if (!s.EndsWith("/"))
                {
                    s += "/";
                }

                return s;
            }

            // 将 http(s)://host[:port]/path 转为 ftp://host/path，使用默认端口（21）
            var httpUri = new Uri(anyUrl);
            var builder = new UriBuilder
            {
                Scheme = "ftp",
                Host = httpUri.Host,
                Path = httpUri.AbsolutePath,
            };
            var ftpUrl = builder.Uri.ToString();
            if (!ftpUrl.EndsWith("/"))
            {
                ftpUrl += "/";
            }

            return ftpUrl;
        }

        /// <summary>
        /// 执行 FTP 发布的核心流程：读取版本号、创建远程目录、上传主程序和更新说明文件。
        /// </summary>
        /// <returns>异步任务。</returns>
        static async Task Run()
        {
            Console.WriteLine("开始发布到 FTP...");
            var version = ReadAssemblyVersion(@"e:\PackageManager\Properties\AssemblyInfo.cs");
            var binExe = Path.Combine(@"e:\PackageManager\bin\Release", "PackageManager.exe");
            Console.WriteLine("版本: " + version);

            var ftpBase = "ftp://192.168.0.215/";
            ftpBase = ToFtpUrl(ftpBase);
            var cred = new NetworkCredential("hwuser", "hongwa666.");

            var pkgDir = ftpBase + "PackageManager/v" + version + "/";
            Console.WriteLine("创建远程目录: " + pkgDir);
            await CreateRemoteDirectoryAsync(pkgDir, cred);
            Console.WriteLine("远程目录已就绪");

            var destUrl = pkgDir + "PackageManager.exe";
            using (var client = new WebClient())
            {
                client.Credentials = cred;
                Console.WriteLine("上传主程序: " + binExe + " -> " + destUrl);
                await client.UploadFileTaskAsync(new Uri(destUrl), WebRequestMethods.Ftp.UploadFile, binExe);
                Console.WriteLine("主程序上传完成");
            }

            var updateSummaryLocal = @"e:\PackageManager\UpdateSummary.txt";
            var updateSummaryDir = ftpBase + "UpdateSummary/";
            Console.WriteLine("清理更新说明目录: " + updateSummaryDir);
            await DeleteDirectoryAsync(updateSummaryDir, cred);
            Console.WriteLine("重建更新说明目录");
            await CreateRemoteDirectoryAsync(updateSummaryDir, cred);

            var updateSummaryDest = updateSummaryDir + "UpdateSummary.txt";
            using (var client = new WebClient())
            {
                client.Credentials = cred;
                Console.WriteLine("上传更新说明: " + updateSummaryLocal + " -> " + updateSummaryDest);
                await client.UploadFileTaskAsync(new Uri(updateSummaryDest), WebRequestMethods.Ftp.UploadFile, updateSummaryLocal);
                Console.WriteLine("更新说明上传完成");
            }

            Console.WriteLine("发布完成");
        }

        /// <summary>
        /// 从 AssemblyInfo.cs 文件中读取程序集版本号。
        /// </summary>
        /// <param name="assemblyInfoPath">AssemblyInfo.cs 文件路径。</param>
        /// <returns>解析到的版本号字符串。</returns>
        /// <exception cref="InvalidOperationException">当无法找到或解析 AssemblyVersion 特性时抛出。</exception>
        static string ReadAssemblyVersion(string assemblyInfoPath)
        {
            var text = File.ReadAllText(assemblyInfoPath);
            var start = text.IndexOf("[assembly: AssemblyVersion(\"", StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("AssemblyVersion not found");
            start += "[assembly: AssemblyVersion(\"".Length;
            var end = text.IndexOf("\")", start, StringComparison.Ordinal);
            if (end < 0) throw new InvalidOperationException("AssemblyVersion parse error");
            return text.Substring(start, end - start);
        }

        /// <summary>
        /// 在 FTP 服务器上逐级创建远程目录。如果目录已存在则跳过。
        /// </summary>
        /// <param name="remoteDir">要创建的远程目录完整 URL。</param>
        /// <param name="cred">FTP 登录凭据。</param>
        /// <returns>异步任务。</returns>
        static async Task CreateRemoteDirectoryAsync(string remoteDir, NetworkCredential cred)
        {
            var uri = new Uri(remoteDir);
            var segments = uri.AbsolutePath.Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var baseUri = uri.Scheme + "://" + uri.Host + ":" + uri.Port + "/";
            var current = baseUri;
            foreach (var segment in segments)
            {
                current += segment + "/";
                var req = (FtpWebRequest)WebRequest.Create(current);
                req.Credentials = cred;
                req.Method = WebRequestMethods.Ftp.MakeDirectory;
                req.UseBinary = true;
                req.KeepAlive = false;
                try
                {
                    using var resp = (FtpWebResponse)await req.GetResponseAsync();
                }
                catch (WebException ex)
                {
                    var resp = ex.Response as FtpWebResponse;
                    if (resp == null) throw;
                    if (resp.StatusCode != FtpStatusCode.ActionNotTakenFileUnavailable) throw;
                }
            }
        }

        /// <summary>
        /// 删除 FTP 服务器上的远程目录及其包含的所有文件。
        /// </summary>
        /// <param name="remoteDir">要删除的远程目录 URL。</param>
        /// <param name="cred">FTP 登录凭据。</param>
        /// <returns>异步任务。</returns>
        static async Task DeleteDirectoryAsync(string remoteDir, NetworkCredential cred)
        {
            string[] list = Array.Empty<string>();
            try
            {
                list = await ListDirectoryAsync(remoteDir, cred);
            }
            catch (WebException)
            {
            }
            foreach (var name in list)
            {
                var fileUri = new Uri(remoteDir + name);
                var delete = (FtpWebRequest)WebRequest.Create(fileUri);
                delete.Credentials = cred;
                delete.Method = WebRequestMethods.Ftp.DeleteFile;
                delete.UseBinary = true;
                delete.KeepAlive = false;
                using var resp = (FtpWebResponse)await delete.GetResponseAsync();
            }
            var req = (FtpWebRequest)WebRequest.Create(remoteDir);
            req.Credentials = cred;
            req.Method = WebRequestMethods.Ftp.RemoveDirectory;
            req.UseBinary = true;
            req.KeepAlive = false;
            try
            {
                using var resp = (FtpWebResponse)await req.GetResponseAsync();
            }
            catch (WebException ex)
            {
                var resp = ex.Response as FtpWebResponse;
                if (resp == null) throw;
                if (resp.StatusCode != FtpStatusCode.ActionNotTakenFileUnavailable) throw;
            }
        }

        /// <summary>
        /// 列出 FTP 远程目录下的文件和子目录名称。
        /// </summary>
        /// <param name="remoteDir">要列出的远程目录 URL。</param>
        /// <param name="cred">FTP 登录凭据。</param>
        /// <returns>目录中的文件/目录名称数组。</returns>
        static async Task<string[]> ListDirectoryAsync(string remoteDir, NetworkCredential cred)
        {
            var req = (FtpWebRequest)WebRequest.Create(remoteDir);
            req.Credentials = cred;
            req.Method = WebRequestMethods.Ftp.ListDirectory;
            req.UseBinary = true;
            req.KeepAlive = false;
            using var resp = (FtpWebResponse)await req.GetResponseAsync();
            using var stream = resp.GetResponseStream();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Where(n => n != "." && n != "..").ToArray();
        }
    }
}
