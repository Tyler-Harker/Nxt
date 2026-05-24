using System.CommandLine;
using Nxt.Cli.Commands;

var root = new RootCommand("Nxt — a Next.js-style web framework for .NET")
{
    NewCommand.Build(),
    DevCommand.Build(),
    BuildCommand.Build(),
    StartCommand.Build(),
    UpdateCommand.Build(),
};

return await root.InvokeAsync(args);
