using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProDataStack.CDP.Segmentation.Api.Models;
using ProDataStack.CDP.Segmentation.Api.Services;

namespace ProDataStack.CDP.Segmentation.Api.Controllers;

[ApiController]
[Route("api/v1")]
public class SegmentationController : ControllerBase
{
    private readonly SegmentationService _segmentationService;
    private readonly SegmentFieldService _fieldService;
    private readonly RuleEvaluationService _ruleService;

    public SegmentationController(
        SegmentationService segmentationService,
        SegmentFieldService fieldService,
        RuleEvaluationService ruleService)
    {
        _segmentationService = segmentationService;
        _fieldService = fieldService;
        _ruleService = ruleService;
    }

    private string? GetOrgId() => User.FindFirst("org_id")?.Value;
    private string? GetUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;

    // --- Health Check ---

    [AllowAnonymous]
    [HttpGet("health-check")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult HealthCheck()
    {
        return Ok(new
        {
            status = "ok",
            service = "CDP Segmentation API",
            version = "2.0",
            environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            deployTag = Environment.GetEnvironmentVariable("DEPLOY_TAG") ?? "local"
        });
    }

    // --- Segments CRUD ---

    [HttpPost("segments")]
    [ProducesResponseType(typeof(SegmentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SegmentResponse>> CreateSegment([FromBody] CreateSegmentRequest request)
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Segment name is required");

        if (!IsValidCategory(request.Category))
            return BadRequest($"Invalid category. Must be one of: {string.Join(", ", ValidCategories)}");

        if (!IsValidJson(request.Rules))
            return BadRequest("Rules must be valid JSON");

        try
        {
            var result = await _segmentationService.CreateAsync(orgId, GetUserId() ?? "unknown", request);
            return CreatedAtAction(nameof(GetSegment), new { id = result.Id }, result);
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("segments")]
    [ProducesResponseType(typeof(SegmentListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SegmentListResponse>> ListSegments(
        [FromQuery] string? search,
        [FromQuery] string? category,
        [FromQuery] bool? archived,
        [FromQuery] string? sort,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        try
        {
            var result = await _segmentationService.ListAsync(orgId, search, category, archived, sort, page, pageSize);
            return Ok(result);
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("segments/{id:guid}")]
    [ProducesResponseType(typeof(SegmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SegmentResponse>> GetSegment(Guid id)
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        try
        {
            var result = await _segmentationService.GetAsync(orgId, id);
            if (result == null) return NotFound();
            return Ok(result);
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPut("segments/{id:guid}")]
    [ProducesResponseType(typeof(SegmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SegmentResponse>> UpdateSegment(Guid id, [FromBody] UpdateSegmentRequest request)
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        if (request.Category != null && !IsValidCategory(request.Category))
            return BadRequest($"Invalid category. Must be one of: {string.Join(", ", ValidCategories)}");

        if (request.Rules != null && !IsValidJson(request.Rules))
            return BadRequest("Rules must be valid JSON");

        try
        {
            var result = await _segmentationService.UpdateAsync(orgId, id, request);
            if (result == null) return NotFound();
            return Ok(result);
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("segments/{id:guid}/archive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> ArchiveSegment(Guid id)
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        try
        {
            var result = await _segmentationService.ArchiveAsync(orgId, id);
            if (!result) return NotFound();
            return NoContent();
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("segments/{id:guid}/restore")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> RestoreSegment(Guid id)
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        try
        {
            var result = await _segmentationService.RestoreAsync(orgId, id);
            if (!result) return NotFound();
            return NoContent();
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("segments/{id:guid}/clone")]
    [ProducesResponseType(typeof(SegmentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SegmentResponse>> CloneSegment(Guid id)
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        try
        {
            var result = await _segmentationService.CloneAsync(orgId, GetUserId() ?? "unknown", id);
            if (result == null) return NotFound();
            return CreatedAtAction(nameof(GetSegment), new { id = result.Id }, result);
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpDelete("segments/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> DeleteSegment(Guid id)
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        try
        {
            var result = await _segmentationService.DeleteAsync(orgId, id);
            if (!result) return NotFound();
            return NoContent();
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("segments/stats")]
    [ProducesResponseType(typeof(SegmentStatsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SegmentStatsResponse>> GetStats()
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        try
        {
            var result = await _segmentationService.GetStatsAsync(orgId);
            return Ok(result);
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    // --- Segment Fields ---

    [HttpGet("segment-fields")]
    [ProducesResponseType(typeof(List<SegmentFieldResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<SegmentFieldResponse>>> ListFields(
        [FromQuery] string? search,
        [FromQuery] string? category,
        [FromQuery] bool includeDisabled = false)
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        try
        {
            var result = await _fieldService.ListAsync(orgId, search, category, includeDisabled);
            return Ok(result);
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPut("segment-fields/{id:guid}")]
    [ProducesResponseType(typeof(SegmentFieldResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SegmentFieldResponse>> UpdateField(Guid id, [FromBody] UpdateSegmentFieldRequest request)
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        try
        {
            var result = await _fieldService.UpdateAsync(orgId, id, request);
            if (result == null) return NotFound();
            return Ok(result);
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("segment-fields/seed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> SeedFields()
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        try
        {
            var count = await _fieldService.SeedAsync(orgId);
            return Ok(new { seeded = count });
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpGet("segment-fields/categories")]
    [ProducesResponseType(typeof(List<CategorySummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<CategorySummary>>> GetCategories()
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        try
        {
            var result = await _fieldService.GetCategoriesAsync(orgId);
            return Ok(result);
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    // --- Rule Evaluation ---

    [HttpPost("segments/{id:guid}/evaluate")]
    [ProducesResponseType(typeof(EvaluationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<EvaluationResult>> EvaluateSegment(Guid id)
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        try
        {
            var result = await _ruleService.EvaluateSegmentAsync(orgId, id);
            return Ok(result);
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("segments/estimate")]
    [ProducesResponseType(typeof(EvaluationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<EvaluationResult>> EstimateSegment([FromBody] EstimateRequest request)
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        if (!IsValidJson(request.Rules))
            return BadRequest("Rules must be valid JSON");

        try
        {
            var result = await _ruleService.EstimateAsync(orgId, request.Rules);
            return Ok(result);
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    // --- Members & Export ---

    [HttpGet("segments/{id:guid}/members")]
    [ProducesResponseType(typeof(MembersResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<MembersResult>> GetMembers(
        Guid id,
        [FromQuery] string? search,
        [FromQuery] string? sort,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 100) pageSize = 100;

        try
        {
            var result = await _ruleService.GetMembersAsync(orgId, id, search, sort, page, pageSize);
            return Ok(result);
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("segments/{id:guid}/export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult> ExportSegment(Guid id, [FromBody] ExportRequest request)
    {
        var orgId = GetOrgId();
        if (string.IsNullOrEmpty(orgId)) return Unauthorized("Missing org_id claim");

        try
        {
            var members = await _ruleService.GetAllMembersForExportAsync(orgId, id);

            // Ensure email is always included
            var fields = request.Fields?.Count > 0 ? request.Fields : ["emailAddress"];
            if (!fields.Contains("emailAddress"))
                fields.Insert(0, "emailAddress");

            // Build CSV
            var csv = new System.Text.StringBuilder();

            // Header
            csv.AppendLine(string.Join(",", fields.Select(QuoteCsvField)));

            // Rows
            foreach (var member in members)
            {
                var values = fields.Select(f => GetMemberFieldValue(member, f));
                csv.AppendLine(string.Join(",", values.Select(QuoteCsvValue)));
            }

            // Log export
            // TODO: Log to ExportLog table in a future commit

            var bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
            return File(bytes, "text/csv", $"segment-export-{id:N}.csv");
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    private static string GetMemberFieldValue(MemberRow member, string field) => field switch
    {
        "emailAddress" => member.EmailAddress ?? "",
        "firstName" => member.FirstName ?? "",
        "lastName" => member.LastName ?? "",
        "mobileNumber" => member.MobileNumber ?? "",
        "gender" => member.Gender ?? "",
        "dateOfBirth" => member.DateOfBirth?.ToString("yyyy-MM-dd") ?? "",
        "postcode" => member.Postcode ?? "",
        "city" => member.City ?? "",
        "country" => member.Country ?? "",
        "onlineStoreTotalValue" => member.OnlineStoreTotalValue?.ToString("F2") ?? "",
        "ticketingTotalValue" => member.TicketingTotalValue?.ToString("F2") ?? "",
        "marketingOptinStatus" => member.MarketingOptinStatus?.ToString() ?? "",
        "source" => member.Source ?? "",
        "dateJoin" => member.DateJoin?.ToString("yyyy-MM-dd") ?? "",
        _ => ""
    };

    private static string QuoteCsvField(string field) => $"\"{field}\"";
    private static string QuoteCsvValue(string value) => value.Contains(',') || value.Contains('"') || value.Contains('\n')
        ? $"\"{value.Replace("\"", "\"\"")}\""
        : value;

    // --- Validation helpers ---

    private static readonly string[] ValidCategories = ["demographic", "engagement", "transactional", "participation", "mixed"];

    private static bool IsValidCategory(string category) => ValidCategories.Contains(category);

    private static bool IsValidJson(string json)
    {
        try
        {
            System.Text.Json.JsonDocument.Parse(json);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
