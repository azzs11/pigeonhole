using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Relay.Api;
using Relay.Api.Auth;
using Relay.Api.Endpoints;
using Relay.Application;
using Relay.Infrastructure;
using Relay.Infrastructure.Persistence;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Relay")
    ?? throw new InvalidOperationException("ConnectionStrings:Relay is not configured.");
var messaging = builder.Configuration.GetSection(MessagingOptions.Section).Get<MessagingOptions>() ?? new MessagingOptions();
builder.Services.AddRelayCore(connectionString, messaging);

builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ProblemDetailsDefaults.AddTraceId);
builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Scopes.MessagesSend, p => p.RequireClaim(ApiKeyAuthenticationHandler.ScopeClaim, Scopes.MessagesSend))
    .AddPolicy(Scopes.MessagesRead, p => p.RequireClaim(ApiKeyAuthenticationHandler.ScopeClaim, Scopes.MessagesRead));

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<RelayDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi().AllowAnonymous();
app.MapScalarApiReference("/docs").AllowAnonymous();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous().ExcludeFromDescription();
app.MapMessageEndpoints();

await app.RunAsync();
