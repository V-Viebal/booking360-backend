var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var envPrefix = Environment.GetEnvironmentVariable("APP_ENV_PREFIX") ?? "BOOKING360";
var frontendUrl = Environment.GetEnvironmentVariable("APP_FRONTEND_URL")
    ?? Environment.GetEnvironmentVariable($"{envPrefix}_FRONTEND_URL")
    ?? "http://localhost:4200";

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(frontendUrl)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.UseAuthorization();
app.MapControllers();

app.Run();