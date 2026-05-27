using Booking360.Api.Abstractions;
using Booking360.Api.Extensions;
using Booking360.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Booking360.Api.Features.Users;

public sealed class UsersEndpoint : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("/api/users").RequireAuthorization();

        group.MapGet("/me", async (HttpContext httpContext, Booking360Database database, IBooking360MailService mailService, ILogger<UsersEndpoint> logger, CancellationToken cancellationToken) =>
        {
            var user = httpContext.User;
            var info = new CurrentUserInfo(
                Subject: user.GetSubject(),
                Email: user.GetEmail(),
                Username: user.GetUsername(),
                DisplayName: user.GetDisplayName(),
                Roles: user.GetRoles(),
                Scopes: user.GetScopes());

            var record = await database.UpsertUserAsync(info, cancellationToken);

            if (record.WasCreated)
            {
                var sent = await mailService.SendWelcomeAsync(record.Email, record.DisplayName, cancellationToken);
                logger.LogInformation("New Booking360 user {Subject} provisioned, welcome mail sent={Sent}", record.Subject, sent);
            }

            return Results.Ok(new
            {
                subject = record.Subject,
                email = record.Email,
                username = record.Username,
                displayName = record.DisplayName,
                roles = record.Roles,
                scopes = info.Scopes,
                createdAt = record.CreatedAt,
                lastSeenAt = record.LastSeenAt
            });
        })
        .WithName("GetCurrentUser")
        .WithTags("Users");
    }
}