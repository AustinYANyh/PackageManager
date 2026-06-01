using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using PackageManager.Services;
using PackageManager.Services.PingCode;
using PackageManager.Services.PingCode.Dto;

namespace PackageManager.Views.KanBan;

/// <summary>
/// 看板列，表示看板中的一个状态列及其包含的工作项。
/// </summary>
public class KanbanColumn : INotifyPropertyChanged
{
    private string title;

    private ObservableCollection<WorkItemInfo> items = new();

    /// <summary>
    /// 属性值变更时触发。
    /// </summary>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// 获取或设置列标题。
    /// </summary>
    public string Title
    {
        get => title;

        set
        {
            if (title != value)
            {
                title = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Count));
            }
        }
    }

    /// <summary>
    /// 获取或设置该列包含的工作项集合。
    /// </summary>
    public ObservableCollection<WorkItemInfo> Items
    {
        get => items;

        set
        {
            if (!ReferenceEquals(items, value))
            {
                if (items != null)
                {
                    items.CollectionChanged -= Items_CollectionChanged;
                }

                items = value ?? new ObservableCollection<WorkItemInfo>();
                items.CollectionChanged += Items_CollectionChanged;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Count));
                OnPropertyChanged(nameof(TotalPoints));
                OnPropertyChanged(nameof(TotalPointsText));
            }
        }
    }

    /// <summary>
    /// 获取该列中的工作项数量。
    /// </summary>
    public int Count => items?.Count ?? 0;

    /// <summary>
    /// 获取该列中工作项的故事点总和。
    /// </summary>
    public double TotalPoints => items?.Sum(i => i?.StoryPoints ?? 0) ?? 0;

    /// <summary>
    /// 获取故事点总和的格式化文本。
    /// </summary>
    public string TotalPointsText => FormatPoints(TotalPoints);

    /// <summary>
    /// 手动刷新 Count、TotalPoints 和 TotalPointsText 属性通知。
    /// </summary>
    public void UpdateCountAndTotalPoints()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(TotalPoints));
        OnPropertyChanged(nameof(TotalPointsText));
    }

    /// <summary>
    /// 触发 <see cref="PropertyChanged"/> 事件。
    /// </summary>
    /// <param name="name">发生变更的属性名称。</param>
    protected void OnPropertyChanged([CallerMemberName] string name = null) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }

    private static string FormatPoints(double v)
    {
        try
        {
            var r = Math.Round(v, 1, MidpointRounding.AwayFromZero);
            if (Math.Abs(r % 1) < 1e-9)
            {
                return r.ToString("0");
            }

            return r.ToString("0.0");
        }
        catch
        {
            return v.ToString("0.0");
        }
    }

    private void Items_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(TotalPoints));
        OnPropertyChanged(nameof(TotalPointsText));
    }
}