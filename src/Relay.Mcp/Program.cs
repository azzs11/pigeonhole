using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Relay.Mcp;

// Relay MCP server (stdio). Configure with RELAY_URL and RELAY_RUN_TOKEN (from POST /v1/agent-runs).
var url = Environment.GetEnvironmentVariable("RELAY_URL") ?? "http://localhost:5238/";
var token = Environment.GetEnvironmentVariable("RELAY_RUN_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    await Console.Error.WriteLineAsync("RELAY_RUN_TOKEN is not set. Create a run with POST /v1/agent-runs or `relay runs create`.");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP channel: every log line goes to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
using var http = RelayAgentClient.CreateHttpClient(new Uri(url), token);
builder.Services.AddSingleton(new RelayAgentClient(http));
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<RelayTools>();
await builder.Build().RunAsync();
return 0;
