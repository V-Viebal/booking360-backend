using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Booking360.Api.Extensions;

namespace Booking360.Api.Infrastructure;

public sealed class Booking360PrincipalSync
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly Booking360Options _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<Booking360PrincipalSync> _logger;

    private readonly Dictionary<string, CachedPayload> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public Booking360PrincipalSync(
        Booking360Options options,
        IHttpClientFactory httpClientFactory,
        ILogger<Booking360PrincipalSync> logger)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task EnrichAsync(ClaimsPrincipal principal, string accessToken, CancellationToken cancellationToken)
    {
        if (principal.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return;
        }

        var subject = identity.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return;
        }

        var payload = await GetPayloadAsync(subject, accessToken, cancellationToken);
        if (payload is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(payload.Email) && identity.FindFirst("email") is null)
        {
            identity.AddClaim(new Claim("email", payload.Email));
        }
        if (!string.IsNullOrWhiteSpace(payload.Name) && identity.FindFirst("name") is null)
        {
            identity.AddClaim(new Claim("name", payload.Name));
        }
        if (!string.IsNullOrWhiteSpace(payload.PreferredUsername) && identity.FindFirst("preferred_username") is null)
        {
            identity.AddClaim(new Claim("preferred_username", payload.PreferredUsername));
        }

        var existingRoles = identity.FindAll("roles").Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var role in payload.Roles)
        {
            if (existingRoles.Add(role))
            {
                identity.AddClaim(new Claim("roles", role));
            }
        }
    }

    private async Task<CachedPayload?> GetPayloadAsync(string subject, string accessToken, CancellationToken cancellationToken)
    {
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(subject, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return cached;
            }
        }
        finally
        {
            _cacheLock.Release();
        }

        var payload = await FetchUserInfoAsync(accessToken, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            _cache[subject] = payload;
        }
        finally
        {
            _cacheLock.Release();
        }
        return payload;
    }

    private async Task<CachedPayload?> FetchUserInfoAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("logto-userinfo");
            var requestUri = $"{_options.AuthIssuer.TrimEnd('/')}/me";
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Logto userinfo fetch returned {Status}", response.StatusCode);
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            string ReadString(string key) =>
                root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String
                    ? prop.GetString() ?? string.Empty
                    : string.Empty;

            var roles = new List<string>();
            if (root.TryGetProperty("roles", out var rolesElem) && rolesElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rolesElem.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var v = item.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            roles.Add(v!);
                        }
                    }
                    else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                    {
                        var v = nameProp.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            roles.Add(v!);
                        }
                    }
                }
            }

            return new CachedPayload(
                Email: ReadString("email"),
                Name: ReadString("name"),
                PreferredUsername: ReadString("username"),
                Roles: roles.ToArray(),
                ExpiresAt: DateTimeOffset.UtcNow.Add(CacheLifetime));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Logto userinfo fetch failed");
            return null;
        }
    }

    private sealed record CachedPayload(
        string Email,
        string Name,
        string PreferredUsername,
        string[] Roles,
        DateTimeOffset ExpiresAt);
}