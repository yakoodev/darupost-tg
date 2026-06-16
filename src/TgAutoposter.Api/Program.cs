using System.Text.Json.Serialization;
using TgAutoposter.Api.Endpoints;
using TgAutoposter.Api.Realtime;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Infrastructure;

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

app.MapHealthChecks("/health");
app.MapHub<PostUpdatesHub>("/hubs/posts");
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "tg-autoposter" }))
    .WithTags("Health");

app
    .MapDashboardEndpoints()
    .MapChannelEndpoints()
    .MapSourceEndpoints()
    .MapPostEndpoints()
    .MapPipelineEndpoints()
    .MapIntegrationEndpoints();

await app.UseInfrastructureAsync();

app.Run();
