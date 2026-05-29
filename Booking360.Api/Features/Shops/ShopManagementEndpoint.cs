using Booking360.Api.Abstractions;
using Booking360.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Booking360.Api.Features.Shops;

public sealed class ShopManagementEndpoint : IEndpoint
{
    public sealed record ShopConfigRequest(
        string? OpenTime,
        string? CloseTime,
        int[]? WorkingDays,
        int? SlotDurationMinutes,
        int? MaxOnlinePerSlot,
        string? EarlyCloseToday,
        DateTimeOffset? PausedUntil);

    public sealed record CancelByShopRequest(string? Reason);

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("/api/shop/m/{token:guid}")
            .AllowAnonymous()
            .WithTags("ShopManagement");

        // GET dashboard for the day (defaults to today)
        group.MapGet("/today", async (
            Guid token,
            [FromQuery] DateOnly? date,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            var shop = await database.GetShopByTokenAsync(token, cancellationToken);
            if (shop is null)
            {
                return Results.NotFound(new { error = "Liên kết quản lý không hợp lệ" });
            }

            var target = date ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
            var bookings = await database.ListBookingsForShopDayAsync(shop.Id, target, cancellationToken);
            var slots = await database.ListSlotsForDayAsync(shop, target, cancellationToken);

            return Results.Ok(new
            {
                shop = MapShopForOwner(shop),
                date = target,
                bookings = bookings.Select(MapBooking),
                slots = slots.Select(slot => new
                {
                    slotTime = slot.SlotTime,
                    onlineCount = slot.OnlineCount,
                    capacity = slot.Capacity,
                    available = slot.Available
                })
            });
        })
        .WithName("ShopTodayDashboard");

        // PATCH update shop config
        group.MapPatch("/configure", async (
            Guid token,
            ShopConfigRequest request,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            var shop = await database.GetShopByTokenAsync(token, cancellationToken);
            if (shop is null)
            {
                return Results.NotFound();
            }

            var openTime = ParseTimeOptional(request.OpenTime);
            var closeTime = ParseTimeOptional(request.CloseTime);
            var earlyClose = ParseTimeOptional(request.EarlyCloseToday);

            if (request.SlotDurationMinutes.HasValue && (request.SlotDurationMinutes.Value < 5 || request.SlotDurationMinutes.Value > 240))
            {
                return Results.BadRequest(new { error = "Thời lượng slot phải từ 5 đến 240 phút" });
            }
            if (request.MaxOnlinePerSlot.HasValue && (request.MaxOnlinePerSlot.Value < 1 || request.MaxOnlinePerSlot.Value > 50))
            {
                return Results.BadRequest(new { error = "Số khách online tối đa phải từ 1 đến 50" });
            }

            await database.UpdateShopConfigAsync(
                shop.Id,
                openTime,
                closeTime,
                request.WorkingDays,
                request.SlotDurationMinutes,
                request.MaxOnlinePerSlot,
                earlyClose,
                request.PausedUntil,
                cancellationToken);

            var refreshed = await database.GetShopByTokenAsync(token, cancellationToken);
            return Results.Ok(MapShopForOwner(refreshed!));
        })
        .WithName("ShopUpdateConfig");

        // POST cancel a specific booking by shop
        group.MapPost("/bookings/{bookingToken:guid}/cancel", async (
            Guid token,
            Guid bookingToken,
            CancelByShopRequest? request,
            Booking360Database database,
            NotificationDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var shop = await database.GetShopByTokenAsync(token, cancellationToken);
            if (shop is null)
            {
                return Results.NotFound(new { error = "Liên kết quản lý không hợp lệ" });
            }

            var booking = await database.GetBookingByTokenAsync(bookingToken, cancellationToken);
            if (booking is null || booking.ShopId != shop.Id)
            {
                return Results.NotFound(new { error = "Không tìm thấy lịch đặt" });
            }

            var record = await database.CancelBookingByTokenAsync(bookingToken, "shop", request?.Reason, cancellationToken);
            if (record is null)
            {
                return Results.BadRequest(new { error = "Không thể huỷ (có thể đã huỷ hoặc đã hoàn thành)" });
            }

            _ = dispatcher.DispatchAsync(new NotificationContext(
                Kind: NotificationKind.BookingCancelledByShop,
                Channel: "log",
                Target: record.CustomerPhone,
                Message: $"Booking360: {shop.Name} đã huỷ lịch lúc {record.SlotTime.ToOffset(TimeSpan.FromHours(7)):HH:mm dd/MM/yyyy}. Lý do: {request?.Reason ?? "không có"}",
                BookingId: record.Id,
                ShopId: shop.Id), CancellationToken.None);

            return Results.Ok(MapBooking(record));
        })
        .WithName("ShopCancelBooking");
    }

    private static TimeOnly? ParseTimeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TimeOnly.TryParse(value, out var parsed) ? parsed : null;
    }

    private static object MapShopForOwner(ShopRecord shop) => new
    {
        id = shop.Id,
        slug = shop.Slug,
        name = shop.Name,
        phone = shop.Phone,
        address = shop.Address,
        lat = shop.Lat,
        lng = shop.Lng,
        openTime = shop.OpenTime.ToString("HH:mm"),
        closeTime = shop.CloseTime.ToString("HH:mm"),
        workingDays = shop.WorkingDays,
        slotDurationMinutes = shop.SlotDurationMinutes,
        maxOnlinePerSlot = shop.MaxOnlinePerSlot,
        status = shop.Status,
        pausedUntil = shop.PausedUntil,
        earlyCloseToday = shop.EarlyCloseToday?.ToString("HH:mm"),
        cancelCount30d = shop.CancelCount30d,
        publicUrl = $"/shops/{shop.Slug}"
    };

    private static object MapBooking(BookingV2Record booking) => new
    {
        bookingToken = booking.BookingToken,
        customerName = booking.CustomerName,
        customerPhone = booking.CustomerPhone,
        slotTime = booking.SlotTime,
        note = booking.Note,
        status = booking.Status,
        cancelledBy = booking.CancelledBy,
        cancelReason = booking.CancelReason,
        cancelledAt = booking.CancelledAt,
        createdAt = booking.CreatedAt
    };
}