# Nxt

A Next.js-style web framework for .NET. File-based routing, SSR / SSG / ISR, both Blazor (`.razor`) and Razor (`.cshtml`) components, first-class DI.

## Getting started

Nxt isn't on NuGet.org yet, so you install everything from a local pack of the source. One-time setup:

```bash
git clone https://github.com/Tyler-Harker/Nxt.git
cd Nxt

# Pack everything the CLI + a generated app will need.
# (Nxt.Generators is bundled into Nxt.Runtime as an analyzer — no separate pack.)
dotnet pack src/Nxt.Runtime   -c Release -o ./nupkg
dotnet pack src/Nxt.Cli       -c Release -o ./nupkg
dotnet pack src/Nxt.Templates -c Release -o ./nupkg

# Add the nupkg folder as a persistent NuGet source — so apps created by
# `nxt new` can find Nxt.Runtime when they restore.
dotnet nuget add source "$(pwd)/nupkg" --name nxt-local

# Install the CLI globally and the project template into `dotnet new`.
dotnet tool install -g --add-source ./nupkg Nxt.Cli
dotnet new install ./nupkg/Nxt.Templates.0.1.0.nupkg
```

If `nxt` isn't on PATH after install, add the dotnet tool directory:

```bash
echo 'export PATH="$PATH:$HOME/.dotnet/tools"' >> ~/.bashrc   # or ~/.zshrc
source ~/.bashrc
```

Then anywhere on your machine:

```bash
nxt new my-app
cd my-app
nxt dev          # http://localhost:5000 (or pass --port 8080)
```

To update later: `git pull`, re-run the three `dotnet pack` commands, then
`dotnet tool update -g --add-source ./nupkg Nxt.Cli` and
`dotnet new install ./nupkg/Nxt.Templates.0.1.0.nupkg --force`.

### Or, just play with the sample in-repo

If you only want to poke at the framework without installing anything globally:

```bash
git clone https://github.com/Tyler-Harker/Nxt.git
cd Nxt
./dev-sample          # http://localhost:8080
```

`./dev-sample` runs the babysitter (debounced rebuilds + browser auto-refresh + crash recovery) against `samples/SampleApp`. No `nxt` command needed — handy when you're hacking on the framework itself.

## How an Nxt project is laid out

```
my-app/
├── Program.cs               # one line: await NxtHost.RunAsync(args);
├── Pages/                   # file-based routes (both .razor and .cshtml)
│   ├── Index.razor          → /
│   ├── About.cshtml         → /about
│   └── Blog/[slug].razor    → /blog/{slug}
├── Api/                     # top-level API endpoints
│   └── HelloEndpoint.cs     → /api/hello
└── Middleware/              # auto-registered classes with [Middleware]
    └── RequestLogger.cs

# Colocated APIs live next to the page they serve:
Pages/Blog/api/
├── Comments.cs              → /api/blog/comments     (no attribute needed)
└── [id].cs                  → /api/blog/{id}         (dynamic segment from filename)
```

## Render modes (set per page via magic comments)

```razor
@* @nxt:static *@                       → SSG (prerendered at build time)
@* @nxt:revalidate=60 *@                → ISR (cached, regenerated every 60s)
@* @nxt:interactive-server *@           → SSR + Blazor Server interactivity
@* @nxt:interactive-wasm *@             → SSR + Blazor WebAssembly hydration
@* @nxt:layout=MyApp.Some.OtherLayout *@   (override; usually unnecessary)
```

Default render mode is SSR.

## Layouts

Drop a `_layout.razor` with `@Body` in any `Pages/` subfolder and it auto-wraps every page
in that folder and below. `@inherits LayoutComponentBase` is **auto-injected** on first
build — you don't have to write it.

Layouts **nest**: a `Pages/Blog/_layout.razor` renders inside `Pages/_layout.razor`, which is
the outermost. Pages under `Pages/Blog/` get the inner layout; pages elsewhere only get the
outer. There's no limit on depth.

```
Pages/_layout.razor                ← wraps everything
Pages/Blog/_layout.razor           ← additionally wraps blog pages
Pages/Blog/[slug].razor            ← rendered inside Blog layout inside root layout
Pages/Counter.razor                ← only the root layout
```

Use `@* @nxt:layout=Type.Full.Name *@` only if you need a non-conventional layout type.

## Solution layout

| Project | What it does |
|---|---|
| `src/Nxt.Runtime` | Runtime — DI, page renderers, route table, SSG/ISR cache, ISR worker |
| `src/Nxt.Generators` | Roslyn incremental generator — scans Pages/Api/Middleware and emits a `[ModuleInitializer]` registering everything |
| `src/Nxt.Cli` | Global tool `nxt` with `new` / `dev` / `build` / `start` |
| `src/Nxt.Templates` | `dotnet new nxt` template package |
| `samples/SampleApp` | End-to-end demo using every feature |
| `tools/Nxt.DevWatcher` | Babysitter — Kestrel reverse proxy on the front port. Runs `dotnet watch` against an internal backend port; restarts it on bad hot-reload delta / bind errors / health-check failures. While the backend is down, the proxy serves a "Rebuilding…" placeholder so the browser never sees raw stack traces. Browser auto-reloads when the backend comes back. Wrapped by `./dev-sample`. |

## Run the sample

```bash
dotnet run --project samples/SampleApp
# then visit:
# http://localhost:5099/                  (Blazor SSR, layout, DI)
# http://localhost:5099/about             (Razor SSG)
# http://localhost:5099/blog/hello-world  (dynamic route)
# http://localhost:5099/dashboard         (ISR, 30s revalidation)
# http://localhost:5099/counter           (Blazor interactive server)
# http://localhost:5099/api/hello         (API endpoint)
```

## DI

Anything registered in `Program.cs` is available everywhere — `@inject` in Blazor components, constructor injection in API endpoints and middleware:

```csharp
await NxtHost.RunAsync(args, builder =>
{
    builder.Services.AddSingleton<IGreeting, Greeting>();
});
```

## Known v1 limitations

- Razor (`.cshtml`) page layout is rendered via the BlazorPageRenderer's wrapper for `.razor` only; for `.cshtml` pages use the standard `_ViewStart.cshtml` / `Layout` convention.
- Interactive WASM render mode is registered but the demo only exercises interactive server.
- ISR regeneration uses a simple periodic poll; no on-demand revalidation API yet.
- Source-generator-discovered routes are case-folded to lowercase URLs.
