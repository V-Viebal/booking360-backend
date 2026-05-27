using Npgsql;

namespace Booking360.Api.Infrastructure;

public sealed class Booking360Options
{
    public Booking360Options(
        string authIssuer,
        string authInternalApi,
        string apiResourceIndicator,
        string databaseConnectionString,
        string minioEndpoint,
        string minioAccessKey,
        string minioSecretKey,
        string productionBucket,
        string localBucket,
        bool minioSecure,
        string frontendUrl,
        string mailHost,
        int mailPort,
        string mailUsername,
        string mailPassword,
        string mailSenderEmail,
        string mailSenderName,
        bool isDevelopment)
    {
        AuthIssuer = authIssuer;
        AuthInternalApi = authInternalApi;
        ApiResourceIndicator = apiResourceIndicator;
        DatabaseConnectionString = databaseConnectionString;
        MinioEndpoint = minioEndpoint;
        MinioAccessKey = minioAccessKey;
        MinioSecretKey = minioSecretKey;
        ProductionBucket = productionBucket;
        LocalBucket = localBucket;
        MinioSecure = minioSecure;
        FrontendUrl = frontendUrl;
        MailHost = mailHost;
        MailPort = mailPort;
        MailUsername = mailUsername;
        MailPassword = mailPassword;
        MailSenderEmail = mailSenderEmail;
        MailSenderName = mailSenderName;
        IsDevelopment = isDevelopment;
    }

    public string AuthIssuer { get; }
    public string AuthInternalApi { get; }
    public string ApiResourceIndicator { get; }
    public string DatabaseConnectionString { get; }
    public string MinioEndpoint { get; }
    public string MinioAccessKey { get; }
    public string MinioSecretKey { get; }
    public string ProductionBucket { get; }
    public string LocalBucket { get; }
    public bool MinioSecure { get; }
    public string FrontendUrl { get; }
    public string MailHost { get; }
    public int MailPort { get; }
    public string MailUsername { get; }
    public string MailPassword { get; }
    public string MailSenderEmail { get; }
    public string MailSenderName { get; }
    public bool IsDevelopment { get; }

    public string ActiveBucket => IsDevelopment ? LocalBucket : ProductionBucket;

    public bool MailEnabled =>
        !string.IsNullOrWhiteSpace(MailHost)
        && !string.IsNullOrWhiteSpace(MailUsername)
        && !string.IsNullOrWhiteSpace(MailPassword)
        && !string.IsNullOrWhiteSpace(MailSenderEmail);

    public IReadOnlyList<string> AllowedOrigins =>
    [
        "https://book360.hmz.one",
        "http://localhost:4101",
        "http://127.0.0.1:4101"
    ];

    public static Booking360Options Load(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var authIssuer = Require(configuration, "BOOKING360_LOGTO_ISSUER");
        var apiResourceIndicator = Require(configuration, "BOOKING360_LOGTO_API_RESOURCE_INDICATOR");
        var databaseUrl = environment.IsDevelopment()
            ? Require(configuration, "BOOKING360_DB_LOCAL_URL")
            : Require(configuration, "BOOKING360_DB_URL");

        var frontendUrl = configuration["APP_FRONTEND_URL"]?.Trim()
            ?? configuration["BOOKING360_FRONTEND_URL"]?.Trim()
            ?? "https://book360.hmz.one";

        var mailHost = configuration["BOOKING360_MAIL_HOST"]?.Trim()
            ?? configuration["SMTP_HOST"]?.Trim()
            ?? string.Empty;
        var mailPortRaw = configuration["BOOKING360_MAIL_PORT"]?.Trim()
            ?? configuration["SMTP_PORT"]?.Trim();
        var mailPort = int.TryParse(mailPortRaw, out var parsedPort) ? parsedPort : 465;
        var mailUsername = configuration["BOOKING360_MAIL_USER"]?.Trim()
            ?? configuration["SMTP_USER"]?.Trim()
            ?? string.Empty;
        var mailPassword = configuration["BOOKING360_MAIL_PASSWORD"]?.Trim()
            ?? configuration["SMTP_PASSWORD"]?.Trim()
            ?? configuration["RESEND_API_KEY"]?.Trim()
            ?? string.Empty;
        var mailSenderEmail = configuration["BOOKING360_MAIL_SENDER"]?.Trim()
            ?? configuration["SENDER_EMAIL"]?.Trim()
            ?? string.Empty;
        var mailSenderName = configuration["BOOKING360_MAIL_SENDER_NAME"]?.Trim()
            ?? "Booking360";

        return new Booking360Options(
            authIssuer: authIssuer,
            authInternalApi: configuration["BOOKING360_LOGTO_INTERNAL_API"]?.Trim() ?? string.Empty,
            apiResourceIndicator: apiResourceIndicator,
            databaseConnectionString: NormalizePostgresConnectionString(databaseUrl),
            minioEndpoint: NormalizeMinioEndpoint(Require(configuration, "BOOKING360_MINIO_SERVER_URL")),
            minioAccessKey: Require(configuration, "BOOKING360_MINIO_ROOT_USER"),
            minioSecretKey: Require(configuration, "BOOKING360_MINIO_ROOT_PASSWORD"),
            productionBucket: Require(configuration, "BOOKING360_MINIO_BUCKET"),
            localBucket: Require(configuration, "BOOKING360_MINIO_LOCAL_BUCKET"),
            minioSecure: bool.TryParse(configuration["BOOKING360_MINIO_SECURE"], out var secure) && secure,
            frontendUrl: frontendUrl,
            mailHost: mailHost,
            mailPort: mailPort,
            mailUsername: mailUsername,
            mailPassword: mailPassword,
            mailSenderEmail: mailSenderEmail,
            mailSenderName: mailSenderName,
            isDevelopment: environment.IsDevelopment());
    }

    private static string Require(IConfiguration configuration, string key)
    {
        var value = configuration[key]?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Missing required configuration value: {key}")
            : value;
    }

    private static string NormalizeMinioEndpoint(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[8..].TrimEnd('/');
        }
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[7..].TrimEnd('/');
        }
        return trimmed.TrimEnd('/');
    }

    private static string NormalizePostgresConnectionString(string connectionValue)
    {
        if (!connectionValue.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !connectionValue.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return connectionValue;
        }

        var uri = new Uri(connectionValue);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty,
            Database = uri.AbsolutePath.TrimStart('/'),
            SslMode = SslMode.Prefer
        };

        if (!string.IsNullOrWhiteSpace(uri.Query))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2)
                {
                    continue;
                }
                var key = Uri.UnescapeDataString(parts[0]);
                var value = Uri.UnescapeDataString(parts[1]);
                if (string.Equals(key, "sslmode", StringComparison.OrdinalIgnoreCase)
                    && Enum.TryParse<SslMode>(value, true, out var ssl))
                {
                    builder.SslMode = ssl;
                }
            }
        }

        return builder.ToString();
    }
}