namespace PackageManager.Services.PingCode.Exception;

/// <summary>
/// 当 PingCode API 返回资源未找到（404 Not Found）时抛出的异常。
/// </summary>
public class ApiNotFoundException : System.Exception
{
    /// <summary>
    /// 初始化 <see cref="ApiNotFoundException"/> 类的新实例。
    /// </summary>
    /// <param name="message">描述资源未找到的错误消息。</param>
    public ApiNotFoundException(string message) : base(message) { }
}