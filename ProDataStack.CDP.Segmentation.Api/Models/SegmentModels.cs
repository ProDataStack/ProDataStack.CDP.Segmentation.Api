namespace ProDataStack.CDP.Segmentation.Api.Models;

// --- Request DTOs ---

public class CreateSegmentRequest
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string Category { get; set; }
    public required string Rules { get; set; }
}

public class UpdateSegmentRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? Rules { get; set; }
}

// --- Response DTOs ---

public class SegmentResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Rules { get; set; } = "{}";
    public int Size { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? LastRefreshedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class SegmentListResponse
{
    public List<SegmentResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class SegmentStatsResponse
{
    public int ActiveSegments { get; set; }
    public int ArchivedSegments { get; set; }
    public int TotalProfiles { get; set; }
}
