using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;

namespace PackageManager.Models;

/// <summary>
/// 产品日志文件信息（用于 CDataGrid 的标签配置）
/// </summary>
public class ProductLogInfo : INotifyPropertyChanged
{
    private string fileName;

    private string directory;

    private string fullPath;

    private string sizeText;

    private string modifiedText;

    private ICommand openCommand;

    private readonly ICommand openWithLogViewProCommand = null;

    private readonly ICommand openWithVSCodeCommand = null;

    private readonly ICommand openWithNotepadCommand = null;

    /// <summary>
    /// 属性值变更时触发。
    /// </summary>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// 获取或设置日志文件名。
    /// </summary>
    [DataGridColumn(1, DisplayName = "日志文件名", Width = "250", IsReadOnly = true)]
    public string FileName
    {
        get => fileName;

        set => SetProperty(ref fileName, value);
    }

    /// <summary>
    /// 获取或设置日志文件所在目录。
    /// </summary>
    [DataGridColumn(2, DisplayName = "所在目录", Width = "370", IsReadOnly = true, IsVisible = false)]
    public string Directory
    {
        get => directory;

        set => SetProperty(ref directory, value);
    }

    /// <summary>
    /// 获取或设置文件大小的显示文本。
    /// </summary>
    [DataGridColumn(3, DisplayName = "大小", Width = "110", IsReadOnly = true)]
    public string SizeText
    {
        get => sizeText;

        set => SetProperty(ref sizeText, value);
    }

    /// <summary>
    /// 获取或设置修改时间的显示文本。
    /// </summary>
    [DataGridColumn(4, DisplayName = "修改时间", Width = "170", IsReadOnly = true)]
    public string ModifiedText
    {
        get => modifiedText;

        set => SetProperty(ref modifiedText, value);
    }

    /// <summary>
    /// 操作按钮列的占位属性。
    /// </summary>
    [DataGridMultiButton(nameof(OpenButtons), 5, DisplayName = "操作", Width = "110", ButtonSpacing = 12)]

    public string Open { get; set; }

    /// <summary>
    /// 获取操作按钮的配置列表。
    /// </summary>
    public List<ButtonConfig> OpenButtons => new()
    {
        new ButtonConfig
        {
            Text = "打开",
            Width = 100,
            Height = 26,
            CommandProperty = nameof(OpenCommand),
        },
    };

    /// <summary>
    /// 获取或设置日志文件的完整路径。
    /// </summary>
    public string FullPath
    {
        get => fullPath;

        set => SetProperty(ref fullPath, value);
    }

    /// <summary>
    /// 获取或设置打开文件的命令。
    /// </summary>
    public ICommand OpenCommand
    {
        get => openCommand;

        set => SetProperty(ref openCommand, value);
    }

    /// <summary>
    /// 获取或设置使用 LogViewPro 打开的命令。
    /// </summary>
    public ICommand OpenWithLogViewProCommand
    {
        get => openWithLogViewProCommand;

        set => SetProperty(ref openCommand, value);
    }

    /// <summary>
    /// 获取或设置使用 VSCode 打开的命令。
    /// </summary>
    public ICommand OpenWithVSCodeCommand
    {
        get => openWithVSCodeCommand;

        set => SetProperty(ref openCommand, value);
    }

    /// <summary>
    /// 获取或设置使用记事本打开的命令。
    /// </summary>
    public ICommand OpenWithNotepadCommand
    {
        get => openWithNotepadCommand;

        set => SetProperty(ref openCommand, value);
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