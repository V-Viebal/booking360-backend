using Npgsql;
using NpgsqlTypes;

namespace Booking360.Api.Infrastructure;

/// <summary>
/// W11 Zalo OA bridge primitives.
///
/// Two tables:
///   shop_zalo_links (zalo_id ↔ shop_id mapping with pairing-code linking flow)
///   zalo_oa_events  (immutable audit log for inbound + outbound events)
///
/// All methods are safe to call before Zalo OA approval — they just produce no
/// inbound traffic. The pairing flow works against /shop/m/{token} owner pages
/// regardless of OA status, so the BE is structurally ready the moment OA is verified.
/// </summary>
public sealed partial class Booking360Database
{
    public async Task<ShopZaloLinkRecord> StartZaloLinkAsync(
        Guid shopId,
        TimeSpan? pairingTtl = null,
        CancellationToken cancellationToken = default)
    {
        var ttl = pairingTtl ?? TimeSpan.FromMinutes(10);
        var code = GeneratePairingCode();
        var expires = DateTimeOffset.UtcNow.Add(ttl);

        const string sql = """
            insert into shop_zalo_links (shop_id, zalo_id, pairing_code, pairing_expires_at)
            values (@shop_id, @placeholder_zalo, @code, @expires)
            returning id, shop_id, zalo_id, pairing_code, pairing_expires_at, linked_at, last_command_at, created_at
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("shop_id", shopId);
        command.Parameters.AddWithValue("placeholder_zalo", "pending:" + Guid.NewGuid().ToString("N")[..8]);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz) { Value = expires.UtcDateTime });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadLink(reader);
    }

    public async Task<ShopZaloLinkRecord?> ClaimZaloPairingCodeAsync(
        string zaloId,
        string pairingCode,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            update shop_zalo_links
               set zalo_id = @zalo_id,
                   linked_at = timezone('utc', now()),
                   pairing_code = null,
                   pairing_expires_at = null
             where pairing_code = @code
               and pairing_expires_at > timezone('utc', now())
               and linked_at is null
            returning id, shop_id, zalo_id, pairing_code, pairing_expires_at, linked_at, last_command_at, created_at
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("zalo_id", zaloId);
        command.Parameters.AddWithValue("code", pairingCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return ReadLink(reader);
    }

    public async Task<ShopZaloLinkRecord?> GetShopForZaloIdAsync(string zaloId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select id, shop_id, zalo_id, pairing_code, pairing_expires_at, linked_at, last_command_at, created_at
              from shop_zalo_links
             where zalo_id = @zalo_id and linked_at is not null
             limit 1
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("zalo_id", zaloId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return ReadLink(reader);
    }

    public async Task TouchZaloLinkAsync(Guid linkId, CancellationToken cancellationToken = default)
    {
        const string sql = "update shop_zalo_links set last_command_at = timezone('utc', now()) where id = @id";
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", linkId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task LogZaloEventAsync(
        string direction,
        string? zaloId,
        Guid? shopId,
        string eventType,
        string? command,
        string? payloadJson,
        string? outcome,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            insert into zalo_oa_events (direction, zalo_id, shop_id, event_type, command, payload, outcome)
            values (@direction, @zalo_id, @shop_id, @event_type, @command, @payload::jsonb, @outcome)
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("direction", direction);
        cmd.Parameters.AddWithValue("zalo_id", (object?)zaloId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("shop_id", (object?)shopId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("event_type", eventType);
        cmd.Parameters.AddWithValue("command", (object?)command ?? DBNull.Value);
        cmd.Parameters.AddWithValue("payload", (object?)payloadJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("outcome", (object?)outcome ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string GeneratePairingCode()
    {
        var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        var bytes = new byte[4];
        rng.GetBytes(bytes);
        var n = BitConverter.ToUInt32(bytes, 0) % 1_000_000u;
        return n.ToString("D6");
    }

    private static ShopZaloLinkRecord ReadLink(NpgsqlDataReader reader)
    {
        return new ShopZaloLinkRecord(
            Id: reader.GetGuid(0),
            ShopId: reader.GetGuid(1),
            ZaloId: reader.GetString(2),
            PairingCode: reader.IsDBNull(3) ? null : reader.GetString(3),
            PairingExpiresAt: reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            LinkedAt: reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            LastCommandAt: reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(7));
    }
}

public sealed record ShopZaloLinkRecord(
    Guid Id,
    Guid ShopId,
    string ZaloId,
    string? PairingCode,
    DateTimeOffset? PairingExpiresAt,
    DateTimeOffset? LinkedAt,
    DateTimeOffset? LastCommandAt,
    DateTimeOffset CreatedAt);