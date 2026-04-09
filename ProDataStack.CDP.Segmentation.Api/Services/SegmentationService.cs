using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ProDataStack.CDP.DataModel.Context;
using ProDataStack.CDP.DataModel.Entities;
using ProDataStack.CDP.Segmentation.Api.Models;
using ProDataStack.CDP.TenantCatalog.Context;
using ProDataStack.CDP.TenantCatalog.Entities;

namespace ProDataStack.CDP.Segmentation.Api.Services;

public class SegmentationService
{
    private readonly IDbContextFactory<TenantCatalogDbContext> _catalogFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SegmentationService> _logger;

    public SegmentationService(
        IDbContextFactory<TenantCatalogDbContext> catalogFactory,
        IMemoryCache cache,
        ILogger<SegmentationService> logger)
    {
        _catalogFactory = catalogFactory;
        _cache = cache;
        _logger = logger;
    }

    // --- Tenant Resolution (same pattern as Import API) ---

    private async Task<CdpDbContext> GetTenantDbAsync(string orgId)
    {
        var cacheKey = $"tenant-conn:{orgId}";
        if (!_cache.TryGetValue<string>(cacheKey, out var connectionString) || connectionString == null)
        {
            await using var catalog = await _catalogFactory.CreateDbContextAsync();
            var tenant = await catalog.Tenants.AsNoTracking()
                .FirstOrDefaultAsync(t => t.ClerkOrganizationId == orgId);

            if (tenant == null)
                throw new KeyNotFoundException($"Organization {orgId} not found in tenant catalog");

            if (tenant.ProvisioningStatus != ProvisioningStatus.Ready)
                throw new InvalidOperationException($"Organization is not ready. Status: {tenant.ProvisioningStatus}");

            connectionString = tenant.ConnectionString
                ?? throw new InvalidOperationException("Tenant has no connection string configured");

            _cache.Set(cacheKey, connectionString, TimeSpan.FromMinutes(5));
        }

        var options = new DbContextOptionsBuilder<CdpDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                sql.CommandTimeout(120);
                sql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null);
            })
            .Options;

        return new CdpDbContext(options);
    }

    // --- Segment CRUD ---

    public async Task<SegmentResponse> CreateAsync(string orgId, string userId, CreateSegmentRequest request)
    {
        await using var db = await GetTenantDbAsync(orgId);

        var segment = new Segment
        {
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            Rules = request.Rules,
            CreatedBy = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.Segments.Add(segment);
        await db.SaveChangesAsync();

        // Increment usage counts for fields referenced in rules
        await IncrementFieldUsageAsync(db, request.Rules);

        _logger.LogInformation("Segment {SegmentId} created by {UserId} in org {OrgId}", segment.Id, userId, orgId);
        return MapToResponse(segment);
    }

    public async Task<SegmentListResponse> ListAsync(string orgId, string? search, string? category, bool? archived, string? sort, int page, int pageSize)
    {
        await using var db = await GetTenantDbAsync(orgId);

        var query = db.Segments.AsNoTracking().AsQueryable();

        // Filter by archived status
        if (archived.HasValue)
            query = query.Where(s => s.IsArchived == archived.Value);

        // Filter by category
        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(s => s.Category == category);

        // Search by name
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(s => s.Name.Contains(search));

        var totalCount = await query.CountAsync();

        // Sort
        query = sort?.ToLowerInvariant() switch
        {
            "name" => query.OrderBy(s => s.Name),
            "name_desc" => query.OrderByDescending(s => s.Name),
            "size" => query.OrderBy(s => s.Size),
            "size_desc" => query.OrderByDescending(s => s.Size),
            "created" => query.OrderBy(s => s.CreatedAt),
            "created_desc" => query.OrderByDescending(s => s.CreatedAt),
            "category" => query.OrderBy(s => s.Category),
            _ => query.OrderByDescending(s => s.LastUsedAt ?? s.CreatedAt)
        };

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new SegmentListResponse
        {
            Items = items.Select(MapToResponse).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<SegmentResponse?> GetAsync(string orgId, Guid segmentId)
    {
        await using var db = await GetTenantDbAsync(orgId);

        var segment = await db.Segments.FindAsync(segmentId);
        if (segment == null) return null;

        // Update LastUsedAt
        segment.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return MapToResponse(segment);
    }

    public async Task<SegmentResponse?> UpdateAsync(string orgId, Guid segmentId, UpdateSegmentRequest request)
    {
        await using var db = await GetTenantDbAsync(orgId);

        var segment = await db.Segments.FindAsync(segmentId);
        if (segment == null) return null;

        if (request.Name != null) segment.Name = request.Name;
        if (request.Description != null) segment.Description = request.Description;
        if (request.Category != null) segment.Category = request.Category;
        if (request.Rules != null) segment.Rules = request.Rules;
        segment.UpdatedAt = DateTimeOffset.UtcNow;
        segment.LastUsedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();

        // Increment usage counts if rules changed
        if (request.Rules != null)
            await IncrementFieldUsageAsync(db, request.Rules);

        return MapToResponse(segment);
    }

    public async Task<bool> ArchiveAsync(string orgId, Guid segmentId)
    {
        await using var db = await GetTenantDbAsync(orgId);

        var segment = await db.Segments.FindAsync(segmentId);
        if (segment == null) return false;

        segment.IsArchived = true;
        segment.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RestoreAsync(string orgId, Guid segmentId)
    {
        await using var db = await GetTenantDbAsync(orgId);

        var segment = await db.Segments.FindAsync(segmentId);
        if (segment == null) return false;

        segment.IsArchived = false;
        segment.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<SegmentResponse?> CloneAsync(string orgId, string userId, Guid segmentId)
    {
        await using var db = await GetTenantDbAsync(orgId);

        var original = await db.Segments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == segmentId);
        if (original == null) return null;

        var clone = new Segment
        {
            Name = original.Name + " (Copy)",
            Description = original.Description,
            Category = original.Category,
            Rules = original.Rules,
            CreatedBy = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.Segments.Add(clone);
        await db.SaveChangesAsync();

        _logger.LogInformation("Segment {OriginalId} cloned to {CloneId} by {UserId}", segmentId, clone.Id, userId);
        return MapToResponse(clone);
    }

    public async Task<bool> DeleteAsync(string orgId, Guid segmentId)
    {
        await using var db = await GetTenantDbAsync(orgId);

        var segment = await db.Segments.FindAsync(segmentId);
        if (segment == null) return false;

        // Soft delete = archive
        segment.IsArchived = true;
        segment.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<SegmentStatsResponse> GetStatsAsync(string orgId)
    {
        await using var db = await GetTenantDbAsync(orgId);

        var activeCount = await db.Segments.CountAsync(s => !s.IsArchived);
        var archivedCount = await db.Segments.CountAsync(s => s.IsArchived);
        var totalProfiles = await db.Profiles.CountAsync();

        return new SegmentStatsResponse
        {
            ActiveSegments = activeCount,
            ArchivedSegments = archivedCount,
            TotalProfiles = totalProfiles
        };
    }

    // --- Helpers ---

    private async Task IncrementFieldUsageAsync(CdpDbContext db, string rulesJson)
    {
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(rulesJson);
            var fieldKeys = new HashSet<string>();

            if (doc.RootElement.TryGetProperty("groups", out var groups))
            {
                foreach (var group in groups.EnumerateArray())
                {
                    if (group.TryGetProperty("rules", out var rules))
                    {
                        foreach (var rule in rules.EnumerateArray())
                        {
                            if (rule.TryGetProperty("field", out var field))
                                fieldKeys.Add(field.GetString()!);
                        }
                    }
                }
            }

            if (fieldKeys.Count > 0)
            {
                var fields = await db.SegmentFields
                    .Where(f => fieldKeys.Contains(f.FieldKey))
                    .ToListAsync();

                foreach (var f in fields)
                    f.UsageCount++;

                await db.SaveChangesAsync();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Invalid JSON — skip usage tracking, validation happens elsewhere
        }
    }

    private static SegmentResponse MapToResponse(Segment s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Description = s.Description,
        Category = s.Category,
        Rules = s.Rules,
        Size = s.Size,
        IsArchived = s.IsArchived,
        LastUsedAt = s.LastUsedAt,
        LastRefreshedAt = s.LastRefreshedAt,
        CreatedBy = s.CreatedBy,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt
    };
}
