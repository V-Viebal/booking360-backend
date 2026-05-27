using Booking360.Api.Abstractions;
using Booking360.Api.Infrastructure;

namespace Booking360.Api.Features.Admin;

public sealed class AdminEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("/api/admin").RequireAuthorization("Admin");

        group.MapGet("/overview", async (Booking360Database database, CancellationToken cancellationToken) =>
        {
            var overview = await database.GetAdminOverviewAsync(cancellationToken);
            return Results.Ok(new
            {
                counts = new
                {
                    users = overview.UserCount,
                    resources = overview.ResourceCount,
                    bookings = overview.BookingCount,
                    assets = overview.AssetCount
                },
                latestUsers = overview.LatestUsers.Select(u => new
                {
                    subject = u.Subject,
                    email = u.Email,
                    username = u.Username,
                    displayName = u.DisplayName,
                    roles = u.Roles,
                    createdAt = u.CreatedAt,
                    lastSeenAt = u.LastSeenAt
                }),
                latestBookings = overview.LatestBookings.Select(b => new
                {
                    id = b.Id,
                    resourceName = b.ResourceName,
                    ownerDisplayName = b.OwnerDisplayName,
                    title = b.Title,
                    startAt = b.StartAt,
                    endAt = b.EndAt,
                    status = b.Status,
                    createdAt = b.CreatedAt
                })
            });
        })
        .WithName("AdminOverview")
        .WithTags("Admin");
    }
}