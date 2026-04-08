using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ProDataStack.CDP.DataModel.Context;
using ProDataStack.CDP.DataModel.Entities;
using ProDataStack.CDP.Segmentation.Api.Models;
using ProDataStack.CDP.TenantCatalog.Context;
using ProDataStack.CDP.TenantCatalog.Entities;

namespace ProDataStack.CDP.Segmentation.Api.Services;

public class RuleEvaluationService
{
    private readonly IDbContextFactory<TenantCatalogDbContext> _catalogFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<RuleEvaluationService> _logger;

    public RuleEvaluationService(
        IDbContextFactory<TenantCatalogDbContext> catalogFactory,
        IMemoryCache cache,
        ILogger<RuleEvaluationService> logger)
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

    /// <summary>Evaluate a segment's rules and return the count + update cached size.</summary>
    public async Task<EvaluationResult> EvaluateSegmentAsync(string orgId, Guid segmentId)
    {
        await using var db = await GetTenantDbAsync(orgId);

        var segment = await db.Segments.FindAsync(segmentId);
        if (segment == null)
            throw new KeyNotFoundException($"Segment {segmentId} not found");

        var result = await EvaluateRulesAsync(db, segment.Rules);

        // Update cached size
        segment.Size = result.Count;
        segment.LastRefreshedAt = DateTimeOffset.UtcNow;
        segment.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return result;
    }

    /// <summary>Evaluate ad-hoc rules (not saved) for the builder's live preview.</summary>
    public async Task<EvaluationResult> EstimateAsync(string orgId, string rulesJson)
    {
        await using var db = await GetTenantDbAsync(orgId);
        return await EvaluateRulesAsync(db, rulesJson);
    }

    /// <summary>Get matching profile IDs for a segment (for member list / export).</summary>
    public async Task<MembersResult> GetMembersAsync(string orgId, Guid segmentId, string? search, string? sort, int page, int pageSize)
    {
        await using var db = await GetTenantDbAsync(orgId);

        var segment = await db.Segments.FindAsync(segmentId);
        if (segment == null)
            throw new KeyNotFoundException($"Segment {segmentId} not found");

        var query = BuildQuery(db, segment.Rules);

        // Search within members
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(p =>
                (p.EmailAddress != null && p.EmailAddress.Contains(search)) ||
                (p.FirstName != null && p.FirstName.Contains(search)) ||
                (p.LastName != null && p.LastName.Contains(search)));
        }

        var totalCount = await query.CountAsync();

        // Sort
        query = sort?.ToLowerInvariant() switch
        {
            "email" => query.OrderBy(p => p.EmailAddress),
            "email_desc" => query.OrderByDescending(p => p.EmailAddress),
            "firstname" => query.OrderBy(p => p.FirstName),
            "firstname_desc" => query.OrderByDescending(p => p.FirstName),
            "lastname" => query.OrderBy(p => p.LastName),
            "lastname_desc" => query.OrderByDescending(p => p.LastName),
            "onlinestoretotalvalue" => query.OrderBy(p => p.OnlineStoreTotalValue),
            "onlinestoretotalvalue_desc" => query.OrderByDescending(p => p.OnlineStoreTotalValue),
            "ticketingtotalvalue" => query.OrderBy(p => p.TicketingTotalValue),
            "ticketingtotalvalue_desc" => query.OrderByDescending(p => p.TicketingTotalValue),
            _ => query.OrderBy(p => p.EmailAddress)
        };

        var profiles = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new MemberRow
            {
                ProfileId = p.Id,
                EmailAddress = p.EmailAddress,
                FirstName = p.FirstName,
                LastName = p.LastName,
                Gender = p.Gender,
                DateOfBirth = p.DateOfBirth,
                City = p.City,
                Country = p.Country,
                Postcode = p.Postcode,
                OnlineStoreTotalValue = p.OnlineStoreTotalValue,
                TicketingTotalValue = p.TicketingTotalValue,
                MarketingOptinStatus = p.MarketingOptinStatus,
                Source = p.Source
            })
            .ToListAsync();

        // Update segment stats
        segment.Size = totalCount;
        segment.LastRefreshedAt = DateTimeOffset.UtcNow;
        segment.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return new MembersResult
        {
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            Members = profiles
        };
    }

    /// <summary>Get all matching profiles for CSV export (streamed).</summary>
    public async Task<List<MemberRow>> GetAllMembersForExportAsync(string orgId, Guid segmentId)
    {
        await using var db = await GetTenantDbAsync(orgId);

        var segment = await db.Segments.FindAsync(segmentId);
        if (segment == null)
            throw new KeyNotFoundException($"Segment {segmentId} not found");

        var query = BuildQuery(db, segment.Rules);

        return await query
            .OrderBy(p => p.EmailAddress)
            .Select(p => new MemberRow
            {
                ProfileId = p.Id,
                EmailAddress = p.EmailAddress,
                FirstName = p.FirstName,
                LastName = p.LastName,
                Gender = p.Gender,
                DateOfBirth = p.DateOfBirth,
                City = p.City,
                Country = p.Country,
                Postcode = p.Postcode,
                MobileNumber = p.MobileNumber,
                OnlineStoreTotalValue = p.OnlineStoreTotalValue,
                TicketingTotalValue = p.TicketingTotalValue,
                MarketingOptinStatus = p.MarketingOptinStatus,
                Source = p.Source,
                DateJoin = p.DateJoin
            })
            .ToListAsync();
    }

    // --- Core Query Builder ---

    private IQueryable<Profile> BuildQuery(CdpDbContext db, string rulesJson)
    {
        var query = db.Profiles.AsNoTracking().AsQueryable();

        RuleSet? ruleSet;
        try
        {
            ruleSet = JsonSerializer.Deserialize<RuleSet>(rulesJson, _jsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid rules JSON");
            return query.Where(p => false); // Invalid rules = no matches
        }

        if (ruleSet?.Groups == null || ruleSet.Groups.Count == 0)
            return query;

        var groupPredicates = new List<Expression<Func<Profile, bool>>>();

        foreach (var group in ruleSet.Groups)
        {
            if (group.Rules == null || group.Rules.Count == 0) continue;

            var rulePredicates = new List<Expression<Func<Profile, bool>>>();

            foreach (var rule in group.Rules)
            {
                var predicate = BuildRulePredicate(db, rule);
                if (predicate != null)
                    rulePredicates.Add(predicate);
            }

            if (rulePredicates.Count == 0) continue;

            // Combine rules within group
            var groupPredicate = group.Logic?.ToUpperInvariant() == "OR"
                ? CombineOr(rulePredicates)
                : CombineAnd(rulePredicates);

            groupPredicates.Add(groupPredicate);
        }

        if (groupPredicates.Count == 0)
            return query;

        // Combine groups
        var finalPredicate = ruleSet.GroupLogic?.ToUpperInvariant() == "OR"
            ? CombineOr(groupPredicates)
            : CombineAnd(groupPredicates);

        return query.Where(finalPredicate);
    }

    private async Task<EvaluationResult> EvaluateRulesAsync(CdpDbContext db, string rulesJson)
    {
        var query = BuildQuery(db, rulesJson);
        var count = await query.CountAsync();
        return new EvaluationResult { Count = count };
    }

    // --- Rule → Predicate ---

    private Expression<Func<Profile, bool>>? BuildRulePredicate(CdpDbContext db, Rule rule)
    {
        if (string.IsNullOrEmpty(rule.Field) || string.IsNullOrEmpty(rule.Operator))
            return null;

        var parts = rule.Field.Split('.', 2);
        if (parts.Length != 2) return null;

        var entity = parts[0];
        var property = parts[1];

        return entity switch
        {
            "profile" => BuildProfilePredicate(property, rule.Operator, rule.Value),
            "espIntegration" => BuildIntegrationPredicate<ProfileEspIntegration>(
                db, p => p.EspIntegrations, property, rule.Operator, rule.Value),
            "onlineStoreIntegration" => BuildIntegrationPredicate<ProfileOnlineStoreIntegration>(
                db, p => p.OnlineStoreIntegrations, property, rule.Operator, rule.Value),
            "ticketingIntegration" => BuildIntegrationPredicate<ProfileTicketingIntegration>(
                db, p => p.TicketingIntegrations, property, rule.Operator, rule.Value),
            "cometIntegration" => BuildIntegrationPredicate<ProfileCometIntegration>(
                db, p => p.CometIntegrations, property, rule.Operator, rule.Value),
            // Cross-entity queries via EXISTS subqueries would be more complex.
            // For v1, handle the most common secondary fields via integration tables.
            _ => null
        };
    }

    private Expression<Func<Profile, bool>>? BuildProfilePredicate(string property, string op, JsonElement? value)
    {
        var param = Expression.Parameter(typeof(Profile), "p");

        // Handle computed fields
        switch (property)
        {
            case "ageGroup":
                return BuildAgeGroupPredicate(op, value);
            case "totalPurchaseValue":
                return BuildTotalPurchaseValuePredicate(op, value);
            case "engagementRate":
                return BuildEngagementRatePredicate(op, value);
        }

        // Map property name to Profile property (camelCase → PascalCase)
        var propName = char.ToUpperInvariant(property[0]) + property[1..];
        var propInfo = typeof(Profile).GetProperty(propName);
        if (propInfo == null) return null;

        var memberExpr = Expression.Property(param, propInfo);
        var propType = propInfo.PropertyType;
        var underlyingType = Nullable.GetUnderlyingType(propType) ?? propType;

        Expression? body = null;

        // Null checks
        if (op == "is_null" || op == "is_not_null")
        {
            if (propType.IsValueType && Nullable.GetUnderlyingType(propType) == null)
                return null; // Can't check null on non-nullable

            var nullCheck = Expression.Equal(memberExpr, Expression.Constant(null, propType));
            body = op == "is_null" ? nullCheck : Expression.Not(nullCheck);
            return Expression.Lambda<Func<Profile, bool>>(body, param);
        }

        if (op == "is_empty" || op == "is_not_empty")
        {
            if (underlyingType != typeof(string)) return null;
            var emptyCheck = Expression.OrElse(
                Expression.Equal(memberExpr, Expression.Constant(null, propType)),
                Expression.Equal(memberExpr, Expression.Constant("")));
            body = op == "is_empty" ? emptyCheck : Expression.Not(emptyCheck);
            return Expression.Lambda<Func<Profile, bool>>(body, param);
        }

        if (value == null) return null;

        // String operations
        if (underlyingType == typeof(string))
        {
            body = BuildStringExpression(memberExpr, op, value.Value, param);
        }
        // Numeric operations
        else if (underlyingType == typeof(int) || underlyingType == typeof(decimal))
        {
            body = BuildNumericExpression(memberExpr, propType, underlyingType, op, value.Value);
        }
        // Boolean operations
        else if (underlyingType == typeof(bool))
        {
            body = BuildBooleanExpression(memberExpr, propType, op, value.Value);
        }
        // Date operations
        else if (underlyingType == typeof(DateTimeOffset))
        {
            body = BuildDateExpression(memberExpr, propType, op, value.Value);
        }

        if (body == null) return null;
        return Expression.Lambda<Func<Profile, bool>>(body, param);
    }

    // --- String expressions ---
    private Expression? BuildStringExpression(Expression member, string op, JsonElement value, ParameterExpression param)
    {
        var strValue = GetStringValue(value);
        if (strValue == null && op != "in_list" && op != "not_in_list") return null;

        return op switch
        {
            "equals" => Expression.Equal(member, Expression.Constant(strValue, typeof(string))),
            "not_equals" => Expression.NotEqual(member, Expression.Constant(strValue, typeof(string))),
            "contains" => CallStringMethod(member, "Contains", strValue!),
            "not_contains" => Expression.Not(CallStringMethod(member, "Contains", strValue!)),
            "starts_with" => CallStringMethod(member, "StartsWith", strValue!),
            "ends_with" => CallStringMethod(member, "EndsWith", strValue!),
            "in_list" => BuildInListExpression(member, value),
            "not_in_list" => Expression.Not(BuildInListExpression(member, value)!),
            _ => null
        };
    }

    private static Expression CallStringMethod(Expression member, string methodName, string value)
    {
        // Handle nullable: coalesce to empty string
        Expression target = member.Type == typeof(string)
            ? Expression.Coalesce(member, Expression.Constant(""))
            : member;

        var method = typeof(string).GetMethod(methodName, [typeof(string)])!;
        return Expression.Call(target, method, Expression.Constant(value));
    }

    private Expression? BuildInListExpression(Expression member, JsonElement value)
    {
        var values = GetStringListValue(value);
        if (values == null || values.Count == 0) return null;

        var containsMethod = typeof(List<string>).GetMethod("Contains", [typeof(string)])!;
        // Coalesce nullable to empty string
        Expression target = member.Type == typeof(string)
            ? Expression.Coalesce(member, Expression.Constant(""))
            : member;
        return Expression.Call(Expression.Constant(values), containsMethod, target);
    }

    // --- Numeric expressions ---
    private Expression? BuildNumericExpression(Expression member, Type propType, Type underlyingType, string op, JsonElement value)
    {
        Expression nonNullMember = member;
        if (propType != underlyingType) // nullable
            nonNullMember = Expression.Property(member, "Value");

        if (underlyingType == typeof(decimal))
        {
            if (!TryGetDecimal(value, out var numValue)) return null;
            var constant = Expression.Constant(numValue);

            var hasValue = propType != underlyingType
                ? Expression.Property(member, "HasValue")
                : (Expression)Expression.Constant(true);

            Expression comparison = op switch
            {
                "equals" => Expression.Equal(nonNullMember, constant),
                "not_equals" => Expression.NotEqual(nonNullMember, constant),
                "greater_than" => Expression.GreaterThan(nonNullMember, constant),
                "less_than" => Expression.LessThan(nonNullMember, constant),
                "greater_equal" => Expression.GreaterThanOrEqual(nonNullMember, constant),
                "less_equal" => Expression.LessThanOrEqual(nonNullMember, constant),
                _ => null!
            };

            if (comparison == null) return null;
            return propType != underlyingType
                ? Expression.AndAlso(hasValue, comparison)
                : comparison;
        }
        else // int
        {
            if (!TryGetInt(value, out var numValue)) return null;
            var constant = Expression.Constant(numValue);

            var hasValue = propType != underlyingType
                ? Expression.Property(member, "HasValue")
                : (Expression)Expression.Constant(true);

            Expression comparison = op switch
            {
                "equals" => Expression.Equal(nonNullMember, constant),
                "not_equals" => Expression.NotEqual(nonNullMember, constant),
                "greater_than" => Expression.GreaterThan(nonNullMember, constant),
                "less_than" => Expression.LessThan(nonNullMember, constant),
                "greater_equal" => Expression.GreaterThanOrEqual(nonNullMember, constant),
                "less_equal" => Expression.LessThanOrEqual(nonNullMember, constant),
                _ => null!
            };

            if (comparison == null) return null;
            return propType != underlyingType
                ? Expression.AndAlso(hasValue, comparison)
                : comparison;
        }
    }

    // --- Boolean expressions ---
    private Expression? BuildBooleanExpression(Expression member, Type propType, string op, JsonElement value)
    {
        var boolValue = GetBoolValue(value);
        if (boolValue == null) return null;

        Expression nonNullMember = member;
        if (Nullable.GetUnderlyingType(propType) != null)
            nonNullMember = Expression.Property(member, "Value");

        var constant = Expression.Constant(boolValue.Value);

        Expression comparison = op switch
        {
            "equals" or "in_list" => Expression.Equal(nonNullMember, constant),
            "not_equals" or "not_in_list" => Expression.NotEqual(nonNullMember, constant),
            _ => null!
        };

        if (comparison == null) return null;

        if (Nullable.GetUnderlyingType(propType) != null)
            return Expression.AndAlso(Expression.Property(member, "HasValue"), comparison);

        return comparison;
    }

    // --- Date expressions ---
    private Expression? BuildDateExpression(Expression member, Type propType, string op, JsonElement value)
    {
        Expression nonNullMember = member;
        var isNullable = Nullable.GetUnderlyingType(propType) != null;
        if (isNullable)
            nonNullMember = Expression.Property(member, "Value");

        Expression? comparison = null;

        if (op == "within_last_days" || op == "more_than_days_ago")
        {
            if (!TryGetInt(value, out var days)) return null;
            var threshold = DateTimeOffset.UtcNow.AddDays(-days);
            var constant = Expression.Constant(threshold);

            comparison = op == "within_last_days"
                ? Expression.GreaterThanOrEqual(nonNullMember, constant)
                : Expression.LessThan(nonNullMember, constant);
        }
        else
        {
            if (!TryGetDateTimeOffset(value, out var dateValue)) return null;
            var constant = Expression.Constant(dateValue);

            comparison = op switch
            {
                "before" => Expression.LessThan(nonNullMember, constant),
                "after" => Expression.GreaterThan(nonNullMember, constant),
                "equals" => Expression.Equal(Expression.Property(nonNullMember, "Date"), Expression.Property(constant, "Date")),
                _ => null
            };
        }

        if (comparison == null) return null;

        if (isNullable)
            return Expression.AndAlso(Expression.Property(member, "HasValue"), comparison);

        return comparison;
    }

    // --- Computed fields ---

    private Expression<Func<Profile, bool>>? BuildAgeGroupPredicate(string op, JsonElement? value)
    {
        if (value == null) return null;
        var targetGroups = GetStringListValue(value.Value);
        if (targetGroups == null || targetGroups.Count == 0) return null;

        // Build predicate that checks DateOfBirth against age ranges
        var now = DateTimeOffset.UtcNow;
        Expression<Func<Profile, bool>>? result = null;

        foreach (var group in targetGroups)
        {
            var (minAge, maxAge) = ParseAgeGroup(group);
            if (minAge < 0) continue;

            var maxDate = now.AddYears(-minAge);   // youngest in group
            var minDate = maxAge == int.MaxValue
                ? DateTimeOffset.MinValue
                : now.AddYears(-(maxAge + 1));     // oldest in group

            Expression<Func<Profile, bool>> groupPred = p =>
                p.DateOfBirth != null &&
                p.DateOfBirth.Value >= minDate &&
                p.DateOfBirth.Value <= maxDate;

            result = result == null ? groupPred : CombineOr([result, groupPred]);
        }

        if (result == null) return null;

        return op == "not_in_list" ? Negate(result) : result;
    }

    private Expression<Func<Profile, bool>>? BuildTotalPurchaseValuePredicate(string op, JsonElement? value)
    {
        if (value == null || !TryGetDecimal(value.Value, out var target)) return null;

        return op switch
        {
            "equals" => p => (p.OnlineStoreTotalValue ?? 0) + (p.TicketingTotalValue ?? 0) == target,
            "not_equals" => p => (p.OnlineStoreTotalValue ?? 0) + (p.TicketingTotalValue ?? 0) != target,
            "greater_than" => p => (p.OnlineStoreTotalValue ?? 0) + (p.TicketingTotalValue ?? 0) > target,
            "less_than" => p => (p.OnlineStoreTotalValue ?? 0) + (p.TicketingTotalValue ?? 0) < target,
            "greater_equal" => p => (p.OnlineStoreTotalValue ?? 0) + (p.TicketingTotalValue ?? 0) >= target,
            "less_equal" => p => (p.OnlineStoreTotalValue ?? 0) + (p.TicketingTotalValue ?? 0) <= target,
            _ => null
        };
    }

    private Expression<Func<Profile, bool>>? BuildEngagementRatePredicate(string op, JsonElement? value)
    {
        if (value == null || !TryGetDecimal(value.Value, out var target)) return null;

        return op switch
        {
            "equals" => p => ((p.AvgOpenRate ?? 0) + (p.AvgClickRate ?? 0)) / 2m == target,
            "not_equals" => p => ((p.AvgOpenRate ?? 0) + (p.AvgClickRate ?? 0)) / 2m != target,
            "greater_than" => p => ((p.AvgOpenRate ?? 0) + (p.AvgClickRate ?? 0)) / 2m > target,
            "less_than" => p => ((p.AvgOpenRate ?? 0) + (p.AvgClickRate ?? 0)) / 2m < target,
            "greater_equal" => p => ((p.AvgOpenRate ?? 0) + (p.AvgClickRate ?? 0)) / 2m >= target,
            "less_equal" => p => ((p.AvgOpenRate ?? 0) + (p.AvgClickRate ?? 0)) / 2m <= target,
            _ => null
        };
    }

    // --- Integration (secondary) field predicates ---

    private Expression<Func<Profile, bool>>? BuildIntegrationPredicate<TIntegration>(
        CdpDbContext db,
        Expression<Func<Profile, IEnumerable<TIntegration>>> navigationExpr,
        string property, string op, JsonElement? value)
        where TIntegration : class
    {
        // For integration fields, check if profile has ANY matching integration record
        var propName = char.ToUpperInvariant(property[0]) + property[1..];
        var propInfo = typeof(TIntegration).GetProperty(propName);
        if (propInfo == null) return null;

        // Build: p => p.Integrations.Any(i => <condition on i.Property>)
        var profileParam = Expression.Parameter(typeof(Profile), "p");
        var integrationParam = Expression.Parameter(typeof(TIntegration), "i");

        var integrationMember = Expression.Property(integrationParam, propInfo);
        var propType = propInfo.PropertyType;
        var underlyingType = Nullable.GetUnderlyingType(propType) ?? propType;

        Expression? condition = null;

        if (value == null && op != "is_null" && op != "is_not_null") return null;

        if (op == "is_null" || op == "is_not_null")
        {
            if (propType.IsValueType && Nullable.GetUnderlyingType(propType) == null)
                return null;
            var nullCheck = Expression.Equal(integrationMember, Expression.Constant(null, propType));
            condition = op == "is_null" ? nullCheck : Expression.Not(nullCheck);
        }
        else if (underlyingType == typeof(string))
        {
            condition = BuildStringExpression(integrationMember, op, value!.Value, integrationParam);
        }
        else if (underlyingType == typeof(bool))
        {
            condition = BuildBooleanExpression(integrationMember, propType, op, value!.Value);
        }
        else if (underlyingType == typeof(DateTimeOffset))
        {
            condition = BuildDateExpression(integrationMember, propType, op, value!.Value);
        }

        if (condition == null) return null;

        var anyLambda = Expression.Lambda<Func<TIntegration, bool>>(condition, integrationParam);

        // p => p.Navigation.Any(i => condition)
        var navigation = Expression.Invoke(navigationExpr, profileParam);
        var anyMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == "Any" && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(TIntegration));

        var anyCall = Expression.Call(anyMethod, navigation, anyLambda);
        return Expression.Lambda<Func<Profile, bool>>(anyCall, profileParam);
    }

    // --- Expression combinators ---

    private static Expression<Func<Profile, bool>> CombineAnd(List<Expression<Func<Profile, bool>>> predicates)
    {
        if (predicates.Count == 1) return predicates[0];

        var param = Expression.Parameter(typeof(Profile), "p");
        Expression body = Expression.Invoke(predicates[0], param);

        for (int i = 1; i < predicates.Count; i++)
            body = Expression.AndAlso(body, Expression.Invoke(predicates[i], param));

        return Expression.Lambda<Func<Profile, bool>>(body, param);
    }

    private static Expression<Func<Profile, bool>> CombineOr(List<Expression<Func<Profile, bool>>> predicates)
    {
        if (predicates.Count == 1) return predicates[0];

        var param = Expression.Parameter(typeof(Profile), "p");
        Expression body = Expression.Invoke(predicates[0], param);

        for (int i = 1; i < predicates.Count; i++)
            body = Expression.OrElse(body, Expression.Invoke(predicates[i], param));

        return Expression.Lambda<Func<Profile, bool>>(body, param);
    }

    private static Expression<Func<Profile, bool>> Negate(Expression<Func<Profile, bool>> predicate)
    {
        var param = Expression.Parameter(typeof(Profile), "p");
        var body = Expression.Not(Expression.Invoke(predicate, param));
        return Expression.Lambda<Func<Profile, bool>>(body, param);
    }

    // --- Value parsing helpers ---

    private static string? GetStringValue(JsonElement el)
    {
        return el.ValueKind == JsonValueKind.String ? el.GetString() :
               el.ValueKind == JsonValueKind.Number ? el.GetRawText() :
               el.ToString();
    }

    private static List<string>? GetStringListValue(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array)
            return el.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        if (el.ValueKind == JsonValueKind.String)
            return [el.GetString()!];
        return null;
    }

    private static bool TryGetDecimal(JsonElement el, out decimal result)
    {
        if (el.ValueKind == JsonValueKind.Number)
            return el.TryGetDecimal(out result);
        if (el.ValueKind == JsonValueKind.String && decimal.TryParse(el.GetString(), out result))
            return true;
        result = 0;
        return false;
    }

    private static bool TryGetInt(JsonElement el, out int result)
    {
        if (el.ValueKind == JsonValueKind.Number)
            return el.TryGetInt32(out result);
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out result))
            return true;
        result = 0;
        return false;
    }

    private static bool? GetBoolValue(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString()?.ToLowerInvariant();
            if (s == "true") return true;
            if (s == "false") return false;
        }
        return null;
    }

    private static bool TryGetDateTimeOffset(JsonElement el, out DateTimeOffset result)
    {
        if (el.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(el.GetString(), out result))
            return true;
        result = default;
        return false;
    }

    private static (int MinAge, int MaxAge) ParseAgeGroup(string group) => group switch
    {
        "Under 18" => (0, 17),
        "18-24" => (18, 24),
        "25-34" => (25, 34),
        "35-44" => (35, 44),
        "45-54" => (45, 54),
        "55-64" => (55, 64),
        "65+" => (65, int.MaxValue),
        _ => (-1, -1)
    };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

// --- Rule JSON Model ---

public class RuleSet
{
    public List<RuleGroup>? Groups { get; set; }
    public string? GroupLogic { get; set; }
}

public class RuleGroup
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Logic { get; set; }
    public List<Rule>? Rules { get; set; }
}

public class Rule
{
    public string? Field { get; set; }
    public string? Operator { get; set; }
    public JsonElement? Value { get; set; }
    public string? DataType { get; set; }
}

// --- Result models ---

public class EvaluationResult
{
    public int Count { get; set; }
}

public class MembersResult
{
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<MemberRow> Members { get; set; } = [];
}

public class MemberRow
{
    public Guid ProfileId { get; set; }
    public string? EmailAddress { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Gender { get; set; }
    public DateTimeOffset? DateOfBirth { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? Postcode { get; set; }
    public string? MobileNumber { get; set; }
    public decimal? OnlineStoreTotalValue { get; set; }
    public decimal? TicketingTotalValue { get; set; }
    public bool? MarketingOptinStatus { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset? DateJoin { get; set; }
}
