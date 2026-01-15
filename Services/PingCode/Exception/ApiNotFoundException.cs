namespace PackageManager.Services.PingCode.Exception;

public class ApiNotFoundException : System.Exception
{
    public ApiNotFoundException(string message) : base(message) { }
}