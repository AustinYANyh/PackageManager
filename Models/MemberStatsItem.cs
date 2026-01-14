using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;

namespace PackageManager.Models;

public class MemberStatsItem : INotifyPropertyChanged
{
    [DataGridColumn(1, DisplayName = "成员", Width = "160", IsReadOnly = true)]
    public string MemberName { get; set; }
            
    public double NotStarted { get => _notStarted; set { if (SetField(ref _notStarted, value)) { OnPropertyChanged(nameof(NotStartedText)); } } }
    [DataGridColumn(2, DisplayName = "未开始", Width = "120", IsReadOnly = true)]
    public string NotStartedText { get => FormatPoints(NotStarted); set { } }
            
    public double InProgress { get => _inProgress; set { if (SetField(ref _inProgress, value)) { OnPropertyChanged(nameof(InProgressText)); } } }
    [DataGridColumn(3, DisplayName = "进行中", Width = "120", IsReadOnly = true)]
    public string InProgressText { get => FormatPoints(InProgress); set { } }
            
    public double Done { get => _done; set { if (SetField(ref _done, value)) { OnPropertyChanged(nameof(DoneText)); } } }
    [DataGridColumn(4, DisplayName = "已完成", Width = "120", IsReadOnly = true)]
    public string DoneText { get => FormatPoints(Done); set { } }
    
    public double Closed { get => _closed; set { if (SetField(ref _closed, value)) { OnPropertyChanged(nameof(ClosedText)); } } }
    [DataGridColumn(5, DisplayName = "已关闭", Width = "120", IsReadOnly = true)]
    public string ClosedText { get => FormatPoints(Closed); set { } }
    
    [DataGridColumn(6, DisplayName = "最高优先级数", Width = "120", IsReadOnly = true)]
    public int HighestPriorityCount { get; set; }
    
    public double HighestPriorityPoints { get => _highestPriorityPoints; set { if (SetField(ref _highestPriorityPoints, value)) { OnPropertyChanged(nameof(HighestPriorityPointsText)); } } }
    [DataGridColumn(7, DisplayName = "最高优先级故事点", Width = "140", IsReadOnly = true)]
    public string HighestPriorityPointsText { get => FormatPoints(HighestPriorityPoints); set { } }
    
    [DataGridColumn(8, DisplayName = "较高优先级数", Width = "120", IsReadOnly = true)]
    public int HigherPriorityCount { get; set; }
    
    public double HigherPriorityPoints { get => _higherPriorityPoints; set { if (SetField(ref _higherPriorityPoints, value)) { OnPropertyChanged(nameof(HigherPriorityPointsText)); } } }
    [DataGridColumn(9, DisplayName = "较高优先级故事点", Width = "140", IsReadOnly = true)]
    public string HigherPriorityPointsText { get => FormatPoints(HigherPriorityPoints); set { } }
    
    [DataGridColumn(10, DisplayName = "其他优先级数", Width = "120", IsReadOnly = true)]
    public int OtherPriorityCount { get; set; }
    
    public double OtherPriorityPoints { get => _otherPriorityPoints; set { if (SetField(ref _otherPriorityPoints, value)) { OnPropertyChanged(nameof(OtherPriorityPointsText)); } } }
    [DataGridColumn(11, DisplayName = "其他优先级故事点", Width = "140", IsReadOnly = true)]
    public string OtherPriorityPointsText { get => FormatPoints(OtherPriorityPoints); set { } }
    
    public double Total { get => _total; set { if (SetField(ref _total, value)) { OnPropertyChanged(nameof(TotalText)); } } }
    [DataGridColumn(12, DisplayName = "总计", Width = "120", IsReadOnly = true)]
    public string TotalText { get => FormatPoints(Total); set { } }

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
    
    private static string FormatPoints(double v)
    {
        try
        {
            var r = System.Math.Round(v, 1, System.MidpointRounding.AwayFromZero);
            if (System.Math.Abs(r % 1) < 1e-9) return r.ToString("0");
            return r.ToString("0.0");
        }
        catch
        {
            return v.ToString("0.0");
        }
    }
    
    private double _notStarted;
    private double _inProgress;
    private double _done;
    private double _closed;
    private double _highestPriorityPoints;
    private double _higherPriorityPoints;
    private double _otherPriorityPoints;
    private double _total;
}
