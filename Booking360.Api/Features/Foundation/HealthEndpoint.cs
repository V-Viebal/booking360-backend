using Booking360.Api.Abstractions;

namespace Booking360.Api.Features.Foundation;

public sealed class HealthEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet("/", () => Results.Ok(new { service = "booking360-api", status = "ok" }))
            .AllowAnonymous();

        routeBuilder.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }))
            .AllowAnonymous();
    }
}