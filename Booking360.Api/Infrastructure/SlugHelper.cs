namespace Booking360.Api.Infrastructure;

internal static class SlugHelper
{
    public static string Slugify(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var trimmed = input.Trim().ToLowerInvariant();
        // Strip Vietnamese diacritics by decomposing then dropping combining marks.
        var normalized = trimmed.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            // Vietnamese 'd' with stroke -> 'd'.
            if (ch == 'đ') { sb.Append('d'); continue; }
            if (ch == 'Đ') { sb.Append('d'); continue; }
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }

        var collapsed = sb.ToString().Trim('-');
        while (collapsed.Contains("--", System.StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("--", "-", System.StringComparison.Ordinal);
        }
        return collapsed;
    }
}