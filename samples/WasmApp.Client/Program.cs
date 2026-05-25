// Minimal WASM bootstrap. The .Client project doesn't run standalone — it's loaded BY
// blazor.web.js as part of the server's response — but the SDK still wants an entry point.
// WebAssemblyHostBuilder.CreateDefault sets up the in-browser DI container; we don't add
// anything to it here, so the file is mostly a placeholder.
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
await builder.Build().RunAsync();
