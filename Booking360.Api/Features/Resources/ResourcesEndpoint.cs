using Booking360.Api.Abstractions;
using Booking360.Api.Extensions;
using Booking360.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Booking360.Api.Features.Resources;

public sealed class ResourcesEndpoint : IEndpoint
{
    public sealed record ResourceWriteRequest(
        string Name,
        string? Slug,
        string? Description,
        string? Location,
        int Capacity,
        decimal HourlyRate,
        bool IsActive);

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("/api/resources").RequireAuthorization();

        group.MapGet("/", async (HttpContext httpContext, Booking360Database database, [FromQuery] bool? includeInactive, CancellationToken cancellationToken) =>
        {
            var includeAll = includeInactive == true && httpContext.User.HasRoleOrScope("Admin", "admin:all");
            var resources = await database.ListResourcesAsync(includeAll, cancellationToken);
            return Results.Ok(resources.Select(MapResource));
        })
        .WithName("ListResources")
        .WithTags("Resources");

        group.MapGet("/{id:guid}", async (Guid id, Booking360Database database, CancellationToken cancellationToken) =>
        {
            var resource = await database.GetResourceAsync(id, cancellationToken);
            return resource is null ? Results.NotFound() : Results.Ok(MapResource(resource));
        })
        .WithName("GetResource")
        .WithTags("Resources");

        group.MapPost("/", async (ResourceWriteRequest request, Booking360Database database, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "Resource name is required" });
            }
            var slug = string.IsNullOrWhiteSpace(request.Slug)
                ? Slugify(request.Name)
                : Slugify(request.Slug);
            var record = await database.CreateResourceAsync(
                slug: slug,
                name: request.Name.Trim(),
                description: request.Description?.Trim() ?? string.Empty,
                location: request.Location?.Trim() ?? string.Empty,
                capacity: Math.Max(1, request.Capacity),
                hourlyRate: request.HourlyRate < 0 ? 0 : request.HourlyRate,
                isActive: request.IsActive,
                cancellationToken);
            return Results.Created($"/api/resources/{record.Id}", MapResource(record));
        })
        .RequireAuthorization("Admin")
        .WithName("CreateResource")
        .WithTags("Resources");

        group.MapPut("/{id:guid}", async (Guid id, ResourceWriteRequest request, Booking360Database database, CancellationToken cancellationToken) =>
        {
            var record = await database.UpdateResourceAsync(
                id: id,
                name: request.Name.Trim(),
                description: request.Description?.Trim() ?? string.Empty,
                location: request.Location?.Trim() ?? string.Empty,
                capacity: Math.Max(1, request.Capacity),
                hourlyRate: request.HourlyRate < 0 ? 0 : request.HourlyRate,
                isActive: request.IsActive,
                cancellationToken);
            return record is null ? Results.NotFound() : Results.Ok(MapResource(record));
        })
        .RequireAuthorization("Admin")
        .WithName("UpdateResource")
        .WithTags("Resources");

        group.MapDelete("/{id:guid}", async (Guid id, Booking360Database database, CancellationToken cancellationToken) =>
        {
            var deleted = await database.DeleteResourceAsync(id, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        })
        .RequireAuthorization("Admin")
        .WithName("DeleteResource")
        .WithTags("Resources");
    }

    private static object MapResource(ResourceRecord resource) => new
    {
        id = resource.Id,
        slug = resource.Slug,
        name = resource.Name,
        description = resource.Description,
        location = resource.Location,
        capacity = resource.Capacity,
        hourlyRate = resource.HourlyRate,
        isActive = resource.IsActive,
        createdAt = resource.CreatedAt
    };

    private static string Slugify(string input)
    {
        var trimmed = input.Trim().ToLowerInvariant();
        var chars = trimmed.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var collapsed = string.Concat(chars).Trim('-');
        while (collapsed.Contains("--", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("--", "-", StringComparison.Ordinal);
        }
        return string.IsNullOrEmpty(collapsed) ? Guid.NewGuid().ToString("n")[..8] : collapsed;
    }
}