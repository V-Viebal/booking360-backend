using Booking360.Api.Abstractions;
using Booking360.Api.Infrastructure;

namespace Booking360.Api.Features.Foundation;

/// <summary>
/// Wave 4: gated probe endpoint to force-run the scheduler tick on demand.
/// Disabled unless BOOKING360_SCHEDULER_PROBE_TOKEN is set; caller must pass that token via X-Scheduler-Token.
/// Used by W4 acceptance tests + production smoke probes; not part of the public API surface.
/// </summary>
public sealed class SchedulerProbeEndpoint : IEndpoint
{
    public sealed record ProbeRequest(bool ForceDailyReset, DateTimeOffset? AtUtc);

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("/api/internal/scheduler")
            .AllowAnonymous()
            .WithTags("Internal");

        group.MapPost("/run-once", async (
            HttpContext http,
            ProbeRequest? request,
            SchedulerJobs jobs,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var expected = configuration["BOOKING360_SCHEDULER_PROBE_TOKEN"]?.Trim();
            if (string.IsNullOrEmpty(expected))
            {
                return Results.NotFound();
            }
            var supplied = http.Request.Headers["X-Scheduler-Token"].ToString().Trim();
            if (!string.Equals(supplied, expected, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }

            var now = request?.AtUtc ?? DateTimeOffset.UtcNow;
            var summary = await jobs.RunOnceAsync(now, cancellationToken);
            int? forcedRows = null;
            if (request?.ForceDailyReset == true)
            {
                forcedRows = await jobs.ForceDailyResetAsync(cancellationToken);
            }
            return Results.Ok(new
            {
                at = summary.At,
                remindersDispatched = summary.RemindersDispatched,
                noShowMarked = summary.NoShowMarked,
                reviewLinksDispatched = summary.ReviewLinksDispatched,
                dailyResetRan = summary.DailyResetRan,
                dailyResetRowCount = summary.DailyResetRowCount,
                forcedDailyResetRows = forcedRows
            });
        })
        .WithName("SchedulerRunOnce");
    }
}