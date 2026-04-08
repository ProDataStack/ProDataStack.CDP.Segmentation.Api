using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ProDataStack.CDP.DataModel.Context;
using ProDataStack.CDP.DataModel.Entities;
using ProDataStack.CDP.Segmentation.Api.Models;
using ProDataStack.CDP.TenantCatalog.Context;
using ProDataStack.CDP.TenantCatalog.Entities;

namespace ProDataStack.CDP.Segmentation.Api.Services;

public class SegmentFieldService
{
    private readonly IDbContextFactory<TenantCatalogDbContext> _catalogFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SegmentFieldService> _logger;

    public SegmentFieldService(
        IDbContextFactory<TenantCatalogDbContext> catalogFactory,
        IMemoryCache cache,
        ILogger<SegmentFieldService> logger)
    {
        _catalogFactory = catalogFactory;
        _cache = cache;
        _logger = logger;
    }

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
            .UseSqlServer(connectionString)
            .Options;

        return new CdpDbContext(options);
    }

    public async Task<List<SegmentFieldResponse>> ListAsync(string orgId, string? search, string? category, bool includeDisabled)
    {
        await using var db = await GetTenantDbAsync(orgId);

        var query = db.SegmentFields.AsNoTracking().AsQueryable();

        if (!includeDisabled)
            query = query.Where(f => f.IsEnabled);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(f => f.Category == category);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(f => f.DisplayName.Contains(search) || f.FieldKey.Contains(search));

        // Smart sort: usage count desc, then sort order
        var fields = await query
            .OrderByDescending(f => f.UsageCount)
            .ThenBy(f => f.SortOrder)
            .ThenBy(f => f.DisplayName)
            .ToListAsync();

        return fields.Select(MapToResponse).ToList();
    }

    public async Task<SegmentFieldResponse?> UpdateAsync(string orgId, Guid fieldId, UpdateSegmentFieldRequest request)
    {
        await using var db = await GetTenantDbAsync(orgId);

        var field = await db.SegmentFields.FindAsync(fieldId);
        if (field == null) return null;

        if (request.DisplayName != null) field.DisplayName = request.DisplayName;
        if (request.IsEnabled.HasValue) field.IsEnabled = request.IsEnabled.Value;
        if (request.SortOrder.HasValue) field.SortOrder = request.SortOrder.Value;
        field.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();
        return MapToResponse(field);
    }

    public async Task<List<CategorySummary>> GetCategoriesAsync(string orgId)
    {
        await using var db = await GetTenantDbAsync(orgId);

        var categories = await db.SegmentFields
            .AsNoTracking()
            .Where(f => f.IsEnabled)
            .GroupBy(f => f.Category)
            .Select(g => new CategorySummary
            {
                Category = g.Key,
                FieldCount = g.Count()
            })
            .ToListAsync();

        // Add display names
        foreach (var c in categories)
            c.DisplayName = GetCategoryDisplayName(c.Category);

        return categories.OrderBy(c => GetCategorySortOrder(c.Category)).ToList();
    }

    public async Task<int> SeedAsync(string orgId)
    {
        await using var db = await GetTenantDbAsync(orgId);

        // Check if already seeded
        var existingCount = await db.SegmentFields.CountAsync();
        if (existingCount > 0)
        {
            _logger.LogInformation("Segment fields already seeded for org {OrgId} ({Count} fields)", orgId, existingCount);
            return existingCount;
        }

        var now = DateTimeOffset.UtcNow;
        var fields = GetSeedFields(now);

        db.SegmentFields.AddRange(fields);
        await db.SaveChangesAsync();

        _logger.LogInformation("Seeded {Count} segment fields for org {OrgId}", fields.Count, orgId);
        return fields.Count;
    }

    // --- Seed Data ---

    private static List<SegmentField> GetSeedFields(DateTimeOffset now)
    {
        var fields = new List<SegmentField>();
        var order = 0;

        // === PRIMARY FIELDS (Profile entity — enabled by default) ===

        // Demographic
        fields.Add(MakeField("profile.emailAddress", "Email Address", "demographic", "string", "text", null, "primary", true, order++, now));
        fields.Add(MakeField("profile.firstName", "First Name", "demographic", "string", "text", null, "primary", true, order++, now));
        fields.Add(MakeField("profile.lastName", "Last Name", "demographic", "string", "text", null, "primary", true, order++, now));
        fields.Add(MakeField("profile.mobileNumber", "Mobile Number", "demographic", "string", "text", null, "primary", true, order++, now));
        fields.Add(MakeField("profile.gender", "Gender", "demographic", "string", "list", null, "primary", true, order++, now, "[\"Male\",\"Female\",\"Other\",\"Prefer not to say\"]"));
        fields.Add(MakeField("profile.dateOfBirth", "Date of Birth", "demographic", "date", "time", null, "primary", true, order++, now));
        fields.Add(MakeField("profile.ethnicity", "Ethnicity", "demographic", "string", "list", null, "primary", true, order++, now));
        fields.Add(MakeField("profile.nationality", "Nationality", "demographic", "string", "list", null, "primary", true, order++, now));
        fields.Add(MakeField("profile.placeOfBirth", "Place of Birth", "demographic", "string", "text", null, "primary", true, order++, now));
        fields.Add(MakeField("profile.city", "City", "demographic", "string", "text", null, "primary", true, order++, now));
        fields.Add(MakeField("profile.region", "Region", "demographic", "string", "list", null, "primary", true, order++, now));
        fields.Add(MakeField("profile.postcode", "Postcode", "demographic", "string", "list", null, "primary", true, order++, now));
        fields.Add(MakeField("profile.country", "Country", "demographic", "string", "list", null, "primary", true, order++, now));
        fields.Add(MakeField("profile.status", "Status", "demographic", "string", "list", null, "primary", true, order++, now));
        fields.Add(MakeField("profile.dateJoin", "Date Joined", "demographic", "date", "time", null, "primary", true, order++, now));
        fields.Add(MakeField("profile.source", "Data Source", "demographic", "string", "list", null, "primary", true, order++, now));

        // Engagement / ESP
        fields.Add(MakeField("profile.marketingOptinStatus", "Marketing Opt-in", "engagement", "boolean", "list", "esp", "primary", true, order++, now, "[\"true\",\"false\"]"));
        fields.Add(MakeField("profile.marketingOptinStatusLastUpdate", "Opt-in Last Updated", "engagement", "date", "time", "esp", "primary", true, order++, now));
        fields.Add(MakeField("profile.avgOpenRate", "Avg Open Rate", "engagement", "number", "comparison", "esp", "primary", true, order++, now));
        fields.Add(MakeField("profile.avgClickRate", "Avg Click Rate", "engagement", "number", "comparison", "esp", "primary", true, order++, now));
        fields.Add(MakeField("profile.emailMarketingScore", "Email Marketing Score", "engagement", "number", "comparison", "esp", "primary", true, order++, now));

        // Online Store aggregates
        fields.Add(MakeField("profile.onlineStoreTotalOrders", "Online Store Total Orders", "onlineStore", "number", "comparison", "onlineStore", "primary", true, order++, now));
        fields.Add(MakeField("profile.onlineStoreTotalValue", "Online Store Total Value", "onlineStore", "number", "comparison", "onlineStore", "primary", true, order++, now));
        fields.Add(MakeField("profile.onlineStoreLastOrderDate", "Online Store Last Order", "onlineStore", "date", "time", "onlineStore", "primary", true, order++, now));

        // Ticketing aggregates
        fields.Add(MakeField("profile.ticketingTotalOrders", "Ticketing Total Orders", "ticketing", "number", "comparison", "ticketing", "primary", true, order++, now));
        fields.Add(MakeField("profile.ticketingTotalValue", "Ticketing Total Value", "ticketing", "number", "comparison", "ticketing", "primary", true, order++, now));
        fields.Add(MakeField("profile.ticketingLastOrderDate", "Ticketing Last Order", "ticketing", "date", "time", "ticketing", "primary", true, order++, now));

        // Computed
        fields.Add(MakeField("profile.ageGroup", "Age Group", "computed", "string", "list", "computed", "primary", true, order++, now, "[\"Under 18\",\"18-24\",\"25-34\",\"35-44\",\"45-54\",\"55-64\",\"65+\"]"));
        fields.Add(MakeField("profile.totalPurchaseValue", "Total Purchase Value", "computed", "number", "comparison", "computed", "primary", true, order++, now));
        fields.Add(MakeField("profile.engagementRate", "Engagement Rate", "computed", "number", "comparison", "computed", "primary", true, order++, now));

        // === SECONDARY FIELDS (cross-entity — disabled by default) ===

        // ESP Integration
        fields.Add(MakeField("espIntegration.platform", "ESP Platform", "engagement", "string", "list", "esp", "secondary", false, order++, now));
        fields.Add(MakeField("espIntegration.consent", "ESP Consent", "engagement", "boolean", "list", "esp", "secondary", false, order++, now, "[\"true\",\"false\"]"));

        // Online Store Transactions
        fields.Add(MakeField("onlineStoreTransaction.orderDate", "Store Order Date", "onlineStore", "date", "time", "onlineStore", "secondary", false, order++, now));
        fields.Add(MakeField("onlineStoreTransaction.totalAmount", "Store Order Amount", "onlineStore", "number", "comparison", "onlineStore", "secondary", false, order++, now));
        fields.Add(MakeField("onlineStoreTransaction.status", "Store Order Status", "onlineStore", "string", "list", "onlineStore", "secondary", false, order++, now));
        fields.Add(MakeField("onlineStoreTransaction.currency", "Store Currency", "onlineStore", "string", "list", "onlineStore", "secondary", false, order++, now));
        fields.Add(MakeField("onlineStoreTransactionDetail.productName", "Product Name", "onlineStore", "string", "text", "onlineStore", "secondary", false, order++, now));
        fields.Add(MakeField("onlineStoreTransactionDetail.price", "Product Price", "onlineStore", "number", "comparison", "onlineStore", "secondary", false, order++, now));

        // Ticketing Events
        fields.Add(MakeField("ticketingEvent.eventName", "Event Name", "ticketing", "string", "text", "ticketing", "secondary", false, order++, now));
        fields.Add(MakeField("ticketingEvent.season", "Season", "ticketing", "string", "list", "ticketing", "secondary", false, order++, now));
        fields.Add(MakeField("ticketingEvent.eventDate", "Event Date", "ticketing", "date", "time", "ticketing", "secondary", false, order++, now));

        // Ticketing Transactions
        fields.Add(MakeField("ticketingTransaction.totalAmount", "Ticket Order Amount", "ticketing", "number", "comparison", "ticketing", "secondary", false, order++, now));
        fields.Add(MakeField("ticketingTransaction.salesChannel", "Sales Channel", "ticketing", "string", "list", "ticketing", "secondary", false, order++, now));
        fields.Add(MakeField("ticketingTransaction.orderStatus", "Ticket Order Status", "ticketing", "string", "list", "ticketing", "secondary", false, order++, now));
        fields.Add(MakeField("ticketingTransaction.orderDate", "Ticket Order Date", "ticketing", "date", "time", "ticketing", "secondary", false, order++, now));

        // Ticketing Transaction Details
        fields.Add(MakeField("ticketingTransactionDetail.seatCategory", "Seat Category", "ticketing", "string", "list", "ticketing", "secondary", false, order++, now));
        fields.Add(MakeField("ticketingTransactionDetail.stand", "Stand", "ticketing", "string", "text", "ticketing", "secondary", false, order++, now));
        fields.Add(MakeField("ticketingTransactionDetail.tariff", "Tariff", "ticketing", "string", "list", "ticketing", "secondary", false, order++, now));

        // COMET Registrations
        fields.Add(MakeField("cometRegistration.discipline", "Discipline", "participation", "string", "list", "comet", "secondary", false, order++, now));
        fields.Add(MakeField("cometRegistration.level", "Level", "participation", "string", "list", "comet", "secondary", false, order++, now));
        fields.Add(MakeField("cometRegistration.registrationCategory", "Registration Category", "participation", "string", "list", "comet", "secondary", false, order++, now));
        fields.Add(MakeField("cometRegistration.registrationType", "Registration Type", "participation", "string", "list", "comet", "secondary", false, order++, now));
        fields.Add(MakeField("cometRegistration.registrationStatus", "Registration Status", "participation", "string", "list", "comet", "secondary", false, order++, now));
        fields.Add(MakeField("cometRegistration.coachQualification", "Coach Qualification", "participation", "string", "list", "comet", "secondary", false, order++, now));
        fields.Add(MakeField("cometRegistration.ageCategory", "COMET Age Category", "participation", "string", "list", "comet", "secondary", false, order++, now));

        // Profile Integrations (secondary)
        fields.Add(MakeField("onlineStoreIntegration.marketingConsent", "Online Store Marketing Consent", "onlineStore", "boolean", "list", "onlineStore", "secondary", false, order++, now, "[\"true\",\"false\"]"));
        fields.Add(MakeField("onlineStoreIntegration.dateAdded", "Online Store Date Added", "onlineStore", "date", "time", "onlineStore", "secondary", false, order++, now));
        fields.Add(MakeField("ticketingIntegration.marketingConsent", "Ticketing Marketing Consent", "ticketing", "boolean", "list", "ticketing", "secondary", false, order++, now, "[\"true\",\"false\"]"));
        fields.Add(MakeField("ticketingIntegration.dateAdded", "Ticketing Date Added", "ticketing", "date", "time", "ticketing", "secondary", false, order++, now));
        fields.Add(MakeField("cometIntegration.consent", "COMET Consent", "participation", "boolean", "list", "comet", "secondary", false, order++, now, "[\"true\",\"false\"]"));
        fields.Add(MakeField("cometIntegration.status", "COMET Status", "participation", "string", "list", "comet", "secondary", false, order++, now));

        return fields;
    }

    private static SegmentField MakeField(string key, string display, string category, string dataType,
        string operatorType, string? source, string priority, bool enabled, int order, DateTimeOffset now, string? enumValues = null)
    {
        return new SegmentField
        {
            FieldKey = key,
            DisplayName = display,
            Category = category,
            DataType = dataType,
            OperatorType = operatorType,
            SourceIntegration = source,
            Priority = priority,
            IsEnabled = enabled,
            SortOrder = order,
            EnumValues = enumValues,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static string GetCategoryDisplayName(string category) => category switch
    {
        "demographic" => "Demographics",
        "engagement" => "Email & Engagement",
        "onlineStore" => "Online Store",
        "ticketing" => "Ticketing",
        "participation" => "Participation",
        "computed" => "Calculated",
        _ => category
    };

    private static int GetCategorySortOrder(string category) => category switch
    {
        "demographic" => 0,
        "engagement" => 1,
        "onlineStore" => 2,
        "ticketing" => 3,
        "participation" => 4,
        "computed" => 5,
        _ => 99
    };

    private static SegmentFieldResponse MapToResponse(SegmentField f) => new()
    {
        Id = f.Id,
        FieldKey = f.FieldKey,
        DisplayName = f.DisplayName,
        Category = f.Category,
        DataType = f.DataType,
        OperatorType = f.OperatorType,
        SourceIntegration = f.SourceIntegration,
        Priority = f.Priority,
        IsEnabled = f.IsEnabled,
        SortOrder = f.SortOrder,
        UsageCount = f.UsageCount,
        EnumValues = f.EnumValues
    };
}
