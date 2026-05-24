using System.CommandLine;

namespace Nxt.Cli.Commands;

/// <summary>
/// <c>nxt build</c> — publishes the app for production and then asks the published
/// app to run the SSG prerender pass. The published app honours the <c>NXT_PRERENDER</c>
/// environment variable: when set, it runs <see cref="Nxt.Rendering.StaticSiteGenerator"/>
/// and exits before binding to a port.
/// </summary>
internal static class BuildCommand
{
    public static Command Build()
    {
        var configOpt = new Option<string>("--configuration", () => "Release", "Build configuration.");
        var outputOpt = new Option<string>("--output", () => "./publish", "Publish output directory.");
        var cmd = new Command("build", "Publish the app for production and prerender static pages.")
        { configOpt, outputOpt };
        cmd.SetHandler(async (string config, string output) =>
        {
            Console.WriteLine("→ dotnet publish");
            var publish = await ProcessRunner.RunAsync("dotnet",
                new[] { "publish", "-c", config, "-o", output });
            if (publish != 0) Environment.Exit(publish);

            Console.WriteLine("→ Nxt prerender pass");
            Environment.SetEnvironmentVariable("NXT_PRERENDER", "1");
            var dll = Directory.GetFiles(output, "*.dll").FirstOrDefault(f =>
                File.Exists(Path.ChangeExtension(f, ".runtimeconfig.json")));
            if (dll is null)
            {
                Console.Error.WriteLine("Could not locate published entry assembly.");
                Environment.Exit(1);
            }
            var prerender = await ProcessRunner.RunAsync("dotnet", new[] { dll! });
            Environment.SetEnvironmentVariable("NXT_PRERENDER", null);
            Environment.Exit(prerender);
        }, configOpt, outputOpt);
        return cmd;
    }
}
