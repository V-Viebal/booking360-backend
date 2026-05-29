using Npgsql;
using NpgsqlTypes;

namespace Booking360.Api.Infrastructure;

// Wave 5 — Reviews + Ratings + Happy Score.
public sealed record ReviewRecord(
    Guid Id,
    Guid BookingId,
    Guid ShopId,
    int Rating,
    string? Comment,
    string? ShopReply,
    DateTimeOffset? ShopRepliedAt,
    int ReportedCount,
    decimal Weight,
    DateTimeOffset CreatedAt,
    string? CustomerDisplay);

public sealed record ReviewSummary(
    decimal HappyScore,
    int ReviewCount,
    int Rating1Count,
    int Rating2Count,
    int Rating3Count,
    int Rating4Count,
    int Rating5Count);

public sealed record ReviewEligibility(
    bool Eligible,
    string? Reason,
    BookingV2Record? Booking,
    ShopRecord? Shop,
    ReviewRecord? Existing);

public sealed partial class Booking360Database
{
    // Soft-weight threshold: 3+ reports drops weight to 0.5; 5+ drops weight to 0.0 (effectively suppressed).
    private const int ReportSoftWeightThreshold = 3;
    private const int ReportHardWeightThreshold = 5;
    private const int HappyScoreWindowDays = 90;

    private const string ReviewColumns = "r.id, r.booking_id, r.shop_id, r.rating, r.comment, r.shop_reply, r.shop_replied_at, r.reported_count, r.weight, r.created_at, b.customer_name";

    public async Task<ReviewEligibility> GetReviewEligibilityByBookingTokenAsync(Guid bookingToken, CancellationToken cancellationToken = default)
    {
        var booking = await GetBookingByTokenAsync(bookingToken, cancellationToken);
        if (booking is null)
        {
            return new ReviewEligibility(false, "Không tìm thấy lịch", null, null, null);
        }
        var shop = await GetShopByIdAsync(booking.ShopId, cancellationToken);
        if (shop is null)
        {
            return new ReviewEligibility(false, "Quán không tồn tại", booking, null, null);
        }
        var existing = await GetReviewByBookingIdAsync(booking.Id, cancellationToken);
        if (existing is not null)
        {
            return new ReviewEligibility(false, "Đã đánh giá", booking, shop, existing);
        }
        if (string.Equals(booking.Status, "no_show", StringComparison.OrdinalIgnoreCase))
        {
            return new ReviewEligibility(false, "Lịch đánh dấu vắng mặt, không thể đánh giá", booking, shop, null);
        }
        if (booking.CancelledAt.HasValue)
        {
            return new ReviewEligibility(false, "Lịch đã huỷ, không thể đánh giá", booking, shop, null);
        }
        if (!string.Equals(booking.Status, "confirmed", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(booking.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return new ReviewEligibility(false, "Trạng thái lịch không cho phép đánh giá", booking, shop, null);
        }
        if (booking.SlotTime > DateTimeOffset.UtcNow)
        {
            return new ReviewEligibility(false, "Chỉ có thể đánh giá sau khi đến lịch", booking, shop, null);
        }
        return new ReviewEligibility(true, null, booking, shop, null);
    }

    public async Task<ReviewRecord?> GetReviewByBookingIdAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select r.id, r.booking_id, r.shop_id, r.rating, r.comment, r.shop_reply, r.shop_replied_at, r.reported_count, r.weight, r.created_at, b.customer_name
              from reviews r
              join bookings_v2 b on b.id = r.booking_id
             where r.booking_id = @bid
             limit 1;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("bid", bookingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapReview(reader) : null;
    }

    public async Task<ReviewRecord?> GetReviewByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select r.id, r.booking_id, r.shop_id, r.rating, r.comment, r.shop_reply, r.shop_replied_at, r.reported_count, r.weight, r.created_at, b.customer_name
              from reviews r
              join bookings_v2 b on b.id = r.booking_id
             where r.id = @id
             limit 1;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapReview(reader) : null;
    }

    public async Task<ReviewRecord> CreateReviewAsync(Guid bookingId, Guid shopId, int rating, string? comment, CancellationToken cancellationToken = default)
    {
        // Atomic insert — relies on `booking_id unique` to enforce 1 booking = 1 review.
        const string sql = """
            insert into reviews (booking_id, shop_id, rating, comment, weight)
            values (@bid, @sid, @rating, @comment, 1.0)
            returning id, booking_id, shop_id, rating, comment, shop_reply, shop_replied_at, reported_count, weight, created_at;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("bid", bookingId);
        command.Parameters.AddWithValue("sid", shopId);
        command.Parameters.AddWithValue("rating", rating);
        command.Parameters.AddWithValue("comment", (object?)(string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()) ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Failed to create review");
        }
        var review = new ReviewRecord(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetInt32(7),
            reader.GetDecimal(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            null);
        return review;
    }

    public async Task<ReviewRecord?> SetShopReplyAsync(Guid reviewId, Guid shopId, string reply, CancellationToken cancellationToken = default)
    {
        const string sql = """
            update reviews
               set shop_reply = @reply,
                   shop_replied_at = timezone('utc', now())
             where id = @id and shop_id = @sid
             returning id;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var update = new NpgsqlCommand(sql, connection))
        {
            update.Parameters.AddWithValue("id", reviewId);
            update.Parameters.AddWithValue("sid", shopId);
            update.Parameters.AddWithValue("reply", reply.Trim());
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
        }
        return await GetReviewByIdAsync(reviewId, cancellationToken);
    }

    public async Task<ReviewRecord?> ReportReviewAsync(Guid reviewId, CancellationToken cancellationToken = default)
    {
        // Increment reported_count and recompute weight in a single statement.
        var sql = $"""
            update reviews
               set reported_count = reported_count + 1,
                   weight = case
                       when reported_count + 1 >= {ReportHardWeightThreshold} then 0.0
                       when reported_count + 1 >= {ReportSoftWeightThreshold} then 0.5
                       else weight
                   end
             where id = @id
             returning shop_id;
            """;
        Guid? shopId = null;
        await using (var connection = await _dataSource.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("id", reviewId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                shopId = reader.GetGuid(0);
            }
        }
        if (shopId is null)
        {
            return null;
        }
        // Recalc rolling aggregates so soft/hard suppression takes effect immediately.
        await RecalculateShopHappyScoreAsync(shopId.Value, cancellationToken);
        return await GetReviewByIdAsync(reviewId, cancellationToken);
    }

    public async Task<ReviewSummary> RecalculateShopHappyScoreAsync(Guid shopId, CancellationToken cancellationToken = default)
    {
        // Weighted avg over the last 90 days, ignoring reviews whose weight is 0.
        // happy_score persisted on shops; review_count counts all non-suppressed (weight > 0) reviews regardless of window.
        var sql = $"""
            with windowed as (
                select rating, weight
                  from reviews
                 where shop_id = @sid
                   and created_at >= timezone('utc', now()) - interval '{HappyScoreWindowDays} days'
                   and weight > 0
            ),
            agg as (
                select coalesce(round((sum(rating::numeric * weight) / nullif(sum(weight), 0))::numeric, 2), 0) as happy_score
                  from windowed
            ),
            cnt as (
                select count(*)::int as total_count,
                       sum((rating=1)::int)::int as r1,
                       sum((rating=2)::int)::int as r2,
                       sum((rating=3)::int)::int as r3,
                       sum((rating=4)::int)::int as r4,
                       sum((rating=5)::int)::int as r5
                  from reviews
                 where shop_id = @sid and weight > 0
            ),
            upd as (
                update shops
                   set happy_score = (select happy_score from agg),
                       review_count = (select total_count from cnt),
                       updated_at = timezone('utc', now())
                 where id = @sid
                returning 1
            )
            select (select happy_score from agg),
                   (select total_count from cnt),
                   (select r1 from cnt),
                   (select r2 from cnt),
                   (select r3 from cnt),
                   (select r4 from cnt),
                   (select r5 from cnt);
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("sid", shopId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new ReviewSummary(0m, 0, 0, 0, 0, 0, 0);
        }
        return new ReviewSummary(
            reader.IsDBNull(0) ? 0m : reader.GetDecimal(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            reader.IsDBNull(6) ? 0 : reader.GetInt32(6));
    }

    public async Task<IReadOnlyList<ReviewRecord>> ListReviewsForShopAsync(Guid shopId, int limit, bool includeSuppressed, CancellationToken cancellationToken = default)
    {
        var weightGate = includeSuppressed ? string.Empty : "and r.weight > 0";
        var sql = $"""
            select {ReviewColumns}
              from reviews r
              join bookings_v2 b on b.id = r.booking_id
             where r.shop_id = @sid {weightGate}
             order by r.created_at desc
             limit @lim;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("sid", shopId);
        command.Parameters.AddWithValue("lim", limit);
        var results = new List<ReviewRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapReview(reader));
        }
        return results;
    }

    private static ReviewRecord MapReview(NpgsqlDataReader reader)
    {
        // Columns expected: id, booking_id, shop_id, rating, comment, shop_reply, shop_replied_at, reported_count, weight, created_at, customer_name
        return new ReviewRecord(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetInt32(7),
            reader.GetDecimal(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : MaskCustomerName(reader.GetString(10)));
    }

    private static string? MaskCustomerName(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return null;
        // Show first word + masked last name (e.g., "Nguyễn V.")
        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            return parts[0].Length <= 1 ? parts[0] : parts[0][..1] + new string('*', parts[0].Length - 1);
        }
        var last = parts[1];
        var initial = last.Length > 0 ? last[..1] + "." : string.Empty;
        return $"{parts[0]} {initial}".Trim();
    }
}
