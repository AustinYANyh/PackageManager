using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PackageManager.Models;

public class PluginAddinInfo : INotifyPropertyChanged
{
    private string name;

    private string fileName;

    private string extension;

    private string fullPath;

    private bool isEnabled;

    public event PropertyChangedEventHandler PropertyChanged;

    public string Name
    {
        get => name;

        set => SetProperty(ref name, value);
    }

    public string FileName
    {
        get => fileName;

        set => SetProperty(ref fileName, value);
    }

    public string Extension
    {
        get => extension;

        set => SetProperty(ref extension, value);
    }

    public string FullPath
    {
        get => fullPath;

        set => SetProperty(ref fullPath, value);
    }

    public bool IsEnabled
    {
        get => isEnabled;

        set => SetProperty(ref isEnabled, value);
    }

    public void UpdateFromPath(string path)
    {
        FullPath = path;
        FileName = System.IO.Path.GetFileName(path);
        Extension = System.IO.Path.GetExtension(path);
        Name = System.IO.Path.GetFileNameWithoutExtension(path);
        IsEnabled = string.Equals(Extension, ".addin", System.StringComparison.OrdinalIgnoreCase);
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

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