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
    string[] PhotoUrls,
    string? District,
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
    string[] PhotoUrls,
    string? PriceSegment,
    string? District,
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
    int[]? WorkingDays,
    string? District = null);

public sealed record AdminDailyMetrics(
    long TodayCreated,
    long YesterdayCreated,
    long TodayConfirmed,
    long YesterdayConfirmed,
    long TodayNoShow,
    long YesterdayNoShow,
    long TodayShopCancel,
    long YesterdayShopCancel,
    long TodayCustomerCancel,
    long YesterdayCustomerCancel,
    decimal HappyScore30d,
    long ActiveShops);

public sealed record DistrictDensity(
    string District,
    long ShopCount,
    long Bookings30d,
    decimal HappyScore);

public sealed partial class Booking360Database
{
    private const string ShopColumns = "id, slug, name, phone, address, lat, lng, open_time, close_time, working_days, slot_duration_minutes, max_online_per_slot, status, shop_access_token, photo_url, price_segment, happy_score, review_count, paused_until, early_close_today, cancel_count_30d, coalesce(photo_urls::text, '[]'), district, created_at, updated_at";

    public async Task<IReadOnlyList<ShopListItem>> ListPublicShopsAsync(double? lat, double? lng, double? radiusKm, int limit, CancellationToken cancellationToken = default)
    {
        var hasGeo = lat.HasValue && lng.HasValue;
        var sql = hasGeo
            ? """
              select id, slug, name, address, lat, lng, photo_url, price_segment, happy_score, review_count, status, open_time, close_time,
                     case when lat is null or lng is null then null
                          else 6371 * 2 * asin(sqrt(power(sin(radians((@lat - lat)/2)),2) + cos(radians(lat)) * cos(radians(@lat)) * power(sin(radians((@lng - lng)/2)),2)))
                     end as distance_km,
                     coalesce(photo_urls::text, '[]') as photo_urls_json, district
              from shops
              where status = 'active'
                and (@radius is null or (lat is not null and lng is not null and 6371 * 2 * asin(sqrt(power(sin(radians((@lat - lat)/2)),2) + cos(radians(lat)) * cos(radians(@lat)) * power(sin(radians((@lng - lng)/2)),2))) <= @radius))
              order by distance_km nulls last, happy_score desc, review_count desc
              limit @limit;
              """
            : """
              select id, slug, name, address, lat, lng, photo_url, price_segment, happy_score, review_count, status, open_time, close_time, null::double precision as distance_km,
                     coalesce(photo_urls::text, '[]') as photo_urls_json, district
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
                ParsePhotoUrls(reader.IsDBNull(14) ? null : reader.GetString(14)),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(15) ? null : reader.GetString(15),
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
            insert into shops (slug, name, phone, address, lat, lng, open_time, close_time, working_days, district)
            values (@slug, @name, @phone, @address, @lat, @lng, @open_time, @close_time, @working_days, @district)
            returning id, slug, name, phone, address, lat, lng, open_time, close_time, working_days, slot_duration_minutes, max_online_per_slot, status, shop_access_token, photo_url, price_segment, happy_score, review_count, paused_until, early_close_today, cancel_count_30d, coalesce(photo_urls::text, '[]'), district, created_at, updated_at;
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
        command.Parameters.AddWithValue("district", (object?)(string.IsNullOrWhiteSpace(input.District) ? null : input.District!.Trim()) ?? DBNull.Value);

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
            ParsePhotoUrls(reader.IsDBNull(21) ? null : reader.GetString(21)),
            reader.IsDBNull(22) ? null : reader.GetString(22),
            reader.GetFieldValue<DateTimeOffset>(23),
            reader.GetFieldValue<DateTimeOffset>(24));
    }


    /// <summary>
    /// W8 REQ-EC-018 / SS-010 polish: shop edits its own profile media + district + price segment via /m/{token}/profile.
    /// Only non-null fields are updated. photoUrls is replaced wholesale when supplied (jsonb of strings).
    /// </summary>
    public async Task<ShopRecord?> UpdateShopProfileAsync(
        Guid shopId,
        string? priceSegment,
        string[]? photoUrls,
        string? district,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            update shops
               set price_segment = coalesce(@price_segment, price_segment),
                   photo_urls    = case when @photo_urls_json is null then photo_urls else cast(@photo_urls_json as jsonb) end,
                   district      = coalesce(@district, district),
                   updated_at    = timezone('utc', now())
             where id = @id
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var cmd = new NpgsqlCommand(sql, connection))
        {
            cmd.Parameters.AddWithValue("id", shopId);
            cmd.Parameters.AddWithValue("price_segment", (object?)(string.IsNullOrWhiteSpace(priceSegment) ? null : priceSegment!.Trim()) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("district", (object?)(string.IsNullOrWhiteSpace(district) ? null : district!.Trim()) ?? DBNull.Value);
            if (photoUrls is null)
            {
                cmd.Parameters.AddWithValue("photo_urls_json", DBNull.Value);
            }
            else
            {
                var json = System.Text.Json.JsonSerializer.Serialize(photoUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).ToArray());
                cmd.Parameters.AddWithValue("photo_urls_json", json);
            }
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        return await GetShopByIdAsync(shopId, cancellationToken);
    }

    /// <summary>
    /// W8 REQ-GM-011/GM-012: today-vs-yesterday counters for the admin metrics dashboard.
    /// All time windows are evaluated in Asia/Ho_Chi_Minh (UTC+07:00) so "today" matches operator expectation.
    /// </summary>
    public async Task<AdminDailyMetrics> GetAdminMetricsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            with vn as (
                select (timezone('Asia/Ho_Chi_Minh', now()))::date as today,
                       (timezone('Asia/Ho_Chi_Minh', now()) - interval '1 day')::date as yesterday
            ),
            ranges as (
                select
                  (vn.today    at time zone 'Asia/Ho_Chi_Minh')                     as today_start,
                  ((vn.today + 1) at time zone 'Asia/Ho_Chi_Minh')                  as today_end,
                  (vn.yesterday at time zone 'Asia/Ho_Chi_Minh')                    as yest_start,
                  ((vn.yesterday + 1) at time zone 'Asia/Ho_Chi_Minh')              as yest_end
                from vn
            )
            select
              -- created today / yesterday
              (select count(*) from bookings_v2, ranges where created_at >= ranges.today_start and created_at < ranges.today_end) as t_created,
              (select count(*) from bookings_v2, ranges where created_at >= ranges.yest_start  and created_at < ranges.yest_end ) as y_created,
              -- confirmed (status = confirmed) created today / yesterday
              (select count(*) from bookings_v2, ranges where status = 'confirmed' and created_at >= ranges.today_start and created_at < ranges.today_end) as t_confirmed,
              (select count(*) from bookings_v2, ranges where status = 'confirmed' and created_at >= ranges.yest_start  and created_at < ranges.yest_end ) as y_confirmed,
              -- no-show marked today / yesterday
              (select count(*) from bookings_v2, ranges where no_show_marked_at >= ranges.today_start and no_show_marked_at < ranges.today_end) as t_noshow,
              (select count(*) from bookings_v2, ranges where no_show_marked_at >= ranges.yest_start  and no_show_marked_at < ranges.yest_end ) as y_noshow,
              -- cancels by shop today / yesterday
              (select count(*) from bookings_v2, ranges where cancelled_by = 'shop' and cancelled_at >= ranges.today_start and cancelled_at < ranges.today_end) as t_shop_cancel,
              (select count(*) from bookings_v2, ranges where cancelled_by = 'shop' and cancelled_at >= ranges.yest_start  and cancelled_at < ranges.yest_end ) as y_shop_cancel,
              -- cancels by customer today / yesterday
              (select count(*) from bookings_v2, ranges where cancelled_by = 'customer' and cancelled_at >= ranges.today_start and cancelled_at < ranges.today_end) as t_cust_cancel,
              (select count(*) from bookings_v2, ranges where cancelled_by = 'customer' and cancelled_at >= ranges.yest_start  and cancelled_at < ranges.yest_end ) as y_cust_cancel,
              -- happy score (avg rating across last 30d) + active shops
              coalesce((select round(avg(rating)::numeric, 2) from reviews where created_at >= now() - interval '30 days'), 0)  as happy_30d,
              (select count(*) from shops where status = 'active') as active_shops
            ;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new AdminDailyMetrics(0,0,0,0,0,0,0,0,0,0,0m,0);
        }
        return new AdminDailyMetrics(
            TodayCreated: reader.GetInt64(0),
            YesterdayCreated: reader.GetInt64(1),
            TodayConfirmed: reader.GetInt64(2),
            YesterdayConfirmed: reader.GetInt64(3),
            TodayNoShow: reader.GetInt64(4),
            YesterdayNoShow: reader.GetInt64(5),
            TodayShopCancel: reader.GetInt64(6),
            YesterdayShopCancel: reader.GetInt64(7),
            TodayCustomerCancel: reader.GetInt64(8),
            YesterdayCustomerCancel: reader.GetInt64(9),
            HappyScore30d: reader.GetDecimal(10),
            ActiveShops: reader.GetInt64(11));
    }

    /// <summary>
    /// W8 REQ-GM-001/GM-008: per-district shop count, last-30d booking volume, and weighted Happy Score.
    /// If <paramref name="district"/> is non-null, returns just that district; otherwise the full set.
    /// </summary>
    public async Task<IReadOnlyList<DistrictDensity>> GetDistrictDensityAsync(string? district, CancellationToken cancellationToken = default)
    {
        var sql = """
            select s.district,
                   count(distinct s.id)                                                        as shop_count,
                   coalesce((select count(*) from bookings_v2 b
                             where b.shop_id in (select id from shops where district = s.district)
                               and b.created_at >= now() - interval '30 days'), 0)             as bookings_30d,
                   coalesce(round(avg(s.happy_score)::numeric, 2), 0)                          as happy_score
              from shops s
             where s.district is not null
               and (@district is null or s.district = @district)
             group by s.district
             order by shop_count desc, s.district asc;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("district", (object?)district ?? DBNull.Value);
        var results = new List<DistrictDensity>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DistrictDensity(
                District: reader.GetString(0),
                ShopCount: reader.GetInt64(1),
                Bookings30d: reader.GetInt64(2),
                HappyScore: reader.GetDecimal(3)));
        }
        return results;
    }

    private static string[] ParsePhotoUrls(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return Array.Empty<string>();
            var list = new List<string>(doc.RootElement.GetArrayLength());
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
            }
            return list.ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}