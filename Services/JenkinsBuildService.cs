using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PackageManager.Models;

namespace PackageManager.Services
{
    /// <summary>
    /// Jenkins 编译服务，负责触发 Jenkins Job 编译并轮询编译结果。
    /// </summary>
    internal sealed class JenkinsBuildService
    {
        private static readonly Regex CrumbFieldRegex = new Regex("\"crumbRequestField\"\\s*:\\s*\"(?<value>[^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex CrumbValueRegex = new Regex("\"crumb\"\\s*:\\s*\"(?<value>[^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex ExecutableRegex = new Regex("\"executable\"\\s*:\\s*\\{[^\\}]*\"number\"\\s*:\\s*(?<number>\\d+)[^\\}]*\"url\"\\s*:\\s*\"(?<url>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex BuildingRegex = new Regex("\"building\"\\s*:\\s*(?<value>true|false)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ResultRegex = new Regex("\"result\"\\s*:\\s*(?<value>null|\"[^\"]+\")", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly AppSettings settings;

        /// <summary>
        /// 初始化 <see cref="JenkinsBuildService"/> 的新实例。
        /// </summary>
        /// <param name="settings">应用程序设置实例。</param>
        /// <exception cref="ArgumentNullException"><paramref name="settings"/> 为 null。</exception>
        public JenkinsBuildService(AppSettings settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// 异步触发指定产品包对应的 Jenkins 编译，并等待编译完成。
        /// </summary>
        /// <param name="package">要编译的产品包信息。</param>
        /// <param name="progressReporter">进度回调，用于向调用方报告编译进度。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>Jenkins 编译结果。</returns>
        public async Task<JenkinsBuildResult> TriggerBuildAsync(PackageInfo package,
                                                                Action<string> progressReporter = null,
                                                                CancellationToken cancellationToken = default)
        {
            if (package == null)
            {
                return JenkinsBuildResult.Fail("未选择产品包");
            }

            var baseUrl = (settings.JenkinsBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            var viewName = (settings.JenkinsViewName ?? string.Empty).Trim();
            var username = (settings.JenkinsUsername ?? string.Empty).Trim();
            var password = CredentialProtectionService.Unprotect(settings.JenkinsPasswordProtected);

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return JenkinsBuildResult.Fail("请先在软件设置中填写 Jenkins 地址");
            }

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                return JenkinsBuildResult.Fail("请先在软件设置中填写 Jenkins 用户名和密码");
            }

            var jobName = ResolveJobName(package.ProductName);
            if (string.IsNullOrWhiteSpace(jobName))
            {
                return JenkinsBuildResult.Fail($"暂时无法从产品名“{package.ProductName}”推导 Jenkins Job 名称");
            }

            var buildUrl = BuildTriggerUrl(baseUrl, viewName, jobName);
            var crumbUrl = $"{baseUrl}/crumbIssuer/api/json";
            var cookies = new CookieContainer();
            var authHeader = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));

            progressReporter?.Invoke($"正在触发 {jobName} 的 Jenkins 编译...");

            string crumbField = null;
            string crumbValue = null;

            try
            {
                var crumbResponse = await SendRequestAsync(crumbUrl, "GET", authHeader, cookies, null, null, cancellationToken);
                if (crumbResponse.StatusCode == HttpStatusCode.OK)
                {
                    crumbField = MatchValue(CrumbFieldRegex, crumbResponse.Body);
                    crumbValue = MatchValue(CrumbValueRegex, crumbResponse.Body);
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response == null || response.StatusCode != HttpStatusCode.NotFound)
                {
                    return JenkinsBuildResult.Fail(ReadWebExceptionMessage(ex, "获取 Jenkins crumb 失败"));
                }
            }

            try
            {
                var triggerResponse = await SendRequestAsync(buildUrl,
                                                             "POST",
                                                             authHeader,
                                                             cookies,
                                                             crumbField,
                                                             crumbValue,
                                                             cancellationToken);

                if (IsLoginRedirect(triggerResponse.Location))
                {
                    return JenkinsBuildResult.Fail("Jenkins 用户名或密码无效，未能完成登录");
                }

                if ((triggerResponse.StatusCode != HttpStatusCode.Created)
                    && (triggerResponse.StatusCode != HttpStatusCode.Found)
                    && (triggerResponse.StatusCode != HttpStatusCode.SeeOther))
                {
                    return JenkinsBuildResult.Fail($"触发 Jenkins 编译失败：{(int)triggerResponse.StatusCode} {triggerResponse.StatusDescription}");
                }

                var queueUrl = NormalizeLocation(baseUrl, triggerResponse.Location);
                if (string.IsNullOrWhiteSpace(queueUrl))
                {
                    return JenkinsBuildResult.Success(buildUrl, null, null, null, "Jenkins 已接收编译请求");
                }

                progressReporter?.Invoke("Jenkins 已接收编译请求，正在排队...");

                var startResult = await WaitForExecutableAsync(baseUrl, queueUrl, authHeader, cookies, progressReporter, cancellationToken);
                if ((startResult == null) || !startResult.IsSuccess || string.IsNullOrWhiteSpace(startResult.BuildUrl))
                {
                    return startResult ?? JenkinsBuildResult.Success(buildUrl, queueUrl, null, null, "Jenkins 已接收编译请求，正在排队");
                }

                var finalResult = await WaitForBuildCompletionAsync(startResult.BuildUrl,
                                                                    startResult.BuildNumber,
                                                                    authHeader,
                                                                    cookies,
                                                                    progressReporter,
                                                                    cancellationToken);
                return finalResult ?? startResult;
            }
            catch (WebException ex)
            {
                return JenkinsBuildResult.Fail(ReadWebExceptionMessage(ex, "触发 Jenkins 编译失败"));
            }
            catch (Exception ex)
            {
                return JenkinsBuildResult.Fail($"触发 Jenkins 编译失败：{ex.Message}");
            }
        }

        private static async Task<JenkinsBuildResult> WaitForExecutableAsync(string baseUrl,
                                                                               string queueUrl,
                                                                               string authHeader,
                                                                               CookieContainer cookies,
                                                                               Action<string> progressReporter,
                                                                               CancellationToken cancellationToken)
        {
            var apiUrl = queueUrl.TrimEnd('/') + "/api/json";

            for (int i = 0; i < 15; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var queueResponse = await SendRequestAsync(apiUrl, "GET", authHeader, cookies, null, null, cancellationToken);
                if (queueResponse.StatusCode == HttpStatusCode.OK)
                {
                    if (queueResponse.Body.IndexOf("\"cancelled\":true", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return JenkinsBuildResult.Fail("Jenkins 编译已取消");
                    }

                    var match = ExecutableRegex.Match(queueResponse.Body ?? string.Empty);
                    if (match.Success)
                    {
                        var buildNumberText = match.Groups["number"].Value;
                        int buildNumber;
                        int? parsedNumber = int.TryParse(buildNumberText, out buildNumber) ? buildNumber : (int?)null;
                        var buildUrl = NormalizeLocation(baseUrl, match.Groups["url"].Value);
                        progressReporter?.Invoke(parsedNumber.HasValue
                                                     ? $"Jenkins 已开始编译 #{parsedNumber.Value}"
                                                     : "Jenkins 已开始编译");
                        return JenkinsBuildResult.Success(buildUrl,
                                                          queueUrl,
                                                          buildUrl,
                                                          parsedNumber,
                                                          parsedNumber.HasValue
                                                              ? $"Jenkins 已开始编译 #{parsedNumber.Value}"
                                                              : "Jenkins 已开始编译");
                    }
                }

                await Task.Delay(2000, cancellationToken);
            }

            return JenkinsBuildResult.Success(null, queueUrl, null, null, "Jenkins 已接收编译请求，正在排队");
        }

        private static async Task<JenkinsBuildResult> WaitForBuildCompletionAsync(string buildUrl,
                                                                                   int? buildNumber,
                                                                                   string authHeader,
                                                                                   CookieContainer cookies,
                                                                                   Action<string> progressReporter,
                                                                                   CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(buildUrl))
            {
                return null;
            }

            var apiUrl = buildUrl.TrimEnd('/') + "/api/json";
            string lastMessage = null;

            for (int i = 0; i < 360; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var buildResponse = await SendRequestAsync(apiUrl, "GET", authHeader, cookies, null, null, cancellationToken);
                if (buildResponse.StatusCode == HttpStatusCode.OK)
                {
                    var body = buildResponse.Body ?? string.Empty;
                    var building = MatchValue(BuildingRegex, body);
                    var resultValue = MatchValue(ResultRegex, body);

                    if (string.Equals(building, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        var runningMessage = buildNumber.HasValue
                            ? $"Jenkins 正在编译 #{buildNumber.Value}..."
                            : "Jenkins 正在编译...";
                        if (!string.Equals(lastMessage, runningMessage, StringComparison.Ordinal))
                        {
                            progressReporter?.Invoke(runningMessage);
                            lastMessage = runningMessage;
                        }
                    }

                    if (!string.Equals(building, "true", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(resultValue)
                        && !string.Equals(resultValue, "null", StringComparison.OrdinalIgnoreCase))
                    {
                        var normalizedResult = resultValue.Trim().Trim('"').ToUpperInvariant();
                        return JenkinsBuildResult.FromBuildResult(buildUrl, buildNumber, normalizedResult);
                    }
                }

                await Task.Delay(5000, cancellationToken);
            }

            return JenkinsBuildResult.Success(buildUrl,
                                              null,
                                              buildUrl,
                                              buildNumber,
                                              buildNumber.HasValue
                                                  ? $"Jenkins 编译 #{buildNumber.Value} 仍在执行，请到 Jenkins 页面查看最新状态"
                                                  : "Jenkins 编译仍在执行，请到 Jenkins 页面查看最新状态");
        }

        private static async Task<JenkinsHttpResponse> SendRequestAsync(string url,
                                                                         string method,
                                                                         string authHeader,
                                                                         CookieContainer cookies,
                                                                         string crumbField,
                                                                         string crumbValue,
                                                                         CancellationToken cancellationToken)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.CookieContainer = cookies;
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip;
            request.UserAgent = "PackageManager";
            request.Accept = "application/json,text/html,application/xhtml+xml,*/*";
            request.Headers[HttpRequestHeader.Authorization] = authHeader;

            if (!string.IsNullOrWhiteSpace(crumbField) && !string.IsNullOrWhiteSpace(crumbValue))
            {
                request.Headers[crumbField] = crumbValue;
            }

            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                request.ContentLength = 0;
            }

            using (cancellationToken.Register(() => request.Abort(), useSynchronizationContext: false))
            {
                try
                {
                    using (var response = (HttpWebResponse)await request.GetResponseAsync())
                    {
                        return await ReadResponseAsync(response);
                    }
                }
                catch (WebException ex)
                {
                    if (ex.Response is HttpWebResponse response)
                    {
                        var result = await ReadResponseAsync(response);
                        if ((result.StatusCode == HttpStatusCode.Found || result.StatusCode == HttpStatusCode.SeeOther)
                            && !string.IsNullOrWhiteSpace(result.Location))
                        {
                            return result;
                        }
                    }

                    throw;
                }
            }
        }

        private static async Task<JenkinsHttpResponse> ReadResponseAsync(HttpWebResponse response)
        {
            string body = string.Empty;
            using (var stream = response.GetResponseStream())
            {
                if (stream != null)
                {
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        body = await reader.ReadToEndAsync();
                    }
                }
            }

            return new JenkinsHttpResponse
            {
                StatusCode = response.StatusCode,
                StatusDescription = response.StatusDescription,
                Location = response.Headers[HttpResponseHeader.Location],
                Body = body,
            };
        }

        private static string ResolveJobName(string productName)
        {
            if (string.IsNullOrWhiteSpace(productName))
            {
                return null;
            }

            var match = Regex.Match(productName, "[（(](?<name>[^）)]+)[）)]\\s*Develop", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return null;
            }

            var code = (match.Groups["name"].Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(code) || Regex.IsMatch(code, "[\\u4e00-\\u9fff]"))
            {
                return null;
            }

            return code + "_Develop";
        }

        private static string BuildTriggerUrl(string baseUrl, string viewName, string jobName)
        {
            if (!string.IsNullOrWhiteSpace(viewName))
            {
                return $"{baseUrl}/view/{Uri.EscapeDataString(viewName)}/job/{Uri.EscapeDataString(jobName)}/build?delay=0sec";
            }

            return $"{baseUrl}/job/{Uri.EscapeDataString(jobName)}/build?delay=0sec";
        }

        private static string MatchValue(Regex regex, string input)
        {
            var match = regex.Match(input ?? string.Empty);
            return match.Success ? match.Groups["value"].Value : null;
        }

        private static string NormalizeLocation(string baseUrl, string location)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return null;
            }

            if (Uri.TryCreate(location, UriKind.Absolute, out var absolute))
            {
                return absolute.ToString();
            }

            if (Uri.TryCreate(new Uri(baseUrl.TrimEnd('/') + "/"), location.TrimStart('/'), out var relative))
            {
                return relative.ToString();
            }

            return location;
        }

        private static bool IsLoginRedirect(string location)
        {
            return !string.IsNullOrWhiteSpace(location)
                   && location.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ReadWebExceptionMessage(WebException ex, string prefix)
        {
            if (ex?.Response is HttpWebResponse response)
            {
                return $"{prefix}：{(int)response.StatusCode} {response.StatusDescription}";
            }

            return $"{prefix}：{ex?.Message}";
        }

        private sealed class JenkinsHttpResponse
        {
            public HttpStatusCode StatusCode { get; set; }
            public string StatusDescription { get; set; }
            public string Location { get; set; }
            public string Body { get; set; }
        }
    }

    /// <summary>
    /// 表示 Jenkins 编译操作的结果。
    /// </summary>
    internal sealed class JenkinsBuildResult
    {
        /// <summary>
        /// 获取编译是否成功。
        /// </summary>
        public bool IsSuccess { get; private set; }

        /// <summary>
        /// 获取编译触发请求的 URL。
        /// </summary>
        public string TriggerUrl { get; private set; }

        /// <summary>
        /// 获取 Jenkins 队列中编译任务的 URL。
        /// </summary>
        public string QueueUrl { get; private set; }

        /// <summary>
        /// 获取 Jenkins 编译任务的 URL。
        /// </summary>
        public string BuildUrl { get; private set; }

        /// <summary>
        /// 获取 Jenkins 编译任务的编号；若未获取到则为 null。
        /// </summary>
        public int? BuildNumber { get; private set; }

        /// <summary>
        /// 获取编译结果描述信息。
        /// </summary>
        public string Message { get; private set; }

        /// <summary>
        /// 创建一个成功的编译结果。
        /// </summary>
        /// <param name="triggerUrl">触发请求的 URL。</param>
        /// <param name="queueUrl">队列中编译任务的 URL。</param>
        /// <param name="buildUrl">编译任务的 URL。</param>
        /// <param name="buildNumber">编译编号。</param>
        /// <param name="message">结果描述信息。</param>
        /// <returns>成功的编译结果实例。</returns>
        public static JenkinsBuildResult Success(string triggerUrl, string queueUrl, string buildUrl, int? buildNumber, string message)
        {
            return new JenkinsBuildResult
            {
                IsSuccess = true,
                TriggerUrl = triggerUrl,
                QueueUrl = queueUrl,
                BuildUrl = buildUrl,
                BuildNumber = buildNumber,
                Message = message,
            };
        }

        /// <summary>
        /// 创建一个失败的编译结果。
        /// </summary>
        /// <param name="message">失败原因描述。</param>
        /// <returns>失败的编译结果实例。</returns>
        public static JenkinsBuildResult Fail(string message)
        {
            return new JenkinsBuildResult
            {
                IsSuccess = false,
                Message = message,
            };
        }

        /// <summary>
        /// 根据 Jenkins 编译状态字符串创建编译结果。
        /// </summary>
        /// <param name="buildUrl">编译任务的 URL。</param>
        /// <param name="buildNumber">编译编号。</param>
        /// <param name="result">Jenkins 返回的编译状态（如 SUCCESS、FAILURE、ABORTED 等）。</param>
        /// <returns>对应的编译结果实例。</returns>
        public static JenkinsBuildResult FromBuildResult(string buildUrl, int? buildNumber, string result)
        {
            var label = buildNumber.HasValue ? $" #{buildNumber.Value}" : string.Empty;

            switch ((result ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "SUCCESS":
                    return Success(buildUrl, null, buildUrl, buildNumber, $"Jenkins 编译成功{label}");
                case "FAILURE":
                    return Fail($"Jenkins 编译失败{label}");
                case "ABORTED":
                    return Fail($"Jenkins 编译已中止{label}");
                case "UNSTABLE":
                    return Success(buildUrl, null, buildUrl, buildNumber, $"Jenkins 编译完成{label}，结果为 UNSTABLE");
                case "NOT_BUILT":
                    return Fail($"Jenkins 编译未执行{label}");
                default:
                    return Success(buildUrl, null, buildUrl, buildNumber, $"Jenkins 编译完成{label}，结果为 {result}");
            }
        }
    }
}
