using Nxt.Bench.Data;

namespace Nxt.Bench.Api;

public class Items(ItemStore store)
{
    public object GET() => store.All;
}
