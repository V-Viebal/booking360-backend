using Booking360.Api.Abstractions;
using Booking360.Api.Extensions;
using Booking360.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Booking360.Api.Features.Bookings;

public sealed class BookingsEndpoint : IEndpoint
{
    public sealed record CreateBookingRequest(
        Guid ResourceId,
        string Title,
        string? Notes,
        DateTimeOffset StartAt,
        DateTimeOffset EndAt);

    public sealed record AttachAssetRequest(Guid AssetId, string? Note);

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("/api/bookings").RequireAuthorization();

        group.MapGet("/", async (
            HttpContext httpContext,
            Booking360Database database,
            [FromQuery] bool? all,
            [FromQuery] Guid? resourceId,
            [FromQuery] DateTimeOffset? from,
            [FromQuery] DateTimeOffset? to,
            [FromQuery] int limit,
            CancellationToken cancellationToken) =>
        {
            var clampLimit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 500);
            var includeAll = all == true && httpContext.User.HasRoleOrScope("Admin", "admin:all");
            var subject = httpContext.User.GetSubject();
            var bookings = await database.ListBookingsAsync(subject, includeAll, resourceId, from, to, clampLimit, cancellationToken);
            return Results.Ok(bookings.Select(MapBooking));
        })
        .WithName("ListBookings")
        .WithTags("Bookings");

        group.MapGet("/{id:guid}", async (HttpContext httpContext, Guid id, Booking360Database database, CancellationToken cancellationToken) =>
        {
            var subject = httpContext.User.GetSubject();
            var includeAll = httpContext.User.HasRoleOrScope("Admin", "admin:all");
            var booking = await database.GetBookingAsync(id, subject, includeAll, cancellationToken);
            return booking is null ? Results.NotFound() : Results.Ok(MapBooking(booking));
        })
        .WithName("GetBooking")
        .WithTags("Bookings");

        group.MapPost("/", async (
            HttpContext httpContext,
            CreateBookingRequest request,
            Booking360Database database,
            IBooking360MailService mail,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.BadRequest(new { error = "Booking title is required" });
            }
            if (request.EndAt <= request.StartAt)
            {
                return Results.BadRequest(new { error = "End time must be after start time" });
            }
            if (request.StartAt < DateTimeOffset.UtcNow.AddMinutes(-5))
            {
                return Results.BadRequest(new { error = "Start time cannot be in the past" });
            }

            var resource = await database.GetResourceAsync(request.ResourceId, cancellationToken);
            if (resource is null || !resource.IsActive)
            {
                return Results.BadRequest(new { error = "Resource is unavailable" });
            }

            var hasOverlap = await database.HasOverlapAsync(request.ResourceId, request.StartAt, request.EndAt, null, cancellationToken);
            if (hasOverlap)
            {
                return Results.Conflict(new { error = "Resource is already booked for this time window" });
            }

            var subject = httpContext.User.GetSubject();
            var record = await database.CreateBookingAsync(
                resourceId: request.ResourceId,
                ownerSubject: subject,
                title: request.Title.Trim(),
                notes: string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
                startAt: request.StartAt,
                endAt: request.EndAt,
                cancellationToken);

            if (record is null)
            {
                return Results.Problem("Failed to create booking");
            }

            _ = mail.SendBookingConfirmationAsync(httpContext.User.GetEmail(), httpContext.User.GetDisplayName(), record, CancellationToken.None);
            return Results.Created($"/api/bookings/{record.Id}", MapBooking(record));
        })
        .WithName("CreateBooking")
        .WithTags("Bookings");

        group.MapPost("/{id:guid}/cancel", async (HttpContext httpContext, Guid id, Booking360Database database, CancellationToken cancellationToken) =>
        {
            var subject = httpContext.User.GetSubject();
            var includeAll = httpContext.User.HasRoleOrScope("Admin", "admin:all");
            var record = await database.CancelBookingAsync(id, subject, includeAll, cancellationToken);
            return record is null ? Results.NotFound() : Results.Ok(MapBooking(record));
        })
        .WithName("CancelBooking")
        .WithTags("Bookings");

        group.MapGet("/{id:guid}/assets", async (HttpContext httpContext, Guid id, Booking360Database database, CancellationToken cancellationToken) =>
        {
            var subject = httpContext.User.GetSubject();
            var includeAll = httpContext.User.HasRoleOrScope("Admin", "admin:all");
            var booking = await database.GetBookingAsync(id, subject, includeAll, cancellationToken);
            if (booking is null)
            {
                return Results.NotFound();
            }
            var assets = await database.ListBookingAssetsAsync(id, cancellationToken);
            return Results.Ok(assets.Select(asset => new
            {
                id = asset.Id,
                originalFileName = asset.OriginalFileName,
                contentType = asset.ContentType,
                sizeBytes = asset.SizeBytes,
                ownerDisplayName = asset.OwnerDisplayName,
                createdAt = asset.CreatedAt
            }));
        })
        .WithName("ListBookingAssets")
        .WithTags("Bookings");

        group.MapPost("/{id:guid}/assets", async (HttpContext httpContext, Guid id, AttachAssetRequest request, Booking360Database database, CancellationToken cancellationToken) =>
        {
            var subject = httpContext.User.GetSubject();
            var includeAll = httpContext.User.HasRoleOrScope("Admin", "admin:all");
            var booking = await database.GetBookingAsync(id, subject, includeAll, cancellationToken);
            if (booking is null)
            {
                return Results.NotFound();
            }

            var asset = await database.GetAssetAsync(request.AssetId, subject, includeAll, cancellationToken);
            if (asset is null)
            {
                return Results.BadRequest(new { error = "Asset not found or not accessible" });
            }

            await database.AttachAssetToBookingAsync(id, request.AssetId, request.Note, cancellationToken);
            return Results.NoContent();
        })
        .WithName("AttachBookingAsset")
        .WithTags("Bookings");
    }

    private static object MapBooking(BookingRecord booking) => new
    {
        id = booking.Id,
        resourceId = booking.ResourceId,
        resourceName = booking.ResourceName,
        ownerSubject = booking.OwnerSubject,
        ownerDisplayName = booking.OwnerDisplayName,
        title = booking.Title,
        notes = booking.Notes,
        startAt = booking.StartAt,
        endAt = booking.EndAt,
        status = booking.Status,
        createdAt = booking.CreatedAt
    };
}