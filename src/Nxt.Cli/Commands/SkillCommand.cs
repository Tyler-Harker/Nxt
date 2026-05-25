using System.CommandLine;

namespace Nxt.Cli.Commands;

/// <summary>
/// <c>nxt skill add &lt;name&gt;</c> / <c>nxt skill list</c> — copies a Claude
/// skill bundled with this CLI into a project's <c>.claude/skills/</c> folder
/// (or the user's <c>~/.claude/skills/</c> with <c>--global</c>).
///
/// The bundled skills live next to the CLI binary at
/// <c>{AppContext.BaseDirectory}/Ai/skills/&lt;name&gt;/</c> — packaged in via
/// the <c>&lt;None Include="..\..\Ai\skills\**" /&gt;</c> block in Nxt.Cli.csproj.
/// </summary>
internal static class SkillCommand
{
    public static Command Build()
    {
        var root = new Command("skill", "Manage Claude skills bundled with Nxt.");
        root.AddCommand(BuildAddCommand());
        root.AddCommand(BuildListCommand());
        return root;
    }

    static Command BuildAddCommand()
    {
        var nameArg   = new Argument<string>("name", "Name of the bundled skill to install (e.g. 'nxt').");
        var globalOpt = new Option<bool>(new[] { "--global", "-g" }, "Install to ~/.claude/skills/ instead of ./.claude/skills/.");
        var forceOpt  = new Option<bool>(new[] { "--force", "-f" }, "Overwrite the destination if it already exists.");
        var outputOpt = new Option<string?>("--output", "Override the destination directory entirely (absolute or relative path to the skill folder).");

        var cmd = new Command("add", "Copy a bundled Claude skill into the current project (or ~/.claude/skills with --global).")
        {
            nameArg, globalOpt, forceOpt, outputOpt,
        };

        cmd.SetHandler((string name, bool global, bool force, string? output) =>
        {
            var skillsRoot = GetBundledSkillsRoot();
            var src = Path.Combine(skillsRoot, name);
            if (!Directory.Exists(src))
            {
                var available = Directory.Exists(skillsRoot)
                    ? string.Join(", ",
                        Directory.EnumerateDirectories(skillsRoot)
                                 .Select(Path.GetFileName))
                    : "(none — bundled skills are missing from this install)";
                Console.Error.WriteLine($"No bundled skill named '{name}'. Available: {available}");
                Environment.Exit(1);
                return;
            }

            string dest = !string.IsNullOrEmpty(output)
                ? Path.GetFullPath(output)
                : global
                    ? Path.Combine(GetHome(), ".claude", "skills", name)
                    : Path.Combine(Environment.CurrentDirectory, ".claude", "skills", name);

            if (Directory.Exists(dest) && !force)
            {
                Console.Error.WriteLine($"Destination already exists: {dest}");
                Console.Error.WriteLine("Re-run with --force to overwrite.");
                Environment.Exit(1);
                return;
            }

            CopyDir(src, dest);
            Console.WriteLine($"Installed skill '{name}' → {dest}");
            Console.WriteLine();
            Console.WriteLine("Claude Code will pick it up on the next session.");
        }, nameArg, globalOpt, forceOpt, outputOpt);

        return cmd;
    }

    static Command BuildListCommand()
    {
        var cmd = new Command("list", "List the Claude skills bundled with this CLI.");
        cmd.SetHandler(() =>
        {
            var skillsRoot = GetBundledSkillsRoot();
            if (!Directory.Exists(skillsRoot))
            {
                Console.WriteLine("(no bundled skills found — this Nxt CLI install is missing the Ai/skills/ payload)");
                return;
            }

            var any = false;
            foreach (var dir in Directory.EnumerateDirectories(skillsRoot).OrderBy(p => p, StringComparer.Ordinal))
            {
                any = true;
                var name = Path.GetFileName(dir);
                var desc = TryReadDescription(Path.Combine(dir, "SKILL.md"));
                Console.WriteLine(desc is null
                    ? $"  {name}"
                    : $"  {name,-16}  {desc}");
            }

            if (!any) Console.WriteLine("(no skills bundled)");
        });
        return cmd;
    }

    static string GetBundledSkillsRoot()
        => Path.Combine(AppContext.BaseDirectory, "Ai", "skills");

    static string GetHome()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // Tiny YAML-frontmatter peek so `nxt skill list` can show each skill's description
    // without taking a YAML dependency. Returns the value of the first `description:`
    // line between the leading `---` markers, truncated to fit on a terminal row.
    static string? TryReadDescription(string skillPath)
    {
        if (!File.Exists(skillPath)) return null;
        using var sr = new StreamReader(skillPath);
        if (sr.ReadLine()?.TrimEnd() != "---") return null;

        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            if (line.TrimEnd() == "---") return null;
            var t = line.TrimStart();
            if (t.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                var v = t["description:".Length..].Trim().Trim('"', '\'');
                return v.Length > 80 ? v[..77] + "..." : v;
            }
        }
        return null;
    }
}
