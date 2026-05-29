using System.Text;
using Booking360.Api.Infrastructure;

namespace Booking360.Api.Features.Admin;

/// <summary>
/// W8 REQ-SS-010: build a printable onboarding checklist for a shop.
/// Returns a single self-contained HTML page (vi-VN) that ops staff can print
/// or hand to the shop owner during in-person onboarding visits.
/// </summary>
public static class OnboardingChecklistBuilder
{
    public static string Render(ShopRecord shop, string? frontendUrl)
    {
        var web = string.IsNullOrWhiteSpace(frontendUrl) ? "https://book360.hmz.one" : frontendUrl!.TrimEnd('/');
        var publicUrl = $"{web}/shops/{shop.Slug}";
        var manageUrl = $"{web}/m/{shop.ShopAccessToken}";

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"vi\"><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>Checklist Onboarding — {Esc(shop.Name)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:'Segoe UI',Roboto,sans-serif;max-width:780px;margin:24px auto;padding:0 20px;color:#1f2937}");
        sb.AppendLine("h1{margin:0 0 4px;font-size:24px}h2{margin:24px 0 8px;font-size:16px;border-bottom:2px solid #e5e7eb;padding-bottom:4px}");
        sb.AppendLine(".muted{color:#6b7280;font-size:14px}");
        sb.AppendLine(".grid{display:grid;grid-template-columns:160px 1fr;gap:6px 16px;font-size:14px}");
        sb.AppendLine(".grid dt{color:#6b7280}.grid dd{margin:0}");
        sb.AppendLine("ol{padding-left:20px;line-height:1.6}");
        sb.AppendLine("ol li{margin:6px 0}");
        sb.AppendLine(".chk{display:inline-block;width:14px;height:14px;border:1.5px solid #6b7280;border-radius:3px;margin-right:8px;vertical-align:-2px}");
        sb.AppendLine("a{color:#0c5cff;word-break:break-all}");
        sb.AppendLine(".footer{margin-top:32px;padding-top:12px;border-top:1px solid #e5e7eb;font-size:12px;color:#6b7280}");
        sb.AppendLine("@media print{body{margin:0}a{color:#000}}");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine("<h1>Checklist onboarding quán</h1>");
        sb.AppendLine($"<p class=\"muted\">Quán: <strong>{Esc(shop.Name)}</strong> · Slug: <code>{Esc(shop.Slug)}</code></p>");

        sb.AppendLine("<h2>Thông tin quán</h2>");
        sb.AppendLine("<dl class=\"grid\">");
        sb.AppendLine($"<dt>Tên quán</dt><dd>{Esc(shop.Name)}</dd>");
        sb.AppendLine($"<dt>SĐT</dt><dd>{Esc(shop.Phone)}</dd>");
        sb.AppendLine($"<dt>Địa chỉ</dt><dd>{Esc(shop.Address)}</dd>");
        sb.AppendLine($"<dt>Quận/khu vực</dt><dd>{Esc(shop.District ?? "(chưa điền)")}</dd>");
        sb.AppendLine($"<dt>Phân khúc giá</dt><dd>{Esc(shop.PriceSegment ?? "(chưa điền)")}</dd>");
        sb.AppendLine($"<dt>Giờ mở/đóng</dt><dd>{shop.OpenTime:HH\\:mm} - {shop.CloseTime:HH\\:mm}</dd>");
        sb.AppendLine($"<dt>Slot/khách online</dt><dd>{shop.SlotDurationMinutes} phút · tối đa {shop.MaxOnlinePerSlot} khách online/slot</dd>");
        sb.AppendLine($"<dt>Số ảnh đã có</dt><dd>{shop.PhotoUrls.Length}</dd>");
        sb.AppendLine("</dl>");

        sb.AppendLine("<h2>Liên kết</h2>");
        sb.AppendLine("<dl class=\"grid\">");
        sb.AppendLine($"<dt>Trang công khai</dt><dd><a href=\"{Esc(publicUrl)}\">{Esc(publicUrl)}</a></dd>");
        sb.AppendLine($"<dt>Trang quản lý</dt><dd><a href=\"{Esc(manageUrl)}\">{Esc(manageUrl)}</a></dd>");
        sb.AppendLine("</dl>");

        sb.AppendLine("<h2>Việc cần làm khi onboarding</h2>");
        sb.AppendLine("<ol>");
        sb.AppendLine("<li><span class=\"chk\"></span>Xác nhận giờ mở/đóng và ngày làm việc đúng thực tế</li>");
        sb.AppendLine("<li><span class=\"chk\"></span>Cập nhật phân khúc giá (50–80k / 80–120k / 120–150k)</li>");
        sb.AppendLine("<li><span class=\"chk\"></span>Bổ sung quận/khu vực để chạy bản đồ mật độ GTM</li>");
        sb.AppendLine("<li><span class=\"chk\"></span>Tải lên 3–5 ảnh thực tế (mặt tiền, bên trong, ghế cắt)</li>");
        sb.AppendLine("<li><span class=\"chk\"></span>Hướng dẫn chủ quán mở liên kết quản lý và đặt thử 1 lịch test</li>");
        sb.AppendLine("<li><span class=\"chk\"></span>Hướng dẫn nút \"Tạm dừng nhận đặt\" / \"Đóng cửa hôm nay\"</li>");
        sb.AppendLine("<li><span class=\"chk\"></span>Hướng dẫn cách trả lời đánh giá khi khách review</li>");
        sb.AppendLine("<li><span class=\"chk\"></span>Gửi link xác minh số điện thoại nếu shop chưa được verify</li>");
        sb.AppendLine("<li><span class=\"chk\"></span>In QR liên kết quán + dán tại quầy</li>");
        sb.AppendLine("</ol>");

        sb.AppendLine($"<div class=\"footer\">In: {DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)):yyyy-MM-dd HH\\:mm} (giờ VN) · Booking360 ops</div>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string Esc(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        return raw
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}