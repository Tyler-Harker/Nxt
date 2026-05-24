using System.Diagnostics;

namespace Nxt.Cli.Commands;

internal static class ProcessRunner
{
    public static async Task<int> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        await p.WaitForExitAsync();
        return p.ExitCode;
    }
}
