using Npgsql;
using NpgsqlTypes;

namespace Booking360.Api.Infrastructure;

/// <summary>
/// W12 Shop owner self-service recovery primitives.
///
/// Flow:
///   1) Owner POSTs phone to /api/shop/recovery/request → creates a 6-digit code,
///      enqueues SMS/zns/email delivery (via NotificationDispatcher).
///   2) Owner POSTs phone+code to /api/shop/recovery/claim → rotates shop_access_token
///      and returns the new /shop/m/{token} link.
///
/// Rate limit: at most 3 active codes per phone within a 15-minute window.
/// Codes expire after 10 minutes; failed claims increment attempt_count and 5+ attempts
/// invalidate that code.
/// </summary>
public sealed partial class Booking360Database
{
    public sealed record ShopRecoveryRequestResult(
        bool Created,
        string? FailureReason,
        Guid? ShopId,
        string? Code,
        DateTimeOffset? ExpiresAt);

    public sealed record ShopRecoveryClaimResult(
        bool Ok,
        string? FailureReason,
        Guid? NewShopAccessToken);

    public async Task<ShopRecoveryRequestResult> RequestShopRecoveryAsync(
        string phone,
        string? requestIp,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        var ttlValue = ttl ?? TimeSpan.FromMinutes(10);
        var trimmed = phone?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            return new ShopRecoveryRequestResult(false, "phone_required", null, null, null);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // 1) find shop by phone
        Guid? shopId = null;
        await using (var lookup = new NpgsqlCommand("select id from shops where phone = @phone limit 1", connection))
        {
            lookup.Parameters.AddWithValue("phone", trimmed);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                shopId = reader.GetGuid(0);
            }
        }
        if (shopId is null)
        {
            // Don't leak whether phone exists; rate-limit check still applies via subsequent calls.
            return new ShopRecoveryRequestResult(false, "shop_not_found", null, null, null);
        }

        // 2) rate limit: max 3 in 15 min
        await using (var rl = new NpgsqlCommand(
            """
            select count(*) from shop_recovery_codes
             where phone = @phone and created_at > timezone('utc', now()) - interval '15 minutes'
            """, connection))
        {
            rl.Parameters.AddWithValue("phone", trimmed);
            var n = (long)(await rl.ExecuteScalarAsync(cancellationToken) ?? 0L);
            if (n >= 3)
            {
                return new ShopRecoveryRequestResult(false, "rate_limited", shopId, null, null);
            }
        }

        // 3) generate code + persist
        var code = GenerateRecoveryCode();
        var expires = DateTimeOffset.UtcNow.Add(ttlValue);
        await using (var insert = new NpgsqlCommand(
            """
            insert into shop_recovery_codes (shop_id, phone, code, expires_at, request_ip)
            values (@shop_id, @phone, @code, @expires,
                    case when @ip is null or @ip = '' then null else cast(@ip as inet) end)
            """, connection))
        {
            insert.Parameters.AddWithValue("shop_id", shopId.Value);
            insert.Parameters.AddWithValue("phone", trimmed);
            insert.Parameters.AddWithValue("code", code);
            insert.Parameters.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz) { Value = expires.UtcDateTime });
            insert.Parameters.AddWithValue("ip", (object?)requestIp ?? string.Empty);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        return new ShopRecoveryRequestResult(true, null, shopId.Value, code, expires);
    }

    public async Task<ShopRecoveryClaimResult> ClaimShopRecoveryAsync(
        string phone,
        string code,
        CancellationToken cancellationToken = default)
    {
        var trimmedPhone = phone?.Trim() ?? string.Empty;
        var trimmedCode = code?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmedPhone) || string.IsNullOrEmpty(trimmedCode))
        {
            return new ShopRecoveryClaimResult(false, "missing_input", null);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        Guid? shopId = null;
        Guid? recoveryId = null;
        int attempts = 0;
        await using (var find = new NpgsqlCommand(
            """
            select id, shop_id, attempt_count
              from shop_recovery_codes
             where phone = @phone and code = @code
               and claimed_at is null
               and expires_at > timezone('utc', now())
               and attempt_count < 5
             order by created_at desc
             limit 1
             for update
            """, connection, (NpgsqlTransaction)tx))
        {
            find.Parameters.AddWithValue("phone", trimmedPhone);
            find.Parameters.AddWithValue("code", trimmedCode);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                recoveryId = reader.GetGuid(0);
                shopId = reader.GetGuid(1);
                attempts = reader.GetInt32(2);
            }
        }

        if (recoveryId is null)
        {
            // Increment attempt_count on the most recent unexpired code for this phone (any code
            // value), so brute-force attempts get throttled even with wrong codes.
            await using (var bump = new NpgsqlCommand(
                """
                update shop_recovery_codes
                   set attempt_count = attempt_count + 1
                 where id = (
                    select id from shop_recovery_codes
                     where phone = @phone and claimed_at is null
                       and expires_at > timezone('utc', now())
                     order by created_at desc
                     limit 1)
                """, connection, (NpgsqlTransaction)tx))
            {
                bump.Parameters.AddWithValue("phone", trimmedPhone);
                await bump.ExecuteNonQueryAsync(cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
            return new ShopRecoveryClaimResult(false, "invalid_or_expired", null);
        }

        // Mark claimed + rotate token in one transaction.
        Guid newToken;
        await using (var rotate = new NpgsqlCommand(
            """
            update shops
               set shop_access_token = gen_random_uuid(),
                   updated_at = timezone('utc', now())
             where id = @shop_id
            returning shop_access_token
            """, connection, (NpgsqlTransaction)tx))
        {
            rotate.Parameters.AddWithValue("shop_id", shopId!.Value);
            var result = await rotate.ExecuteScalarAsync(cancellationToken);
            if (result is not Guid token)
            {
                await tx.RollbackAsync(cancellationToken);
                return new ShopRecoveryClaimResult(false, "rotation_failed", null);
            }
            newToken = token;
        }

        await using (var mark = new NpgsqlCommand(
            "update shop_recovery_codes set claimed_at = timezone('utc', now()) where id = @id",
            connection, (NpgsqlTransaction)tx))
        {
            mark.Parameters.AddWithValue("id", recoveryId.Value);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return new ShopRecoveryClaimResult(true, null, newToken);
    }

    private static string GenerateRecoveryCode()
    {
        var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        var bytes = new byte[4];
        rng.GetBytes(bytes);
        var n = BitConverter.ToUInt32(bytes, 0) % 1_000_000u;
        return n.ToString("D6");
    }
}