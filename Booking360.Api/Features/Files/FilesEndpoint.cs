using Booking360.Api.Abstractions;
using Booking360.Api.Extensions;
using Booking360.Api.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Booking360.Api.Features.Files;

public sealed class FilesEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("/api/files").RequireAuthorization().DisableAntiforgery();

        group.MapGet("/", async (HttpContext httpContext, Booking360Database database, [FromQuery] int limit, [FromQuery] bool? all, CancellationToken cancellationToken) =>
        {
            var clampLimit = Math.Clamp(limit <= 0 ? 50 : limit, 1, 200);
            var includeAll = all == true && httpContext.User.HasRoleOrScope("Admin", "admin:all");
            var subject = httpContext.User.GetSubject();
            var assets = await database.ListAssetsAsync(subject, includeAll, clampLimit, cancellationToken);
            return Results.Ok(assets.Select(MapAsset));
        })
        .WithName("ListFiles")
        .WithTags("Files");

        group.MapPost("/", async (HttpContext httpContext, Booking360Database database, Booking360ObjectStorageService storage, IFormFile file, CancellationToken cancellationToken) =>
        {
            if (file is null || file.Length <= 0)
            {
                return Results.BadRequest(new { error = "A file payload is required" });
            }

            await storage.EnsureBucketExistsAsync(cancellationToken);
            var subject = httpContext.User.GetSubject();
            await using var stream = file.OpenReadStream();
            var objectKey = await storage.UploadAsync(
                stream,
                file.Length,
                file.ContentType ?? "application/octet-stream",
                file.FileName,
                subject,
                cancellationToken);

            var record = await database.CreateAssetAsync(
                ownerSubject: subject,
                originalFileName: file.FileName,
                objectKey: objectKey,
                contentType: file.ContentType ?? "application/octet-stream",
                sizeBytes: file.Length,
                bucketName: storage.ActiveBucket,
                cancellationToken);

            return Results.Created($"/api/files/{record.Id}", MapAsset(record));
        })
        .WithName("UploadFile")
        .WithTags("Files");

        group.MapGet("/{id:guid}/download-url", async (HttpContext httpContext, Booking360Database database, Booking360ObjectStorageService storage, Guid id, CancellationToken cancellationToken) =>
        {
            var subject = httpContext.User.GetSubject();
            var includeAll = httpContext.User.HasRoleOrScope("Admin", "admin:all");
            var asset = await database.GetAssetAsync(id, subject, includeAll, cancellationToken);
            if (asset is null)
            {
                return Results.NotFound();
            }
            var url = await storage.GetDownloadUrlAsync(asset.ObjectKey);
            return Results.Ok(new { url, expiresInSeconds = 900 });
        })
        .WithName("GetFileDownloadUrl")
        .WithTags("Files");
    }

    private static object MapAsset(StoredAssetRecord asset) => new
    {
        id = asset.Id,
        ownerSubject = asset.OwnerSubject,
        ownerDisplayName = asset.OwnerDisplayName,
        originalFileName = asset.OriginalFileName,
        contentType = asset.ContentType,
        sizeBytes = asset.SizeBytes,
        createdAt = asset.CreatedAt,
        bucketName = asset.BucketName
    };
}