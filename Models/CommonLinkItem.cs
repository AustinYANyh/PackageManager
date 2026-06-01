namespace PackageManager;

public class CommonLinkItem
{
    public CommonLinkItem(string name, string url)
    {
        Name = name;
        Url = url;
    }

    public string Name { get; }

    public string Url { get; }
}
