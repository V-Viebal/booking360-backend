using Booking360.Api.Abstractions;
using Booking360.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Booking360.Api.Features.Shops;

public sealed class ShopsEndpoint : IEndpoint
{
    public sealed record ShopRegisterRequest(
        string Name,
        string Phone,
        string Address,
        double? Lat,
        double? Lng,
        string? OpenTime,
        string? CloseTime,
        int[]? WorkingDays);

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var publicGroup = routeBuilder.MapGroup("/api/public/shops")
            .AllowAnonymous()
            .WithTags("PublicShops");

        publicGroup.MapGet("/", async (
            Booking360Database database,
            [FromQuery] double? lat,
            [FromQuery] double? lng,
            [FromQuery] double? radiusKm,
            [FromQuery] int? limit,
            CancellationToken cancellationToken) =>
        {
            var capped = Math.Clamp(limit ?? 50, 1, 100);
            var shops = await database.ListPublicShopsAsync(lat, lng, radiusKm, capped, cancellationToken);
            return Results.Ok(shops.Select(MapShopListItem));
        })
        .WithName("ListPublicShops");

        publicGroup.MapGet("/{slug}", async (string slug, Booking360Database database, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                return Results.BadRequest(new { error = "slug is required" });
            }
            var shop = await database.GetShopBySlugAsync(slug.Trim().ToLowerInvariant(), cancellationToken);
            return shop is null ? Results.NotFound() : Results.Ok(MapShopPublic(shop));
        })
        .WithName("GetPublicShop");

        publicGroup.MapPost("/register", async (
            ShopRegisterRequest request,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            var error = ValidateRegistration(request);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            var input = new ShopRegistrationInput(
                Name: request.Name.Trim(),
                Phone: NormalizePhone(request.Phone),
                Address: (request.Address ?? string.Empty).Trim(),
                Lat: request.Lat,
                Lng: request.Lng,
                OpenTime: ParseTime(request.OpenTime, new TimeOnly(9, 0)),
                CloseTime: ParseTime(request.CloseTime, new TimeOnly(20, 0)),
                WorkingDays: request.WorkingDays);

            var record = await database.CreateShopAsync(input, cancellationToken);

            return Results.Created($"/api/public/shops/{Uri.EscapeDataString(record.Slug)}", new
            {
                id = record.Id,
                slug = record.Slug,
                shopAccessToken = record.ShopAccessToken,
                managementUrl = $"/shop/m/{record.ShopAccessToken}",
                publicUrl = $"/shops/{record.Slug}"
            });
        })
        .WithName("RegisterPublicShop");
    }

    private static string? ValidateRegistration(ShopRegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length < 2)
        {
            return "Tên quán phải có ít nhất 2 ký tự";
        }
        if (string.IsNullOrWhiteSpace(request.Phone))
        {
            return "Số điện thoại không được để trống";
        }
        var normalized = NormalizePhone(request.Phone);
        if (normalized.Length < 9 || normalized.Length > 15 || !normalized.All(c => char.IsDigit(c) || c == '+'))
        {
            return "Số điện thoại không hợp lệ";
        }
        if (string.IsNullOrWhiteSpace(request.Address))
        {
            return "Địa chỉ không được để trống";
        }
        if (request.Lat.HasValue && (request.Lat.Value < -90 || request.Lat.Value > 90))
        {
            return "Vĩ độ không hợp lệ";
        }
        if (request.Lng.HasValue && (request.Lng.Value < -180 || request.Lng.Value > 180))
        {
            return "Kinh độ không hợp lệ";
        }
        return null;
    }

    private static string NormalizePhone(string raw)
    {
        var trimmed = raw.Trim().Replace(" ", string.Empty).Replace("-", string.Empty);
        return trimmed;
    }

    private static TimeOnly ParseTime(string? value, TimeOnly fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        return TimeOnly.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static object MapShopListItem(ShopListItem item) => new
    {
        id = item.Id,
        slug = item.Slug,
        name = item.Name,
        address = item.Address,
        lat = item.Lat,
        lng = item.Lng,
        photoUrl = item.PhotoUrl,
        priceSegment = item.PriceSegment,
        happyScore = item.HappyScore,
        reviewCount = item.ReviewCount,
        status = item.Status,
        openTime = item.OpenTime.ToString("HH:mm"),
        closeTime = item.CloseTime.ToString("HH:mm"),
        distanceKm = item.DistanceKm
    };

    private static object MapShopPublic(ShopRecord shop) => new
    {
        id = shop.Id,
        slug = shop.Slug,
        name = shop.Name,
        address = shop.Address,
        lat = shop.Lat,
        lng = shop.Lng,
        photoUrl = shop.PhotoUrl,
        priceSegment = shop.PriceSegment,
        happyScore = shop.HappyScore,
        reviewCount = shop.ReviewCount,
        openTime = shop.OpenTime.ToString("HH:mm"),
        closeTime = shop.CloseTime.ToString("HH:mm"),
        workingDays = shop.WorkingDays,
        slotDurationMinutes = shop.SlotDurationMinutes,
        maxOnlinePerSlot = shop.MaxOnlinePerSlot,
        status = shop.Status,
        pausedUntil = shop.PausedUntil,
        earlyCloseToday = shop.EarlyCloseToday?.ToString("HH:mm"),
        createdAt = shop.CreatedAt
    };
}