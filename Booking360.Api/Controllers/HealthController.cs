using Microsoft.AspNetCore.Mvc;

namespace Booking360.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        status = "ok",
        service = "booking360-backend",
        timestamp = DateTimeOffset.UtcNow
    });
}