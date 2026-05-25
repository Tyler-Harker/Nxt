# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

Nxt — a Next.js-style web framework for .NET, built on ASP.NET Core 10 + Blazor Web App. This is the framework's source, CLI, project template, dev watcher, and end-to-end samples. The README has the user-facing story; this file captures what's needed to work *on* the framework.

## Solution shape

| Project | Role |
|---|---|
| `src/Nxt.Runtime` | DI, page renderer, route table, SSG/ISR cache, ISR worker, `NxtHost.RunAsync` entry point, `NxtHostOptions` hooks |
| `src/Nxt.Generators` | Roslyn incremental generator — scans `Pages/`, `api/` folders, classes tagged `[Middleware]`, emits a `[ModuleInitializer]` that registers everything at startup |
| `src/Nxt.Cli` | Global tool `nxt` — `new` / `dev` / `build` / `start` / `publish` / `update` / `skill` |
| `src/Nxt.Templates` | `dotnet new nxt` template package |
| `tools/Nxt.DevWatcher` | Kestrel reverse proxy on the user-facing port, wraps `dotnet watch` on an internal port, restarts the backend on bad hot-reload deltas / bind errors / health failures, serves a "Rebuilding…" placeholder during downtime so the browser never sees raw stack traces |
| `samples/SampleApp` | Reference app — exercises every feature |
| `samples/IdentityApp` | ASP.NET Core Identity + SQLite, demonstrates `NxtHostOptions` customization |
| `samples/WasmApp` + `samples/WasmApp.Client` | Two-project Blazor WebAssembly setup |
| `samples/TailwindApp` | Tailwind v4 via standalone CLI (no Node) |
| `samples/DocsApp` | The framework's docs site; deployed to GitHub Pages via `.github/workflows/deploy-docs.yml` |
| `Ai/skills/nxt/` | Source for the Claude skill that `nxt skill add` ships to user projects. Bundled into the CLI via `<None Include="..\..\Ai\skills\**" />` in `Nxt.Cli.csproj` (`CopyToOutputDirectory=PreserveNewest`, swept into the nupkg by `PackAsTool`). |

There are no test projects.

## Working *on* the framework — convenience scripts

```bash
./dev-sample              # run SampleApp under the DevWatcher (port 8080, override with PORT=)
./nxt <subcommand> ...    # runs src/Nxt.Cli from source — use instead of the installed `nxt`
                          #   so you exercise the latest CLI without packing/installing
./install-tailwind        # downloads the Tailwind v4 standalone CLI to tools/tailwindcss
                          #   (required to build samples/DocsApp and samples/TailwindApp)
```

`./nxt` and `./dev-sample` are the right way to iterate. The globally-installed `nxt` will lag behind the source tree until you repack and reinstall.

## Reinstalling the CLI globally from a local build

```bash
dotnet pack src/Nxt.Cli -c Release
dotnet tool uninstall -g Nxt.Cli
dotnet tool install   -g --source src/Nxt.Cli/nupkg Nxt.Cli
```

**Use `--source` (singular), not `--add-source`.** There is a public `Nxt.Cli 0.1.0` package somewhere on nuget.org; `--add-source` *adds* the local folder as another feed and the resolver can still pick the public package (which doesn't have local changes). `--source` restricts the install to the local folder. Until `Directory.Build.props` bumps `<Version>` past whatever's public, this trap is permanent.

## Architecture — what the generator does (load-bearing)

`Nxt.Generators` runs as an analyzer (`OutputItemType="Analyzer"` `ReferenceOutputAssembly="false"`) on every project that references it. It walks each project's `Pages/` tree at compile time and emits a generated `.cs` file containing:

- A `[ModuleInitializer]` static method that registers every page's `@page` route, every `api/` class's HTTP-verb methods (`Get`/`Post`/`Put`/`Patch`/`Delete`), and every class tagged `[Middleware(order:)]`.
- Cascading `[Authorize]` attributes from layouts: if `_layout.razor` declares `@attribute [Authorize]`, every page below inherits a generator-emitted `[Authorize]`. This is the canonical way to gate a section.
- Route templates derived from filenames: brackets in filenames become parameters (`[slug].razor` → `{slug}`, `[id:int].razor` → `{id:int}`, `[...rest].razor` → `{*rest}`). Names are lowercased.

**Hard rules these conventions impose:**

- Don't hand-write `@page` directives on files under `Pages/` — the generator emits them and duplicates will fail to compile.
- Don't put non-page components under `Pages/`. Any `.razor` the generator sees in `Pages/` (except files starting with `_`) becomes a route. Shared components go in a separate `Components/` folder.
- `_layout.razor` needs `@inherits LayoutComponentBase`. The `AspNextEnsureLayoutInherits` MSBuild task (in `src/Nxt.Runtime/build/Nxt.Runtime.targets`) injects it idempotently if missing, so explicit `@inherits` is optional but harmless.
- Static assets: `NxtHost.RunAsync` calls `builder.WebHost.UseStaticWebAssets()` so `MapStaticAssets()` finds the asset manifest. Without that, assets return 0 bytes — don't remove it.

## Extensibility — `NxtHostOptions`

`NxtHost.RunAsync(args, opts)` exposes five hooks: `ConfigureServices`, `ConfigureBeforeRouting`, `ConfigureMiddleware`, `ConfigureAfterEndpoints`, `ConfigureRazorEndpoints`. All non-default ASP.NET wiring (auth pipelines, custom endpoints, Identity, EF Core registration, etc.) goes through these — `samples/IdentityApp/Program.cs` is the reference. Don't bake feature-specific code into the default `NxtHost` pipeline; route it through options instead.

## Tailwind in samples

`samples/DocsApp` and `samples/TailwindApp` use Tailwind v4's standalone binary (no Node). The csproj defines:

- `<TailwindCli>` property that probes `tools/tailwindcss` (or `.exe` on Windows)
- A `BuildTailwind` MSBuild target with `BeforeTargets="BeforeBuild"` and `Inputs="Styles/site.css;Pages/**/*.razor"` `Outputs="wwwroot/site.css"` so it regenerates only when sources change

If `tools/tailwindcss` isn't present, the build emits a warning but doesn't fail; pages will render unstyled. Run `./install-tailwind` once at the repo root.

For dark mode: `@custom-variant dark (&:where(.dark, .dark *));` in `site.css` enables `dark:` utilities; toggle a `.dark` class on `<html>`. DocsApp's `Pages/_layout.razor` is the reference implementation (pre-paint detection script in `<HeadContent>`, persists to localStorage, falls back to `prefers-color-scheme`, defaults to dark).

## `nxt publish` modes

`src/Nxt.Cli/Commands/PublishCommand.cs` provides three combinable modes:

- `--static <dir>` — boots the published app on a random port, crawls every reachable page via `<a href>` links, saves each as `<dir>/<path>/index.html`, copies `wwwroot/`. With `--base-path /Foo/`, rewrites absolute internal URLs to be prefixed (for GitHub Pages project sites). Drops `.nojekyll` when a base path is set or WASM `_framework/` exists.
- `--image <tag>` — generates a Dockerfile if missing, runs `docker build`. Pair with `--push <registry/tag>`. `--docker-context <path>` overrides the build context (needed when the csproj has `<ProjectReference>`s outside the project dir — i.e., every in-repo sample).
- `--bundle <path>` — tars `publish/`. Compression chosen by extension (`.tar.gz`/`.tar.bz2`/`.tar.xz`).

## GitHub Pages deploy

`.github/workflows/deploy-docs.yml` builds and deploys `samples/DocsApp` via `actions/upload-pages-artifact@v3` + `actions/deploy-pages@v4`. `REPO_BASE_PATH` env var (`/Nxt/` for `Tyler-Harker/Nxt`) is passed to `nxt publish --base-path`. Forking to a different repo name requires updating that variable. **GitHub Pages must be enabled** on the repo (Settings → Pages → Source: "GitHub Actions") — without that, the deploy step 404s while the build step succeeds.

## Versioning

`Directory.Build.props` sets `<Version>0.1.0</Version>` for every project. Bump it there, not in individual csprojs.
