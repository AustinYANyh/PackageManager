using System.IO;
using Microsoft.Win32;

namespace PackageManager.Services;

internal static class FolderPickerService
{
    /// <summary>
    /// 弹出文件夹选择对话框（Windows Explorer 风格）。
    /// </summary>
    /// <param name="description">对话框标题文本。</param>
    /// <param name="selectedPath">初始目录路径。</param>
    /// <returns>用户选择的文件夹路径；取消时返回 null。</returns>
    public static string PickFolder(string description, string selectedPath = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = string.IsNullOrWhiteSpace(description) ? "选择文件夹" : description,
            CheckFileExists = false,
            CheckPathExists = true,
            FileName = "选择文件夹",
            Filter = "文件夹 (*.folder)|*.folder",
            ValidateNames = false,
        };

        if (!string.IsNullOrWhiteSpace(selectedPath) && Directory.Exists(selectedPath))
        {
            dialog.InitialDirectory = selectedPath;
        }

        return dialog.ShowDialog() == true ? Path.GetDirectoryName(dialog.FileName) : null;
    }
}