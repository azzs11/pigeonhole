using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Relay.Api;
using Relay.Api.Auth;
using Relay.Api.Endpoints;
using Relay.Application;
using Relay.Infrastructure;
using Relay.Infrastructure.Observability;
using Relay.Infrastructure.Persistence;
using Relay.Infrastructure.Sandbox;
using Relay.Infrastructure.Webhooks;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
builder.AddRelayObservability("relay-api").WithTracing(t => t.AddAspNetCoreInstrumentation()).WithMetrics(m => m.AddAspNetCoreInstrumentation());

var sandbox = config.GetSection(SandboxOptions.Section).Get<SandboxOptions>() ?? new();
var connectionString = config.GetConnectionString("Relay")
    ?? throw new InvalidOperationException("ConnectionStrings:Relay is not configured.");
builder.Services
    .AddRelayCore(connectionString, config.GetSection(MessagingOptions.Section).Get<MessagingOptions>() ?? new())
    .AddRelayDelivery(config.GetSection(DeliveryOptions.Section).Get<DeliveryOptions>() ?? new())
    .AddRelayWebhooks(
        config.GetSection(WebhookOptions.Section).Get<WebhookOptions>() ?? new(),
        config.GetSection(ReputationOptions.Section).Get<ReputationOptions>() ?? new())
    .AddRelayOps(sandbox)
    .AddRelaySandboxApi(sandbox);

builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ProblemDetailsDefaults.AddTraceId);
builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddRelayRateLimiting(config.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new());

builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Scopes.MessagesSend, p => p.RequireClaim(ApiKeyAuthenticationHandler.ScopeClaim, Scopes.MessagesSend))
    .AddPolicy(Scopes.MessagesRead, p => p.RequireClaim(ApiKeyAuthenticationHandler.ScopeClaim, Scopes.MessagesRead))
    .AddPolicy(Scopes.AgentsManage, p => p.RequireClaim(ApiKeyAuthenticationHandler.ScopeClaim, Scopes.AgentsManage))
    .AddPolicy(Scopes.AgentSend, p => p
        .RequireClaim(ApiKeyAuthenticationHandler.ScopeClaim, Scopes.AgentSend)
        .RequireClaim(ApiKeyAuthenticationHandler.RunClaim));

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<RelayDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapOpenApi().AllowAnonymous();
app.MapScalarApiReference("/docs").AllowAnonymous();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous().ExcludeFromDescription();
app.MapMessageEndpoints();
app.MapSuppressionEndpoints();
app.MapWebhookEndpoints();
app.MapAgentEndpoints();
app.MapOpsEndpoints(sandbox);

await app.RunAsync();
