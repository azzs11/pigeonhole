using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using MailChaos;
using Npgsql;
using Testcontainers.PostgreSql;

// MailChaos: inject faults into three delivery designs and count lost and duplicated emails.
// Usage: dotnet run -c Release --project bench/MailChaos -- [--messages 2000] [--rates 0,0.01,0.02,0.05,0.1]
//        [--seed 42] [--out bench/results/mailchaos/results.json] [--quick]
string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var quick = args.Contains("--quick");
var messages = int.Parse(Arg("--messages", quick ? "300" : "2000"), CultureInfo.InvariantCulture);
var rates = Arg("--rates", quick ? "0,0.05" : "0,0.01,0.02,0.05,0.1")
    .Split(',').Select(r => double.Parse(r, CultureInfo.InvariantCulture)).ToArray();
var seed = int.Parse(Arg("--seed", "42"), CultureInfo.InvariantCulture);
var output = Arg("--out", "bench/results/mailchaos/results.json");

await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").WithCommand("-c", "max_connections=500").Build();
Console.WriteLine("Starting PostgreSQL...");
await postgres.StartAsync();

IDeliverySystem[] systems = [new NaiveSystem(), new OutboxSystem(reconcile: false), new OutboxSystem(reconcile: true)];
var runs = new List<object>();
foreach (var rate in rates)
{
    foreach (var system in systems)
    {
        var database = $"chaos_{Guid.NewGuid():N}";
        await using (var conn = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await conn.OpenAsync();
#pragma warning disable CA2100 // database name is a generated GUID
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {database}", conn);
#pragma warning restore CA2100
            await cmd.ExecuteNonQueryAsync();
        }

        var cs = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = database, MaxPoolSize = 60 }.ConnectionString;
        var run = new RunContext(cs, messages, new FaultPlan(seed, rate), new Ledger(), TimeSpan.FromSeconds(60));
        var clock = Stopwatch.StartNew();
        await system.RunAsync(run, CancellationToken.None);
        clock.Stop();

        var latencies = run.Ledger.Firsts()
            .Select(f => (f.First - run.Submitted[f.Subject]).TotalMilliseconds)
            .Order()
            .ToArray();
        double Percentile(double p) => latencies.Length == 0 ? 0 : latencies[(int)Math.Min(latencies.Length - 1, Math.Floor(p * latencies.Length))];
        var lost = messages - run.Ledger.Distinct;
        var duplicates = run.Ledger.Total - run.Ledger.Distinct;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"rate {rate,5:P0}  {system.Name,-24} lost {lost,4}  duplicates {duplicates,4}  unresolved {run.Unresolved,3}  {clock.Elapsed.TotalSeconds,5:F1}s"));
        runs.Add(new
        {
            system = system.Name,
            faultRate = rate,
            messages,
            delivered = run.Ledger.Distinct,
            lost,
            duplicates,
            lostPer10k = lost * 10_000.0 / messages,
            duplicatesPer10k = duplicates * 10_000.0 / messages,
            unresolved = run.Unresolved,
            clientGaveUp = run.ClientGaveUp,
            deliveryLatencyMs = new { p50 = Percentile(0.5), p99 = Percentile(0.99) },
            faultsInjected = run.Plan.Injected.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(),
            durationSeconds = Math.Round(clock.Elapsed.TotalSeconds, 2),
        });

        NpgsqlConnection.ClearAllPools();
    }
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

var commit = Git("rev-parse --short HEAD") + (Git("status --porcelain").Length > 0 ? "-dirty" : string.Empty);
var result = new
{
    benchmark = "MailChaos",
    version = 1,
    commit,
    seed,
    generatedAt = DateTimeOffset.UtcNow,
    environment = new { os = RuntimeInformation.OSDescription, cpus = Environment.ProcessorCount, dotnet = RuntimeInformation.FrameworkDescription },
    method = "docs/benchmarks/mailchaos.md",
    messagesPerRun = messages,
    faultRates = rates,
    runs,
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"Wrote {output}");
