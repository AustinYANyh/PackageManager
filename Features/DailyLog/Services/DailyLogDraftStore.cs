using System;
using System.IO;
using System.Text;

namespace PackageManager.Features.DailyLog.Services
{
    /// <summary>
    /// 工作日报草稿存储，仅保存页面当前草稿内容，不影响应用设置配置。
    /// </summary>
    public sealed class DailyLogDraftStore
    {
        private readonly string draftFilePath;

        public DailyLogDraftStore()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var appFolder = Path.Combine(appDataPath, "PackageManager", "daily-log");
            Directory.CreateDirectory(appFolder);
            draftFilePath = Path.Combine(appFolder, "draft.txt");
        }

        public string LoadDraft()
        {
            try
            {
                return File.Exists(draftFilePath) ? File.ReadAllText(draftFilePath, Encoding.UTF8) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public bool SaveDraft(string text)
        {
            try
            {
                File.WriteAllText(draftFilePath, text ?? string.Empty, Encoding.UTF8);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool ClearDraft()
        {
            try
            {
                if (File.Exists(draftFilePath))
                {
                    File.Delete(draftFilePath);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
