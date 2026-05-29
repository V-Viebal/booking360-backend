using Npgsql;
using NpgsqlTypes;

namespace Booking360.Api.Infrastructure;

public sealed record CurrentUserInfo(
    string Subject,
    string Email,
    string Username,
    string DisplayName,
    string[] Roles,
    string[] Scopes);

public sealed record Booking360UserRecord(
    string Subject,
    string Email,
    string Username,
    string DisplayName,
    string[] Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    bool WasCreated);

public sealed record StoredAssetRecord(
    Guid Id,
    string OwnerSubject,
    string OriginalFileName,
    string ObjectKey,
    string ContentType,
    long SizeBytes,
    string BucketName,
    DateTimeOffset CreatedAt,
    string OwnerDisplayName);

public sealed record ResourceRecord(
    Guid Id,
    string Slug,
    string Name,
    string Description,
    string Location,
    int Capacity,
    decimal HourlyRate,
    bool IsActive,
    DateTimeOffset CreatedAt);

public sealed record BookingRecord(
    Guid Id,
    Guid ResourceId,
    string ResourceName,
    string OwnerSubject,
    string OwnerDisplayName,
    string Title,
    string? Notes,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record AdminOverview(
    long UserCount,
    long ResourceCount,
    long BookingCount,
    long AssetCount,
    IReadOnlyList<Booking360UserRecord> LatestUsers,
    IReadOnlyList<BookingRecord> LatestBookings);

public sealed partial class Booking360Database
{
    private readonly NpgsqlDataSource _dataSource;

    public Booking360Database(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var bootstrap = new NpgsqlCommand(
            """
            create table if not exists schema_migrations (
                version text primary key,
                applied_at timestamptz not null default timezone('utc', now())
            );
            """,
            connection))
        {
            await bootstrap.ExecuteNonQueryAsync(cancellationToken);
        }

        await ApplyMigrationAsync(
            connection,
            "001_foundation_identity_assets",
            """
            create table if not exists app_users (
                subject text primary key,
                email text not null,
                username text not null,
                display_name text not null,
                roles text[] not null default array[]::text[],
                created_at timestamptz not null default timezone('utc', now()),
                last_seen_at timestamptz not null default timezone('utc', now())
            );

            create table if not exists stored_assets (
                id uuid primary key,
                owner_subject text not null references app_users(subject) on delete cascade,
                original_file_name text not null,
                object_key text not null unique,
                content_type text not null,
                size_bytes bigint not null,
                bucket_name text not null,
                created_at timestamptz not null default timezone('utc', now())
            );

            create index if not exists idx_stored_assets_owner_subject on stored_assets(owner_subject);
            create index if not exists idx_stored_assets_created_at on stored_assets(created_at desc);
            """,
            cancellationToken);

        await ApplyMigrationAsync(
            connection,
            "002_resources_bookings",
            """
            create table if not exists resources (
                id uuid primary key,
                slug text not null unique,
                name text not null,
                description text not null default '',
                location text not null default '',
                capacity int not null default 1,
                hourly_rate numeric(10,2) not null default 0,
                is_active boolean not null default true,
                created_at timestamptz not null default timezone('utc', now())
            );

            create table if not exists bookings (
                id uuid primary key,
                resource_id uuid not null references resources(id) on delete cascade,
                owner_subject text not null references app_users(subject) on delete cascade,
                title text not null,
                notes text,
                start_at timestamptz not null,
                end_at timestamptz not null,
                status text not null default 'confirmed',
                created_at timestamptz not null default timezone('utc', now()),
                check (end_at > start_at)
            );

            create index if not exists idx_bookings_resource on bookings(resource_id, start_at);
            create index if not exists idx_bookings_owner on bookings(owner_subject, start_at desc);

            create table if not exists booking_assets (
                id uuid primary key,
                booking_id uuid not null references bookings(id) on delete cascade,
                asset_id uuid not null references stored_assets(id) on delete cascade,
                note text,
                created_at timestamptz not null default timezone('utc', now()),
                unique (booking_id, asset_id)
            );

            insert into resources (id, slug, name, description, location, capacity, hourly_rate, is_active)
            values
              (gen_random_uuid(), 'studio-aurora', 'Studio Aurora', 'Photo and video studio with cyclorama and natural light.', 'District 3 - HCMC', 6, 35.00, true),
              (gen_random_uuid(), 'meeting-room-orion', 'Meeting Room Orion', '10-seat meeting room with 4K display and whiteboard.', 'District 1 - HCMC', 10, 18.00, true),
              (gen_random_uuid(), 'workshop-nebula', 'Workshop Nebula', 'Hands-on workshop floor with movable benches.', 'Thu Duc City', 24, 22.50, true)
            on conflict (slug) do nothing;
            """,
            cancellationToken);

        await ApplyMigrationAsync(
            connection,
            "003_book360_core",
            """
            create table if not exists shops (
                id uuid primary key default gen_random_uuid(),
                slug text not null unique,
                name text not null,
                phone text not null,
                address text not null default '',
                lat double precision,
                lng double precision,
                open_time time not null default '09:00',
                close_time time not null default '20:00',
                working_days int[] not null default array[1,2,3,4,5,6,0],
                slot_duration_minutes int not null default 30,
                max_online_per_slot int not null default 2,
                status text not null default 'active',
                shop_access_token uuid not null default gen_random_uuid() unique,
                zalo_user_id text,
                photo_url text,
                price_segment text,
                happy_score numeric(3,2) not null default 0,
                review_count int not null default 0,
                paused_until timestamptz,
                early_close_today time,
                cancel_count_30d int not null default 0,
                created_at timestamptz not null default timezone('utc', now()),
                updated_at timestamptz not null default timezone('utc', now())
            );

            create index if not exists idx_shops_status on shops(status);
            create index if not exists idx_shops_geo on shops(lat, lng);

            create table if not exists bookings_v2 (
                id uuid primary key default gen_random_uuid(),
                shop_id uuid not null references shops(id) on delete cascade,
                booking_token uuid not null default gen_random_uuid() unique,
                customer_name text not null,
                customer_phone text not null,
                slot_time timestamptz not null,
                note text,
                status text not null default 'confirmed',
                cancelled_by text,
                cancel_reason text,
                cancelled_at timestamptz,
                reminder_sent_at timestamptz,
                confirmed_via_reminder bool not null default false,
                no_show_marked_at timestamptz,
                review_link_sent_at timestamptz,
                created_at timestamptz not null default timezone('utc', now()),
                updated_at timestamptz not null default timezone('utc', now())
            );

            create index if not exists idx_bookings_v2_shop_slot on bookings_v2(shop_id, slot_time);
            create index if not exists idx_bookings_v2_phone on bookings_v2(customer_phone, created_at desc);
            create index if not exists idx_bookings_v2_status on bookings_v2(status, slot_time);

            create table if not exists reviews (
                id uuid primary key default gen_random_uuid(),
                booking_id uuid not null unique references bookings_v2(id) on delete cascade,
                shop_id uuid not null references shops(id) on delete cascade,
                rating int not null check (rating between 1 and 5),
                comment text,
                shop_reply text,
                shop_replied_at timestamptz,
                reported_count int not null default 0,
                weight numeric(3,2) not null default 1.0,
                created_at timestamptz not null default timezone('utc', now())
            );

            create index if not exists idx_reviews_shop on reviews(shop_id, created_at desc);

            create table if not exists notification_log (
                id uuid primary key default gen_random_uuid(),
                booking_id uuid references bookings_v2(id) on delete cascade,
                shop_id uuid references shops(id) on delete cascade,
                type text not null,
                channel text not null,
                target text not null,
                status text not null default 'pending',
                failure_reason text,
                provider_message_id text,
                sent_at timestamptz,
                delivered_at timestamptz,
                created_at timestamptz not null default timezone('utc', now())
            );

            create index if not exists idx_notif_log_booking on notification_log(booking_id, created_at desc);
            create index if not exists idx_notif_log_status on notification_log(status, created_at desc);

            create table if not exists phone_blacklist (
                phone text primary key,
                reason text,
                created_at timestamptz not null default timezone('utc', now())
            );
            """,
            cancellationToken);

        await ApplyMigrationAsync(
            connection,
            "004_w4_scheduler_state",
            """
            create table if not exists scheduler_state (
                job_name text primary key,
                last_run_at timestamptz not null,
                last_run_vn_date date,
                notes text
            );
            """,
            cancellationToken);

        await ApplyMigrationAsync(
            connection,
            "005_w7_late_cancel",
            """
            -- W7: late-cancel tracking, per-IP rate limits, phone verification.
            -- Additive only; never drops or rewrites existing rows.

            alter table bookings_v2 add column if not exists cancel_lead_minutes int;
            alter table bookings_v2 add column if not exists customer_ip inet;
            alter table bookings_v2 add column if not exists phone_verified_at timestamptz;

            -- Per-phone rate-limit lookups: one active booking + N-per-day.
            create index if not exists idx_bookings_v2_phone_status
                on bookings_v2(customer_phone, status, slot_time);
            create index if not exists idx_bookings_v2_phone_created_at
                on bookings_v2(customer_phone, created_at desc);

            -- Per-IP rate-limit lookups (sparse: nullable until W7 ships).
            create index if not exists idx_bookings_v2_ip_created_at
                on bookings_v2(customer_ip, created_at desc) where customer_ip is not null;

            -- 1-click phone verification tokens. Single-use, 25-min TTL.
            create table if not exists phone_verifications (
                id uuid primary key default gen_random_uuid(),
                token uuid not null unique default gen_random_uuid(),
                phone text not null,
                booking_id uuid references bookings_v2(id) on delete cascade,
                sent_at timestamptz not null default timezone('utc', now()),
                expires_at timestamptz not null,
                verified_at timestamptz,
                created_at timestamptz not null default timezone('utc', now())
            );
            create index if not exists idx_phone_verifications_phone
                on phone_verifications(phone, created_at desc);
            create index if not exists idx_phone_verifications_booking
                on phone_verifications(booking_id);

            -- No-show repeat tracking helper (counts last-30d no-shows per phone).
            create index if not exists idx_bookings_v2_phone_noshow
                on bookings_v2(customer_phone, no_show_marked_at desc)
                where no_show_marked_at is not null;
            """,
            cancellationToken);

        await ApplyMigrationAsync(
            connection,
            "006_w8_shop_media",
            """
            -- W8: shop media (multi-photo) + district key for GTM density.
            -- Additive only; preserves existing photo_url/price_segment columns.
            alter table shops add column if not exists photo_urls jsonb not null default '[]'::jsonb;
            alter table shops add column if not exists district text;

            create index if not exists idx_shops_district on shops(district) where district is not null;
            """,
            cancellationToken);

        await ApplyMigrationAsync(
            connection,
            "007_w11_zalo_oa",
            """
            -- W11: Zalo Official Account integration scaffold.
            -- Maps OA user_id (zalo_id) -> shop_id so OA chat commands can authenticate to a shop.
            -- Linking flow: shop owner taps "Liên kết Zalo OA" on /shop/m/{token}, gets a 6-digit
            -- pairing code, sends it to the OA -> webhook claims the code -> mapping persisted.
            -- Webhook + command parser are wired regardless of OA approval; they no-op until the
            -- BOOK360_ZALO_OA_ENABLED env flag is set after Zalo verifies the OA.

            create table if not exists shop_zalo_links (
                id uuid primary key default gen_random_uuid(),
                shop_id uuid not null references shops(id) on delete cascade,
                zalo_id text not null,                                  -- Zalo OA user_id (sender)
                pairing_code text,                                      -- 6-digit code while pending
                pairing_expires_at timestamptz,
                linked_at timestamptz,
                last_command_at timestamptz,
                created_at timestamptz not null default timezone('utc', now())
            );
            create unique index if not exists ux_shop_zalo_links_zalo
                on shop_zalo_links(zalo_id) where linked_at is not null;
            create index if not exists idx_shop_zalo_links_shop on shop_zalo_links(shop_id);
            create unique index if not exists ux_shop_zalo_links_pairing
                on shop_zalo_links(pairing_code) where pairing_code is not null;

            -- Audit log: every inbound OA event + every outbound command result.
            create table if not exists zalo_oa_events (
                id uuid primary key default gen_random_uuid(),
                direction text not null check (direction in ('in','out')),
                zalo_id text,
                shop_id uuid references shops(id) on delete set null,
                event_type text not null,                               -- 'text','command','reply','error'
                command text,                                           -- nghi|day|giam|mo|dong|lich|null
                payload jsonb,
                outcome text,
                created_at timestamptz not null default timezone('utc', now())
            );
            create index if not exists idx_zalo_oa_events_shop
                on zalo_oa_events(shop_id, created_at desc);
            create index if not exists idx_zalo_oa_events_zalo
                on zalo_oa_events(zalo_id, created_at desc);
            """,
            cancellationToken);
    }
    private static async Task ApplyMigrationAsync(NpgsqlConnection connection, string version, string script, CancellationToken cancellationToken)
    {
        await using (var check = new NpgsqlCommand("select 1 from schema_migrations where version = @v", connection))
        {
            check.Parameters.AddWithValue("v", version);
            var existing = await check.ExecuteScalarAsync(cancellationToken);
            if (existing is not null)
            {
                return;
            }
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var apply = new NpgsqlCommand(script, connection, transaction))
            {
                await apply.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var record = new NpgsqlCommand("insert into schema_migrations (version) values (@v)", connection, transaction))
            {
                record.Parameters.AddWithValue("v", version);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<Booking360UserRecord> UpsertUserAsync(CurrentUserInfo user, CancellationToken cancellationToken = default)
    {
        const string sql = """
            with upsert as (
                insert into app_users (subject, email, username, display_name, roles)
                values (@subject, @email, @username, @display_name, @roles)
                on conflict (subject) do update set
                    email = excluded.email,
                    username = excluded.username,
                    display_name = excluded.display_name,
                    roles = excluded.roles,
                    last_seen_at = timezone('utc', now())
                returning subject, email, username, display_name, roles, created_at, last_seen_at,
                          (xmax = 0) as inserted
            )
            select subject, email, username, display_name, roles, created_at, last_seen_at, inserted from upsert;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("subject", user.Subject);
        command.Parameters.AddWithValue("email", user.Email);
        command.Parameters.AddWithValue("username", user.Username);
        command.Parameters.AddWithValue("display_name", user.DisplayName);
        command.Parameters.Add(new NpgsqlParameter<string[]>("roles", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = user.Roles
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new Booking360UserRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<string[]>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetBoolean(7));
    }

    public async Task<StoredAssetRecord> CreateAssetAsync(
        string ownerSubject,
        string originalFileName,
        string objectKey,
        string contentType,
        long sizeBytes,
        string bucketName,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            insert into stored_assets (id, owner_subject, original_file_name, object_key, content_type, size_bytes, bucket_name)
            values (@id, @owner_subject, @original_file_name, @object_key, @content_type, @size_bytes, @bucket_name)
            returning id, owner_subject, original_file_name, object_key, content_type, size_bytes, bucket_name, created_at;
            """;

        var assetId = Guid.NewGuid();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", assetId);
        command.Parameters.AddWithValue("owner_subject", ownerSubject);
        command.Parameters.AddWithValue("original_file_name", originalFileName);
        command.Parameters.AddWithValue("object_key", objectKey);
        command.Parameters.AddWithValue("content_type", contentType);
        command.Parameters.AddWithValue("size_bytes", sizeBytes);
        command.Parameters.AddWithValue("bucket_name", bucketName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return MapAsset(reader, ownerDisplayName: string.Empty);
    }

    public async Task<IReadOnlyList<StoredAssetRecord>> ListAssetsAsync(
        string ownerSubject,
        bool includeAll,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var sql = includeAll
            ? """
                select a.id, a.owner_subject, a.original_file_name, a.object_key, a.content_type,
                       a.size_bytes, a.bucket_name, a.created_at, u.display_name
                  from stored_assets a
                  join app_users u on u.subject = a.owner_subject
                 order by a.created_at desc
                 limit @limit;
              """
            : """
                select a.id, a.owner_subject, a.original_file_name, a.object_key, a.content_type,
                       a.size_bytes, a.bucket_name, a.created_at, u.display_name
                  from stored_assets a
                  join app_users u on u.subject = a.owner_subject
                 where a.owner_subject = @owner
                 order by a.created_at desc
                 limit @limit;
              """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);
        if (!includeAll)
        {
            command.Parameters.AddWithValue("owner", ownerSubject);
        }

        var items = new List<StoredAssetRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapAssetWithOwner(reader));
        }
        return items;
    }

    public async Task<StoredAssetRecord?> GetAssetAsync(
        Guid assetId,
        string ownerSubject,
        bool includeAll,
        CancellationToken cancellationToken = default)
    {
        var sql = includeAll
            ? """
                select a.id, a.owner_subject, a.original_file_name, a.object_key, a.content_type,
                       a.size_bytes, a.bucket_name, a.created_at, u.display_name
                  from stored_assets a
                  join app_users u on u.subject = a.owner_subject
                 where a.id = @id;
              """
            : """
                select a.id, a.owner_subject, a.original_file_name, a.object_key, a.content_type,
                       a.size_bytes, a.bucket_name, a.created_at, u.display_name
                  from stored_assets a
                  join app_users u on u.subject = a.owner_subject
                 where a.id = @id and a.owner_subject = @owner;
              """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", assetId);
        if (!includeAll)
        {
            command.Parameters.AddWithValue("owner", ownerSubject);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapAssetWithOwner(reader) : null;
    }
    public async Task<IReadOnlyList<ResourceRecord>> ListResourcesAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        var sql = includeInactive
            ? "select id, slug, name, description, location, capacity, hourly_rate, is_active, created_at from resources order by name;"
            : "select id, slug, name, description, location, capacity, hourly_rate, is_active, created_at from resources where is_active = true order by name;";

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var items = new List<ResourceRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapResource(reader));
        }
        return items;
    }

    public async Task<ResourceRecord?> GetResourceAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "select id, slug, name, description, location, capacity, hourly_rate, is_active, created_at from resources where id = @id;",
            connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapResource(reader) : null;
    }

    public async Task<ResourceRecord> CreateResourceAsync(
        string slug,
        string name,
        string description,
        string location,
        int capacity,
        decimal hourlyRate,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            insert into resources (id, slug, name, description, location, capacity, hourly_rate, is_active)
            values (@id, @slug, @name, @description, @location, @capacity, @hourly_rate, @is_active)
            returning id, slug, name, description, location, capacity, hourly_rate, is_active, created_at;
            """;

        var id = Guid.NewGuid();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("description", description);
        command.Parameters.AddWithValue("location", location);
        command.Parameters.AddWithValue("capacity", capacity);
        command.Parameters.AddWithValue("hourly_rate", hourlyRate);
        command.Parameters.AddWithValue("is_active", isActive);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return MapResource(reader);
    }

    public async Task<ResourceRecord?> UpdateResourceAsync(
        Guid id,
        string name,
        string description,
        string location,
        int capacity,
        decimal hourlyRate,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            update resources
               set name = @name, description = @description, location = @location,
                   capacity = @capacity, hourly_rate = @hourly_rate, is_active = @is_active
             where id = @id
            returning id, slug, name, description, location, capacity, hourly_rate, is_active, created_at;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("description", description);
        command.Parameters.AddWithValue("location", location);
        command.Parameters.AddWithValue("capacity", capacity);
        command.Parameters.AddWithValue("hourly_rate", hourlyRate);
        command.Parameters.AddWithValue("is_active", isActive);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapResource(reader) : null;
    }

    public async Task<bool> DeleteResourceAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("delete from resources where id = @id;", connection);
        command.Parameters.AddWithValue("id", id);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }
    public async Task<IReadOnlyList<BookingRecord>> ListBookingsAsync(
        string ownerSubject,
        bool includeAll,
        Guid? resourceId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var clauses = new List<string>();
        if (!includeAll)
        {
            clauses.Add("b.owner_subject = @owner");
        }
        if (resourceId.HasValue)
        {
            clauses.Add("b.resource_id = @resourceId");
        }
        if (from.HasValue)
        {
            clauses.Add("b.end_at >= @from");
        }
        if (to.HasValue)
        {
            clauses.Add("b.start_at <= @to");
        }

        var where = clauses.Count == 0 ? string.Empty : "where " + string.Join(" and ", clauses);
        var sql = $"""
            select b.id, b.resource_id, r.name, b.owner_subject, u.display_name,
                   b.title, b.notes, b.start_at, b.end_at, b.status, b.created_at
              from bookings b
              join resources r on r.id = b.resource_id
              join app_users u on u.subject = b.owner_subject
              {where}
             order by b.start_at desc
             limit @limit;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("limit", limit);
        if (!includeAll)
        {
            command.Parameters.AddWithValue("owner", ownerSubject);
        }
        if (resourceId.HasValue)
        {
            command.Parameters.AddWithValue("resourceId", resourceId.Value);
        }
        if (from.HasValue)
        {
            command.Parameters.AddWithValue("from", from.Value.UtcDateTime);
        }
        if (to.HasValue)
        {
            command.Parameters.AddWithValue("to", to.Value.UtcDateTime);
        }

        var items = new List<BookingRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapBooking(reader));
        }
        return items;
    }

    public async Task<BookingRecord?> GetBookingAsync(
        Guid id,
        string ownerSubject,
        bool includeAll,
        CancellationToken cancellationToken = default)
    {
        var sql = includeAll
            ? """
                select b.id, b.resource_id, r.name, b.owner_subject, u.display_name,
                       b.title, b.notes, b.start_at, b.end_at, b.status, b.created_at
                  from bookings b
                  join resources r on r.id = b.resource_id
                  join app_users u on u.subject = b.owner_subject
                 where b.id = @id;
              """
            : """
                select b.id, b.resource_id, r.name, b.owner_subject, u.display_name,
                       b.title, b.notes, b.start_at, b.end_at, b.status, b.created_at
                  from bookings b
                  join resources r on r.id = b.resource_id
                  join app_users u on u.subject = b.owner_subject
                 where b.id = @id and b.owner_subject = @owner;
              """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        if (!includeAll)
        {
            command.Parameters.AddWithValue("owner", ownerSubject);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapBooking(reader) : null;
    }

    public async Task<bool> HasOverlapAsync(
        Guid resourceId,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        Guid? excludeBookingId,
        CancellationToken cancellationToken = default)
    {
        var sql = """
            select exists (
                select 1 from bookings
                 where resource_id = @resource_id
                   and status <> 'cancelled'
                   and start_at < @end_at
                   and end_at > @start_at
                   and (@exclude::uuid is null or id <> @exclude::uuid)
            );
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("resource_id", resourceId);
        command.Parameters.AddWithValue("start_at", startAt.UtcDateTime);
        command.Parameters.AddWithValue("end_at", endAt.UtcDateTime);
        command.Parameters.AddWithValue("exclude", (object?)excludeBookingId ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is bool exists && exists;
    }

    public async Task<BookingRecord?> CreateBookingAsync(
        Guid resourceId,
        string ownerSubject,
        string title,
        string? notes,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            with inserted as (
                insert into bookings (id, resource_id, owner_subject, title, notes, start_at, end_at, status)
                values (@id, @resource_id, @owner, @title, @notes, @start_at, @end_at, 'confirmed')
                returning id
            )
            select b.id, b.resource_id, r.name, b.owner_subject, u.display_name,
                   b.title, b.notes, b.start_at, b.end_at, b.status, b.created_at
              from inserted i
              join bookings b on b.id = i.id
              join resources r on r.id = b.resource_id
              join app_users u on u.subject = b.owner_subject;
            """;
        var id = Guid.NewGuid();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("resource_id", resourceId);
        command.Parameters.AddWithValue("owner", ownerSubject);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("notes", (object?)notes ?? DBNull.Value);
        command.Parameters.AddWithValue("start_at", startAt.UtcDateTime);
        command.Parameters.AddWithValue("end_at", endAt.UtcDateTime);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapBooking(reader) : null;
    }

    public async Task<BookingRecord?> CancelBookingAsync(
        Guid id,
        string ownerSubject,
        bool includeAll,
        CancellationToken cancellationToken = default)
    {
        var sql = includeAll
            ? """
                with updated as (
                    update bookings set status = 'cancelled'
                     where id = @id and status <> 'cancelled'
                    returning id
                )
                select b.id, b.resource_id, r.name, b.owner_subject, u.display_name,
                       b.title, b.notes, b.start_at, b.end_at, b.status, b.created_at
                  from updated up
                  join bookings b on b.id = up.id
                  join resources r on r.id = b.resource_id
                  join app_users u on u.subject = b.owner_subject;
              """
            : """
                with updated as (
                    update bookings set status = 'cancelled'
                     where id = @id and owner_subject = @owner and status <> 'cancelled'
                    returning id
                )
                select b.id, b.resource_id, r.name, b.owner_subject, u.display_name,
                       b.title, b.notes, b.start_at, b.end_at, b.status, b.created_at
                  from updated up
                  join bookings b on b.id = up.id
                  join resources r on r.id = b.resource_id
                  join app_users u on u.subject = b.owner_subject;
              """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        if (!includeAll)
        {
            command.Parameters.AddWithValue("owner", ownerSubject);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapBooking(reader) : null;
    }

    public async Task<bool> AttachAssetToBookingAsync(
        Guid bookingId,
        Guid assetId,
        string? note,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            insert into booking_assets (id, booking_id, asset_id, note)
            values (gen_random_uuid(), @booking_id, @asset_id, @note)
            on conflict (booking_id, asset_id) do update set note = excluded.note;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("booking_id", bookingId);
        command.Parameters.AddWithValue("asset_id", assetId);
        command.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task<IReadOnlyList<StoredAssetRecord>> ListBookingAssetsAsync(
        Guid bookingId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            select a.id, a.owner_subject, a.original_file_name, a.object_key, a.content_type,
                   a.size_bytes, a.bucket_name, a.created_at, u.display_name
              from booking_assets ba
              join stored_assets a on a.id = ba.asset_id
              join app_users u on u.subject = a.owner_subject
             where ba.booking_id = @booking_id
             order by ba.created_at desc;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("booking_id", bookingId);
        var items = new List<StoredAssetRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapAssetWithOwner(reader));
        }
        return items;
    }
    public async Task<AdminOverview> GetAdminOverviewAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        long users, resources, bookings, assets;
        await using (var counts = new NpgsqlCommand(
            """
            select
                (select count(*) from app_users),
                (select count(*) from resources),
                (select count(*) from bookings),
                (select count(*) from stored_assets);
            """,
            connection))
        await using (var reader = await counts.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            users = reader.GetInt64(0);
            resources = reader.GetInt64(1);
            bookings = reader.GetInt64(2);
            assets = reader.GetInt64(3);
        }

        var latestUsers = new List<Booking360UserRecord>();
        await using (var usersCmd = new NpgsqlCommand(
            "select subject, email, username, display_name, roles, created_at, last_seen_at from app_users order by last_seen_at desc limit 5;",
            connection))
        await using (var reader = await usersCmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                latestUsers.Add(new Booking360UserRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetFieldValue<string[]>(4),
                    reader.GetFieldValue<DateTimeOffset>(5),
                    reader.GetFieldValue<DateTimeOffset>(6),
                    WasCreated: false));
            }
        }

        var latestBookings = new List<BookingRecord>();
        await using (var bookingsCmd = new NpgsqlCommand(
            """
            select b.id, b.resource_id, r.name, b.owner_subject, u.display_name,
                   b.title, b.notes, b.start_at, b.end_at, b.status, b.created_at
              from bookings b
              join resources r on r.id = b.resource_id
              join app_users u on u.subject = b.owner_subject
             order by b.created_at desc
             limit 5;
            """,
            connection))
        await using (var reader = await bookingsCmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                latestBookings.Add(MapBooking(reader));
            }
        }

        return new AdminOverview(users, resources, bookings, assets, latestUsers, latestBookings);
    }

    private static StoredAssetRecord MapAsset(NpgsqlDataReader reader, string ownerDisplayName)
    {
        return new StoredAssetRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            ownerDisplayName);
    }

    private static StoredAssetRecord MapAssetWithOwner(NpgsqlDataReader reader)
    {
        return new StoredAssetRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? string.Empty : reader.GetString(8));
    }

    private static ResourceRecord MapResource(NpgsqlDataReader reader)
    {
        return new ResourceRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetDecimal(6),
            reader.GetBoolean(7),
            reader.GetFieldValue<DateTimeOffset>(8));
    }

    private static BookingRecord MapBooking(NpgsqlDataReader reader)
    {
        return new BookingRecord(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetString(9),
            reader.GetFieldValue<DateTimeOffset>(10));
    }
}