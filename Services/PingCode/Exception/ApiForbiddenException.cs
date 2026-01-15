namespace PackageManager.Services.PingCode.Exception;

public class ApiForbiddenException(string message)
    : System.Exception(message);