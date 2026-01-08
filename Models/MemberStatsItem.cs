using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;

namespace PackageManager.Models;

public class MemberStatsItem : INotifyPropertyChanged
{
    [DataGridColumn(1, DisplayName = "成员", Width = "160", IsReadOnly = true)]
    public string MemberName { get; set; }
            
    [DataGridColumn(2, DisplayName = "未开始", Width = "120", IsReadOnly = true)]
    public double NotStarted { get; set; }
            
    [DataGridColumn(3, DisplayName = "进行中", Width = "120", IsReadOnly = true)]
    public double InProgress { get; set; }
            
    [DataGridColumn(4, DisplayName = "已完成", Width = "120", IsReadOnly = true)]
    public double Done { get; set; }
    
    [DataGridColumn(5, DisplayName = "已关闭", Width = "120", IsReadOnly = true)]
    public double Closed { get; set; }
    
    [DataGridColumn(6, DisplayName = "最高优先级数", Width = "120", IsReadOnly = true)]
    public int HighestPriorityCount { get; set; }
    
    [DataGridColumn(7, DisplayName = "最高优先级故事点", Width = "140", IsReadOnly = true)]
    public double HighestPriorityPoints { get; set; }
    
    [DataGridColumn(8, DisplayName = "较高优先级数", Width = "120", IsReadOnly = true)]
    public int HigherPriorityCount { get; set; }
    
    [DataGridColumn(9, DisplayName = "较高优先级故事点", Width = "140", IsReadOnly = true)]
    public double HigherPriorityPoints { get; set; }
    
    [DataGridColumn(10, DisplayName = "其他优先级数", Width = "120", IsReadOnly = true)]
    public int OtherPriorityCount { get; set; }
    
    [DataGridColumn(11, DisplayName = "其他优先级故事点", Width = "140", IsReadOnly = true)]
    public double OtherPriorityPoints { get; set; }
    
    [DataGridColumn(12, DisplayName = "总计", Width = "120", IsReadOnly = true)]
    public double Total { get; set; }

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
}
