namespace PackageManager.Services.PingCode.Exception;

/// <summary>
/// 当 PingCode API 返回权限不足（403 Forbidden）时抛出的异常。
/// </summary>
/// <param name="message">描述权限不足的错误消息。</param>
public class ApiForbiddenException(string message)
    : System.Exception(message);