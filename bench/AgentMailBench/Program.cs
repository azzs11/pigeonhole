using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentMailBench;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Relay.Application;
using Relay.Infrastructure;
using Relay.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

// AgentMailBench: replay ~150 scripted agent scenarios against four Guard configurations.
// Usage: dotnet run -c Release --project bench/AgentMailBench -- [--seed 7] [--out bench/results/agentmailbench/results.json]
string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var seed = int.Parse(Arg("--seed", "7"), CultureInfo.InvariantCulture);
var output = Arg("--out", "bench/results/agentmailbench/results.json");
var scenarios = Corpus.Build(seed);

await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").WithCommand("-c", "max_connections=300").Build();
Console.WriteLine("Starting PostgreSQL...");
await postgres.StartAsync();

var services = new ServiceCollection().AddLogging(l => l.SetMinimumLevel(LogLevel.None));
services.AddRelayCore(postgres.GetConnectionString(), new MessagingOptions());
await using var sp = services.BuildServiceProvider();
await using (var scope = sp.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<RelayDbContext>().Database.MigrateAsync();
}

var summaries = new List<ConfigSummary>();
foreach (var (name, checks) in Runner.Configs)
{
    var clock = Stopwatch.StartNew();
    var summary = await Runner.RunAsync(sp, name, checks, scenarios, CancellationToken.None);
    summaries.Add(summary);
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{name,-18} block {summary.BlockRate,6:P1}  false-block {summary.FalseBlockRate,6:P1}  approval {summary.ApprovalShare,6:P1}  guard p99 {summary.GuardP99Ms,6:F2} ms  ({clock.Elapsed.TotalSeconds:F0}s)"));
}

static string Git(string arguments)
{
    try
    {
        using var p = Process.Start(new ProcessStartInfo("git", arguments) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return text;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return "unknown";
    }
}

var result = new
{
    benchmark = "AgentMailBench",
    version = 1,
    commit = Git("rev-parse --short HEAD") + (Git("status --porcelain").Length > 0 ? "-dirty" : string.Empty),
    seed,
    generatedAt = DateTimeOffset.UtcNow,
    environment = new { os = RuntimeInformation.OSDescription, cpus = Environment.ProcessorCount },
    method = "docs/benchmarks/agentmailbench.md",
    scenarios = scenarios.GroupBy(s => s.Group).ToDictionary(g => g.Key.ToString(), g => g.Count()),
    knownLimitations = scenarios.Count(s => s.KnownLimitation),
    configs = summaries,
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result,
    new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n");
Console.WriteLine($"Wrote {output}");
