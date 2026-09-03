using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Booking360.Api.Extensions;
using Booking360.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.IdentityModel.Tokens;
using Minio;
using Npgsql;
using Scalar.AspNetCore;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Workspace .env loader for local dev
    if (builder.Environment.IsDevelopment())
    {
        var envValues = WorkspaceEnvLoader.LoadNearest(builder.Environment.ContentRootPath);
        if (envValues.Count > 0)
        {
            builder.Configuration.AddInMemoryCollection(envValues);
        }
    }

    builder.Host.UseSerilog((context, services, config) =>
    {
        config.ReadFrom.Configuration(context.Configuration)
              .ReadFrom.Services(services)
              .Enrich.FromLogContext()
              .WriteTo.Console();
    });

    builder.Services.Configure<JsonOptions>(options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

    builder.Services.Configure<FormOptions>(options =>
    {
        options.MultipartBodyLengthLimit = 200L * 1024 * 1024; // 200 MB upload ceiling
    });

    var booking360Options = Booking360Options.Load(builder.Configuration, builder.Environment);
    builder.Services.AddSingleton(booking360Options);

    // Postgres
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(booking360Options.DatabaseConnectionString);
    var dataSource = dataSourceBuilder.Build();
    builder.Services.AddSingleton(dataSource);
    builder.Services.AddSingleton<Booking360Database>();

    // MinIO
    builder.Services.AddSingleton<IMinioClient>(_ =>
        new MinioClient()
            .WithEndpoint(booking360Options.MinioEndpoint)
            .WithCredentials(booking360Options.MinioAccessKey, booking360Options.MinioSecretKey)
            .WithSSL(booking360Options.MinioSecure)
            .Build());
    builder.Services.AddSingleton<Booking360ObjectStorageService>();

    // Mail
    builder.Services.AddSingleton<IBooking360MailService, Booking360MailService>();

    // Notifications (Wave 3: Log + Email + ZNS-stub providers, routed by Channel + env default)
    builder.Services.AddSingleton<INotificationProvider, LogNotificationProvider>();
    builder.Services.AddSingleton<INotificationProvider, EmailNotificationProvider>();
    builder.Services.AddSingleton<INotificationProvider, ZaloSmsNotificationProvider>();
    builder.Services.AddSingleton<RoutingNotificationProvider>();
    builder.Services.AddScoped<NotificationDispatcher>();

    // W11 Zalo OA bridge — executor is scoped because it touches per-request DB state.
    builder.Services.AddScoped<Booking360.Api.Features.Zalo.ZaloCommandExecutor>();

    // Wave 4: per-minute scheduler (reminder T-30, no-show T+15, review-link T+45, 00:00 VN daily reset)
    builder.Services.AddScoped<SchedulerJobs>();
    builder.Services.AddHostedService<BookingScheduler>();

    // Auth (Logto JWT bearer)
    builder.Services.AddHttpClient("logto-userinfo")
        .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(8));
    builder.Services.AddSingleton<Booking360PrincipalSync>();

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = booking360Options.AuthIssuer;
            options.MetadataAddress = $"{booking360Options.AuthIssuer.TrimEnd('/')}/.well-known/openid-configuration";
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = booking360Options.AuthIssuer,
                ValidateAudience = true,
                ValidAudiences = new[] { booking360Options.ApiResourceIndicator },
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                NameClaimType = "sub",
                RoleClaimType = "roles"
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    if (context.Principal is null)
                    {
                        return;
                    }
                    var token = context.SecurityToken is Microsoft.IdentityModel.JsonWebTokens.JsonWebToken jwt
                        ? jwt.EncodedToken
                        : context.Request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase);
                    var sync = context.HttpContext.RequestServices.GetRequiredService<Booking360PrincipalSync>();
                    await sync.EnrichAsync(context.Principal, token, context.HttpContext.RequestAborted);
                }
            };
        });

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("Admin", policy =>
            policy.RequireAssertion(context =>
                context.User.HasRoleOrScope("Admin", "admin:all")));
    });

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins(booking360Options.AllowedOrigins.ToArray())
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        });
    });

    builder.Services.AddOpenApi();
    builder.Services.AddEndpoints(typeof(Program).Assembly);

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        var database = scope.ServiceProvider.GetRequiredService<Booking360Database>();
        await database.InitializeAsync();

        var storage = scope.ServiceProvider.GetRequiredService<Booking360ObjectStorageService>();
        try
        {
            await storage.EnsureBucketExistsAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to ensure MinIO bucket on startup; will retry on first upload");
        }
    }

    app.UseSerilogRequestLogging();
    app.UseCors();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapEndpoints();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Booking360 API terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program;
