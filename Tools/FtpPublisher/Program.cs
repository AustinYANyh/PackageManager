using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace FtpPublisher
{
    class Program
    {
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
