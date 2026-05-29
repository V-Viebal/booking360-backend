using NpgsqlTypes;
using Npgsql;

namespace Booking360.Api.Infrastructure;

public sealed record ShopRecord(
    Guid Id,
    string Slug,
    string Name,
    string Phone,
    string Address,
    double? Lat,
    double? Lng,
    TimeOnly OpenTime,
    TimeOnly CloseTime,
    int[] WorkingDays,
    int SlotDurationMinutes,
    int MaxOnlinePerSlot,
    string Status,
    Guid ShopAccessToken,
    string? PhotoUrl,
    string? PriceSegment,
    decimal HappyScore,
    int ReviewCount,
    DateTimeOffset? PausedUntil,
    TimeOnly? EarlyCloseToday,
    int CancelCount30d,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ShopListItem(
    Guid Id,
    string Slug,
    string Name,
    string Address,
    double? Lat,
    double? Lng,
    string? PhotoUrl,
    string? PriceSegment,
    decimal HappyScore,
    int ReviewCount,
    string Status,
    TimeOnly OpenTime,
    TimeOnly CloseTime,
    double? DistanceKm);

public sealed record ShopRegistrationInput(
    string Name,
    string Phone,
    string Address,
    double? Lat,
    double? Lng,
    TimeOnly OpenTime,
    TimeOnly CloseTime,
    int[]? WorkingDays);

public sealed partial class Booking360Database
{
    private const string ShopColumns = "id, slug, name, phone, address, lat, lng, open_time, close_time, working_days, slot_duration_minutes, max_online_per_slot, status, shop_access_token, photo_url, price_segment, happy_score, review_count, paused_until, early_close_today, cancel_count_30d, created_at, updated_at";

    public async Task<IReadOnlyList<ShopListItem>> ListPublicShopsAsync(double? lat, double? lng, double? radiusKm, int limit, CancellationToken cancellationToken = default)
    {
        var hasGeo = lat.HasValue && lng.HasValue;
        var sql = hasGeo
            ? """
              select id, slug, name, address, lat, lng, photo_url, price_segment, happy_score, review_count, status, open_time, close_time,
                     case when lat is null or lng is null then null
                          else 6371 * 2 * asin(sqrt(power(sin(radians((@lat - lat)/2)),2) + cos(radians(lat)) * cos(radians(@lat)) * power(sin(radians((@lng - lng)/2)),2)))
                     end as distance_km
              from shops
              where status = 'active'
                and (@radius is null or (lat is not null and lng is not null and 6371 * 2 * asin(sqrt(power(sin(radians((@lat - lat)/2)),2) + cos(radians(lat)) * cos(radians(@lat)) * power(sin(radians((@lng - lng)/2)),2))) <= @radius))
              order by distance_km nulls last, happy_score desc, review_count desc
              limit @limit;
              """
            : """
              select id, slug, name, address, lat, lng, photo_url, price_segment, happy_score, review_count, status, open_time, close_time, null::double precision as distance_km
              from shops
              where status = 'active'
              order by happy_score desc, review_count desc, created_at desc
              limit @limit;
              """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);
        if (hasGeo)
        {
            command.Parameters.AddWithValue("lat", lat!.Value);
            command.Parameters.AddWithValue("lng", lng!.Value);
            command.Parameters.AddWithValue("radius", (object?)radiusKm ?? DBNull.Value);
        }

        var results = new List<ShopListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ShopListItem(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetDecimal(8),
                reader.GetInt32(9),
                reader.GetString(10),
                TimeOnly.FromTimeSpan(reader.GetFieldValue<TimeSpan>(11)),
                TimeOnly.FromTimeSpan(reader.GetFieldValue<TimeSpan>(12)),
                reader.IsDBNull(13) ? null : reader.GetDouble(13)));
        }
        return results;
    }

    public async Task<ShopRecord?> GetShopBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"select {ShopColumns} from shops where slug = @slug limit 1", connection);
        command.Parameters.AddWithValue("slug", slug);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapShop(reader) : null;
    }

    public async Task<ShopRecord?> GetShopByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"select {ShopColumns} from shops where id = @id limit 1", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapShop(reader) : null;
    }

    public async Task<ShopRecord?> GetShopByTokenAsync(Guid token, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"select {ShopColumns} from shops where shop_access_token = @t limit 1", connection);
        command.Parameters.AddWithValue("t", token);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapShop(reader) : null;
    }

    public async Task<ShopRecord> CreateShopAsync(ShopRegistrationInput input, CancellationToken cancellationToken = default)
    {
        var slug = await GenerateUniqueSlugAsync(input.Name, cancellationToken);
        var workingDays = input.WorkingDays ?? new[] { 1, 2, 3, 4, 5, 6, 0 };

        const string sql = """
            insert into shops (slug, name, phone, address, lat, lng, open_time, close_time, working_days)
            values (@slug, @name, @phone, @address, @lat, @lng, @open_time, @close_time, @working_days)
            returning id, slug, name, phone, address, lat, lng, open_time, close_time, working_days, slot_duration_minutes, max_online_per_slot, status, shop_access_token, photo_url, price_segment, happy_score, review_count, paused_until, early_close_today, cancel_count_30d, created_at, updated_at;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("name", input.Name.Trim());
        command.Parameters.AddWithValue("phone", input.Phone.Trim());
        command.Parameters.AddWithValue("address", input.Address.Trim());
        command.Parameters.AddWithValue("lat", (object?)input.Lat ?? DBNull.Value);
        command.Parameters.AddWithValue("lng", (object?)input.Lng ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("open_time", NpgsqlDbType.Time) { Value = input.OpenTime.ToTimeSpan() });
        command.Parameters.Add(new NpgsqlParameter("close_time", NpgsqlDbType.Time) { Value = input.CloseTime.ToTimeSpan() });
        command.Parameters.Add(new NpgsqlParameter<int[]>("working_days", NpgsqlDbType.Array | NpgsqlDbType.Integer) { TypedValue = workingDays });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Failed to create shop");
        }
        return MapShop(reader);
    }

    private async Task<string> GenerateUniqueSlugAsync(string name, CancellationToken cancellationToken)
    {
        var baseSlug = SlugHelper.Slugify(name);
        if (string.IsNullOrEmpty(baseSlug))
        {
            baseSlug = "shop";
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var attempt = 0;
        while (true)
        {
            var candidate = attempt == 0 ? baseSlug : $"{baseSlug}-{attempt}";
            await using var command = new NpgsqlCommand("select 1 from shops where slug = @s limit 1", connection);
            command.Parameters.AddWithValue("s", candidate);
            var existing = await command.ExecuteScalarAsync(cancellationToken);
            if (existing is null)
            {
                return candidate;
            }
            attempt++;
            if (attempt > 50)
            {
                return $"{baseSlug}-{Guid.NewGuid().ToString("N")[..6]}";
            }
        }
    }

    private static ShopRecord MapShop(NpgsqlDataReader reader)
    {
        return new ShopRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetDouble(5),
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            TimeOnly.FromTimeSpan(reader.GetFieldValue<TimeSpan>(7)),
            TimeOnly.FromTimeSpan(reader.GetFieldValue<TimeSpan>(8)),
            reader.GetFieldValue<int[]>(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetString(12),
            reader.GetGuid(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.GetDecimal(16),
            reader.GetInt32(17),
            reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18),
            reader.IsDBNull(19) ? null : TimeOnly.FromTimeSpan(reader.GetFieldValue<TimeSpan>(19)),
            reader.GetInt32(20),
            reader.GetFieldValue<DateTimeOffset>(21),
            reader.GetFieldValue<DateTimeOffset>(22));
    }
}