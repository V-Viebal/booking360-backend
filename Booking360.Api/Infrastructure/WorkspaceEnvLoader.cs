namespace Booking360.Api.Infrastructure;

public static class WorkspaceEnvLoader
{
    public static IReadOnlyDictionary<string, string?> LoadNearest(string startingDirectory)
    {
        var current = new DirectoryInfo(startingDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ".env");
            if (File.Exists(candidate))
            {
                return Parse(candidate);
            }
            current = current.Parent;
        }
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string?> Parse(string envPath)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(envPath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            if (value.Length >= 2
                && ((value.StartsWith('"') && value.EndsWith('"'))
                    || (value.StartsWith('\'') && value.EndsWith('\''))))
            {
                value = value[1..^1];
            }

            values[key] = value;
        }
        return values;
    }
}