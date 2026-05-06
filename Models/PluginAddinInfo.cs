using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PackageManager.Models;

/// <summary>
/// 插件 Addin 信息，表示一个 Revit 插件文件的相关数据。
/// </summary>
public class PluginAddinInfo : INotifyPropertyChanged
{
    private string name;

    private string fileName;

    private string extension;

    private string fullPath;

    private bool isEnabled;

    /// <summary>
    /// 属性值变更时触发。
    /// </summary>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// 获取或设置插件名称（不含扩展名）。
    /// </summary>
    public string Name
    {
        get => name;

        set => SetProperty(ref name, value);
    }

    /// <summary>
    /// 获取或设置文件名（含扩展名）。
    /// </summary>
    public string FileName
    {
        get => fileName;

        set => SetProperty(ref fileName, value);
    }

    /// <summary>
    /// 获取或设置文件扩展名。
    /// </summary>
    public string Extension
    {
        get => extension;

        set => SetProperty(ref extension, value);
    }

    /// <summary>
    /// 获取或设置文件完整路径。
    /// </summary>
    public string FullPath
    {
        get => fullPath;

        set => SetProperty(ref fullPath, value);
    }

    /// <summary>
    /// 获取或设置插件是否启用。
    /// </summary>
    public bool IsEnabled
    {
        get => isEnabled;

        set => SetProperty(ref isEnabled, value);
    }

    /// <summary>
    /// 根据文件路径更新插件的各项属性。
    /// </summary>
    /// <param name="path">插件文件的完整路径。</param>
    /// <param name="isEnabled">是否启用。</param>
    public void UpdateFromPath(string path, bool isEnabled)
    {
        FullPath = path;
        FileName = System.IO.Path.GetFileName(path);
        Extension = System.IO.Path.GetExtension(path);
        Name = System.IO.Path.GetFileNameWithoutExtension(path);
        IsEnabled = isEnabled;
    }

    /// <summary>
    /// 触发 <see cref="PropertyChanged"/> 事件。
    /// </summary>
    /// <param name="propertyName">发生变更的属性名称。</param>
    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 设置属性值并在值变更时触发 <see cref="PropertyChanged"/> 事件。
    /// </summary>
    /// <typeparam name="T">属性类型。</typeparam>
    /// <param name="field">属性 backing 字段的引用。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">属性名称。</param>
    /// <returns>值是否发生变更。</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
