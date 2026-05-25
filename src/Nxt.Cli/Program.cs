using System.CommandLine;
using Nxt.Cli.Commands;

var root = new RootCommand("Nxt — a Next.js-style web framework for .NET")
{
    NewCommand.Build(),
    DevCommand.Build(),
    BuildCommand.Build(),
    StartCommand.Build(),
    PublishCommand.Build(),
    UpdateCommand.Build(),
    SkillCommand.Build(),
};

return await root.InvokeAsync(args);
