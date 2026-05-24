using System.CommandLine;
using System.IO.Compression;
using System.Net.Http;

namespace Nxt.Cli.Commands;

/// <summary>
/// <c>nxt update</c> — downloads the latest Nxt source from GitHub, packs the runtime/CLI/template
/// NuGet packages, and reinstalls them via <c>dotnet tool update</c> and <c>dotnet new install --force</c>.
///
/// Self-contained: no git required. The default branch and repo URL can be overridden with
/// <c>--repo</c> and <c>--branch</c> for forks or for testing.
/// </summary>
internal static class UpdateCommand
{
    private const string DefaultRepo = "https://github.com/Tyler-Harker/Nxt";
    private const string DefaultBranch = "master";

    public static Command Build()
    {
        var repoOpt = new Option<string>("--repo", () => DefaultRepo, "GitHub repo URL (https://github.com/owner/name).");
        var branchOpt = new Option<string>("--branch", () => DefaultBranch, "Branch or tag to pull.");
        var keepOpt = new Option<bool>("--keep-source", () => false, "Keep the downloaded source on disk after install (in TMPDIR).");

        var cmd = new Command("update", "Update the Nxt CLI and project template from GitHub.")
        { repoOpt, branchOpt, keepOpt };

        cmd.SetHandler(async (string repo, string branch, bool keep) =>
        {
            var exit = await RunAsync(repo, branch, keep);
            Environment.Exit(exit);
        }, repoOpt, branchOpt, keepOpt);
        return cmd;
    }

    private static async Task<int> RunAsync(string repo, string branch, bool keep)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"nxt-update-{DateTime.UtcNow:yyyyMMddHHmmss}");
        Directory.CreateDirectory(workDir);
        Console.WriteLine($"→ workspace: {workDir}");

        try
        {
            var tarUrl = $"{repo.TrimEnd('/')}/archive/refs/heads/{branch}.tar.gz";
            Console.WriteLine($"→ downloading {tarUrl}");

            var tarPath = Path.Combine(workDir, "source.tar.gz");
            if (!await DownloadAsync(tarUrl, tarPath))
            {
                Console.Error.WriteLine($"  ✗ failed to download from {tarUrl}");
                return 1;
            }

            Console.WriteLine("→ extracting");
            if (await ProcessRunner.RunAsync("tar", new[] { "xzf", tarPath, "-C", workDir }) != 0)
            {
                Console.Error.WriteLine("  ✗ tar failed (is tar installed?)");
                return 1;
            }

            var repoDir = Directory.GetDirectories(workDir).FirstOrDefault(d => !Path.GetFileName(d).StartsWith("."))
                ?? throw new InvalidOperationException("No extracted directory found.");
            Console.WriteLine($"→ source: {repoDir}");

            var nupkgDir = Path.Combine(repoDir, "nupkg");

            Console.WriteLine("→ dotnet pack (3 packages)");
            foreach (var proj in new[] { "Nxt.Runtime", "Nxt.Cli", "Nxt.Templates" })
            {
                var rel = $"src/{proj}";
                var code = await ProcessRunner.RunAsync("dotnet",
                    new[] { "pack", rel, "-c", "Release", "-o", "./nupkg", "--nologo", "-v", "quiet" },
                    repoDir);
                if (code != 0)
                {
                    Console.Error.WriteLine($"  ✗ pack {proj} failed (exit {code})");
                    return code;
                }
                Console.WriteLine($"  ✓ {proj}");
            }

            // Clear NuGet's caches so a same-version package gets re-resolved.
            Console.WriteLine("→ clearing NuGet caches");
            await ProcessRunner.RunAsync("dotnet", new[] { "nuget", "locals", "all", "--clear" });

            Console.WriteLine("→ updating CLI");
            var updateCode = await ProcessRunner.RunAsync("dotnet",
                new[] { "tool", "update", "-g", "--add-source", nupkgDir, "Nxt.Cli" });
            if (updateCode != 0)
            {
                // Fall back to uninstall + install in case the local tool was installed from a
                // different source originally.
                Console.WriteLine("  (update returned non-zero — trying uninstall + install)");
                await ProcessRunner.RunAsync("dotnet", new[] { "tool", "uninstall", "-g", "Nxt.Cli" });
                var installCode = await ProcessRunner.RunAsync("dotnet",
                    new[] { "tool", "install", "-g", "--add-source", nupkgDir, "Nxt.Cli" });
                if (installCode != 0)
                {
                    Console.Error.WriteLine($"  ✗ tool install failed (exit {installCode})");
                    return installCode;
                }
            }

            Console.WriteLine("→ updating project template");
            var templatePkg = Directory.GetFiles(nupkgDir, "Nxt.Templates.*.nupkg").FirstOrDefault();
            if (templatePkg is null)
            {
                Console.Error.WriteLine("  ✗ no Nxt.Templates.*.nupkg in pack output");
                return 1;
            }
            var newCode = await ProcessRunner.RunAsync("dotnet",
                new[] { "new", "install", templatePkg, "--force" });
            if (newCode != 0) Console.Error.WriteLine($"  ! template install returned exit {newCode} (probably non-fatal)");

            Console.WriteLine();
            Console.WriteLine("✓ Done. Try: nxt new my-app");
            return 0;
        }
        finally
        {
            if (!keep)
            {
                try { Directory.Delete(workDir, recursive: true); } catch { }
            }
            else
            {
                Console.WriteLine($"  (source kept at {workDir})");
            }
        }
    }

    private static async Task<bool> DownloadAsync(string url, string destination)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("nxt-cli/0.1");
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return false;
            await using var src = await response.Content.ReadAsStreamAsync();
            await using var dst = File.Create(destination);
            await src.CopyToAsync(dst);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  download error: {ex.Message}");
            return false;
        }
    }
}
