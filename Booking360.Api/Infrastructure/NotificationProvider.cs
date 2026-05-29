namespace Booking360.Api.Infrastructure;

public enum NotificationKind
{
    BookingConfirmation,
    BookingCancelledByCustomer,
    BookingCancelledByShop,
    SlotReminder,
    ShopRegistration
}

public sealed record NotificationContext(
    NotificationKind Kind,
    string Channel,         // "zns" | "sms" | "email" | "log"
    string Target,          // phone or email
    string Message,
    Guid? BookingId,
    Guid? ShopId);

public sealed record NotificationResult(bool Sent, string Status, string? FailureReason, string? ProviderMessageId);

public interface INotificationProvider
{
    Task<NotificationResult> SendAsync(NotificationContext context, CancellationToken cancellationToken = default);
}

public sealed class MockNotificationProvider : INotificationProvider
{
    private readonly ILogger<MockNotificationProvider> _logger;

    public MockNotificationProvider(ILogger<MockNotificationProvider> logger)
    {
        _logger = logger;
    }

    public Task<NotificationResult> SendAsync(NotificationContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[NotificationMock] kind={Kind} channel={Channel} target={Target} bookingId={BookingId} shopId={ShopId} message={Message}",
            context.Kind,
            context.Channel,
            context.Target,
            context.BookingId,
            context.ShopId,
            context.Message);

        var providerId = Guid.NewGuid().ToString("N")[..12];
        return Task.FromResult(new NotificationResult(true, "sent", null, providerId));
    }
}

public sealed class NotificationDispatcher
{
    private readonly INotificationProvider _provider;
    private readonly Booking360Database _database;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(INotificationProvider provider, Booking360Database database, ILogger<NotificationDispatcher> logger)
    {
        _provider = provider;
        _database = database;
        _logger = logger;
    }

    public async Task DispatchAsync(NotificationContext context, CancellationToken cancellationToken = default)
    {
        NotificationResult result;
        try
        {
            result = await _provider.SendAsync(context, cancellationToken);
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