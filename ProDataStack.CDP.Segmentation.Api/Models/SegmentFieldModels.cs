namespace ProDataStack.CDP.Segmentation.Api.Models;

// --- Response DTOs ---

public class SegmentFieldResponse
{
    public Guid Id { get; set; }
    public string FieldKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string OperatorType { get; set; } = string.Empty;
    public string? SourceIntegration { get; set; }
    public string Priority { get; set; } = "primary";
    public bool IsEnabled { get; set; }
    public int SortOrder { get; set; }
    public int UsageCount { get; set; }
    public string? EnumValues { get; set; }
}

public class UpdateSegmentFieldRequest
{
    public string? DisplayName { get; set; }
    public bool? IsEnabled { get; set; }
    public int? SortOrder { get; set; }
}

public class CategorySummary
{
    public string Category { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int FieldCount { get; set; }
}
