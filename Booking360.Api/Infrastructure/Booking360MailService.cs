using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Booking360.Api.Infrastructure;

public interface IBooking360MailService
{
    bool IsEnabled { get; }
    Task<bool> SendWelcomeAsync(string toAddress, string displayName, CancellationToken cancellationToken = default);
    Task<bool> SendBookingConfirmationAsync(string toAddress, string displayName, BookingRecord booking, CancellationToken cancellationToken = default);
}

public sealed class Booking360MailService : IBooking360MailService
{
    private readonly Booking360Options _options;
    private readonly ILogger<Booking360MailService> _logger;

    public Booking360MailService(Booking360Options options, ILogger<Booking360MailService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool IsEnabled => _options.MailEnabled;

    public async Task<bool> SendWelcomeAsync(string toAddress, string displayName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toAddress))
        {
            return false;
        }

        var subject = "Welcome to Booking360";
        var bodyHtml = $$"""
            <p>Hi {{System.Net.WebUtility.HtmlEncode(displayName)}},</p>
            <p>Your Booking360 workspace is ready. You can browse bookable resources, create bookings, and attach files from <a href="{{_options.FrontendUrl}}/workspace">{{_options.FrontendUrl}}</a>.</p>
            <p>If you did not expect this email, you can ignore it.</p>
            <p>Booking360</p>
            """;
        var bodyText = $"Hi {displayName},\n\nYour Booking360 workspace is ready: {_options.FrontendUrl}/workspace\n\nBooking360";
        return await SendAsync(toAddress, subject, bodyHtml, bodyText, cancellationToken);
    }

    public async Task<bool> SendBookingConfirmationAsync(string toAddress, string displayName, BookingRecord booking, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toAddress))
        {
            return false;
        }

        var subject = $"Booking confirmed: {booking.ResourceName}";
        var safeName = System.Net.WebUtility.HtmlEncode(displayName);
        var safeResource = System.Net.WebUtility.HtmlEncode(booking.ResourceName);
        var safeTitle = System.Net.WebUtility.HtmlEncode(booking.Title);
        var startLocal = booking.StartAt.ToString("yyyy-MM-dd HH:mm 'UTC'");
        var endLocal = booking.EndAt.ToString("yyyy-MM-dd HH:mm 'UTC'");
        var bodyHtml = $$"""
            <p>Hi {{safeName}},</p>
            <p>Your booking <strong>{{safeTitle}}</strong> for <strong>{{safeResource}}</strong> is confirmed.</p>
            <ul>
              <li>Start: {{startLocal}}</li>
              <li>End: {{endLocal}}</li>
              <li>Status: {{booking.Status}}</li>
            </ul>
            <p>Manage it at <a href="{{_options.FrontendUrl}}/bookings">{{_options.FrontendUrl}}/bookings</a>.</p>
            <p>Booking360</p>
            """;
        var bodyText = $"Hi {displayName},\n\nBooking confirmed: {booking.Title} -> {booking.ResourceName}\nStart: {startLocal}\nEnd: {endLocal}\nManage: {_options.FrontendUrl}/bookings";
        return await SendAsync(toAddress, subject, bodyHtml, bodyText, cancellationToken);
    }

    private async Task<bool> SendAsync(string to, string subject, string html, string text, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            _logger.LogDebug("Mail disabled; skipping send to {To} subject {Subject}", to, subject);
            return false;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_options.MailSenderName, _options.MailSenderEmail));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;
            var builder = new BodyBuilder
            {
                HtmlBody = html,
                TextBody = text
            };
            message.Body = builder.ToMessageBody();

            using var client = new SmtpClient();
            var secureOption = _options.MailPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(_options.MailHost, _options.MailPort, secureOption, cancellationToken);
            await client.AuthenticateAsync(_options.MailUsername, _options.MailPassword, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            _logger.LogInformation("Sent mail to {To} subject {Subject}", to, subject);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send mail to {To} subject {Subject}", to, subject);
            return false;
        }
    }
}