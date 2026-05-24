namespace PackageManager.Services.PingCode.Exception;

/// <summary>
/// 当 PingCode API 返回认证失败（401 Unauthorized）或 Token 请求失败时抛出的异常。
/// </summary>
/// <param name="message">描述认证失败的错误消息。</param>
public class ApiAuthException(string message)
    : System.Exception(message);