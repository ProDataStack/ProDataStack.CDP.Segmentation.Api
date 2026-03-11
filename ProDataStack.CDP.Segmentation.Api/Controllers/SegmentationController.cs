using Microsoft.AspNetCore.Mvc;

namespace ProDataStack.CDP.Segmentation.Api.Controllers
{
    [ApiController]
    [Route("api/v1/[controller]")]
    public class SegmentationController : ControllerBase
    {
        [HttpGet("/api/v1/health-check")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult HealthCheck()
        {
            return Ok(new { status = "ok", service = "CDP Segmentation API" });
        }

        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult GetSegments()
        {
            var segments = new[]
            {
                new { id = 1, name = "High-Value Fans", description = "Fans with high engagement and spending", memberCount = 12500 },
                new { id = 2, name = "Season Ticket Holders", description = "Current season ticket holders", memberCount = 8300 },
                new { id = 3, name = "Lapsed Members", description = "Members who haven't renewed in 12+ months", memberCount = 4200 }
            };

            return Ok(segments);
        }

        [HttpGet("{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult GetSegment(int id)
        {
            var segments = new Dictionary<int, object>
            {
                { 1, new { id = 1, name = "High-Value Fans", description = "Fans with high engagement and spending", memberCount = 12500 } },
                { 2, new { id = 2, name = "Season Ticket Holders", description = "Current season ticket holders", memberCount = 8300 } },
                { 3, new { id = 3, name = "Lapsed Members", description = "Members who haven't renewed in 12+ months", memberCount = 4200 } }
            };

            if (!segments.TryGetValue(id, out var segment))
                return NotFound();

            return Ok(segment);
        }
    }
}
