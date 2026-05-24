using Nxt;
using SampleApp;

await NxtHost.RunAsync(args, builder =>
{
    // App-level DI — anything registered here is available everywhere (pages, API, middleware).
    builder.Services.AddSingleton<IGreeting, Greeting>();
});
