using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PackageManager.Services.PingCode.Dto;

public class WorkItemInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    public string Id { get; set; }

    public string StateId { get; set; }

    public string ProjectId { get; set; }

    public string Identifier { get; set; }

    public string Title { get; set; }

    public string Status { get; set; }

    public string StateCategory { get; set; }

    public string AssigneeId { get; set; }

    public string AssigneeName { get; set; }

    public string AssigneeAvatar { get; set; }

    public double StoryPoints { get; set; }

    public string Priority { get; set; }

    public string Severity { get; set; }

    public string Type { get; set; }

    public string HtmlUrl { get; set; }

    public DateTime? StartAt { get; set; }

    public DateTime? EndAt { get; set; }

    public int CommentCount { get; set; }

    public List<string> Tags { get; set; } = new();

    public List<string> ParticipantIds { get; set; } = new();

    public List<string> ParticipantNames { get; set; } = new();

    public List<string> WatcherIds { get; set; } = new();

    public List<string> WatcherNames { get; set; } = new();

    public void UpdateFrom(WorkItemInfo other)
    {
        if (other == null)
        {
            return;
        }

        Id = other.Id;
        StateId = other.StateId;
        ProjectId = other.ProjectId;
        Identifier = other.Identifier;
        Title = other.Title;
        Status = other.Status;
        StateCategory = other.StateCategory;
        AssigneeId = other.AssigneeId;
        AssigneeName = other.AssigneeName;
        AssigneeAvatar = other.AssigneeAvatar;
        StoryPoints = other.StoryPoints;
        Priority = other.Priority;
        Severity = other.Severity;
        Type = other.Type;
        HtmlUrl = other.HtmlUrl;
        StartAt = other.StartAt;
        EndAt = other.EndAt;
        CommentCount = other.CommentCount;
        Tags = new List<string>(other.Tags ?? new List<string>());
        ParticipantIds = new List<string>(other.ParticipantIds ?? new List<string>());
        ParticipantNames = new List<string>(other.ParticipantNames ?? new List<string>());
        WatcherIds = new List<string>(other.WatcherIds ?? new List<string>());
        WatcherNames = new List<string>(other.WatcherNames ?? new List<string>());
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(StateId));
        OnPropertyChanged(nameof(ProjectId));
        OnPropertyChanged(nameof(Identifier));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StateCategory));
        OnPropertyChanged(nameof(AssigneeId));
        OnPropertyChanged(nameof(AssigneeName));
        OnPropertyChanged(nameof(AssigneeAvatar));
        OnPropertyChanged(nameof(StoryPoints));
        OnPropertyChanged(nameof(Priority));
        OnPropertyChanged(nameof(Severity));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(HtmlUrl));
        OnPropertyChanged(nameof(StartAt));
        OnPropertyChanged(nameof(EndAt));
        OnPropertyChanged(nameof(CommentCount));
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(ParticipantIds));
        OnPropertyChanged(nameof(ParticipantNames));
        OnPropertyChanged(nameof(WatcherIds));
        OnPropertyChanged(nameof(WatcherNames));
    }

    protected void OnPropertyChanged([CallerMemberName] string name = null) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}