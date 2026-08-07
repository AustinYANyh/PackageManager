using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PackageManager.Features.SubmitDefect.Services
{
    /// <summary>
    /// PingCode 平台 Cookie 持久化管理（用于内部 atlas 上传链路）。
    /// 存储路径：%AppData%/PackageManager/pingcode_cookies.json
    /// </summary>
    public class PingCodeCookieManager
    {
        private readonly string _cookiesFilePath;

        /// <summary>
        /// 初始化 <see cref="PingCodeCookieManager"/> 的新实例。
        /// </summary>
        public PingCodeCookieManager()
        {
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PackageManager");
            _cookiesFilePath = Path.Combine(dataFolder, "pingcode_cookies.json");
        }

        /// <summary>
        /// 保存 Cookie 字符串到本地文件。
        /// </summary>
        /// <param name="rawCookieString">原始 Cookie 字符串（key=value; key=value 形式）。</param>
        public async Task SaveCookiesAsync(string rawCookieString)
        {
            var data = new PingCodeCookieData
            {
                RawCookie = rawCookieString,
                SavedAt = DateTime.Now,
            };

            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            var dir = Path.GetDirectoryName(_cookiesFilePath);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await Task.Run(() => File.WriteAllText(_cookiesFilePath, json));
        }

        /// <summary>
        /// 读取已保存的 Cookie。无文件或解析失败返回 null。
        /// </summary>
        /// <returns>原始 Cookie 字符串；无则 null。</returns>
        public async Task<string> LoadCookiesAsync()
        {
            try
            {
                if (!File.Exists(_cookiesFilePath))
                {
                    return null;
                }

                var json = await Task.Run(() => File.ReadAllText(_cookiesFilePath));
                var data = JsonConvert.DeserializeObject<PingCodeCookieData>(json);
                if ((data == null) || string.IsNullOrWhiteSpace(data.RawCookie))
                {
                    return null;
                }

                return data.RawCookie;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 是否有已存储的 Cookie。
        /// </summary>
        public bool HasStoredCookies()
        {
            return File.Exists(_cookiesFilePath);
        }

        /// <summary>
        /// 清除已保存的 Cookie（登出/失效用）。
        /// </summary>
        public void ClearCookies()
        {
            try
            {
                if (File.Exists(_cookiesFilePath))
                {
                    File.Delete(_cookiesFilePath);
                }
            }
            catch
            {
                // 忽略删除失败
            }
        }

        /// <summary>
        /// Cookie 持久化数据模型。
        /// </summary>
        private class PingCodeCookieData
        {
            [JsonProperty("rawCookie")]
            public string RawCookie { get; set; }

            [JsonProperty("savedAt")]
            public DateTime SavedAt { get; set; }
        }
    }
}
