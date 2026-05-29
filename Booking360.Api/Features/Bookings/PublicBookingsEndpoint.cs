using Booking360.Api.Abstractions;
using Booking360.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Booking360.Api.Features.Bookings;

public sealed class PublicBookingsEndpoint : IEndpoint
{
    public sealed record PublicBookingRequest(
        Guid ShopId,
        string CustomerName,
        string CustomerPhone,
        DateTimeOffset SlotTime,
        string? Note);

    public sealed record CancelByTokenRequest(string? Reason);

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("/api/public/bookings")
            .AllowAnonymous()
            .WithTags("PublicBookings");

        // POST create booking
        group.MapPost("/", async (
            PublicBookingRequest request,
            Booking360Database database,
            NotificationDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var error = ValidateBooking(request);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            var shop = await database.GetShopByIdAsync(request.ShopId, cancellationToken);
            if (shop is null)
            {
                return Results.BadRequest(new { error = "Quán không tồn tại" });
            }
            if (!string.Equals(shop.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "Quán đang tạm dừng nhận lịch" });
            }

            // Slot must align to shop config and be in future.
            if (request.SlotTime <= DateTimeOffset.UtcNow.AddMinutes(-1))
            {
                return Results.BadRequest(new { error = "Khung giờ đã qua, vui lòng chọn lại" });
            }

            var capacity = shop.MaxOnlinePerSlot;
            var existing = await database.CountActiveBookingsForSlotAsync(shop.Id, request.SlotTime, cancellationToken);
            if (existing >= capacity)
            {
                return Results.Conflict(new { error = "Khung giờ này đã đầy, vui lòng chọn khung khác" });
            }

            var input = new BookingCreateInput(
                ShopId: shop.Id,
                CustomerName: request.CustomerName.Trim(),
                CustomerPhone: NormalizePhone(request.CustomerPhone),
                SlotTime: request.SlotTime,
                Note: string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim());

            var booking = await database.CreateBookingV2Async(input, cancellationToken);

            // Dispatch confirmation notification (mock provider in Wave 1).
            _ = dispatcher.DispatchAsync(new NotificationContext(
                Kind: NotificationKind.BookingConfirmation,
                Channel: "log",
                Target: booking.CustomerPhone,
                Message: $"Booking360: Đặt lịch tại {shop.Name} lúc {booking.SlotTime.ToOffset(TimeSpan.FromHours(7)):HH:mm dd/MM/yyyy}. Mã: {booking.BookingToken}",
                BookingId: booking.Id,
                ShopId: shop.Id), CancellationToken.None);

            return Results.Created($"/api/public/bookings/{booking.BookingToken}", new
            {
                bookingToken = booking.BookingToken,
                shopSlug = shop.Slug,
                shopName = shop.Name,
                slotTime = booking.SlotTime,
                customerName = booking.CustomerName,
                customerPhone = booking.CustomerPhone,
                status = booking.Status,
                manageUrl = $"/b/{booking.BookingToken}"
            });
        })
        .WithName("CreatePublicBooking");

        // GET booking by token
        group.MapGet("/{token:guid}", async (Guid token, Booking360Database database, CancellationToken cancellationToken) =>
        {
            var booking = await database.GetBookingByTokenAsync(token, cancellationToken);
            if (booking is null)
            {
                return Results.NotFound();
            }
            var shop = await database.GetShopByIdAsync(booking.ShopId, cancellationToken);
            return Results.Ok(MapBookingPublic(booking, shop));
        })
        .WithName("GetPublicBookingByToken");

        // POST cancel by token
        group.MapPost("/{token:guid}/cancel", async (
            Guid token,
            CancelByTokenRequest? request,
            Booking360Database database,
            NotificationDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var record = await database.CancelBookingByTokenAsync(token, "customer", request?.Reason, cancellationToken);
            if (record is null)
            {
                return Results.NotFound(new { error = "Không tìm thấy lịch hoặc đã bị huỷ" });
            }
            var shop = await database.GetShopByIdAsync(record.ShopId, cancellationToken);

            _ = dispatcher.DispatchAsync(new NotificationContext(
                Kind: NotificationKind.BookingCancelledByCustomer,
                Channel: "log",
                Target: record.CustomerPhone,
                Message: $"Booking360: Bạn đã huỷ lịch tại {shop?.Name ?? "quán"} lúc {record.SlotTime.ToOffset(TimeSpan.FromHours(7)):HH:mm dd/MM/yyyy}",
                BookingId: record.Id,
                ShopId: record.ShopId), CancellationToken.None);

            return Results.Ok(MapBookingPublic(record, shop));
        })
        .WithName("CancelPublicBookingByToken");

        // GET slots for a shop on a given date
        routeBuilder.MapGet("/api/public/shops/{slug}/slots", async (
            string slug,
            [FromQuery] DateOnly? date,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            var shop = await database.GetShopBySlugAsync(slug.Trim().ToLowerInvariant(), cancellationToken);
            if (shop is null)
            {
                return Results.NotFound();
            }
            var target = date ?? DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
            var slots = await database.ListSlotsForDayAsync(shop, target, cancellationToken);
            return Results.Ok(new
            {
                shopSlug = shop.Slug,
                date = target,
                slotDurationMinutes = shop.SlotDurationMinutes,
                maxOnlinePerSlot = shop.MaxOnlinePerSlot,
                openTime = shop.OpenTime.ToString("HH:mm"),
                closeTime = shop.CloseTime.ToString("HH:mm"),
                slots = slots.Select(slot => new
                {
                    slotTime = slot.SlotTime,
                    onlineCount = slot.OnlineCount,
                    capacity = slot.Capacity,
                    available = slot.Available
                })
            });
        })
        .AllowAnonymous()
        .WithTags("PublicBookings")
        .WithName("ListShopSlots");
    }

    private static string? ValidateBooking(PublicBookingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerName) || request.CustomerName.Trim().Length < 2)
        {
            return "Tên không hợp lệ";
        }
        if (string.IsNullOrWhiteSpace(request.CustomerPhone))
        {
            return "Số điện thoại không được để trống";
        }
        var phone = NormalizePhone(request.CustomerPhone);
        if (phone.Length < 9 || phone.Length > 15 || !phone.All(c => char.IsDigit(c) || c == '+'))
        {
            return "Số điện thoại không hợp lệ";
        }
        return null;
    }

    private static string NormalizePhone(string raw)
    {
        return raw.Trim().Replace(" ", string.Empty).Replace("-", string.Empty);
    }

    private static object MapBookingPublic(BookingV2Record booking, ShopRecord? shop) => new
    {
        bookingToken = booking.BookingToken,
        shopId = booking.ShopId,
        shopSlug = shop?.Slug,
        shopName = shop?.Name,
        shopAddress = shop?.Address,
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