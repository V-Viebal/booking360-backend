using Booking360.Api.Infrastructure;

namespace Booking360.Api.Features.Zalo;

/// <summary>
/// Parses inbound Zalo OA chat text into shop owner commands.
///
/// Supported keywords (lowercase, accent-stripped, first token wins):
///   nghi   -> pause shop today  (status=paused_today)
///   day    -> resume shop       (status=active)
///   mo     -> resume shop       (alias of "day")
///   dong   -> close today       (status=closed_today)
///   giam N -> reduce capacity to N (1..6); "giam 0" => temp_full
///   lich   -> request today's bookings summary
///
/// The parser is pure (no I/O) so it stays unit-testable without a database.
/// Execution is performed by <see cref="ZaloCommandExecutor"/> which calls the
/// existing W6 owner-toggle primitives (SetShopStatusAsync, SetShopCapacityAsync).
/// </summary>
public static class ZaloCommandParser
{
    public enum CommandKind { Unknown, Pause, Resume, CloseToday, Capacity, Schedule, Help }

    public sealed record ParsedCommand(CommandKind Kind, int? CapacityArg = null, string? RawText = null);

    public static ParsedCommand Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ParsedCommand(CommandKind.Unknown, RawText: text);
        }

        var stripped = StripDiacritics(text.Trim()).ToLowerInvariant();
        var tokens = stripped.Split(new[] { ' ', '\t', ',', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return new ParsedCommand(CommandKind.Unknown, RawText: text);
        }

        return tokens[0] switch
        {
            "nghi" => new ParsedCommand(CommandKind.Pause, RawText: text),
            "day"  => new ParsedCommand(CommandKind.Resume, RawText: text),
            "mo"   => new ParsedCommand(CommandKind.Resume, RawText: text),
            "dong" => new ParsedCommand(CommandKind.CloseToday, RawText: text),
            "lich" => new ParsedCommand(CommandKind.Schedule, RawText: text),
            "help" => new ParsedCommand(CommandKind.Help, RawText: text),
            "?"    => new ParsedCommand(CommandKind.Help, RawText: text),
            "giam" when tokens.Length >= 2 && int.TryParse(tokens[1], out var n) && n >= 0 && n <= 6
                   => new ParsedCommand(CommandKind.Capacity, CapacityArg: n, RawText: text),
            _ => new ParsedCommand(CommandKind.Unknown, RawText: text),
        };
    }

    /// <summary>
    /// Lightweight diacritic strip — turns "nghỉ" / "đóng" / "giảm" into "nghi" / "dong" / "giam".
    /// Intentionally narrow (Vietnamese-only) to keep the parser dependency-free.
    /// </summary>
    public static string StripDiacritics(string input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        var normalized = input.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            // đ/Đ aren't decomposed by FormD, handle manually.
            if (ch == 'đ') { sb.Append('d'); continue; }
            if (ch == 'Đ') { sb.Append('D'); continue; }
            sb.Append(ch);
        }
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }
}

/// <summary>
/// Executes parsed Zalo commands against an authenticated shop link.
/// Returns the human-readable Vietnamese reply that the OA outbound provider
/// will echo back to the owner. Persists an audit row via <see cref="Booking360Database.LogZaloEventAsync"/>.
/// </summary>
public sealed class ZaloCommandExecutor
{
    private readonly Booking360Database _database;
    private readonly ILogger<ZaloCommandExecutor> _logger;

    public ZaloCommandExecutor(Booking360Database database, ILogger<ZaloCommandExecutor> logger)
    {
        _database = database;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        ShopZaloLinkRecord link,
        ZaloCommandParser.ParsedCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            string reply;
            string outcome;
            switch (command.Kind)
            {
                case ZaloCommandParser.CommandKind.Pause:
                    await _database.SetShopStatusAsync(link.ShopId, "paused_today", null, cancellationToken);
                    reply = "Đã tạm nghỉ hôm nay. Khách online sẽ thấy trạng thái 'tạm nghỉ'. Soạn 'day' để mở lại.";
                    outcome = "ok:paused_today";
                    break;

                case ZaloCommandParser.CommandKind.Resume:
                    await _database.SetShopStatusAsync(link.ShopId, "active", null, cancellationToken);
                    reply = "Đã mở lại tiệm. Khách có thể đặt slot bình thường.";
                    outcome = "ok:active";
                    break;

                case ZaloCommandParser.CommandKind.CloseToday:
                    await _database.SetShopStatusAsync(link.ShopId, "closed_today", null, cancellationToken);
                    reply = "Đã đóng cửa hôm nay. Slot mới sẽ mở lại sáng mai.";
                    outcome = "ok:closed_today";
                    break;

                case ZaloCommandParser.CommandKind.Capacity:
                    var cap = command.CapacityArg ?? 0;
                    await _database.SetShopCapacityAsync(link.ShopId, cap, cancellationToken);
                    reply = cap == 0
                        ? "Đã đánh dấu hết chỗ tạm thời. Reset tự động lúc 00:00."
                        : $"Đã đặt sức chứa mỗi slot = {cap}.";
                    outcome = "ok:capacity=" + cap;
                    break;

                case ZaloCommandParser.CommandKind.Schedule:
                    var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
                    var bookings = await _database.ListBookingsForShopDayAsync(link.ShopId, today, cancellationToken);
                    var counts = bookings
                        .GroupBy(b => b.Status)
                        .ToDictionary(g => g.Key, g => g.Count());
                    reply = $"Lịch hôm nay: tổng {bookings.Count} | xác nhận {counts.GetValueOrDefault("confirmed", 0)} | chờ {counts.GetValueOrDefault("pending", 0)} | hủy {counts.GetValueOrDefault("cancelled_by_customer", 0) + counts.GetValueOrDefault("cancelled_by_shop", 0)}.";
                    outcome = "ok:schedule";
                    break;

                case ZaloCommandParser.CommandKind.Help:
                    reply = "Cú pháp: nghi (tạm nghỉ) | day/mo (mở lại) | dong (đóng hôm nay) | giam N (sức chứa, 0..6) | lich (xem lịch hôm nay).";
                    outcome = "ok:help";
                    break;

                default:
                    reply = "Không hiểu lệnh. Soạn 'help' để xem danh sách lệnh.";
                    outcome = "unknown";
                    break;
            }

            await _database.TouchZaloLinkAsync(link.Id, cancellationToken);
            await _database.LogZaloEventAsync(
                direction: "in",
                zaloId: link.ZaloId,
                shopId: link.ShopId,
                eventType: "command",
                command: command.Kind.ToString().ToLowerInvariant(),
                payloadJson: System.Text.Json.JsonSerializer.Serialize(new { raw = command.RawText, capacity = command.CapacityArg }),
                outcome: outcome,
                cancellationToken: cancellationToken);

            return reply;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Zalo command execution failed: shop={ShopId} kind={Kind}", link.ShopId, command.Kind);
            await _database.LogZaloEventAsync(
                direction: "in",
                zaloId: link.ZaloId,
                shopId: link.ShopId,
                eventType: "error",
                command: command.Kind.ToString().ToLowerInvariant(),
                payloadJson: null,
                outcome: "exception:" + ex.GetType().Name,
                cancellationToken: cancellationToken);
            return "Lỗi xử lý lệnh. Vui lòng thử lại sau.";
        }
    }
}