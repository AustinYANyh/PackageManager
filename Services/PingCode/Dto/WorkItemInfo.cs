using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PackageManager.Services.PingCode.Dto;

/// <summary>
/// 表示工作项的摘要信息，支持属性变更通知。
/// </summary>
public class WorkItemInfo : INotifyPropertyChanged
{
    /// <summary>
    /// 当属性值发生变更时触发。
    /// </summary>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// 获取或设置工作项的唯一标识。
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// 获取或设置工作项状态的唯一标识。
    /// </summary>
    public string StateId { get; set; }

    /// <summary>
    /// 获取或设置工作项所属项目的唯一标识。
    /// </summary>
    public string ProjectId { get; set; }

    /// <summary>
    /// 获取或设置工作项的标识符（如 PROJ-123）。
    /// </summary>
    public string Identifier { get; set; }

    /// <summary>
    /// 获取或设置工作项的标题。
    /// </summary>
    public string Title { get; set; }

    /// <summary>
    /// 获取或设置工作项的状态名称。
    /// </summary>
    public string Status { get; set; }

    /// <summary>
    /// 获取或设置工作项的状态分类（如 未开始、进行中、已完成、已关闭 等）。
    /// </summary>
    public string StateCategory { get; set; }

    /// <summary>
    /// 获取或设置工作项指派人的唯一标识。
    /// </summary>
    public string AssigneeId { get; set; }

    /// <summary>
    /// 获取或设置工作项指派人的名称。
    /// </summary>
    public string AssigneeName { get; set; }

    /// <summary>
    /// 获取或设置工作项指派人的头像 URL。
    /// </summary>
    public string AssigneeAvatar { get; set; }

    /// <summary>
    /// 获取或设置工作项的故事点数。
    /// </summary>
    public double StoryPoints { get; set; }

    /// <summary>
    /// 获取或设置工作项的优先级名称。
    /// </summary>
    public string Priority { get; set; }

    /// <summary>
    /// 获取或设置工作项的严重程度。
    /// </summary>
    public string Severity { get; set; }

    /// <summary>
    /// 获取或设置工作项的类型（如 story、bug、task 等）。
    /// </summary>
    public string Type { get; set; }

    /// <summary>
    /// 获取或设置工作项在 Web 端的访问地址。
    /// </summary>
    public string HtmlUrl { get; set; }

    /// <summary>
    /// 获取或设置工作项的开始时间。
    /// </summary>
    public DateTime? StartAt { get; set; }

    /// <summary>
    /// 获取或设置工作项的结束时间。
    /// </summary>
    public DateTime? EndAt { get; set; }

    /// <summary>
    /// 获取或设置工作项的评论数量。
    /// </summary>
    public int CommentCount { get; set; }

    /// <summary>
    /// 获取或设置工作项的标签名称列表。
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 获取或设置工作项的参与者唯一标识列表。
    /// </summary>
    public List<string> ParticipantIds { get; set; } = new();

    /// <summary>
    /// 获取或设置工作项的参与者名称列表。
    /// </summary>
    public List<string> ParticipantNames { get; set; } = new();

    /// <summary>
    /// 获取或设置工作项的关注者唯一标识列表。
    /// </summary>
    public List<string> WatcherIds { get; set; } = new();

    /// <summary>
    /// 获取或设置工作项的关注者名称列表。
    /// </summary>
    public List<string> WatcherNames { get; set; } = new();

    /// <summary>
    /// 从另一个 <see cref="WorkItemInfo"/> 实例复制所有属性值到当前实例，并触发属性变更通知。
    /// </summary>
    /// <param name="other">要复制数据的源实例。</param>
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

    /// <summary>
    /// 触发指定属性名的 <see cref="PropertyChanged"/> 事件。
    /// </summary>
    /// <param name="name">发生变更的属性名称，默认为调用方成员名称。</param>
    protected void OnPropertyChanged([CallerMemberName] string name = null) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}