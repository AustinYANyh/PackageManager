using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;

namespace PackageManager.Models;

/// <summary>
/// 成员统计项，按成员汇总看板工作项的状态和优先级故事点。
/// </summary>
public class MemberStatsItem : INotifyPropertyChanged
{
    /// <summary>
    /// 获取或设置成员名称。
    /// </summary>
    [DataGridColumn(1, DisplayName = "成员", Width = "160", IsReadOnly = true)]
    public string MemberName { get; set; }

    /// <summary>
    /// 获取或设置未开始的故事点数。
    /// </summary>
    public double NotStarted { get => _notStarted; set { if (SetField(ref _notStarted, value)) { OnPropertyChanged(nameof(NotStartedText)); } } }

    /// <summary>
    /// 获取未开始故事点的格式化文本。
    /// </summary>
    [DataGridColumn(2, DisplayName = "未开始", Width = "120", IsReadOnly = true)]
    public string NotStartedText { get => FormatPoints(NotStarted); set { } }

    /// <summary>
    /// 获取或设置进行中的故事点数。
    /// </summary>
    public double InProgress { get => _inProgress; set { if (SetField(ref _inProgress, value)) { OnPropertyChanged(nameof(InProgressText)); } } }

    /// <summary>
    /// 获取进行中故事点的格式化文本。
    /// </summary>
    [DataGridColumn(3, DisplayName = "进行中", Width = "120", IsReadOnly = true)]
    public string InProgressText { get => FormatPoints(InProgress); set { } }

    /// <summary>
    /// 获取或设置已完成的故事点数。
    /// </summary>
    public double Done { get => _done; set { if (SetField(ref _done, value)) { OnPropertyChanged(nameof(DoneText)); } } }

    /// <summary>
    /// 获取已完成故事点的格式化文本。
    /// </summary>
    [DataGridColumn(4, DisplayName = "已完成", Width = "120", IsReadOnly = true)]
    public string DoneText { get => FormatPoints(Done); set { } }

    /// <summary>
    /// 获取或设置已关闭的故事点数。
    /// </summary>
    public double Closed { get => _closed; set { if (SetField(ref _closed, value)) { OnPropertyChanged(nameof(ClosedText)); } } }

    /// <summary>
    /// 获取已关闭故事点的格式化文本。
    /// </summary>
    [DataGridColumn(5, DisplayName = "已关闭", Width = "120", IsReadOnly = true)]
    public string ClosedText { get => FormatPoints(Closed); set { } }

    /// <summary>
    /// 获取或设置最高优先级工作项数量。
    /// </summary>
    [DataGridColumn(6, DisplayName = "最高优先级数", Width = "120", IsReadOnly = true)]
    public int HighestPriorityCount { get; set; }

    /// <summary>
    /// 获取或设置最高优先级的故事点数。
    /// </summary>
    public double HighestPriorityPoints { get => _highestPriorityPoints; set { if (SetField(ref _highestPriorityPoints, value)) { OnPropertyChanged(nameof(HighestPriorityPointsText)); } } }

    /// <summary>
    /// 获取最高优先级故事点的格式化文本。
    /// </summary>
    [DataGridColumn(7, DisplayName = "最高优先级故事点", Width = "140", IsReadOnly = true)]
    public string HighestPriorityPointsText { get => FormatPoints(HighestPriorityPoints); set { } }

    /// <summary>
    /// 获取或设置较高优先级工作项数量。
    /// </summary>
    [DataGridColumn(8, DisplayName = "较高优先级数", Width = "120", IsReadOnly = true)]
    public int HigherPriorityCount { get; set; }

    /// <summary>
    /// 获取或设置较高优先级的故事点数。
    /// </summary>
    public double HigherPriorityPoints { get => _higherPriorityPoints; set { if (SetField(ref _higherPriorityPoints, value)) { OnPropertyChanged(nameof(HigherPriorityPointsText)); } } }

    /// <summary>
    /// 获取较高优先级故事点的格式化文本。
    /// </summary>
    [DataGridColumn(9, DisplayName = "较高优先级故事点", Width = "140", IsReadOnly = true)]
    public string HigherPriorityPointsText { get => FormatPoints(HigherPriorityPoints); set { } }

    /// <summary>
    /// 获取或设置其他优先级工作项数量。
    /// </summary>
    [DataGridColumn(10, DisplayName = "其他优先级数", Width = "120", IsReadOnly = true)]
    public int OtherPriorityCount { get; set; }

    /// <summary>
    /// 获取或设置其他优先级的故事点数。
    /// </summary>
    public double OtherPriorityPoints { get => _otherPriorityPoints; set { if (SetField(ref _otherPriorityPoints, value)) { OnPropertyChanged(nameof(OtherPriorityPointsText)); } } }

    /// <summary>
    /// 获取其他优先级故事点的格式化文本。
    /// </summary>
    [DataGridColumn(11, DisplayName = "其他优先级故事点", Width = "140", IsReadOnly = true)]
    public string OtherPriorityPointsText { get => FormatPoints(OtherPriorityPoints); set { } }

    /// <summary>
    /// 获取或设置总计故事点数。
    /// </summary>
    public double Total { get => _total; set { if (SetField(ref _total, value)) { OnPropertyChanged(nameof(TotalText)); } } }

    /// <summary>
    /// 获取总计故事点的格式化文本。
    /// </summary>
    [DataGridColumn(12, DisplayName = "总计", Width = "120", IsReadOnly = true)]
    public string TotalText { get => FormatPoints(Total); set { } }

    /// <summary>
    /// 属性值变更时触发。
    /// </summary>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// 触发 <see cref="PropertyChanged"/> 事件。
    /// </summary>
    /// <param name="propertyName">发生变更的属性名称。</param>
    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 设置字段值并在值变更时触发 <see cref="PropertyChanged"/> 事件。
    /// </summary>
    /// <typeparam name="T">字段类型。</typeparam>
    /// <param name="field">字段引用。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">属性名称。</param>
    /// <returns>值是否发生变更。</returns>
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
