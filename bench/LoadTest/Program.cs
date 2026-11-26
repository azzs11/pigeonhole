using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

// Open-loop load test for the accept path. Request i is *scheduled* at t0 + i/rate and fired on time
// whether or not earlier requests have returned, so a slow server cannot slow the generator down
// (no coordinated omission). Latency is measured from the scheduled time, not from the actual send.
// Usage: dotnet run -c Release --project bench/LoadTest -- --url http://localhost:8080 --key rly_...
//        [--rates 100,250,500,1000] [--seconds 30] [--warmup 5] [--out bench/results/load/accept.json]
string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var url = new Uri(Arg("--url", "http://localhost:8080"));
var key = Arg("--key", Environment.GetEnvironmentVariable("RELAY_KEY") ?? string.Empty);
var rates = Arg("--rates", "100,250,500,1000").Split(',').Select(r => int.Parse(r, CultureInfo.InvariantCulture)).ToArray();
var seconds = int.Parse(Arg("--seconds", "30"), CultureInfo.InvariantCulture);
var warmup = int.Parse(Arg("--warmup", "5"), CultureInfo.InvariantCulture);
var output = Arg("--out", "bench/results/load/accept.json");
var label = Arg("--label", "api 2 vCPU, postgres 2 vCPU");
if (key.Length == 0)
{
    await Console.Error.WriteLineAsync("Pass --key or set RELAY_KEY.");
    return 2;
}

using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 2000, PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
using var http = new HttpClient(handler) { BaseAddress = url, Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
var runId = Guid.CreateVersion7().ToString("N")[..8];

async Task<(double LatencyMs, int Status)> SendAsync(long scheduledTicks, Stopwatch clock, int n)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
    {
        Content = new StringContent(
            $$"""{"to":["load-{{n % 500}}@example.test"],"subject":"load {{runId}} {{n}}","text":"Load test message {{n}}."}""",
            Encoding.UTF8,
            "application/json"),
    };
    request.Headers.Add("Idempotency-Key", $"load-{runId}-{n}");
    int status;
    try
    {
        using var response = await http.SendAsync(request);
        status = (int)response.StatusCode;
    }
    catch (HttpRequestException)
    {
        status = 0;
    }
    catch (TaskCanceledException)
    {
        status = -1;
    }

    return ((clock.ElapsedTicks - scheduledTicks) * 1000.0 / Stopwatch.Frequency, status);
}

async Task<List<(double LatencyMs, int Status)>> RunAsync(int rate, int durationSeconds, int offset)
{
    var total = rate * durationSeconds;
    var tasks = new List<Task<(double, int)>>(total);
    var clock = Stopwatch.StartNew();
    for (var i = 0; i < total; i++)
    {
        var scheduled = (long)(i * (double)Stopwatch.Frequency / rate);
        var wait = scheduled - clock.ElapsedTicks;
        if (wait > Stopwatch.Frequency / 1000)
        {
            await Task.Delay(TimeSpan.FromTicks(wait * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
        }

        tasks.Add(SendAsync(scheduled, clock, offset + i));
    }

    return [.. await Task.WhenAll(tasks)];
}

static double Percentile(double[] sorted, double p) =>
    sorted.Length == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Ceiling(p / 100.0 * sorted.Length) - 1, 0, sorted.Length - 1)];

var steps = new List<object>();
var offset = 0;
Console.WriteLine($"{"rate/s",7} {"sent",7} {"2xx",7} {"err",5} {"p50 ms",8} {"p95 ms",8} {"p99 ms",8} {"max ms",8}");
foreach (var rate in rates)
{
    await RunAsync(Math.Max(10, rate / 5), warmup, offset); // warm the JIT, pools and caches
    offset += Math.Max(10, rate / 5) * warmup;
    var results = await RunAsync(rate, seconds, offset);
    offset += rate * seconds;
    var ok = results.Where(r => r.Status is >= 200 and < 300).Select(r => r.LatencyMs).Order().ToArray();
    var errors = results.Count(r => r.Status is < 200 or >= 300);
    var byStatus = results.GroupBy(r => r.Status).ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => g.Count());
    var step = new
    {
        rate,
        sent = results.Count,
        ok = ok.Length,
        errors,
        byStatus,
        p50 = Math.Round(Percentile(ok, 50), 2),
        p95 = Math.Round(Percentile(ok, 95), 2),
        p99 = Math.Round(Percentile(ok, 99), 2),
        max = Math.Round(ok.Length > 0 ? ok[^1] : double.NaN, 2),
    };
    steps.Add(step);
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{rate,7} {results.Count,7} {ok.Length,7} {errors,5} {step.p50,8:F1} {step.p95,8:F1} {step.p99,8:F1} {step.max,8:F1}"));
}

var report = new
{
    benchmark = "accept latency (open loop)",
    generatedAt = DateTimeOffset.UtcNow,
    environment = new { label, cpus = Environment.ProcessorCount, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription },
    secondsPerStep = seconds,
    warmupSeconds = warmup,
    steps,
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"wrote {output}");
return steps.Count > 0 ? 0 : 1;
