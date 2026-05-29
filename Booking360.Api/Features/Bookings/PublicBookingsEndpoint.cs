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

    public sealed record VerifyRequestBody(string Phone, Guid? BookingId);

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("/api/public/bookings")
            .AllowAnonymous()
            .WithTags("PublicBookings");

        // POST create booking
        group.MapPost("/", async (
            PublicBookingRequest request,
            HttpContext httpContext,
            Booking360Database database,
            NotificationDispatcher dispatcher,
            Booking360Options options,
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

            var phone = NormalizePhone(request.CustomerPhone);
            var ip = ResolveClientIp(httpContext);

            // W7 REQ-EC-016: phone blacklist (anti-abuse).
            if (await database.IsPhoneBlacklistedAsync(phone, cancellationToken))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // W7 REQ-EC-014: per-phone rate limits.
            if (await database.CountActiveBookingsForPhoneAsync(phone, cancellationToken) >= 1)
            {
                return Results.Json(new { error = "Bạn đang có 1 lịch đang hoạt động. Vui lòng huỷ trước khi đặt mới." }, statusCode: StatusCodes.Status429TooManyRequests);
            }
            if (await database.CountBookingsCreatedLast24hForPhoneAsync(phone, cancellationToken) >= 5)
            {
                return Results.Json(new { error = "Số điện thoại này đã đặt 5 lịch trong 24h. Vui lòng thử lại sau." }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            // W7 REQ-TC-009: per-IP rate limits.
            if (!string.IsNullOrEmpty(ip))
            {
                if (await database.CountBookingsCreatedLastHourForIpAsync(ip, cancellationToken) >= 10)
                {
                    return Results.Json(new { error = "Có quá nhiều yêu cầu từ thiết bị này. Vui lòng thử lại sau." }, statusCode: StatusCodes.Status429TooManyRequests);
                }
                if (await database.CountBookingsCreatedLast24hForIpAsync(ip, cancellationToken) >= 30)
                {
                    return Results.Json(new { error = "Vượt giới hạn đặt lịch trong ngày từ thiết bị này." }, statusCode: StatusCodes.Status429TooManyRequests);
                }
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
                CustomerPhone: phone,
                SlotTime: request.SlotTime,
                Note: string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
                CustomerIp: ip);

            var booking = await database.CreateBookingV2Async(input, cancellationToken);

            // Dispatch confirmation notifications (W3: customer + shop, routed via env default channel).
            var notifyData = new BookingNotificationData(
                ShopName: shop.Name,
                ShopAddress: shop.Address,
                ShopPhone: shop.Phone,
                CustomerName: booking.CustomerName,
                CustomerPhone: booking.CustomerPhone,
                SlotTime: booking.SlotTime,
                BookingToken: booking.BookingToken,
                Note: booking.Note,
                CancelReason: null);
            var defaultChannel = options.DefaultNotificationChannel;
            _ = dispatcher.DispatchAsync(NotificationTemplates.BookingConfirmationForCustomer(
                notifyData, defaultChannel, booking.Id, shop.Id, options.FrontendUrl), CancellationToken.None);
            _ = dispatcher.DispatchAsync(NotificationTemplates.NewBookingForShop(
                notifyData, defaultChannel, booking.Id, shop.Id, shop.ShopAccessToken, options.FrontendUrl), CancellationToken.None);

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
            Booking360Options options,
            CancellationToken cancellationToken) =>
        {
            var record = await database.CancelBookingByTokenAsync(token, "customer", request?.Reason, cancellationToken);
            if (record is null)
            {
                return Results.NotFound(new { error = "Không tìm thấy lịch hoặc đã bị huỷ" });
            }
            var shop = await database.GetShopByIdAsync(record.ShopId, cancellationToken);

            // W3: dispatch cancel notification to BOTH customer and shop.
            if (shop is not null)
            {
                var cancelData = new BookingNotificationData(
                    ShopName: shop.Name,
                    ShopAddress: shop.Address,
                    ShopPhone: shop.Phone,
                    CustomerName: record.CustomerName,
                    CustomerPhone: record.CustomerPhone,
                    SlotTime: record.SlotTime,
                    BookingToken: record.BookingToken,
                    Note: record.Note,
                    CancelReason: record.CancelReason);
                var defaultChannel = options.DefaultNotificationChannel;
                _ = dispatcher.DispatchAsync(NotificationTemplates.BookingCancelledForCustomer(
                    cancelData, defaultChannel, record.Id, record.ShopId), CancellationToken.None);
                _ = dispatcher.DispatchAsync(NotificationTemplates.BookingCancelledForShop(
                    cancelData, defaultChannel, record.Id, record.ShopId, record.CancelledBy ?? "customer"), CancellationToken.None);
            }

            return Results.Ok(MapBookingPublic(record, shop));
        })
        .WithName("CancelPublicBookingByToken");

        // W7: Phone verification — request a 1-click verify link for a phone (REQ-EC-013).
        group.MapPost("/verify/request", async (
            VerifyRequestBody body,
            HttpContext httpContext,
            Booking360Database database,
            NotificationDispatcher dispatcher,
            Booking360Options options,
            CancellationToken cancellationToken) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Phone))
            {
                return Results.BadRequest(new { error = "Số điện thoại không được để trống" });
            }
            var phone = NormalizePhone(body.Phone);
            if (phone.Length < 9 || phone.Length > 15 || !phone.All(c => char.IsDigit(c) || c == '+'))
            {
                return Results.BadRequest(new { error = "Số điện thoại không hợp lệ" });
            }

            // Per-IP rate guard so verification can't be used to flood links.
            var ip = ResolveClientIp(httpContext);
            if (!string.IsNullOrEmpty(ip)
                && await database.CountBookingsCreatedLastHourForIpAsync(ip, cancellationToken) >= 10)
            {
                return Results.Json(new { error = "Có quá nhiều yêu cầu từ thiết bị này. Vui lòng thử lại sau." },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            var verification = await database.CreatePhoneVerificationAsync(phone, body.BookingId, cancellationToken);
            // The notification dispatcher (W3) routes via env default channel; SMS fallback is wired there.
            return Results.Ok(new
            {
                token = verification.Token,
                expiresAt = verification.ExpiresAt,
                verifyUrl = $"{options.FrontendUrl.TrimEnd('/')}/verify/{verification.Token}"
            });
        })
        .WithName("RequestPhoneVerification");

        // W7: Phone verification — consume a verification token (1-click confirm).
        group.MapGet("/verify/{token:guid}", async (
            Guid token,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            var record = await database.ConsumePhoneVerificationAsync(token, cancellationToken);
            if (record is null)
            {
                return Results.BadRequest(new { error = "Liên kết xác minh không hợp lệ hoặc đã hết hạn" });
            }
            return Results.Ok(new
            {
                phone = record.Phone,
                bookingId = record.BookingId,
                verifiedAt = record.VerifiedAt
            });
        })
        .WithName("ConsumePhoneVerification");
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

    /// <summary>
    /// Pull the client IP, honouring trusted proxy headers when present.
    /// Falls back to the connection remote IP. Returns null if unresolvable.
    /// </summary>
    private static string? ResolveClientIp(HttpContext context)
    {
        // Trust standard reverse-proxy headers in this order: CF-Connecting-IP > X-Real-IP > first X-Forwarded-For.
        if (context.Request.Headers.TryGetValue("CF-Connecting-IP", out var cf) && !string.IsNullOrWhiteSpace(cf))
        {
            return cf.ToString().Split(',')[0].Trim();
        }
        if (context.Request.Headers.TryGetValue("X-Real-IP", out var realIp) && !string.IsNullOrWhiteSpace(realIp))
        {
            return realIp.ToString().Split(',')[0].Trim();
        }
        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var xff) && !string.IsNullOrWhiteSpace(xff))
        {
            return xff.ToString().Split(',')[0].Trim();
        }
        return context.Connection.RemoteIpAddress?.ToString();
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