using System;
using System.Collections.Generic;

namespace PackageManager.Services.PingCode.Model;

public class WorkItemDetails
{
    public string Id { get; set; }

    public string Identifier { get; set; }

    public string Title { get; set; }

    public string HtmlUrl { get; set; }

    public string Type { get; set; }

    public string ProjectId { get; set; }

    public string ParentId { get; set; }

    public string ParentIdentifier { get; set; }

    public string ParentTitle { get; set; }

    public string AssigneeId { get; set; }

    public string AssigneeName { get; set; }

    public string StateName { get; set; }

    public string StateType { get; set; }

    public string StateId { get; set; }

    public string PriorityName { get; set; }

    public string SeverityName { get; set; }

    public double StoryPoints { get; set; }

    public double StoryPointsSummary { get; set; }

    public string VersionName { get; set; }

    public DateTime? StartAt { get; set; }

    public DateTime? EndAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string ProductName { get; set; }

    public string ReproduceVersion { get; set; }

    public string ReproduceProbability { get; set; }

    public string DefectCategory { get; set; }

    public List<string> Tags { get; set; } = new();

    public string SketchHtml { get; set; }

    public string DescriptionHtml { get; set; }

    public string ExpectedResult { get; set; }

    public List<WorkItemComment> Comments { get; set; } = new();

    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string PublicImageToken { get; set; }
}