using System.Net.Sockets;
using System.Text.RegularExpressions;
using Nxt.DevWatcher;
using Microsoft.Extensions.Hosting;

// Nxt dev babysitter.
//
// Architecture:
//
//   browser ─HTTP/WS─► Babysitter Kestrel proxy (front port, e.g. 8080)
//                            │
//                            ▼  (reverse proxy + HTML script injection)
//                      dotnet watch ─► SampleApp (backend port, e.g. 18080)
//
// The user only ever talks to the front port. When the backend wedges (bad hot-reload delta,
// bind race, app crash), the supervisor restarts it; the proxy keeps serving and shows a
// friendly "Rebuilding…" page until the backend is healthy again. The browser sees no raw
// stack traces and auto-reloads when recovery completes.
//
// Usage:
//   dotnet run --project tools/Nxt.DevWatcher -- \
//       --project samples/SampleApp/SampleApp.csproj \
//       --urls http://localhost:8080 \
//       [--backend-port 18080] [--health-grace 15] [--health-fail-threshold 5]

var projectArg = GetArg("--project") ?? throw new ArgumentException("--project is required");
var frontUrl = GetArg("--urls") ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:8080";
var backendPort = int.Parse(GetArg("--backend-port") ?? PickFreePort(18080).ToString());
var healthGraceSec = int.Parse(GetArg("--health-grace") ?? "15");
var healthFailThreshold = int.Parse(GetArg("--health-fail-threshold") ?? "5");

var projectPath = Path.GetFullPath(projectArg);
Banner($"project       : {projectPath}");
Banner($"front (user)  : {frontUrl}");
Banner($"backend (app) : http://127.0.0.1:{backendPort}");

var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

var state = new State { BackendPort = backendPort };
var proxy = ProxyApp.Build(frontUrl, state);

// Start proxy first so the user-facing port is always responsive (it'll serve the placeholder
// until the backend comes up). Use Start/Stop pattern — RunAsync(string) is the other overload.
await proxy.StartAsync(shutdown.Token);
var proxyTask = WaitForShutdownAsync(proxy, shutdown.Token);

static async Task WaitForShutdownAsync(Microsoft.AspNetCore.Builder.WebApplication app, CancellationToken ct)
{
    try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
    try { await app.StopAsync(TimeSpan.FromSeconds(5)); } catch { }
}

// Run the supervisor on the same task pool.
var supervisor = new Supervisor(projectPath, backendPort, state, healthGraceSec, healthFailThreshold, shutdown.Token);
var supervisorTask = supervisor.RunForeverAsync();

await Task.WhenAny(proxyTask, supervisorTask);
shutdown.Cancel();
try { await Task.WhenAll(proxyTask, supervisorTask); } catch { }
Banner("shutting down");
return 0;

static int PickFreePort(int prefer)
{
    // Try `prefer` first, then walk forward until a free port is found.
    for (var p = prefer; p < prefer + 100; p++)
    {
        try
        {
            using var l = new TcpListener(System.Net.IPAddress.Loopback, p);
            l.Start();
            l.Stop();
            return p;
        }
        catch (SocketException) { continue; }
    }
    throw new InvalidOperationException("No free backend port found.");
}

static void Banner(string msg) =>
    Console.WriteLine($"[36m[dev-babysitter][0m {DateTime.Now:HH:mm:ss} {msg}");

string? GetArg(string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}
