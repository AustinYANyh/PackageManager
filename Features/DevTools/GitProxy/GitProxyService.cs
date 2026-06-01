using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace PackageManager.Services
{
    /// <summary>
    /// Git 代理状态信息。
    /// </summary>
    public sealed class GitProxyStatus
    {
        /// <summary>
        /// 获取或设置 HTTP 代理地址。
        /// </summary>
        public string HttpProxy { get; set; }

        /// <summary>
        /// 获取或设置 HTTPS 代理地址。
        /// </summary>
        public string HttpsProxy { get; set; }

        /// <summary>
        /// 获取一个值，指示是否已启用代理（任一代理地址非空即为已启用）。
        /// </summary>
        public bool IsEnabled => !string.IsNullOrWhiteSpace(HttpProxy) || !string.IsNullOrWhiteSpace(HttpsProxy);
    }

    /// <summary>
    /// Git 代理切换结果。
    /// </summary>
    public sealed class GitProxyToggleResult
    {
        /// <summary>
        /// 获取或设置一个值，指示切换后代理是否处于启用状态。
        /// </summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// 获取或设置切换后的 Git 代理状态。
        /// </summary>
        public GitProxyStatus Status { get; set; }

        /// <summary>
        /// 获取或设置切换结果的消息描述。
        /// </summary>
        public string Message { get; set; }
    }

    internal sealed class GitCommandResult
    {
        /// <summary>
        /// 获取或设置 Git 命令的退出代码。
        /// </summary>
        public int ExitCode { get; set; }

        /// <summary>
        /// 获取或设置 Git 命令的标准输出内容。
        /// </summary>
        public string StandardOutput { get; set; }

        /// <summary>
        /// 获取或设置 Git 命令的标准错误输出内容。
        /// </summary>
        public string StandardError { get; set; }
    }

    /// <summary>
    /// Git 全局代理切换服务。
    /// </summary>
    public static class GitProxyService
    {
        private const string DefaultHttpProxy = "http://127.0.0.1:7897";
        private const string DefaultHttpsProxy = "http://127.0.0.1:7897";

        /// <summary>
        /// 异步获取当前 Git 全局代理状态。
        /// </summary>
        /// <returns>包含 HTTP 与 HTTPS 代理配置的状态对象。</returns>
        public static async Task<GitProxyStatus> GetStatusAsync()
        {
            var httpProxy = await GetConfigValueAsync("http.proxy").ConfigureAwait(false);
            var httpsProxy = await GetConfigValueAsync("https.proxy").ConfigureAwait(false);

            return new GitProxyStatus
            {
                HttpProxy = httpProxy,
                HttpsProxy = httpsProxy
            };
        }

        /// <summary>
        /// 异步切换 Git 全局代理状态（启用/禁用切换）。
        /// </summary>
        /// <returns>包含切换后状态与结果消息的切换结果对象。</returns>
        public static async Task<GitProxyToggleResult> ToggleAsync()
        {
            var currentStatus = await GetStatusAsync().ConfigureAwait(false);
            if (currentStatus.IsEnabled)
            {
                return await DisableAsync(currentStatus).ConfigureAwait(false);
            }

            return await EnableAsync().ConfigureAwait(false);
        }

        private static async Task<GitProxyToggleResult> DisableAsync(GitProxyStatus currentStatus)
        {
            PersistProxyValues(currentStatus);

            await UnsetConfigAsync("http.proxy").ConfigureAwait(false);
            await UnsetConfigAsync("https.proxy").ConfigureAwait(false);

            var status = await GetStatusAsync().ConfigureAwait(false);
            var message =
                $"Git代理已关闭，当前 http.proxy={FormatProxy(status.HttpProxy)}，https.proxy={FormatProxy(status.HttpsProxy)}。";

            LoggingService.LogInfo(
                $"Git代理切换为关闭。关闭前 http.proxy={FormatProxy(currentStatus.HttpProxy)}，https.proxy={FormatProxy(currentStatus.HttpsProxy)}。");

            return new GitProxyToggleResult
            {
                IsEnabled = false,
                Status = status,
                Message = message
            };
        }

        private static async Task<GitProxyToggleResult> EnableAsync()
        {
            var dataService = new DataPersistenceService();
            var settings = dataService.LoadSettings() ?? new AppSettings();
            var httpProxy = FirstNonEmpty(settings.GitProxyHttpUrl, settings.GitProxyHttpsUrl, DefaultHttpProxy);
            var httpsProxy = FirstNonEmpty(settings.GitProxyHttpsUrl, settings.GitProxyHttpUrl, DefaultHttpsProxy);

            await SetConfigAsync("http.proxy", httpProxy).ConfigureAwait(false);
            await SetConfigAsync("https.proxy", httpsProxy).ConfigureAwait(false);

            settings.GitProxyHttpUrl = httpProxy;
            settings.GitProxyHttpsUrl = httpsProxy;
            dataService.SaveSettings(settings);

            var status = await GetStatusAsync().ConfigureAwait(false);
            var message =
                $"Git代理已开启，当前 http.proxy={FormatProxy(status.HttpProxy)}，https.proxy={FormatProxy(status.HttpsProxy)}。";

            LoggingService.LogInfo(
                $"Git代理切换为开启。当前 http.proxy={FormatProxy(status.HttpProxy)}，https.proxy={FormatProxy(status.HttpsProxy)}。");

            return new GitProxyToggleResult
            {
                IsEnabled = true,
                Status = status,
                Message = message
            };
        }

        private static void PersistProxyValues(GitProxyStatus status)
        {
            if (status == null)
            {
                return;
            }

            var dataService = new DataPersistenceService();
            var settings = dataService.LoadSettings() ?? new AppSettings();
            var changed = false;

            if (!string.IsNullOrWhiteSpace(status.HttpProxy))
            {
                settings.GitProxyHttpUrl = status.HttpProxy;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(status.HttpsProxy))
            {
                settings.GitProxyHttpsUrl = status.HttpsProxy;
                changed = true;
            }

            if (changed)
            {
                dataService.SaveSettings(settings);
            }
        }

        private static async Task<string> GetConfigValueAsync(string key)
        {
            var result = await RunGitCommandAsync($"config --global --get {key}", true).ConfigureAwait(false);
            return result.ExitCode == 0 ? NormalizeOutput(result.StandardOutput) : null;
        }

        private static async Task SetConfigAsync(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Git 代理配置 {key} 为空，无法启用代理。");
            }

            await RunGitCommandAsync($"config --global {key} {Quote(value)}", false).ConfigureAwait(false);
        }

        private static async Task UnsetConfigAsync(string key)
        {
            var result = await RunGitCommandAsync($"config --global --unset-all {key}", true).ConfigureAwait(false);
            if ((result.ExitCode != 0) && (result.ExitCode != 5))
            {
                throw BuildGitCommandException($"清除 Git 配置 {key} 失败", result);
            }
        }

        private static async Task<GitCommandResult> RunGitCommandAsync(string arguments, bool allowFailure)
        {
            return await Task.Run(() =>
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    process.Start();
                    var stdout = process.StandardOutput.ReadToEnd();
                    var stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    var result = new GitCommandResult
                    {
                        ExitCode = process.ExitCode,
                        StandardOutput = stdout,
                        StandardError = stderr
                    };

                    if (!allowFailure && (result.ExitCode != 0))
                    {
                        throw BuildGitCommandException($"执行 git {arguments} 失败", result);
                    }

                    return result;
                }
            }).ConfigureAwait(false);
        }

        private static Exception BuildGitCommandException(string message, GitCommandResult result)
        {
            var details = new StringBuilder(message);
            details.Append($"，ExitCode={result?.ExitCode ?? -1}");

            var error = NormalizeOutput(result?.StandardError);
            if (!string.IsNullOrWhiteSpace(error))
            {
                details.Append($"，Error={error}");
            }
            else
            {
                var output = NormalizeOutput(result?.StandardOutput);
                if (!string.IsNullOrWhiteSpace(output))
                {
                    details.Append($"，Output={output}");
                }
            }

            return new InvalidOperationException(details.ToString());
        }

        private static string NormalizeOutput(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string FormatProxy(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "未设置" : value;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return null;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
