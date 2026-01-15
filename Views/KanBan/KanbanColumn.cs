using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using PackageManager.Services;
using PackageManager.Services.PingCode;
using PackageManager.Services.PingCode.Dto;

namespace PackageManager.Views.KanBan;

public class KanbanColumn : INotifyPropertyChanged
{
    private string title;

    private ObservableCollection<WorkItemInfo> items = new();

    public event PropertyChangedEventHandler PropertyChanged;

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

    public int Count => items?.Count ?? 0;

    public double TotalPoints => items?.Sum(i => i?.StoryPoints ?? 0) ?? 0;

    public string TotalPointsText => FormatPoints(TotalPoints);

    public void UpdateCountAndTotalPoints()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(TotalPoints));
        OnPropertyChanged(nameof(TotalPointsText));
    }

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