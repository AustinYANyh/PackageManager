using System.IO;

namespace PackageManager.Services;

internal static class FolderPickerService
{
    /// <summary>
    /// 弹出文件夹选择对话框。
    /// </summary>
    /// <param name="description">对话框描述文本。</param>
    /// <param name="selectedPath">初始选中的路径。</param>
    /// <returns>用户选择的文件夹路径；取消时返回 null。</returns>
    public static string PickFolder(string description, string selectedPath = null)
    {
        using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
        {
            dialog.Description = string.IsNullOrWhiteSpace(description) ? "选择文件夹" : description;
            dialog.ShowNewFolderButton = true;

            if (!string.IsNullOrWhiteSpace(selectedPath) && Directory.Exists(selectedPath))
            {
                dialog.SelectedPath = selectedPath;
            }

            var result = dialog.ShowDialog();
            return result == System.Windows.Forms.DialogResult.OK
                       ? dialog.SelectedPath
                       : null;
        }
    }
}