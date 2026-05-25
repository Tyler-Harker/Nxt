using Nxt;
using Nxt.Bench.Data;

await NxtHost.RunAsync(args, opts =>
{
    opts.ConfigureServices = b => b.Services.AddSingleton<ItemStore>();
});
