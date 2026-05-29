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
            if (ch == 'đ' || ch == 'Đ') { sb.Append('d'); continue; }
            // Force ASCII-only output: letter, digit, or hyphen for everything else.
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('-');
            }
        }

        var collapsed = sb.ToString().Trim('-');
        while (collapsed.Contains("--", System.StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("--", "-", System.StringComparison.Ordinal);
        }
        return collapsed;
    }
}