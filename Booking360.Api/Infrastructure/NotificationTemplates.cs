namespace Booking360.Api.Infrastructure;

public sealed record NotificationPayload(
    NotificationKind Kind,
    string Channel,
    string Target,
    string Subject,
    string Message,
    Guid? BookingId,
    Guid? ShopId);

public sealed record BookingNotificationData(
    string ShopName,
    string ShopAddress,
    string ShopPhone,
    string CustomerName,
    string CustomerPhone,
    DateTimeOffset SlotTime,
    Guid BookingToken,
    string? Note,
    string? CancelReason);

public sealed record ShopRegistrationData(
    string ShopName,
    string ShopPhone,
    Guid ShopAccessToken,
    string Slug,
    string FrontendUrl);

public static class NotificationTemplates
{
    private static readonly TimeSpan VnOffset = TimeSpan.FromHours(7);

    private static string FormatVn(DateTimeOffset slot) =>
        slot.ToOffset(VnOffset).ToString("HH:mm 'ngay' dd/MM/yyyy");

    public static NotificationPayload BookingConfirmationForCustomer(
        BookingNotificationData data,
        string channel,
        Guid bookingId,
        Guid shopId,
        string frontendUrl)
    {
        var url = $"{frontendUrl.TrimEnd('/')}/b/{data.BookingToken}";
        var subject = $"Booking360: Xac nhan dat lich tai {data.ShopName}";
        var message = channel == "email"
            ? $"<p>Xin chao {data.CustomerName},</p>" +
              $"<p>Lich cua ban tai <strong>{System.Net.WebUtility.HtmlEncode(data.ShopName)}</strong> da duoc xac nhan.</p>" +
              $"<ul><li>Thoi gian: <strong>{FormatVn(data.SlotTime)}</strong></li>" +
              $"<li>Dia chi: {System.Net.WebUtility.HtmlEncode(data.ShopAddress)}</li>" +
              $"<li>SDT quan: {System.Net.WebUtility.HtmlEncode(data.ShopPhone)}</li></ul>" +
              $"<p>Quan ly hoac huy lich: <a href=\"{url}\">{url}</a></p>"
            : $"Booking360: Da dat lich tai {data.ShopName} luc {FormatVn(data.SlotTime)}. " +
              $"Dia chi: {data.ShopAddress}. Quan ly: {url}";
        return new NotificationPayload(
            NotificationKind.BookingConfirmation,
            channel,
            data.CustomerPhone,
            subject,
            message,
            bookingId,
            shopId);
    }

    public static NotificationPayload NewBookingForShop(
        BookingNotificationData data,
        string channel,
        Guid bookingId,
        Guid shopId,
        Guid shopAccessToken,
        string frontendUrl)
    {
        var manageUrl = $"{frontendUrl.TrimEnd('/')}/m/{shopAccessToken}";
        var subject = $"Booking360: Khach hang moi {data.CustomerName} - {FormatVn(data.SlotTime)}";
        var noteLine = string.IsNullOrWhiteSpace(data.Note) ? string.Empty : $" Ghi chu: {data.Note}.";
        var message = channel == "email"
            ? $"<p>Quan vua co lich moi:</p>" +
              $"<ul><li>Khach: <strong>{System.Net.WebUtility.HtmlEncode(data.CustomerName)}</strong> ({data.CustomerPhone})</li>" +
              $"<li>Thoi gian: <strong>{FormatVn(data.SlotTime)}</strong></li>" +
              (string.IsNullOrWhiteSpace(data.Note) ? string.Empty : $"<li>Ghi chu: {System.Net.WebUtility.HtmlEncode(data.Note)}</li>") +
              $"</ul>" +
              $"<p>Xem lich hom nay: <a href=\"{manageUrl}\">{manageUrl}</a></p>"
            : $"Booking360: Lich moi - {data.CustomerName} ({data.CustomerPhone}) luc {FormatVn(data.SlotTime)}.{noteLine} " +
              $"Xem: {manageUrl}";
        return new NotificationPayload(
            NotificationKind.BookingConfirmation,
            channel,
            data.ShopPhone,
            subject,
            message,
            bookingId,
            shopId);
    }

    public static NotificationPayload BookingCancelledForCustomer(
        BookingNotificationData data,
        string channel,
        Guid bookingId,
        Guid shopId)
    {
        var subject = $"Booking360: Lich tai {data.ShopName} da huy";
        var message = channel == "email"
            ? $"<p>Lich cua ban tai <strong>{System.Net.WebUtility.HtmlEncode(data.ShopName)}</strong> luc " +
              $"<strong>{FormatVn(data.SlotTime)}</strong> da huy thanh cong. Hen gap lai!</p>"
            : $"Booking360: Ban da huy lich tai {data.ShopName} luc {FormatVn(data.SlotTime)}. Hen gap lai!";
        return new NotificationPayload(
            NotificationKind.BookingCancelledByCustomer,
            channel,
            data.CustomerPhone,
            subject,
            message,
            bookingId,
            shopId);
    }

    public static NotificationPayload BookingCancelledForShop(
        BookingNotificationData data,
        string channel,
        Guid bookingId,
        Guid shopId,
        string cancelledBy)
    {
        var who = cancelledBy == "customer" ? "Khach" : "He thong";
        var reasonLine = string.IsNullOrWhiteSpace(data.CancelReason) ? string.Empty : $" Ly do: {data.CancelReason}.";
        var subject = $"Booking360: {who} huy lich {data.CustomerName} - {FormatVn(data.SlotTime)}";
        var message = channel == "email"
            ? $"<p>{who} da huy lich:</p>" +
              $"<ul><li>Khach: {System.Net.WebUtility.HtmlEncode(data.CustomerName)} ({data.CustomerPhone})</li>" +
              $"<li>Thoi gian: <strong>{FormatVn(data.SlotTime)}</strong></li>" +
              (string.IsNullOrWhiteSpace(data.CancelReason) ? string.Empty : $"<li>Ly do: {System.Net.WebUtility.HtmlEncode(data.CancelReason)}</li>") +
              $"</ul><p>Khung gio da duoc giai phong.</p>"
            : $"Booking360: {who} huy lich {data.CustomerName} ({data.CustomerPhone}) luc {FormatVn(data.SlotTime)}.{reasonLine}";
        return new NotificationPayload(
            cancelledBy == "customer" ? NotificationKind.BookingCancelledByCustomer : NotificationKind.BookingCancelledByShop,
            channel,
            data.ShopPhone,
            subject,
            message,
            bookingId,
            shopId);
    }

    public static NotificationPayload ShopRegistrationForOwner(
        ShopRegistrationData data,
        string channel)
    {
        var manageUrl = $"{data.FrontendUrl.TrimEnd('/')}/m/{data.ShopAccessToken}";
        var publicUrl = $"{data.FrontendUrl.TrimEnd('/')}/shops/{data.Slug}";
        var subject = $"Booking360: Quan {data.ShopName} dang ky thanh cong";
        var message = channel == "email"
            ? $"<p>Chao mung <strong>{System.Net.WebUtility.HtmlEncode(data.ShopName)}</strong> den voi Booking360!</p>" +
              $"<ul><li>Trang quan: <a href=\"{publicUrl}\">{publicUrl}</a></li>" +
              $"<li>Quan ly lich: <a href=\"{manageUrl}\">{manageUrl}</a></li></ul>" +
              $"<p>Luu lai lien ket quan ly de xem va dieu chinh lich hang ngay.</p>"
            : $"Booking360: Quan {data.ShopName} da dang ky. Quan ly lich: {manageUrl}";
        return new NotificationPayload(
            NotificationKind.ShopRegistration,
            channel,
            data.ShopPhone,
            subject,
            message,
            null,
            null);
    }
}