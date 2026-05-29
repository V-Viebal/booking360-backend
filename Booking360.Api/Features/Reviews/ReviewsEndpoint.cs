using Booking360.Api.Abstractions;
using Booking360.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Booking360.Api.Features.Reviews;

// Wave 5 — Reviews + Ratings + Happy Score.
// Review token is embedded in the W4 review-link as the booking_token (POST /api/public/reviews/{bookingToken}).
// Eligibility:
//   * 1 booking = 1 review (DB-enforced unique on reviews.booking_id)
//   * Only confirmed/completed bookings whose slot_time has passed
//   * Reviews disabled for cancelled or no_show bookings
public sealed class ReviewsEndpoint : IEndpoint
{
    public sealed record ReviewCreateRequest(int Rating, string? Comment);
    public sealed record ReviewReportRequest(string? Reason);

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var reviewsGroup = routeBuilder.MapGroup("/api/public/reviews")
            .AllowAnonymous()
            .WithTags("PublicReviews");

        // GET /api/public/reviews/{bookingToken} — page lookup before submitting
        reviewsGroup.MapGet("/{bookingToken:guid}", async (
            Guid bookingToken,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            var elig = await database.GetReviewEligibilityByBookingTokenAsync(bookingToken, cancellationToken);
            return Results.Ok(MapEligibility(elig));
        })
        .WithName("GetReviewEligibility");

        // POST /api/public/reviews/{bookingToken} — submit
        reviewsGroup.MapPost("/{bookingToken:guid}", async (
            Guid bookingToken,
            ReviewCreateRequest request,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            if (request.Rating < 1 || request.Rating > 5)
            {
                return Results.BadRequest(new { error = "Số sao phải từ 1 đến 5" });
            }
            if (!string.IsNullOrEmpty(request.Comment) && request.Comment.Length > 2000)
            {
                return Results.BadRequest(new { error = "Nhận xét không được dài quá 2000 ký tự" });
            }

            var elig = await database.GetReviewEligibilityByBookingTokenAsync(bookingToken, cancellationToken);
            if (!elig.Eligible || elig.Booking is null || elig.Shop is null)
            {
                if (elig.Existing is not null)
                {
                    return Results.Conflict(new { error = "Lịch này đã được đánh giá", review = MapReviewPublic(elig.Existing) });
                }
                return Results.BadRequest(new { error = elig.Reason ?? "Không thể đánh giá" });
            }

            try
            {
                var review = await database.CreateReviewAsync(elig.Booking.Id, elig.Shop.Id, request.Rating, request.Comment, cancellationToken);
                // Recalc happy_score on shops table — keeps the public detail in sync without a daily job.
                await database.RecalculateShopHappyScoreAsync(elig.Shop.Id, cancellationToken);
                return Results.Created($"/api/public/reviews/{bookingToken}", new
                {
                    review = MapReviewPublic(review with { CustomerDisplay = MaskCustomer(elig.Booking.CustomerName) }),
                    shopSlug = elig.Shop.Slug
                });
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
            {
                // unique_violation — concurrent insert — surface conflict, not 500.
                var existing = await database.GetReviewByBookingIdAsync(elig.Booking.Id, cancellationToken);
                return Results.Conflict(new { error = "Lịch này đã được đánh giá", review = existing is null ? null : MapReviewPublic(existing) });
            }
        })
        .WithName("CreatePublicReview");

        // POST /api/public/reviews/{reviewId}/report — community report
        reviewsGroup.MapPost("/{reviewId:guid}/report", async (
            Guid reviewId,
            ReviewReportRequest? request,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            var updated = await database.ReportReviewAsync(reviewId, cancellationToken);
            if (updated is null)
            {
                return Results.NotFound(new { error = "Không tìm thấy đánh giá" });
            }
            return Results.Ok(new
            {
                reportedCount = updated.ReportedCount,
                weight = updated.Weight,
                suppressed = updated.Weight == 0m
            });
        })
        .WithName("ReportPublicReview");

        // GET /api/public/shops/{slug}/reviews — public reviews list (used by shop detail page)
        routeBuilder.MapGet("/api/public/shops/{slug}/reviews", async (
            string slug,
            [FromQuery] int? limit,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                return Results.BadRequest(new { error = "slug là bắt buộc" });
            }
            var shop = await database.GetShopBySlugAsync(slug.Trim().ToLowerInvariant(), cancellationToken);
            if (shop is null)
            {
                return Results.NotFound();
            }
            var capped = Math.Clamp(limit ?? 20, 1, 50);
            var reviews = await database.ListReviewsForShopAsync(shop.Id, capped, includeSuppressed: false, cancellationToken);
            return Results.Ok(new
            {
                shopSlug = shop.Slug,
                happyScore = shop.HappyScore,
                reviewCount = shop.ReviewCount,
                reviews = reviews.Select(MapReviewPublic)
            });
        })
        .AllowAnonymous()
        .WithTags("PublicReviews")
        .WithName("ListPublicShopReviews");
    }

    private static object MapEligibility(ReviewEligibility e) => new
    {
        eligible = e.Eligible,
        reason = e.Reason,
        booking = e.Booking is null ? null : (object)new
        {
            bookingToken = e.Booking.BookingToken,
            slotTime = e.Booking.SlotTime,
            customerName = MaskCustomer(e.Booking.CustomerName),
            status = e.Booking.Status
        },
        shop = e.Shop is null ? null : (object)new
        {
            id = e.Shop.Id,
            slug = e.Shop.Slug,
            name = e.Shop.Name,
            address = e.Shop.Address
        },
        existing = e.Existing is null ? null : MapReviewPublic(e.Existing)
    };

    internal static object MapReviewPublic(ReviewRecord r) => new
    {
        id = r.Id,
        rating = r.Rating,
        comment = r.Comment,
        shopReply = r.ShopReply,
        shopRepliedAt = r.ShopRepliedAt,
        createdAt = r.CreatedAt,
        customerDisplay = r.CustomerDisplay
    };

    private static string MaskCustomer(string raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0) return string.Empty;
        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            return parts[0].Length <= 1 ? parts[0] : parts[0][..1] + new string('*', parts[0].Length - 1);
        }
        var initial = parts[1].Length > 0 ? parts[1][..1] + "." : string.Empty;
        return $"{parts[0]} {initial}".Trim();
    }
}
