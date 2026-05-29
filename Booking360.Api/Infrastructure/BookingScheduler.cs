using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Booking360.Api.Infrastructure;

/// <summary>
/// Wave 4 — scheduled jobs (per-minute tick).
/// Drives reminder (T-30), no-show (T+15), review-link (T+45), and 00:00 VN daily reset.
/// All jobs use atomic mark-on-update for race-free idempotency. Safe to run concurrently
/// with other instances or to be replayed (e.g. via the gated probe endpoint).
/// </summary>
public sealed class BookingScheduler : BackgroundService
{
    private static readonly TimeSpan VnOffset = TimeSpan.FromHours(7);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly ILogger<BookingScheduler> _logger;
    private readonly Booking360Options _options;

    public BookingScheduler(
        IServiceProvider services,
        ILogger<BookingScheduler> logger,
        Booking360Options options)
    {
        _services = services;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small startup delay so DB migrations finish on cold start.
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation("BookingScheduler started; tick={TickSeconds}s", TickInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _services.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<SchedulerJobs>();
                await jobs.RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BookingScheduler tick failed; will retry next interval");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("BookingScheduler stopped");
    }
}

public sealed record SchedulerRunSummary(
    int RemindersDispatched,
    int NoShowMarked,
    int ReviewLinksDispatched,
    bool DailyResetRan,
    int DailyResetRowCount,
    DateTimeOffset At);

public sealed class SchedulerJobs
{
    private static readonly TimeSpan VnOffset = TimeSpan.FromHours(7);

    private readonly Booking360Database _db;
    private readonly NotificationDispatcher _dispatcher;
    private readonly Booking360Options _options;
    private readonly ILogger<SchedulerJobs> _logger;

    public SchedulerJobs(
        Booking360Database db,
        NotificationDispatcher dispatcher,
        Booking360Options options,
        ILogger<SchedulerJobs> logger)
    {
        _db = db;
        _dispatcher = dispatcher;
        _options = options;
        _logger = logger;
    }

    public async Task<SchedulerRunSummary> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var reminders = await DispatchRemindersAsync(now, cancellationToken);
        var noShows = await MarkNoShowsAsync(now, cancellationToken);
        var reviews = await DispatchReviewLinksAsync(now, cancellationToken);
        var (resetRan, resetRows) = await MaybeRunDailyResetAsync(now, cancellationToken);

        if (reminders > 0 || noShows > 0 || reviews > 0 || resetRan)
        {
            _logger.LogInformation(
                "Scheduler tick: reminders={Reminders} noShows={NoShows} reviews={Reviews} dailyReset={DailyReset} dailyResetRows={DailyResetRows}",
                reminders, noShows, reviews, resetRan, resetRows);
        }
        return new SchedulerRunSummary(reminders, noShows, reviews, resetRan, resetRows, now);
    }

    private async Task<int> DispatchRemindersAsync(DateTimeOffset now, CancellationToken ct)
    {
        var due = await _db.ListBookingsDueForReminderAsync(now, ct);
        if (due.Count == 0) return 0;
        var sent = 0;
        var channel = _options.DefaultNotificationChannel;
        foreach (var booking in due)
        {
            if (ct.IsCancellationRequested) break;
            if (!await _db.TryMarkReminderSentAsync(booking.Id, ct)) continue;
            var shop = await _db.GetShopByIdAsync(booking.ShopId, ct);
            if (shop is null) continue;
            var data = BuildData(shop, booking);
            await _dispatcher.DispatchAsync(NotificationTemplates.BookingReminderForCustomer(
                data, channel, booking.Id, shop.Id, booking.BookingToken, _options.FrontendUrl), ct);
            sent++;
        }
        return sent;
    }

    private async Task<int> MarkNoShowsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var due = await _db.ListBookingsForNoShowAsync(now, ct);
        if (due.Count == 0) return 0;
        var marked = 0;
        var channel = _options.DefaultNotificationChannel;
        foreach (var booking in due)
        {
            if (ct.IsCancellationRequested) break;
            if (!await _db.TryMarkNoShowAsync(booking.Id, ct)) continue;
            var shop = await _db.GetShopByIdAsync(booking.ShopId, ct);
            if (shop is null) continue;
            var data = BuildData(shop, booking);
            await _dispatcher.DispatchAsync(NotificationTemplates.NoShowForShop(
                data, channel, booking.Id, shop.Id), ct);
            marked++;
        }
        return marked;
    }

    private async Task<int> DispatchReviewLinksAsync(DateTimeOffset now, CancellationToken ct)
    {
        var due = await _db.ListBookingsForReviewLinkAsync(now, ct);
        if (due.Count == 0) return 0;
        var sent = 0;
        var channel = _options.DefaultNotificationChannel;
        foreach (var booking in due)
        {
            if (ct.IsCancellationRequested) break;
            if (!await _db.TryMarkReviewLinkSentAsync(booking.Id, ct)) continue;
            var shop = await _db.GetShopByIdAsync(booking.ShopId, ct);
            if (shop is null) continue;
            var data = BuildData(shop, booking);
            await _dispatcher.DispatchAsync(NotificationTemplates.ReviewLinkForCustomer(
                data, channel, booking.Id, shop.Id, booking.BookingToken, _options.FrontendUrl), ct);
            sent++;
        }
        return sent;
    }

    private async Task<(bool Ran, int Rows)> MaybeRunDailyResetAsync(DateTimeOffset now, CancellationToken ct)
    {
        // Trigger between 00:00 and 00:30 VN, exactly once per VN date (claim guarded).
        var vnNow = now.ToOffset(VnOffset);
        if (vnNow.TimeOfDay >= TimeSpan.FromMinutes(30))
        {
            return (false, 0);
        }
        var vnDate = DateOnly.FromDateTime(vnNow.DateTime);
        var claimed = await _db.TryClaimDailyJobAsync("daily_reset", vnDate, ct);
        if (!claimed)
        {
            return (false, 0);
        }
        var rows = await _db.ResetDailyShopStatusAsync(ct);
        _logger.LogInformation("Daily reset ran for vnDate={VnDate}; rows touched={Rows}", vnDate, rows);
        return (true, rows);
    }

    /// <summary>
    /// Force-run the daily reset regardless of clock (probe-only, used to verify the path).
    /// Bypasses the once-per-day claim so probes can replay against the same VN date.
    /// </summary>
    public async Task<int> ForceDailyResetAsync(CancellationToken ct)
    {
        var rows = await _db.ResetDailyShopStatusAsync(ct);
        _logger.LogInformation("Daily reset FORCED via probe; rows touched={Rows}", rows);
        return rows;
    }

    private static BookingNotificationData BuildData(ShopRecord shop, BookingV2Record booking) =>
        new(
            ShopName: shop.Name,
            ShopAddress: shop.Address,
            ShopPhone: shop.Phone,
            CustomerName: booking.CustomerName,
            CustomerPhone: booking.CustomerPhone,
            SlotTime: booking.SlotTime,
            BookingToken: booking.BookingToken,
            Note: booking.Note,
            CancelReason: booking.CancelReason);
}