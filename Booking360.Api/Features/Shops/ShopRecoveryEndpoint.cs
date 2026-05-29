using Booking360.Api.Abstractions;
using Booking360.Api.Infrastructure;

namespace Booking360.Api.Features.Shops;

/// <summary>
/// W12 — Shop owner self-service recovery.
///
/// Owners who lose their /shop/m/{token} magic link can recover access by entering
/// their shop phone (already on file). We send a 6-digit code through the configured
/// notification channel (zns/sms/email/log) and, when claimed, rotate shop_access_token
/// so any leaked link is invalidated.
///
/// Privacy: request endpoint always returns a generic "đã gửi mã" message — never
/// leaks whether the phone is registered. Rate limit (3 codes / 15 min / phone) is
/// enforced in the DB primitive, and the request endpoint surfaces the limit silently
/// so attackers can't enumerate accounts.
/// </summary>
public sealed class ShopRecoveryEndpoint : IEndpoint
{
    public sealed record RequestBody(string Phone);
    public sealed record ClaimBody(string Phone, string Code);

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // POST /api/shop/recovery/request
        routeBuilder.MapPost("/api/shop/recovery/request", async (
            RequestBody body,
            HttpContext httpContext,
            Booking360Database database,
            NotificationDispatcher dispatcher,
            Booking360Options options,
            ILogger<ShopRecoveryEndpoint> logger,
            CancellationToken cancellationToken) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Phone))
            {
                return Results.BadRequest(new { error = "Vui lòng nhập số điện thoại tiệm." });
            }

            var requestIp = httpContext.Connection.RemoteIpAddress?.ToString();
            var result = await database.RequestShopRecoveryAsync(body.Phone, requestIp, cancellationToken: cancellationToken);

            // Privacy: same response for shop_not_found, rate_limited, and success.
            // We log the real outcome for observability but the client gets a uniform reply.
            if (!result.Created)
            {
                logger.LogInformation(
                    "Shop recovery request for {Phone} not created: {Reason}",
                    Mask(body.Phone), result.FailureReason);
                return Results.Ok(new { ok = true, message = "Nếu số điện thoại đã đăng ký, mã 6 số sẽ được gửi trong vài phút." });
            }

            // Fire-and-forget delivery via the routing notification provider.
            // Channel = configured default (zns in prod, log in dev) so OPS can flip transports
            // without code changes once Zalo OA is live.
            var message = $"Booking360: Mã khôi phục tiệm: {result.Code}. Có hiệu lực 10 phút. Không chia sẻ mã này.";
            _ = dispatcher.DispatchAsync(new NotificationContext(
                Kind: NotificationKind.ShopRegistration,
                Channel: options.DefaultNotificationChannel,
                Target: body.Phone.Trim(),
                Message: message,
                BookingId: null,
                ShopId: result.ShopId,
                Subject: "Mã khôi phục tiệm Booking360"), CancellationToken.None);

            return Results.Ok(new { ok = true, message = "Nếu số điện thoại đã đăng ký, mã 6 số sẽ được gửi trong vài phút." });
        })
        .AllowAnonymous()
        .WithTags("ShopRecovery")
        .WithName("ShopRecoveryRequest");

        // POST /api/shop/recovery/claim — exchange phone+code for a fresh manage URL.
        routeBuilder.MapPost("/api/shop/recovery/claim", async (
            ClaimBody body,
            Booking360Database database,
            Booking360Options options,
            CancellationToken cancellationToken) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Phone) || string.IsNullOrWhiteSpace(body.Code))
            {
                return Results.BadRequest(new { error = "Vui lòng nhập số điện thoại và mã 6 số." });
            }

            var result = await database.ClaimShopRecoveryAsync(body.Phone, body.Code, cancellationToken);
            if (!result.Ok)
            {
                return Results.BadRequest(new
                {
                    error = result.FailureReason switch
                    {
                        "invalid_or_expired" => "Mã không hợp lệ hoặc đã hết hạn. Yêu cầu mã mới.",
                        "missing_input" => "Thiếu thông tin.",
                        _ => "Không thể khôi phục lúc này. Vui lòng thử lại sau.",
                    }
                });
            }

            var manageUrl = options.FrontendUrl.TrimEnd('/') + "/shop/m/" + result.NewShopAccessToken!.Value;
            return Results.Ok(new
            {
                ok = true,
                shopAccessToken = result.NewShopAccessToken!.Value,
                manageUrl,
                message = "Đã khôi phục. Lưu lại liên kết quản lý mới.",
            });
        })
        .AllowAnonymous()
        .WithTags("ShopRecovery")
        .WithName("ShopRecoveryClaim");
    }

    private static string Mask(string phone)
    {
        var s = phone?.Trim() ?? string.Empty;
        if (s.Length <= 4) return new string('*', s.Length);
        return new string('*', s.Length - 4) + s[^4..];
    }
}