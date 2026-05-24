using System.CommandLine;

namespace Nxt.Cli.Commands;

/// <summary>
/// <c>nxt new &lt;name&gt;</c> — scaffolds a new Nxt app from the
/// <c>Nxt.Templates</c> template package via <c>dotnet new</c>.
/// </summary>
internal static class NewCommand
{
    public static Command Build()
    {
        var nameArg = new Argument<string>("name", "The name of the new app.");
        var cmd = new Command("new", "Scaffold a new Nxt application.") { nameArg };
        cmd.SetHandler(async (string name) =>
        {
            Console.WriteLine($"Creating Nxt app '{name}'...");

            var exit = await ProcessRunner.RunAsync("dotnet", new[] { "new", "nxt", "-n", name });
            if (exit != 0)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Template instantiation failed with exit code {exit}.");
                Console.Error.WriteLine("Is the Nxt.Templates template installed?");
                Console.Error.WriteLine("  dotnet new list nxt    # check");
                Console.Error.WriteLine();
                Console.Error.WriteLine("If missing, install it from a local pack of the repo:");
                Console.Error.WriteLine("  dotnet new install <repo>/nupkg/Nxt.Templates.<version>.nupkg");
                Environment.Exit(exit);
            }

            Console.WriteLine();
            Console.WriteLine($"  cd {name}");
            Console.WriteLine($"  nxt dev");
            Console.WriteLine();
        }, nameArg);
        return cmd;
    }
}
