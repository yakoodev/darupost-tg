using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using TgAutoposter.Api.Auth;
using TgAutoposter.Api.Endpoints;
using TgAutoposter.Api.Realtime;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Infrastructure;
using TgAutoposter.Infrastructure.Options;
using TgAutoposter.Infrastructure.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// --- Authentication & authorization ---
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<IChannelAccess, ChannelAccess>();
builder.Services.AddScoped<IAuditLogger, AuditLogger>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authOptions.Jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = authOptions.Jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOptions.Jwt.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

// Require an authenticated user by default on every endpoint; opt out with [AllowAnonymous].
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("admin", policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:5173",
                "http://127.0.0.1:5173",
                "http://localhost:8081",
                "http://127.0.0.1:8081")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("admin");
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapHub<PostUpdatesHub>("/hubs/posts").AllowAnonymous();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "tg-autoposter" }))
    .AllowAnonymous()
    .WithTags("Health");
app.MapGet("/api/media/{**path}", (string path, IOptions<MediaOptions> mediaOptions) =>
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return Results.NotFound();
    }

    var publicPath = LocalMediaPaths.BuildPublicPath(Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty, Path.GetFileName(path));
    string fullPath;
    try
    {
        fullPath = LocalMediaPaths.BuildFullPath(mediaOptions.Value, publicPath);
    }
    catch (InvalidOperationException)
    {
        return Results.NotFound();
    }

    if (!File.Exists(fullPath))
    {
        return Results.NotFound();
    }

    var contentType = Path.GetExtension(fullPath).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".png" => "image/png",
        _ => "application/octet-stream"
    };

    return Results.File(fullPath, contentType);
})
    .AllowAnonymous()
    .WithTags("Media");

app
    .MapAuthEndpoints()
    .MapUserEndpoints()
    .MapDashboardEndpoints()
    .MapChannelEndpoints()
    .MapSourceEndpoints()
    .MapScheduleEndpoints()
    .MapPostEndpoints()
    .MapPipelineEndpoints()
    .MapIntegrationEndpoints();

await app.UseInfrastructureAsync();
await OwnerBootstrapper.EnsureOwnerAsync(app.Services);

app.Run();
