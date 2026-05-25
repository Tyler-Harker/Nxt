using Microsoft.Extensions.DependencyInjection;
using Nxt;

await NxtHost.RunAsync(args, opts =>
{
    // Service-side registration: the Razor render-mode resolver needs a provider for
    // InteractiveWebAssembly. AddRazorComponents().AddInteractiveServerComponents() is
    // already wired by Nxt.Runtime; we layer WASM on top.
    opts.ConfigureServices = builder =>
    {
        builder.Services.AddRazorComponents().AddInteractiveWebAssemblyComponents();
    };

    // Endpoint-side: tell MapRazorComponents to also accept WASM render-mode components,
    // and that the additional assemblies (where those components live) include WasmApp.Client.
    // The build pipeline ships the client's compiled WASM artifacts; blazor.web.js loads them
    // on the first interactive hit.
    opts.ConfigureRazorEndpoints = blazor =>
    {
        blazor
            .AddInteractiveWebAssemblyRenderMode()
            .AddAdditionalAssemblies(typeof(WasmApp.Client.Counter).Assembly);
    };
});
