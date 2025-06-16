namespace TradingAlertSystem.Core.Models;

public class PipelineNotice
{
    public string Id { get; set; } = string.Empty;
    public string PipelineName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string NoticeType { get; set; } = string.Empty;
    public DateTime NoticeDate { get; set; }
    public string Location { get; set; } = string.Empty;
    public string? Segment { get; set; }
    public string Description { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public decimal? VolumeImpactMmbtu { get; set; }
    public string? VolumeUnit { get; set; }
    public DateTime ScrapedAt { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> AdditionalProperties { get; set; } = new();

    public bool IsWithinDays(int days)
    {
        return NoticeDate >= DateTime.UtcNow.AddDays(-days);
    }

    public bool ContainsKeyword(params string[] keywords)
    {
        var searchText = $"{Title} {Description} {NoticeType}".ToLowerInvariant();
        return keywords.Any(keyword => searchText.Contains(keyword.ToLowerInvariant()));
    }
}