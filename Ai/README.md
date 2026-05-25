# Ai

AI assets bundled with the Nxt repo.

## `skills/`

Claude skills that teach an AI assistant how to use the Nxt framework. Each
subdirectory is one skill: a `SKILL.md` (with YAML frontmatter) plus any
supporting reference files.

Install a skill into a project with the CLI:

```bash
nxt skill add nxt          # → ./.claude/skills/nxt/
nxt skill add nxt --global # → ~/.claude/skills/nxt/
nxt skill list             # show which skills are bundled
```

The skill files in this folder are the source of truth — they get packaged
into the `Nxt.Cli` tool and copied to the destination by `nxt skill add`.
