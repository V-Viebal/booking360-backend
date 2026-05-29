using System.Collections.Concurrent;

namespace Booking360.Api.Infrastructure;

public enum NotificationKind
{
    BookingConfirmation,
    BookingCancelledByCustomer,
    BookingCancelledByShop,
    SlotReminder,
    ShopRegistration,
    ShopPaused,
    ShopResumed,
    NoShow
}

public sealed record NotificationContext(
    NotificationKind Kind,
    string Channel,         // "zns" | "sms" | "email" | "log"
    string Target,          // phone or email
    string Message,
    Guid? BookingId,
    Guid? ShopId,
    string? Subject = null);

public sealed record NotificationResult(bool Sent, string Status, string? FailureReason, string? ProviderMessageId);

public interface INotificationProvider
{
    string Channel { get; }
    Task<NotificationResult> SendAsync(NotificationContext context, CancellationToken cancellationToken = default);
}

public sealed class LogNotificationProvider : INotificationProvider
{
    private readonly ILogger<LogNotificationProvider> _logger;

    public LogNotificationProvider(ILogger<LogNotificationProvider> logger)
    {
        _logger = logger;
    }

    public string Channel => "log";

    public Task<NotificationResult> SendAsync(NotificationContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[NotificationLog] kind={Kind} channel={Channel} target={Target} bookingId={BookingId} shopId={ShopId} subject={Subject} message={Message}",
            context.Kind, context.Channel, context.Target, context.BookingId, context.ShopId, context.Subject, context.Message);
        var providerId = "log-" + Guid.NewGuid().ToString("N")[..10];
        return Task.FromResult(new NotificationResult(true, "sent", null, providerId));
    }
}

public sealed class EmailNotificationProvider : INotificationProvider
{
    private readonly Booking360Options _options;
    private readonly IBooking360MailService _mail;
    private readonly ILogger<EmailNotificationProvider> _logger;

    public EmailNotificationProvider(
        Booking360Options options,
        IBooking360MailService mail,
        ILogger<EmailNotificationProvider> logger)
    {
        _options = options;
        _mail = mail;
        _logger = logger;
    }

    public string Channel => "email";

    public async Task<NotificationResult> SendAsync(NotificationContext context, CancellationToken cancellationToken = default)
    {
        if (!_mail.IsEnabled)
        {
            _logger.LogDebug("Email channel skipped (mail disabled) for kind={Kind} target={Target}", context.Kind, context.Target);
            return new NotificationResult(false, "skipped", "mail-disabled", null);
        }
        if (!context.Target.Contains('@'))
        {
            return new NotificationResult(false, "skipped", "non-email-target", null);
        }
        try
        {
            var sent = await _mail.SendRawAsync(
                context.Target,
                context.Subject ?? $"Booking360 - {context.Kind}",
                context.Message,
                cancellationToken);
            return sent
                ? new NotificationResult(true, "sent", null, "email-" + Guid.NewGuid().ToString("N")[..10])
                : new NotificationResult(false, "failed", "smtp-send-returned-false", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Email send failed for kind={Kind} target={Target}", context.Kind, context.Target);
            return new NotificationResult(false, "failed", ex.Message, null);
        }
    }
}

/// <summary>
/// Stub for Zalo ZNS / SMS. Until ZNS template approval lands, behaves as a log provider but
/// reports its channel as the requested transport so the routing + persistence layer behaves
/// like prod. Wave 4 swaps in the real OA client.
/// </summary>
public sealed class ZaloSmsNotificationProvider : INotificationProvider
{
    private readonly ILogger<ZaloSmsNotificationProvider> _logger;

    public ZaloSmsNotificationProvider(ILogger<ZaloSmsNotificationProvider> logger)
    {
        _logger = logger;
    }

    public string Channel => "zns";

    public Task<NotificationResult> SendAsync(NotificationContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[ZNS-stub] kind={Kind} target={Target} message={Message}",
            context.Kind, context.Target, context.Message);
        var providerId = "zns-stub-" + Guid.NewGuid().ToString("N")[..8];
        return Task.FromResult(new NotificationResult(true, "queued", "zns-stub-not-yet-live", providerId));
    }
}

/// <summary>
/// Routes a NotificationContext to the registered provider whose Channel matches
/// context.Channel. Falls back to the configured default channel and finally to log.
/// </summary>
public sealed class RoutingNotificationProvider : INotificationProvider
{
    private readonly Booking360Options _options;
    private readonly ConcurrentDictionary<string, INotificationProvider> _byChannel;
    private readonly INotificationProvider _fallback;
    private readonly ILogger<RoutingNotificationProvider> _logger;

    public RoutingNotificationProvider(
        Booking360Options options,
        IEnumerable<INotificationProvider> providers,
        ILogger<RoutingNotificationProvider> logger)
    {
        _options = options;
        _logger = logger;
        _byChannel = new ConcurrentDictionary<string, INotificationProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (provider is RoutingNotificationProvider) continue;
            _byChannel[provider.Channel] = provider;
        }
        _fallback = _byChannel.TryGetValue(options.DefaultNotificationChannel, out var d)
            ? d
            : _byChannel.GetValueOrDefault("log") ?? throw new InvalidOperationException("No log provider registered");
    }

    public string Channel => "router";

    public Task<NotificationResult> SendAsync(NotificationContext context, CancellationToken cancellationToken = default)
    {
        var channel = string.IsNullOrWhiteSpace(context.Channel) ? _options.DefaultNotificationChannel : context.Channel;
        if (!_byChannel.TryGetValue(channel, out var provider))
        {
            _logger.LogWarning("No provider registered for channel {Channel}, falling back to {Fallback}", channel, _fallback.Channel);
            provider = _fallback;
        }
        return provider.SendAsync(context, cancellationToken);
    }
}

public sealed class NotificationDispatcher
{
    private readonly RoutingNotificationProvider _router;
    private readonly Booking360Database _database;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(
        RoutingNotificationProvider router,
        Booking360Database database,
        ILogger<NotificationDispatcher> logger)
    {
        _router = router;
        _database = database;
        _logger = logger;
    }

    public Task DispatchAsync(NotificationPayload payload, CancellationToken cancellationToken = default)
    {
        var ctx = new NotificationContext(
            payload.Kind,
            payload.Channel,
            payload.Target,
            payload.Message,
            payload.BookingId,
            payload.ShopId,
            payload.Subject);
        return DispatchAsync(ctx, cancellationToken);
    }

    public async Task DispatchAsync(NotificationContext context, CancellationToken cancellationToken = default)
    {
        NotificationResult result;
        try
        {
            result = await _router.SendAsync(context, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notification dispatch failed for {Kind} -> {Target}", context.Kind, context.Target);
            result = new NotificationResult(false, "failed", ex.Message, null);
        }

        try
        {
            await _database.LogNotificationAsync(
                context.BookingId,
                context.ShopId,
                context.Kind.ToString(),
                context.Channel,
                context.Target,
                result.Status,
                result.FailureReason,
                result.ProviderMessageId,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist notification log for {Kind}", context.Kind);
        }
    }
}