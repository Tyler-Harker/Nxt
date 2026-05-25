---
name: nxt
description: Build and edit Nxt applications. Nxt is a Next.js-style web framework for .NET — file-based routing via the Pages/ folder, _layout.razor convention, per-page render modes (SSR / SSG / ISR / InteractiveServer / InteractiveWebAssembly), co-located API endpoints, attribute-based middleware, and a `nxt` CLI for dev/build/publish. Use this skill whenever working in a project that references Nxt.Runtime or runs `nxt` commands.
---

# Nxt — Next.js for .NET

Nxt is a thin convention layer over ASP.NET Core 10 + Blazor Web App. Pages are Razor components. Everything under `Pages/` becomes a route. The runtime + a Roslyn source generator turn the file tree into a router, attach layouts, wire up render modes, register API endpoints, and discover middleware.

## When to apply this skill

- The csproj has `<PackageReference Include="Nxt.Runtime" />` or a `<ProjectReference>` to `Nxt.Runtime.csproj`.
- The user runs `nxt` commands (`nxt dev`, `nxt build`, `nxt publish`, etc).
- The user is editing Razor pages, layouts, or API endpoints under a `Pages/` folder.

## Project shape

```
MyApp/
  Pages/
    Index.razor              → "/"
    about.razor              → "/about"
    docs/
      [slug].razor           → "/docs/{slug}"
      _layout.razor          → wraps everything under /docs/
    api/
      hello.cs               → endpoints under /api/hello
  Program.cs                 → `await NxtHost.RunAsync(args);`
  wwwroot/                   → static assets, served by MapStaticAssets
```

A minimal `Program.cs`:

```csharp
using Nxt;

await NxtHost.RunAsync(args);
```

For apps that need extra services or middleware, pass a callback that mutates `NxtHostOptions`. Each property takes a `WebApplicationBuilder` (services) or `WebApplication` (pipeline) — NOT an `IServiceCollection` directly:

```csharp
await NxtHost.RunAsync(args, opts =>
{
    opts.ConfigureServices       = b   => b.Services.AddDbContext<Db>(...);
    opts.ConfigureBeforeRouting  = app => app.UseAuthentication();
    opts.ConfigureMiddleware     = app => app.UseAuthorization();
    opts.ConfigureAfterEndpoints = app => app.MapIdentityApi<User>();
});
```

## Routing conventions

- File name `MyPage.razor` → route `/mypage` (lowercased). `Index.razor` is the folder's `/` route.
- Folder names map to URL segments: `Pages/blog/Post.razor` → `/blog/post`.
- Brackets in file names become route parameters: `[slug].razor` → `{slug}`, `[id:int].razor` → `{id:int}`.
- Catch-all: `[...rest].razor` → `{*rest}`.
- Files/folders starting with `_` are NOT pages (they're conventions like `_layout.razor` or shared partials). Do not put plain components in `Pages/` — use a separate `Components/` folder.

Do NOT hand-write `@page` directives in files under `Pages/`. The generator emits them.

## Render modes (per page, top of the .razor file)

```razor
@nxt:static              @* SSG — rendered at `nxt build` time, served as a static HTML cache *@
@nxt:revalidate=60       @* ISR — re-render on demand at most every 60 seconds *@
@rendermode InteractiveServer       @* Blazor Server interactivity *@
@rendermode InteractiveWebAssembly  @* WASM interactivity (requires a .Client project — see "WebAssembly") *@
```

Default (no directive) = server-side rendered on every request (SSR). Use `@nxt:static` for docs/marketing/anything content-stable. Use `@rendermode InteractiveServer` for interactive components that need server-side state.

## Layouts — `_layout.razor`

Drop a `_layout.razor` in any folder under `Pages/` to wrap every page below it. Layouts compose: an inner `_layout.razor` is automatically nested inside the layout from the parent folder.

```razor
@* Pages/dashboard/_layout.razor *@
@inherits LayoutComponentBase

<div class="dashboard-shell">
    <nav>...</nav>
    <main>@Body</main>
</div>
```

The MSBuild task injects `@inherits LayoutComponentBase` automatically if missing, so it's safe to omit — but explicit is fine.

### Cascading `[Authorize]`

Attributes you put on a layout class CASCADE to every page below it via the generator. The canonical pattern for an authenticated section:

```razor
@* Pages/dashboard/_layout.razor *@
@attribute [Authorize]
@inherits LayoutComponentBase

<DashboardShell>@Body</DashboardShell>
```

Every page in `Pages/dashboard/**` now requires auth — no need to repeat the attribute.

To chain layouts manually (skip a folder layer): `@attribute [Layout(typeof(OuterLayout))]`.

## Code-behind: split markup and logic into `.razor` + `.razor.cs`

**Prefer this structure for any page or component with more than ~5 lines of logic.** Keep `.razor` files pure markup; put DI, state, and lifecycle in a sibling `.razor.cs`. The Razor SDK compiles each `.razor` into a `partial class`, and the Nxt generator emits its own partial with the `[Route]` attribute. Adding a third partial in `.razor.cs` merges cleanly:

```razor
@* Pages/Profile.razor — markup only, no @code block *@
<h1>@Name</h1>
<button @onclick="Refresh">Reload</button>
```

```csharp
// Pages/Profile.razor.cs — logic, DI, lifecycle
using Microsoft.AspNetCore.Components;

namespace MyApp.Pages;

public partial class Profile : ComponentBase
{
    [Inject] public IUserService Users { get; set; } = default!;

    private string Name { get; set; } = "";

    protected override async Task OnInitializedAsync()
        => Name = await Users.GetNameAsync();

    private async Task Refresh()
        => Name = await Users.GetNameAsync();
}
```

Three rules for the partials to merge cleanly:

1. **Namespace must match** the generator's — for `Pages/X/Y.razor` it's `<RootNamespace>.Pages.X`. (Check `obj/GeneratedFiles/.../NxtPage.*.g.cs` if unsure.)
2. **Class name must match the filename**, case-sensitive — `Profile.razor` ↔ `class Profile`.
3. **Exactly one partial declares `: ComponentBase`.** The Nxt generator deliberately doesn't, so your `.razor.cs` is the natural place. If `_Imports.razor` has `@inherits ComponentBase`, you can skip it there too.

`@inject IService Foo` in markup still works alongside `[Inject]` in the code-behind — pick one style per project. `[Inject]` is the convention when you've already crossed into a `.razor.cs`.

## API endpoints — co-located in `api/` folders

Any class in any folder named `api` (anywhere, not just `Pages/api/`) gets scanned for HTTP-verb methods. **Method names must be UPPERCASE HTTP verbs: `GET`, `POST`, `PUT`, `PATCH`, `DELETE`** — the runtime looks them up by exact name. The route is derived from the folder path + the class name (lowercased, with `Endpoint` suffix stripped if present):

```csharp
// Pages/dashboard/api/Profile.cs  →  /dashboard/api/profile
namespace MyApp.Pages.Dashboard.Api;

public class Profile(Db db)
{
    public async Task<Results<Ok<User>, NotFound>> GET(ClaimsPrincipal user)
    {
        var u = await db.Users.FindAsync(user.GetUserId());
        return u is null ? TypedResults.NotFound() : TypedResults.Ok(u);
    }

    public async Task<IResult> POST(UpdateProfile body)
        => TypedResults.Ok(await db.UpdateProfileAsync(body));
}
```

These are vanilla minimal-API handlers — full DI via primary-constructor or method parameters, return `Results<…>` or `IResult`, use `[FromRoute]` / `[FromQuery]` / `[FromBody]` if you need to override the default binding source.

Use `[ApiRoute("custom-name")]` on the class to override the URL segment when the class name doesn't read well.

## Middleware via `[Middleware(order:)]`

Tag any class with `[Middleware(order: N)]` and implement `InvokeAsync(HttpContext, RequestDelegate)`. It gets discovered and registered in order:

```csharp
[Middleware(order: 10)]
public class RequestLogging
{
    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        var sw = Stopwatch.StartNew();
        await next(ctx);
        Console.WriteLine($"{ctx.Request.Path} took {sw.ElapsedMilliseconds}ms");
    }
}
```

Lower order = earlier in the pipeline.

## WebAssembly (Blazor WASM)

For client-interactive pages, you need a second project (`MyApp.Client.csproj`) using `Microsoft.NET.Sdk.BlazorWebAssembly`. The host project references it and Nxt wires up `AddInteractiveWebAssemblyRenderMode()` + `AddAdditionalAssemblies()` automatically. Pages in the client project marked `@rendermode InteractiveWebAssembly` run in the browser.

See `samples/WasmApp/` + `samples/WasmApp.Client/` for the reference setup.

## Tailwind

Nxt has no opinion on CSS, but the canonical Tailwind v4 setup is the standalone CLI (no Node required). Layout:

```
MyApp/
  Styles/site.css           @import "tailwindcss"; @source "../Pages/**/*.razor";
  wwwroot/site.css          generated, referenced from your layout
```

The `BuildTailwind` MSBuild target (see `samples/TailwindApp` or `samples/DocsApp` csproj) runs `tools/tailwindcss` on every build. Install once with `./install-tailwind` from the repo root.

For `dark:` variants, add `@custom-variant dark (&:where(.dark, .dark *));` to `site.css` and toggle a `dark` class on `<html>`.

## CLI reference

| Command | What |
|---|---|
| `nxt new <name>` | Scaffold a new app from the `Nxt.Templates` template. |
| `nxt dev` | Run with hot reload. Wraps `dotnet watch` with crash recovery + a "Rebuilding…" placeholder. Flags: `--project`, `--port`/`-p`, `--urls`. |
| `nxt build` | Publish for production AND prerender every `@nxt:static` page to disk. Flags: `--configuration`, `--output`. |
| `nxt start` | Run the published build. |
| `nxt publish` | Three modes (combinable): `--static <dir>` (crawl & export to HTML, drops `.nojekyll`, `--base-path /repo/` for GitHub Pages project sites), `--image <tag>` (Dockerfile + `docker build`; pair with `--push` and optionally `--docker-context`), `--bundle <path>` (tar the publish dir; compression by extension). |
| `nxt update` | Reinstall the CLI from the GitHub repo. |
| `nxt skill add <name>` | Copy a bundled Claude skill into the current project (or `--global` for `~/.claude/skills`). |
| `nxt skill list` | List bundled skills. |

## Common patterns

- **A docs/marketing site**: every page starts with `@nxt:static`; deploy with `nxt publish --static ./out`.
- **An authenticated dashboard**: put `[Authorize]` on `Pages/dashboard/_layout.razor`; pages below inherit it.
- **A REST API for an existing Razor app**: drop a `.cs` class with `GET`/`POST` methods into any folder named `api`; no manual `MapGet` calls needed.
- **Custom middleware**: a class with `[Middleware(order: N)]` — no manual `app.UseMiddleware<…>()`.

## Things NOT to do

- Don't put `@page` directives on files under `Pages/` — the generator emits them; manual ones collide.
- Don't put non-page components under `Pages/`; use a separate `Components/` folder. Anything (except `_layout.razor`) the generator sees as a `.razor` under `Pages/` becomes a route.
- Don't pile logic into `@code { }` blocks in `.razor` files when it's more than ~5 lines — split into a sibling `.razor.cs` partial class (see "Code-behind"). Markup and logic separated this way is far easier to read, diff, and refactor.
- Don't name API methods `Get` / `Post` (PascalCase) — the runtime looks for `GET` / `POST` (uppercase) and won't find them. Building the app starts fine; the first request throws `API handler method '<Class>.GET' not found.`
- Don't call `WebApplication.CreateBuilder(args)` or `MapRazorComponents<…>()` by hand — `NxtHost.RunAsync` does it. Customize via `NxtHostOptions`.
- Don't repeat `[Authorize]` on every page in an authenticated section — put it on the section's `_layout.razor`.
