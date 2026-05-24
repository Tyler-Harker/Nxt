using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Nxt.DevWatcher;

/// <summary>
/// Runs <c>dotnet watch</c> and watches its output for known failure signatures. On failure,
/// kills the watch process tree, sweeps any leftover orphan listeners off the backend port,
/// and loops back to restart. Reports health transitions to the shared <see cref="State"/>.
/// </summary>
public sealed class Supervisor(
    string projectPath, int backendPort, State state,
    int healthGraceSec, int healthFailThreshold,
    CancellationToken shutdown)
{
    private static readonly Regex[] FatalPatterns =
    {
        new(@"BadImageFormatException", RegexOptions.Compiled),
        new(@"Failed to bind to address.*address already in use", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"No string associated with token", RegexOptions.Compiled),
    };
    private static readonly Regex ListeningPattern = new(@"Now listening on:", RegexOptions.Compiled);

    public async Task RunForeverAsync()
    {
        var consecutiveCrashes = 0;
        while (!shutdown.IsCancellationRequested)
        {
            var outcome = await RunOnceAsync();
            if (shutdown.IsCancellationRequested) break;

            consecutiveCrashes = outcome.WasHealthyAtLeastOnce ? 0 : consecutiveCrashes + 1;
            state.MarkDown(outcome.Reason);

            Banner($"[33m{outcome.Reason}[0m — recovering…");
            KillPortOrphans(backendPort);

            if (consecutiveCrashes >= 3)
            {
                Banner($"[31m{consecutiveCrashes} consecutive failures — wiping bin/obj[0m");
                var dir = Path.GetDirectoryName(projectPath)!;
                TryDelete(Path.Combine(dir, "bin"));
                TryDelete(Path.Combine(dir, "obj"));
                consecutiveCrashes = 0;
            }

            try { await Task.Delay(TimeSpan.FromMilliseconds(500), shutdown); }
            catch (OperationCanceledException) { break; }
        }
        KillPortOrphans(backendPort);
    }

    private async Task<Outcome> RunOnceAsync()
    {
        var psi = new ProcessStartInfo("dotnet",
            new[] { "watch", "--project", projectPath, "--non-interactive", "run" })
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment =
            {
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{backendPort}",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
            },
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet watch");
        var triggerTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var becameHealthy = false;

        void OnLine(string? line, bool isErr)
        {
            if (line is null) return;
            (isErr ? Console.Error : Console.Out).WriteLine(line);
            if (ListeningPattern.IsMatch(line))
            {
                becameHealthy = true;
                state.MarkHealthy();
            }
            foreach (var pat in FatalPatterns)
            {
                if (pat.IsMatch(line))
                {
                    triggerTcs.TrySetResult($"matched: {pat}");
                    return;
                }
            }
        }

        var stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) is not null) OnLine(line, false);
        });
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync()) is not null) OnLine(line, true);
        });

        _ = HealthCheckLoopAsync(() => becameHealthy, triggerTcs);
        var exitTask = p.WaitForExitAsync(shutdown);

        var done = await Task.WhenAny(triggerTcs.Task, exitTask, Task.Delay(Timeout.Infinite, shutdown));

        if (!p.HasExited)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            try { await p.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }

        if (shutdown.IsCancellationRequested) return new(true, "user shutdown");
        if (done == triggerTcs.Task) return new(becameHealthy, await triggerTcs.Task);
        if (done == exitTask) return new(becameHealthy, $"dotnet watch exited (code {p.ExitCode})");
        return new(becameHealthy, "unknown");
    }

    private async Task HealthCheckLoopAsync(Func<bool> becameHealthy, TaskCompletionSource<string> trigger)
    {
        var start = DateTime.UtcNow;
        var failStreak = 0;
        while (!shutdown.IsCancellationRequested && !trigger.Task.IsCompleted)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), shutdown); } catch { return; }
            if (!becameHealthy() && (DateTime.UtcNow - start).TotalSeconds < healthGraceSec) continue;

            var ok = await CanConnectAsync(backendPort);
            if (ok)
            {
                failStreak = 0;
                if (becameHealthy()) state.MarkHealthy();
                continue;
            }

            failStreak++;
            if (becameHealthy() && failStreak >= healthFailThreshold)
            {
                trigger.TrySetResult($"health check failed {failStreak}× on :{backendPort} after app was ready");
                return;
            }
        }
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var connect = sock.ConnectAsync("127.0.0.1", port);
            var done = await Task.WhenAny(connect, Task.Delay(1000));
            return done == connect && sock.Connected;
        }
        catch { return false; }
    }

    private static void KillPortOrphans(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("lsof", new[] { "-ti", $":{port}", "-sTCP:LISTEN" })
                { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p is null) return;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(line.Trim(), out var pid)) continue;
                try
                {
                    var orphan = Process.GetProcessById(pid);
                    Banner($"  killing orphan PID {pid} ({orphan.ProcessName}) on :{port}");
                    orphan.Kill(entireProcessTree: true);
                    orphan.WaitForExit(2000);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private static void Banner(string msg) =>
        Console.WriteLine($"[36m[dev-babysitter][0m {DateTime.Now:HH:mm:ss} {msg}");

    public sealed record Outcome(bool WasHealthyAtLeastOnce, string Reason);
}
