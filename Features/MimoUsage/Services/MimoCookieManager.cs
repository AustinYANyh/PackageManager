using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PackageManager.Features.MimoUsage.Services
{
    /// <summary>
    /// MiMo 平台 Cookie 持久化管理。
    /// 存储路径：%AppData%/PackageManager/mimo_cookies.json
    /// </summary>
    public class MimoCookieManager
    {
        private readonly string _cookiesFilePath;

        public MimoCookieManager()
        {
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PackageManager");
            _cookiesFilePath = Path.Combine(dataFolder, "mimo_cookies.json");
        }

        /// <summary>
        /// 保存 Cookie 字符串到本地文件。
        /// </summary>
        public async Task SaveCookiesAsync(string rawCookieString, string userId)
        {
            var data = new MimoCookieData
            {
                RawCookie = rawCookieString,
                UserId = userId,
                SavedAt = DateTime.Now
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
        /// 读取已保存的 Cookie。返回 (cookie字符串, userId)；若无文件或解析失败返回 (null, null)。
        /// </summary>
        public async Task<(string RawCookie, string UserId)> LoadCookiesAsync()
        {
            try
            {
                if (!File.Exists(_cookiesFilePath))
                {
                    return (null, null);
                }

                var json = await Task.Run(() => File.ReadAllText(_cookiesFilePath));
                var data = JsonConvert.DeserializeObject<MimoCookieData>(json);
                if (data == null || string.IsNullOrWhiteSpace(data.RawCookie))
                {
                    return (null, null);
                }

                return (data.RawCookie, data.UserId);
            }
            catch
            {
                return (null, null);
            }
        }

        /// <summary>
        /// 检查是否有已存储的 Cookie。
        /// </summary>
        public bool HasStoredCookies()
        {
            return File.Exists(_cookiesFilePath);
        }

        /// <summary>
        /// 清除已保存的 Cookie（登出用）。
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
        private class MimoCookieData
        {
            [JsonProperty("rawCookie")]
            public string RawCookie { get; set; }

            [JsonProperty("userId")]
            public string UserId { get; set; }

            [JsonProperty("savedAt")]
            public DateTime SavedAt { get; set; }
        }
    }
}
