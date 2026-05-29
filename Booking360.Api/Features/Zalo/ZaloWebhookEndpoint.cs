using Booking360.Api.Abstractions;
using Booking360.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Booking360.Api.Features.Zalo;

/// <summary>
/// W11 — Zalo Official Account webhook + owner-side pairing endpoints.
///
/// /api/zalo/webhook is the inbound entry the OA platform will POST to once Zalo
/// approves the OA. The handler is mounted regardless of approval status; it
/// no-ops cleanly when the OA isn't yet verified (no traffic arrives) but is
/// fully wired so flipping BOOK360_ZALO_OA_ENABLED activates it without code change.
///
/// /api/shop/m/{token}/zalo/pair-start + /pair-status let the shop owner
/// initiate linking from the existing W6 management page. The pairing-code
/// flow is provider-agnostic: works against any messaging channel that can
/// echo the 6-digit code into the OA chat.
/// </summary>
public sealed class ZaloWebhookEndpoint : IEndpoint
{
    public sealed record WebhookRequest(string? sender_id, string? message_text, string? event_name);
    public sealed record PairingStartResponse(string PairingCode, DateTimeOffset ExpiresAt, string Instructions);
    public sealed record PairingStatusResponse(bool Linked, string? ZaloId, DateTimeOffset? LinkedAt, DateTimeOffset? LastCommandAt);

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // === Inbound OA webhook ===
        routeBuilder.MapPost("/api/zalo/webhook", async (
            HttpContext httpContext,
            Booking360Database database,
            ZaloCommandExecutor executor,
            IConfiguration config,
            ILogger<ZaloWebhookEndpoint> logger,
            CancellationToken cancellationToken) =>
        {
            // Read body once so we can audit it even on auth failure.
            httpContext.Request.EnableBuffering();
            using var reader = new StreamReader(httpContext.Request.Body, leaveOpen: true);
            var rawBody = await reader.ReadToEndAsync(cancellationToken);
            httpContext.Request.Body.Position = 0;

            // Accept either flat {sender_id, message_text} or nested OA payloads.
            string? senderId = null;
            string? messageText = null;
            string? eventName = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawBody) ? "{}" : rawBody);
                var root = doc.RootElement;
                if (root.TryGetProperty("sender_id", out var s)) senderId = s.GetString();
                else if (root.TryGetProperty("sender", out var sObj) && sObj.ValueKind == JsonValueKind.Object && sObj.TryGetProperty("id", out var sId)) senderId = sId.GetString();
                if (root.TryGetProperty("message_text", out var m)) messageText = m.GetString();
                else if (root.TryGetProperty("message", out var mObj) && mObj.ValueKind == JsonValueKind.Object && mObj.TryGetProperty("text", out var mTxt)) messageText = mTxt.GetString();
                if (root.TryGetProperty("event_name", out var e)) eventName = e.GetString();
            }
            catch (JsonException)
            {
                logger.LogWarning("Zalo webhook received non-JSON body (len={Len})", rawBody.Length);
            }

            // Always log inbound — useful for the gated period (we'll see test pings even before OA approval).
            await database.LogZaloEventAsync(
                direction: "in",
                zaloId: senderId,
                shopId: null,
                eventType: eventName ?? "text",
                command: null,
                payloadJson: rawBody.Length > 8192 ? rawBody[..8192] : rawBody,
                outcome: "received",
                cancellationToken: cancellationToken);

            // Feature flag gate. Until Zalo approves the OA, we 200 OK to keep their
            // platform happy but do not run any side-effecting command logic.
            var enabled = string.Equals(config["BOOK360_ZALO_OA_ENABLED"], "true", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(Environment.GetEnvironmentVariable("BOOK360_ZALO_OA_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
            if (!enabled)
            {
                return Results.Ok(new { ok = true, gated = true, reason = "BOOK360_ZALO_OA_ENABLED=false" });
            }

            if (string.IsNullOrWhiteSpace(senderId) || string.IsNullOrWhiteSpace(messageText))
            {
                return Results.Ok(new { ok = true, ignored = "missing_sender_or_text" });
            }

            // Pairing-code claim path: a sender who is not yet linked sends a 6-digit code.
            var trimmed = messageText.Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^\\d{6}$"))
            {
                var claimed = await database.ClaimZaloPairingCodeAsync(senderId, trimmed, cancellationToken);
                if (claimed is not null)
                {
                    return Results.Ok(new { ok = true, reply = "Đã liên kết Zalo OA với tiệm. Soạn 'help' để xem lệnh." });
                }
                return Results.Ok(new { ok = true, reply = "Mã ghép không hợp lệ hoặc đã hết hạn. Vào trang quản lý tiệm để lấy mã mới." });
            }

            // Command path: requires existing link.
            var link = await database.GetShopForZaloIdAsync(senderId, cancellationToken);
            if (link is null)
            {
                return Results.Ok(new { ok = true, reply = "Số Zalo này chưa liên kết tiệm. Vào trang quản lý tiệm và lấy mã 6 số ở mục 'Liên kết Zalo OA'." });
            }

            var parsed = ZaloCommandParser.Parse(trimmed);
            var reply = await executor.ExecuteAsync(link, parsed, cancellationToken);
            return Results.Ok(new { ok = true, reply });
        })
        .AllowAnonymous()
        .WithTags("Zalo")
        .WithName("ZaloOaWebhook");

        // === Owner-side: start pairing flow ===
        routeBuilder.MapPost("/api/shop/m/{token:guid}/zalo/pair-start", async (
            Guid token,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            var shop = await database.GetShopByTokenAsync(token, cancellationToken);
            if (shop is null) return Results.NotFound(new { error = "Liên kết quản lý không hợp lệ" });

            var link = await database.StartZaloLinkAsync(shop.Id, TimeSpan.FromMinutes(10), cancellationToken);
            return Results.Ok(new PairingStartResponse(
                PairingCode: link.PairingCode!,
                ExpiresAt: link.PairingExpiresAt!.Value,
                Instructions: "Mở chat Zalo OA Book360 và gửi mã 6 số trong 10 phút để liên kết."));
        })
        .AllowAnonymous()
        .WithTags("Zalo")
        .WithName("ZaloPairStart");

        // === Owner-side: poll pairing status ===
        routeBuilder.MapGet("/api/shop/m/{token:guid}/zalo/pair-status", async (
            Guid token,
            Booking360Database database,
            CancellationToken cancellationToken) =>
        {
            var shop = await database.GetShopByTokenAsync(token, cancellationToken);
            if (shop is null) return Results.NotFound(new { error = "Liên kết quản lý không hợp lệ" });

            // Heuristic: any *linked* row for this shop counts. Reading by shop_id keeps
            // it simple for the owner UI; the OA-side enforces uniqueness on zalo_id.
            const string sql = """
                select zalo_id, linked_at, last_command_at
                  from shop_zalo_links
                 where shop_id = @shop_id and linked_at is not null
                 order by linked_at desc
                 limit 1
                """;
            await using var connection = await GetDataSource(database).OpenConnectionAsync(cancellationToken);
            await using var command = new Npgsql.NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("shop_id", shop.Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return Results.Ok(new PairingStatusResponse(false, null, null, null));
            }
            return Results.Ok(new PairingStatusResponse(
                Linked: true,
                ZaloId: reader.GetString(0),
                LinkedAt: reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
                LastCommandAt: reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)));
        })
        .AllowAnonymous()
        .WithTags("Zalo")
        .WithName("ZaloPairStatus");
    }

    // Small reflection shim so this endpoint can read the same NpgsqlDataSource
    // the partial class uses for its own queries, without exposing it publicly.
    private static Npgsql.NpgsqlDataSource GetDataSource(Booking360Database database)
    {
        var field = typeof(Booking360Database).GetField("_dataSource",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return (Npgsql.NpgsqlDataSource)field!.GetValue(database)!;
    }
}