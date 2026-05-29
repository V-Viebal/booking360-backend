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

    public sealed record ShopReviewReplyRequest(string Reply);

    public sealed record ShopStatusRequest(string? Status, DateTimeOffset? PausedUntil);
    public sealed record ShopCapacityRequest(int MaxOnlinePerSlot);
    public sealed record ShopEarlyCloseRequest(string? EarlyCloseToday);

    // W8: shop profile patch — price segment, photo gallery, district.
    public sealed record ShopProfileRequest(string? PriceSegment, string[]? PhotoUrls, string? District);

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

        // === W6: status state machine + quick toggles ===

        // POST set shop status (active / paused_today / paused / closed_today / temp_full)
        group.MapPost("/status", async (
            Guid token,
            ShopStatusRequest request,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            var shop = await database.GetShopByTokenAsync(token, cancellationToken);
            if (shop is null) return Results.NotFound(new { error = "Liên kết quản lý không hợp lệ" });

            var status = (request.Status ?? string.Empty).Trim().ToLowerInvariant();
            string[] allowed = { "active", "paused_today", "paused", "closed_today", "temp_full" };
            if (Array.IndexOf(allowed, status) < 0)
            {
                return Results.BadRequest(new { error = "Trạng thái không hợp lệ" });
            }
            // Transition guard: pausedUntil only valid when status='paused'.
            if (request.PausedUntil.HasValue && status != "paused")
            {
                return Results.BadRequest(new { error = "Thời điểm hết tạm dừng chỉ áp dụng cho trạng thái paused" });
            }
            if (status == "paused" && request.PausedUntil.HasValue && request.PausedUntil.Value <= DateTimeOffset.UtcNow)
            {
                return Results.BadRequest(new { error = "Thời điểm hết tạm dừng phải ở tương lai" });
            }

            await database.SetShopStatusAsync(shop.Id, status, request.PausedUntil, cancellationToken);
            var refreshed = await database.GetShopByTokenAsync(token, cancellationToken);
            return Results.Ok(MapShopForOwner(refreshed!));
        })
        .WithName("ShopSetStatus");

        // POST set per-slot capacity (0 = temp_full shorthand, 1..6 = real cap)
        group.MapPost("/capacity", async (
            Guid token,
            ShopCapacityRequest request,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            var shop = await database.GetShopByTokenAsync(token, cancellationToken);
            if (shop is null) return Results.NotFound(new { error = "Liên kết quản lý không hợp lệ" });

            if (request.MaxOnlinePerSlot < 0 || request.MaxOnlinePerSlot > 6)
            {
                return Results.BadRequest(new { error = "Sức chứa mỗi slot phải từ 0 đến 6" });
            }

            await database.SetShopCapacityAsync(shop.Id, request.MaxOnlinePerSlot, cancellationToken);
            var refreshed = await database.GetShopByTokenAsync(token, cancellationToken);
            return Results.Ok(MapShopForOwner(refreshed!));
        })
        .WithName("ShopSetCapacity");

        // POST set/clear early close time for today (HH:mm or null)
        group.MapPost("/early-close", async (
            Guid token,
            ShopEarlyCloseRequest request,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            var shop = await database.GetShopByTokenAsync(token, cancellationToken);
            if (shop is null) return Results.NotFound(new { error = "Liên kết quản lý không hợp lệ" });

            TimeOnly? early = null;
            if (!string.IsNullOrWhiteSpace(request.EarlyCloseToday))
            {
                if (!TimeOnly.TryParse(request.EarlyCloseToday, out var parsed))
                {
                    return Results.BadRequest(new { error = "Giờ đóng cửa sớm không hợp lệ (HH:mm)" });
                }
                if (parsed <= shop.OpenTime)
                {
                    return Results.BadRequest(new { error = "Giờ đóng cửa sớm phải sau giờ mở cửa" });
                }
                early = parsed;
            }

            await database.SetShopEarlyCloseAsync(shop.Id, early, cancellationToken);
            var refreshed = await database.GetShopByTokenAsync(token, cancellationToken);
            return Results.Ok(MapShopForOwner(refreshed!));
        })
        .WithName("ShopSetEarlyClose");
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

        // PATCH shop profile (W8 REQ-SS-010 / EC-018) — price segment, photo gallery, district.
        group.MapPatch("/profile", async (
            Guid token,
            ShopProfileRequest request,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            var shop = await database.GetShopByTokenAsync(token, cancellationToken);
            if (shop is null)
            {
                return Results.NotFound(new { error = "Liên kết quản lý không hợp lệ" });
            }

            // Validation: cap photo gallery and price segment vocabulary.
            string? priceSegment = null;
            if (!string.IsNullOrWhiteSpace(request.PriceSegment))
            {
                var seg = request.PriceSegment.Trim();
                string[] allowed = { "50-80k", "80-120k", "120-150k", "150k+" };
                if (Array.IndexOf(allowed, seg) < 0)
                {
                    return Results.BadRequest(new { error = "Phân khúc giá không hợp lệ" });
                }
                priceSegment = seg;
            }

            string[]? photos = null;
            if (request.PhotoUrls is not null)
            {
                if (request.PhotoUrls.Length > 8)
                {
                    return Results.BadRequest(new { error = "Tối đa 8 ảnh" });
                }
                foreach (var p in request.PhotoUrls)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    if (!Uri.TryCreate(p, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    {
                        return Results.BadRequest(new { error = "Liên kết ảnh không hợp lệ" });
                    }
                }
                photos = request.PhotoUrls.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToArray();
            }

            string? district = null;
            if (!string.IsNullOrWhiteSpace(request.District))
            {
                var d = request.District.Trim();
                if (d.Length > 80)
                {
                    return Results.BadRequest(new { error = "Tên quận quá dài" });
                }
                district = d;
            }

            var refreshed = await database.UpdateShopProfileAsync(shop.Id, priceSegment, photos, district, cancellationToken);
            if (refreshed is null)
            {
                return Results.Problem("Không thể cập nhật hồ sơ quán", statusCode: 500);
            }
            return Results.Ok(MapShopForOwner(refreshed));
        })
        .WithName("ShopUpdateProfile");
        // POST shop reply to a review (W5)
        group.MapPost("/reviews/{reviewId:guid}/reply", async (
            Guid token,
            Guid reviewId,
            ShopReviewReplyRequest request,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            var shop = await database.GetShopByTokenAsync(token, cancellationToken);
            if (shop is null)
            {
                return Results.NotFound(new { error = "Liên kết quản lý không hợp lệ" });
            }
            if (string.IsNullOrWhiteSpace(request.Reply) || request.Reply.Trim().Length < 2)
            {
                return Results.BadRequest(new { error = "Nội dung phản hồi không hợp lệ" });
            }
            if (request.Reply.Length > 2000)
            {
                return Results.BadRequest(new { error = "Phản hồi không được dài quá 2000 ký tự" });
            }
            var existing = await database.GetReviewByIdAsync(reviewId, cancellationToken);
            if (existing is null || existing.ShopId != shop.Id)
            {
                return Results.NotFound(new { error = "Không tìm thấy đánh giá" });
            }
            var updated = await database.SetShopReplyAsync(reviewId, shop.Id, request.Reply, cancellationToken);
            if (updated is null)
            {
                return Results.Problem("Không thể lưu phản hồi", statusCode: 500);
            }
            return Results.Ok(new
            {
                id = updated.Id,
                rating = updated.Rating,
                comment = updated.Comment,
                shopReply = updated.ShopReply,
                shopRepliedAt = updated.ShopRepliedAt,
                createdAt = updated.CreatedAt
            });
        })
        .WithName("ShopReplyToReview");

        // GET shop dashboard reviews list (W5)
        group.MapGet("/reviews", async (
            Guid token,
            [FromQuery] int? limit,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            var shop = await database.GetShopByTokenAsync(token, cancellationToken);
            if (shop is null)
            {
                return Results.NotFound(new { error = "Liên kết quản lý không hợp lệ" });
            }
            var capped = Math.Clamp(limit ?? 50, 1, 200);
            // Shop dashboard sees suppressed reviews so the owner can spot moderation issues.
            var reviews = await database.ListReviewsForShopAsync(shop.Id, capped, includeSuppressed: true, cancellationToken);
            return Results.Ok(new
            {
                shop = new { id = shop.Id, slug = shop.Slug, happyScore = shop.HappyScore, reviewCount = shop.ReviewCount },
                reviews = reviews.Select(r => new
                {
                    id = r.Id,
                    rating = r.Rating,
                    comment = r.Comment,
                    shopReply = r.ShopReply,
                    shopRepliedAt = r.ShopRepliedAt,
                    reportedCount = r.ReportedCount,
                    weight = r.Weight,
                    suppressed = r.Weight == 0m,
                    createdAt = r.CreatedAt,
                    customerDisplay = r.CustomerDisplay
                })
            });
        })
        .WithName("ShopListReviews");
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
        // W8: media + GTM fields
        photoUrl = shop.PhotoUrl,
        photoUrls = shop.PhotoUrls,
        priceSegment = shop.PriceSegment,
        district = shop.District,
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
