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

        // W8 REQ-GM-011/GM-012: today vs yesterday counters for the ops dashboard.
        group.MapGet("/metrics", async (Booking360Database database, CancellationToken cancellationToken) =>
        {
            var m = await database.GetAdminMetricsAsync(cancellationToken);
            return Results.Ok(new
            {
                today = new
                {
                    bookingsCreated = m.TodayCreated,
                    confirmed = m.TodayConfirmed,
                    noShow = m.TodayNoShow,
                    cancelByShop = m.TodayShopCancel,
                    cancelByCustomer = m.TodayCustomerCancel
                },
                yesterday = new
                {
                    bookingsCreated = m.YesterdayCreated,
                    confirmed = m.YesterdayConfirmed,
                    noShow = m.YesterdayNoShow,
                    cancelByShop = m.YesterdayShopCancel,
                    cancelByCustomer = m.YesterdayCustomerCancel
                },
                happyScore30d = m.HappyScore30d,
                activeShops = m.ActiveShops
            });
        })
        .WithName("AdminDailyMetrics")
        .WithTags("Admin");

        // W8 REQ-GM-001/GM-008: per-district shop count, last-30d bookings, weighted Happy Score.
        group.MapGet("/density", async (
            [Microsoft.AspNetCore.Mvc.FromQuery] string? district,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            var rows = await database.GetDistrictDensityAsync(string.IsNullOrWhiteSpace(district) ? null : district.Trim(), cancellationToken);
            return Results.Ok(rows.Select(r => new
            {
                district = r.District,
                shopCount = r.ShopCount,
                bookings30d = r.Bookings30d,
                happyScore = r.HappyScore
            }));
        })
        .WithName("AdminDistrictDensity")
        .WithTags("Admin");

        // W8 REQ-SS-010: printable onboarding checklist for ops staff visiting a shop.
        // Returns a vi-VN, print-ready HTML page derived from the shop record.
        group.MapGet("/onboarding-checklist", async (
            [Microsoft.AspNetCore.Mvc.FromQuery] Guid shop_id,
            Booking360Database database,
            Booking360Options options,
            CancellationToken cancellationToken) =>
        {
            if (shop_id == Guid.Empty)
            {
                return Results.BadRequest(new { error = "shop_id là bắt buộc" });
            }
            var shop = await database.GetShopByIdAsync(shop_id, cancellationToken);
            if (shop is null)
            {
                return Results.NotFound(new { error = "Không tìm thấy quán" });
            }
            var html = OnboardingChecklistBuilder.Render(shop, options.FrontendUrl);
            return Results.Content(html, "text/html; charset=utf-8");
        })
        .WithName("AdminOnboardingChecklist")
        .WithTags("Admin");
    }
}