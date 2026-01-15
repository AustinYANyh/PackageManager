namespace PackageManager.Services.PingCode.Model;

public class StoryPointBreakdown
{
    public double NotStarted { get; set; }
    public double InProgress { get; set; }
    public double Done { get; set; }
    public double Closed { get; set; }
    public double Total { get; set; }
            
    public int HighestPriorityCount { get; set; }
    public double HighestPriorityPoints { get; set; }
    public int HigherPriorityCount { get; set; }
    public double HigherPriorityPoints { get; set; }
    public int OtherPriorityCount { get; set; }
    public double OtherPriorityPoints { get; set; }
}