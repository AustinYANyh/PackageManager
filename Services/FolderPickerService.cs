using System.IO;

namespace PackageManager.Services;

internal static class FolderPickerService
{
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