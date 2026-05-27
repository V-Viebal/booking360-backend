using System.Security.Claims;

namespace Booking360.Api.Extensions;

public static class Booking360PrincipalExtensions
{
    public static string GetSubject(this ClaimsPrincipal principal) =>
        principal.FindFirstValue("sub")
        ?? throw new InvalidOperationException("Authenticated subject claim is missing.");

    public static string GetEmail(this ClaimsPrincipal principal) =>
        principal.FindFirstValue("email") ?? string.Empty;

    public static string GetDisplayName(this ClaimsPrincipal principal) =>
        principal.FindFirstValue("name")
        ?? principal.FindFirstValue("preferred_username")
        ?? principal.FindFirstValue("username")
        ?? principal.GetEmail()
        ?? principal.GetSubject();

    public static string GetUsername(this ClaimsPrincipal principal) =>
        principal.FindFirstValue("preferred_username")
        ?? principal.FindFirstValue("username")
        ?? principal.GetEmail().Split('@', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
        ?? principal.GetSubject();

    public static string[] GetRoles(this ClaimsPrincipal principal) =>
        principal.FindAll("roles")
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string[] GetScopes(this ClaimsPrincipal principal) =>
        principal.FindAll("scope")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool HasRoleOrScope(this ClaimsPrincipal principal, string role, string scope) =>
        principal.GetRoles().Contains(role, StringComparer.OrdinalIgnoreCase)
        || principal.GetScopes().Contains(scope, StringComparer.OrdinalIgnoreCase);
}