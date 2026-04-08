namespace ProDataStack.CDP.Segmentation.Api.Models;

public class EstimateRequest
{
    public required string Rules { get; set; }
}

public class ExportRequest
{
    public List<string> Fields { get; set; } = [];
}

public class ExportLogResponse
{
    public Guid Id { get; set; }
    public Guid SegmentId { get; set; }
    public string SegmentName { get; set; } = string.Empty;
    public string Fields { get; set; } = "[]";
    public int RecordCount { get; set; }
    public string? ExportedBy { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
}
