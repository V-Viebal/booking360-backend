using Npgsql;
using NpgsqlTypes;

namespace Booking360.Api.Infrastructure;

public sealed record BookingV2Record(
    Guid Id,
    Guid ShopId,
    Guid BookingToken,
    string CustomerName,
    string CustomerPhone,
    DateTimeOffset SlotTime,
    string? Note,
    string Status,
    string? CancelledBy,
    string? CancelReason,
    DateTimeOffset? CancelledAt,
    DateTimeOffset CreatedAt);

public sealed record BookingCreateInput(
    Guid ShopId,
    string CustomerName,
    string CustomerPhone,
    DateTimeOffset SlotTime,
    string? Note);

public sealed record SlotAvailability(
    DateTimeOffset SlotTime,
    int OnlineCount,
    int Capacity,
    bool Available);

public sealed partial class Booking360Database
{
    private const string BookingV2Columns = "id, shop_id, booking_token, customer_name, customer_phone, slot_time, note, status, cancelled_by, cancel_reason, cancelled_at, created_at";

    public async Task<BookingV2Record> CreateBookingV2Async(BookingCreateInput input, CancellationToken cancellationToken = default)
    {
        const string sql = """
            insert into bookings_v2 (shop_id, customer_name, customer_phone, slot_time, note)
            values (@shop_id, @customer_name, @customer_phone, @slot_time, @note)
            returning id, shop_id, booking_token, customer_name, customer_phone, slot_time, note, status, cancelled_by, cancel_reason, cancelled_at, created_at;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("shop_id", input.ShopId);
        command.Parameters.AddWithValue("customer_name", input.CustomerName.Trim());
        command.Parameters.AddWithValue("customer_phone", input.CustomerPhone.Trim());
        command.Parameters.AddWithValue("slot_time", input.SlotTime.UtcDateTime);
        command.Parameters.AddWithValue("note", (object?)input.Note ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Failed to create booking");
        }
        return MapBookingV2(reader);
    }

    public async Task<BookingV2Record?> GetBookingByTokenAsync(Guid token, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"select {BookingV2Columns} from bookings_v2 where booking_token = @t limit 1", connection);
        command.Parameters.AddWithValue("t", token);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapBookingV2(reader) : null;
    }

    public async Task<BookingV2Record?> CancelBookingByTokenAsync(Guid token, string cancelledBy, string? reason, CancellationToken cancellationToken = default)
    {
        const string sql = """
            update bookings_v2
               set status = 'cancelled',
                   cancelled_by = @by,
                   cancel_reason = @reason,
                   cancelled_at = timezone('utc', now()),
                   updated_at = timezone('utc', now())
             where booking_token = @t
               and status in ('pending','confirmed')
            returning id, shop_id, booking_token, customer_name, customer_phone, slot_time, note, status, cancelled_by, cancel_reason, cancelled_at, created_at;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("t", token);
        command.Parameters.AddWithValue("by", cancelledBy);
        command.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapBookingV2(reader) : null;
    }

    public async Task<int> CountActiveBookingsForSlotAsync(Guid shopId, DateTimeOffset slotTime, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select count(*) from bookings_v2
             where shop_id = @shop_id
               and slot_time = @slot_time
               and status in ('pending','confirmed','completed');
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("shop_id", shopId);
        command.Parameters.AddWithValue("slot_time", slotTime.UtcDateTime);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result ?? 0);
    }

    public async Task<IReadOnlyList<SlotAvailability>> ListSlotsForDayAsync(ShopRecord shop, DateOnly date, CancellationToken cancellationToken = default)
    {
        // Determine the shop's effective close time for today.
        var todayInVn = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        var closeTime = (date == todayInVn && shop.EarlyCloseToday.HasValue) ? shop.EarlyCloseToday.Value : shop.CloseTime;
        var capacity = shop.MaxOnlinePerSlot;
        var slotMinutes = shop.SlotDurationMinutes <= 0 ? 30 : shop.SlotDurationMinutes;

        // Working day check (0=Sunday).
        var dow = (int)date.DayOfWeek;
        if (!shop.WorkingDays.Contains(dow))
        {
            return Array.Empty<SlotAvailability>();
        }

        var slots = new List<SlotAvailability>();
        var cursor = shop.OpenTime;
        while (cursor < closeTime)
        {
            var localDateTime = date.ToDateTime(cursor);
            // Treat shop times as Asia/Ho_Chi_Minh (+07:00).
            var slotTime = new DateTimeOffset(localDateTime, TimeSpan.FromHours(7));
            var count = await CountActiveBookingsForSlotAsync(shop.Id, slotTime, cancellationToken);
            slots.Add(new SlotAvailability(slotTime, count, capacity, count < capacity));

            cursor = cursor.AddMinutes(slotMinutes);
            if (cursor <= shop.OpenTime) break; // safety
        }
        return slots;
    }

    public async Task<IReadOnlyList<BookingV2Record>> ListBookingsForShopDayAsync(Guid shopId, DateOnly date, CancellationToken cancellationToken = default)
    {
        var startLocal = date.ToDateTime(TimeOnly.MinValue);
        var endLocal = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var startUtc = new DateTimeOffset(startLocal, TimeSpan.FromHours(7)).UtcDateTime;
        var endUtc = new DateTimeOffset(endLocal, TimeSpan.FromHours(7)).UtcDateTime;

        const string sql = """
            select id, shop_id, booking_token, customer_name, customer_phone, slot_time, note, status, cancelled_by, cancel_reason, cancelled_at, created_at
              from bookings_v2
             where shop_id = @shop_id
               and slot_time >= @start
               and slot_time <  @end
             order by slot_time asc, created_at asc;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("shop_id", shopId);
        command.Parameters.AddWithValue("start", startUtc);
        command.Parameters.AddWithValue("end", endUtc);

        var results = new List<BookingV2Record>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(MapBookingV2(reader));
        }
        return results;
    }

    public async Task<bool> UpdateShopConfigAsync(
        Guid shopId,
        TimeOnly? openTime,
        TimeOnly? closeTime,
        int[]? workingDays,
        int? slotDurationMinutes,
        int? maxOnlinePerSlot,
        TimeOnly? earlyCloseToday,
        DateTimeOffset? pausedUntil,
        CancellationToken cancellationToken = default)
    {
        var sets = new List<string>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand();
        command.Connection = connection;
        command.Parameters.AddWithValue("id", shopId);

        if (openTime.HasValue) { sets.Add("open_time = @open_time"); command.Parameters.Add(new NpgsqlParameter("open_time", NpgsqlDbType.Time) { Value = openTime.Value.ToTimeSpan() }); }
        if (closeTime.HasValue) { sets.Add("close_time = @close_time"); command.Parameters.Add(new NpgsqlParameter("close_time", NpgsqlDbType.Time) { Value = closeTime.Value.ToTimeSpan() }); }
        if (workingDays is not null) { sets.Add("working_days = @working_days"); command.Parameters.Add(new NpgsqlParameter<int[]>("working_days", NpgsqlDbType.Array | NpgsqlDbType.Integer) { TypedValue = workingDays }); }
        if (slotDurationMinutes.HasValue) { sets.Add("slot_duration_minutes = @slot_duration_minutes"); command.Parameters.AddWithValue("slot_duration_minutes", slotDurationMinutes.Value); }
        if (maxOnlinePerSlot.HasValue) { sets.Add("max_online_per_slot = @max_online_per_slot"); command.Parameters.AddWithValue("max_online_per_slot", maxOnlinePerSlot.Value); }
        if (earlyCloseToday.HasValue) { sets.Add("early_close_today = @early_close_today"); command.Parameters.Add(new NpgsqlParameter("early_close_today", NpgsqlDbType.Time) { Value = earlyCloseToday.Value.ToTimeSpan() }); }
        if (pausedUntil.HasValue) { sets.Add("paused_until = @paused_until"); command.Parameters.AddWithValue("paused_until", pausedUntil.Value.UtcDateTime); }

        if (sets.Count == 0) return true;
        sets.Add("updated_at = timezone('utc', now())");
        command.CommandText = $"update shops set {string.Join(", ", sets)} where id = @id";
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task LogNotificationAsync(
        Guid? bookingId,
        Guid? shopId,
        string type,
        string channel,
        string target,
        string status,
        string? failureReason,
        string? providerMessageId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            insert into notification_log (booking_id, shop_id, type, channel, target, status, failure_reason, provider_message_id, sent_at)
            values (@booking_id, @shop_id, @type, @channel, @target, @status, @failure_reason, @provider_message_id, case when @status = 'sent' then timezone('utc', now()) else null end);
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("booking_id", (object?)bookingId ?? DBNull.Value);
        command.Parameters.AddWithValue("shop_id", (object?)shopId ?? DBNull.Value);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("channel", channel);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("failure_reason", (object?)failureReason ?? DBNull.Value);
        command.Parameters.AddWithValue("provider_message_id", (object?)providerMessageId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static BookingV2Record MapBookingV2(NpgsqlDataReader reader)
    {
        return new BookingV2Record(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
            reader.GetFieldValue<DateTimeOffset>(11));
    }
}