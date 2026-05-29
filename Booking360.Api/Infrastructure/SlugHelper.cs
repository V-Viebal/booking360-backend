namespace Booking360.Api.Infrastructure;

internal static class SlugHelper
{
    /// <summary>
    /// Convert a (typically Vietnamese) display name into an ASCII URL slug.
    /// Works under InvariantGlobalization=true (does not rely on ICU /
    /// String.Normalize), which is a no-op in invariant mode.
    /// </summary>
    public static string Slugify(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var raw in input.Trim())
        {
            var ch = char.ToLowerInvariant(raw);
            var mapped = MapVietnamese(ch);
            if (mapped is null)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    sb.Append(ch);
                }
                else
                {
                    sb.Append('-');
                }
            }
            else
            {
                sb.Append(mapped);
            }
        }

        var collapsed = sb.ToString().Trim('-');
        while (collapsed.Contains("--", System.StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("--", "-", System.StringComparison.Ordinal);
        }
        return collapsed;
    }

    private static string? MapVietnamese(char ch)
    {
        // Lowercase Vietnamese vowels + đ -> ASCII base letter.
        switch (ch)
        {
            // a family
            case 'à': case 'á': case 'ả': case 'ã': case 'ạ':
            case 'ă': case 'ằ': case 'ắ': case 'ẳ': case 'ẵ': case 'ặ':
            case 'â': case 'ầ': case 'ấ': case 'ẩ': case 'ẫ': case 'ậ':
                return "a";
            // e family
            case 'è': case 'é': case 'ẻ': case 'ẽ': case 'ẹ':
            case 'ê': case 'ề': case 'ế': case 'ể': case 'ễ': case 'ệ':
                return "e";
            // i family
            case 'ì': case 'í': case 'ỉ': case 'ĩ': case 'ị':
                return "i";
            // o family
            case 'ò': case 'ó': case 'ỏ': case 'õ': case 'ọ':
            case 'ô': case 'ồ': case 'ố': case 'ổ': case 'ỗ': case 'ộ':
            case 'ơ': case 'ờ': case 'ớ': case 'ở': case 'ỡ': case 'ợ':
                return "o";
            // u family
            case 'ù': case 'ú': case 'ủ': case 'ũ': case 'ụ':
            case 'ư': case 'ừ': case 'ứ': case 'ử': case 'ữ': case 'ự':
                return "u";
            // y family
            case 'ỳ': case 'ý': case 'ỷ': case 'ỹ': case 'ỵ':
                return "y";
            // d with stroke
            case 'đ':
                return "d";
            default:
                return null;
        }
    }
}